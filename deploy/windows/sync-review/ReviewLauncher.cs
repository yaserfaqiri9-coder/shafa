using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace AlShifa.SyncReview.Launcher {
 static class Program {
  [STAThread] static void Main() {
   Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false); Application.Run(new ReviewForm());
  }
 }
 sealed class ReviewForm : Form {
  Label text; Button start,stop; ProgressBar progress; bool running,finished,syncAccepted;string workDirectory;
  public ReviewForm() {
   Text="AlShifa — اتصال و فعال‌سازی امن Sync";Size=new Size(710,335);StartPosition=FormStartPosition.CenterScreen;
   RightToLeft=RightToLeft.Yes;RightToLeftLayout=true;Font=new Font("Tahoma",11);FormBorderStyle=FormBorderStyle.FixedDialog;MaximizeBox=false;
   text=new Label {Left=25,Top=20,Width=645,Height=155,Text="این ابزار، نشست موقت مدیریتی را با ارتباط خروجی HTTPS به سرور خود الشفا برقرار می‌کند. دستورها فقط با امضای دفتر اجرا می‌شوند.\r\nپس از تطبیق و بررسی خطر تکرار، مراحل ثبت، کلید و Sync از همین نشست ادامه می‌یابد.\r\nبرای بکاپ یکپارچه، برنامه مدت کوتاه متوقف و دوباره راه‌اندازی می‌شود؛ ابتدا کارهای باز را ذخیره کنید.\r\nهیچ Reset یا پاک‌سازی معلومات انجام نمی‌شود. تا پایان، این پنجره باز بماند."};
   progress=new ProgressBar {Left=25,Top=185,Width=645,Height=20,Style=ProgressBarStyle.Marquee,Visible=false};
   start=new Button {Left=220,Top=218,Width=270,Height=38,Text="شروع بررسی و بکاپ امن"};start.Click+=async (s,e)=>{if(finished)Close();else await Run();};
   start.Text="شروع اتصال و فعال‌سازی امن";
   stop=new Button {Left=160,Top=265,Width=390,Height=27,Text="توقف نشست پس از پایان مرحلهٔ جاری",Visible=false};stop.Click+=(s,e)=>RequestStop();
   Controls.Add(text);Controls.Add(progress);Controls.Add(start);Controls.Add(stop);FormClosing+=(s,e)=>{if(running){RequestStop();e.Cancel=true;}};
  }
  void RequestStop(){if(workDirectory!=null){File.WriteAllText(Path.Combine(workDirectory,"session-stop.request"),"stop");stop.Enabled=false;stop.Text="پس از پایان مرحلهٔ جاری، نشست متوقف می‌شود";}}
  static void PrivateDirectory(string path) {
   var existing=new DirectoryInfo(path);while(existing!=null){if(existing.Exists && (existing.Attributes&FileAttributes.ReparsePoint)!=0)throw new IOException("Unsafe path");existing=existing.Parent;}
   Directory.CreateDirectory(path);var acl=new DirectorySecurity();acl.SetAccessRuleProtection(true,false);
   foreach(var sid in new[]{"S-1-5-18","S-1-5-32-544"})acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid),FileSystemRights.FullControl,InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit,PropagationFlags.None,AccessControlType.Allow));
   new DirectoryInfo(path).SetAccessControl(acl);
  }
  static string Quote(string s) {return "\""+s.Replace("\"","\\\"")+"\"";}
  async Task Run() {
   if(running)return;running=true;start.Enabled=false;progress.Visible=true;stop.Visible=true;
   string returned=null;
   try {
    var assembly=Assembly.GetExecutingAssembly();byte[] payload;
    using(var source=assembly.GetManifestResourceStream("payload.zip"))using(var bytes=new MemoryStream()){source.CopyTo(bytes);payload=bytes.ToArray();}
    string actual;using(var sha=SHA256.Create())actual=BitConverter.ToString(sha.ComputeHash(payload)).Replace("-","").ToLowerInvariant();
    if(actual!="__PAYLOAD_SHA256__")throw new InvalidOperationException("Integrity failed");
    var parent=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),"AlShifaSyncReview");PrivateDirectory(parent);
    var work=Path.Combine(parent,Guid.NewGuid().ToString("N"));PrivateDirectory(work);workDirectory=work;
    using(var bytes=new MemoryStream(payload))using(var zip=new ZipArchive(bytes,ZipArchiveMode.Read))foreach(var entry in zip.Entries) {
     if(entry.Name!=entry.FullName || entry.Name.Contains("..") || entry.Name.IndexOfAny(new char[]{'/', '\\',':'})>=0)throw new IOException("Invalid entry");
     using(var source=entry.Open())using(var target=new FileStream(Path.Combine(work,entry.Name),FileMode.CreateNew))source.CopyTo(target);
    }
    returned=Path.Combine(work,"unused-return");
    var psi=new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),@"System32\WindowsPowerShell\v1.0\powershell.exe"),
      "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "+Quote(Path.Combine(work,"Review.ps1"))+" -PackageDirectory "+Quote(work)+" -ReturnDirectory "+Quote(returned)) {
      UseShellExecute=false,CreateNoWindow=true,WorkingDirectory=work,RedirectStandardOutput=true,RedirectStandardError=true };
    int code=await Task.Run(()=>{using(var p=new Process {StartInfo=psi}) {
      p.OutputDataReceived+=(s,e)=>{var line=e.Data;if(line==null)return;string stage=null;
       if(line=="STAGE:BACKUP")stage="در حال تهیهٔ بکاپ یکپارچه و شواهد صف؛ برنامه دوباره راه‌اندازی می‌شود.";
       if(line=="STAGE:RESTORE")stage="در حال بازیابی و تطبیق روی MySQL جداگانه؛ برنامهٔ اصلی دوباره راه‌اندازی شده است.";
       if(line=="STAGE:ENCRYPT")stage="در حال حفاظت و رمزگذاری نتیجهٔ بررسی.";
       if(line=="STAGE:CONNECTED")stage="ارتباط حفاظت‌شده برقرار شد؛ بررسی، بکاپ و تطبیق در حال انجام است.";
       if(line=="STAGE:WAITING")stage="نشست فعال است؛ مراحل تطبیق و اتصال از دفتر ادامه می‌یابد. این پنجره باز بماند.";
       if(line=="STAGE:RETRY")stage="ارتباط موقتاً در دسترس نیست؛ تلاش خودکار ادامه دارد. برنامهٔ محلی مستقل کار می‌کند.";
       if(line.StartsWith("STAGE:JOB_"))stage="در حال اجرای مرحلهٔ امضاشدهٔ اتصال یا آزمایش Sync. تا پایان مرحله منتظر بمانید.";
       if(line=="RESULT:SYNC_ACCEPTED")syncAccepted=true;
       if(stage!=null)BeginInvoke((Action)(()=>text.Text=stage));};
      p.Start();p.BeginOutputReadLine();var ignored=p.StandardError.ReadToEndAsync();p.WaitForExit();ignored.GetAwaiter().GetResult();return p.ExitCode;
    }});
    text.Text=(code==0 && syncAccepted?"همگام‌سازی و آزمایش عملی از سوی دفتر تأیید شد؛ نشست موقت پایان یافت.":"نشست متوقف یا منقضی شد. تکمیل Sync هنوز تأیید نشده است. نتیجهٔ هر مرحله به شکل حفاظت‌شده در نشست محفوظ است.")+"\r\n\r\nکلید یا پوشهٔ secrets را کپی یا ارسال نکنید.";
   } catch {
    text.Text="نشست اجرا نشد یا عملیات حفاظت‌شده کامل نشد. تکمیل Sync تأیید نشده است. این پنجره را ببندید و وضعیت را به دفتر اطلاع دهید.";
   } finally {running=false;finished=true;progress.Visible=false;stop.Visible=false;start.Text="بستن";start.Enabled=true;}
  }
 }
}
