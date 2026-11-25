using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Security.Cryptography;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Collections.Generic;

namespace AlShifa.SyncReview {
 public sealed class NativeResult { public int ExitCode; public string Output; }
 public static class Native {
  public static string Quote(string value) {
   if (value.IndexOfAny(new char[]{'\0','\r','\n'})>=0) throw new InvalidOperationException("Invalid argument");
   return "\""+System.Text.RegularExpressions.Regex.Replace(System.Text.RegularExpressions.Regex.Replace(value,@"(\\*)""", "$1$1\\\""),@"(\\+)$", "$1$1")+"\"";
  }
  public static ProcessStartInfo Info(string exe, string[] args) {
   return new ProcessStartInfo(exe,string.Join(" ",args.Select(Quote))) { UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,RedirectStandardInput=true,StandardOutputEncoding=new UTF8Encoding(false),StandardErrorEncoding=new UTF8Encoding(false) };
  }
  public static NativeResult Run(string exe,string[] args,string inputFile,string errorFile,int timeoutMs) {
   using(var p=new Process {StartInfo=Info(exe,args)}) {
    p.Start(); var stdout=p.StandardOutput.ReadToEndAsync(); var stderr=p.StandardError.ReadToEndAsync();
    var input=Task.Run(()=> { try {if(!string.IsNullOrEmpty(inputFile)) using(var f=File.OpenRead(inputFile)) f.CopyTo(p.StandardInput.BaseStream); } finally {p.StandardInput.Close();} });
    if(!p.WaitForExit(timeoutMs)) {p.Kill();p.WaitForExit();throw new InvalidOperationException("Private process timed out");}
    Task.WaitAll(stdout,stderr,input);
    if(!string.IsNullOrEmpty(errorFile)) File.WriteAllText(errorFile,stderr.Result);
    return new NativeResult {ExitCode=p.ExitCode,Output=stdout.Result};
   }
  }
 }
 public static class Crypto {
  public static byte[] Random(int n) {var b=new byte[n]; using(var r=RandomNumberGenerator.Create())r.GetBytes(b);return b;}
  public static string Hash(byte[] b) {using(var s=SHA256.Create()) return BitConverter.ToString(s.ComputeHash(b)).Replace("-","").ToLowerInvariant();}
  public static string TextHash(string s) {return Hash(Encoding.UTF8.GetBytes(s));}
  public static string Mac(byte[] key,string text) {using(var h=new HMACSHA256(key))return BitConverter.ToString(h.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-","").ToLowerInvariant();}
  public static string NewProtectedKey(string path) {
   if(File.Exists(path))throw new InvalidOperationException("Key exists");
   using(var rsa=new RSACng(3072)) {
    var priv=ProtectedData.Protect(Encoding.UTF8.GetBytes(rsa.ToXmlString(true)),null,DataProtectionScope.LocalMachine);
    using(var f=new FileStream(path,FileMode.CreateNew))f.Write(priv,0,priv.Length);
    return rsa.ToXmlString(false);
   }
  }
  public static string Sign(string privatePath,byte[] data) {
   using(var rsa=new RSACng()) {
    rsa.FromXmlString(Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(privatePath),null,DataProtectionScope.LocalMachine)));
    return Convert.ToBase64String(rsa.SignData(data,HashAlgorithmName.SHA256,RSASignaturePadding.Pss));
   }
  }
  public static bool Verify(string publicXml,byte[] data,string signature) {
   try {using(var rsa=new RSACng()) {rsa.FromXmlString(publicXml);return rsa.VerifyData(data,Convert.FromBase64String(signature),HashAlgorithmName.SHA256,RSASignaturePadding.Pss);}}
   catch(CryptographicException){return false;}
   catch(FormatException){return false;}
  }
  public static string Seal(string publicXml,string packageId,byte[] plaintext) {
   var material=Random(64); var iv=Random(16);byte[] cipher,wrapped;
   using(var rsa=new RSACng()) {rsa.FromXmlString(publicXml);wrapped=rsa.Encrypt(material,RSAEncryptionPadding.OaepSHA256);}
   using(var aes=Aes.Create()) {aes.Key=material.Take(32).ToArray();aes.IV=iv;aes.Mode=CipherMode.CBC;aes.Padding=PaddingMode.PKCS7;using(var enc=aes.CreateEncryptor())cipher=enc.TransformFinalBlock(plaintext,0,plaintext.Length);}
   var d=new Dictionary<string,string>{{"format","AlShifa.SyncReview.1"},{"packageId",packageId},{"recipientKeyId",TextHash(publicXml)},
    {"wrappedKeys",Convert.ToBase64String(wrapped)},{"iv",Convert.ToBase64String(iv)},{"ciphertext",Convert.ToBase64String(cipher)}};
   var aad=string.Join("\n",new[]{d["format"],d["packageId"],d["recipientKeyId"],d["wrappedKeys"],d["iv"],d["ciphertext"]});
   d["tag"]=Mac(material.Skip(32).ToArray(),aad);Array.Clear(material,0,material.Length);
   return new JavaScriptSerializer {MaxJsonLength=int.MaxValue}.Serialize(d);
  }
  public static byte[] Open(string privatePath,string packageId,string envelope) {
   var d=new JavaScriptSerializer {MaxJsonLength=int.MaxValue}.Deserialize<Dictionary<string,string>>(envelope);
   if(d["format"]!="AlShifa.SyncReview.1" || d["packageId"]!=packageId)throw new CryptographicException("Wrong package");
   byte[] material;
   using(var rsa=new RSACng()) {
    rsa.FromXmlString(Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(privatePath),null,DataProtectionScope.LocalMachine)));
    if(d["recipientKeyId"]!=TextHash(rsa.ToXmlString(false)))throw new CryptographicException("Wrong recipient");
    material=rsa.Decrypt(Convert.FromBase64String(d["wrappedKeys"]),RSAEncryptionPadding.OaepSHA256);
   }
   if(material.Length!=64)throw new CryptographicException("Invalid material");
   var aad=string.Join("\n",new[]{d["format"],d["packageId"],d["recipientKeyId"],d["wrappedKeys"],d["iv"],d["ciphertext"]});
   var expected=Mac(material.Skip(32).ToArray(),aad);var supplied=d["tag"];int diff=expected.Length^supplied.Length;
   for(int i=0;i<expected.Length;i++)diff|=expected[i]^(i<supplied.Length?supplied[i]:'\0');
   if(diff!=0)throw new CryptographicException("Authentication failed");
   try {using(var aes=Aes.Create()) {
    aes.Key=material.Take(32).ToArray();aes.IV=Convert.FromBase64String(d["iv"]);aes.Mode=CipherMode.CBC;aes.Padding=PaddingMode.PKCS7;
    var cipher=Convert.FromBase64String(d["ciphertext"]);using(var dec=aes.CreateDecryptor())return dec.TransformFinalBlock(cipher,0,cipher.Length);
   }} finally {Array.Clear(material,0,material.Length);}
  }
 }
}
