using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace AlShifa.Recovery
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            using (var mutex = new Mutex(false, @"Global\AlShifaReplacementRecovery"))
            {
                if (!mutex.WaitOne(0)) { MessageBox.Show("بازیابی دیگری در حال اجرا است."); return; }
                try { Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false); Application.Run(new RecoveryForm()); }
                finally { mutex.ReleaseMutex(); }
            }
        }
    }

    internal sealed class RecoveryForm : Form
    {
        private readonly TextBox source = new TextBox { Width = 570, RightToLeft = RightToLeft.No };
        private readonly TextBox password = new TextBox { Width = 570, UseSystemPasswordChar = true, RightToLeft = RightToLeft.No };
        private readonly TextBox target = new TextBox { Width = 570, Text = @"C:\AlShifa-Recovered", RightToLeft = RightToLeft.No };
        private readonly TextBox log = new TextBox { Width = 660, Height = 250, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, RightToLeft = RightToLeft.No };
        private readonly Button start = new Button { Text = "شروع بازیابی", AutoSize = true };
        private bool busy;
        public RecoveryForm()
        {
            Text = "بازیابی سرور جایگزین الشفا"; ClientSize = new Size(720, 530);
            Font = new Font("Segoe UI", 10); RightToLeft = RightToLeft.Yes; RightToLeftLayout = true;
            StartPosition = FormStartPosition.CenterScreen;
            var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(22), FlowDirection = FlowDirection.TopDown, WrapContents = false };
            Controls.Add(panel);
#if FULL_RECOVERY
            Text = "نصب کامل سرور جایگزین الشفا"; target.Text = @"C:\AlShifa";
            panel.Controls.Add(new Label { Text = "برنامه و اطلاعات شفاخانه روی کمپیوتر جدید نصب می‌شوند. سرور قبلی باید خاموش بماند.", AutoSize = true, MaximumSize = new Size(660, 0) });
            panel.Controls.Add(new Label { Text = "رمز خصوصی نصب:", AutoSize = true }); panel.Controls.Add(password);
#else
            panel.Controls.Add(new Label { Text = "پوشهٔ کامل AlShifa سرور قبلی را انتخاب کنید. سرور قبلی باید خاموش بماند.", AutoSize = true, MaximumSize = new Size(660, 0) });
            panel.Controls.Add(source);
            var browse = new Button { Text = "انتخاب پوشهٔ کپی‌شده", AutoSize = true };
            browse.Click += delegate { using (var picker = new FolderBrowserDialog()) { if (picker.ShowDialog(this) == DialogResult.OK) source.Text = picker.SelectedPath; } };
            panel.Controls.Add(browse);
#endif
            panel.Controls.Add(new Label { Text = "پوشهٔ نصب جدید؛ باید خالی باشد:", AutoSize = true }); panel.Controls.Add(target);
            panel.Controls.Add(new Label { Text = "حساب‌ها، اطلاعات و هویت قبلی Sync بازیابی می‌شوند. آیکون‌های قبلی کارمندان در همان شبکه سرور را پیدا می‌کنند.", AutoSize = true, MaximumSize = new Size(660, 0) });
            panel.Controls.Add(start); panel.Controls.Add(log);
            start.Click += delegate { BeginRecovery(); };
            FormClosing += delegate(object sender, FormClosingEventArgs e) { if (busy) e.Cancel = true; };
        }
        private void Line(string value)
        {
            if (value == null || IsDisposed) return;
            BeginInvoke((Action)delegate { log.AppendText(value + Environment.NewLine); });
        }
        private void BeginRecovery()
        {
            var from = source.Text.Trim(); var to = target.Text.Trim();
#if FULL_RECOVERY
            var recoveryPassword = password.Text.Trim();
            if (string.IsNullOrWhiteSpace(recoveryPassword) || to.Contains('"') || to.Contains('\r') || to.Contains('\n')) { MessageBox.Show(this, "رمز خصوصی نصب و پوشهٔ معتبر را وارد کنید."); return; }
            password.Clear(); password.Enabled = false;
#else
            if (!Directory.Exists(from) || from.Contains('"') || to.Contains('"') || from.Contains('\r') || from.Contains('\n') || to.Contains('\r') || to.Contains('\n')) { MessageBox.Show(this, "پوشهٔ معتبر انتخاب کنید."); return; }
#endif
            busy = true; start.Enabled = false; source.Enabled = target.Enabled = false;
            new Thread(delegate()
            {
                try
                {
                    var work = Extract();
                    var shell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), @"System32\WindowsPowerShell\v1.0\powershell.exe");
#if FULL_RECOVERY
                    var args = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"" + Path.Combine(work, @"scripts\install-replacement-server.ps1") + "\" -Root \"" + to.TrimEnd('\\') + "\" -Package \"" + work + "\" -InstallerFolder \"" + Path.GetDirectoryName(Application.ExecutablePath) + "\" -Apply";
#else
                    var args = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"" + Path.Combine(work, @"scripts\recover-copied-server.ps1") + "\" -Source \"" + from.TrimEnd('\\') + "\" -Root \"" + to.TrimEnd('\\') + "\" -Package \"" + work + "\" -Apply";
#endif
                    var info = new ProcessStartInfo(shell, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
#if FULL_RECOVERY
                    info.EnvironmentVariables["ALSHIFA_RECOVERY_PASSWORD"] = recoveryPassword;
#endif
                    using (var process = Process.Start(info))
                    {
                        process.OutputDataReceived += delegate(object s, DataReceivedEventArgs e) { Line(e.Data); };
                        process.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e) { Line(e.Data); };
                        process.BeginOutputReadLine(); process.BeginErrorReadLine(); process.WaitForExit();
                        Line(process.ExitCode == 0 ? "بازیابی پایان یافت. نتیجهٔ Sync و بکاپ را در گزارش بالا بررسی کنید." : "بازیابی متوقف شد. پوشهٔ اصلی را حفظ کنید و گزارش را با مسئول فنی بررسی کنید.");
                    }
                }
                catch (Exception) { Line("فایل بازیابی قابل اجرا نیست. نسخهٔ کامل و تأییدشده را دوباره کپی کنید."); }
                finally { BeginInvoke((Action)delegate { busy = false; start.Enabled = true; source.Enabled = target.Enabled = password.Enabled = true; }); }
            }) { IsBackground = true }.Start();
        }
        private static string Extract()
        {
            var parent = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "AlShifaRecovery");
            var work = Path.Combine(parent, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(work);
            var acl = new DirectorySecurity(); acl.SetAccessRuleProtection(true, false);
            foreach (var sid in new[] { WellKnownSidType.BuiltinAdministratorsSid, WellKnownSidType.LocalSystemSid })
                acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid, null), FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            Directory.SetAccessControl(work, acl);
            var zip = Path.Combine(work, "package.zip");
            using (var input = File.OpenRead(Application.ExecutablePath))
            {
                if (input.Length < 48) throw new InvalidDataException();
                input.Seek(-48, SeekOrigin.End); var tail = new byte[48]; ReadExactly(input, tail);
                if (!tail.Skip(40).SequenceEqual(Encoding.ASCII.GetBytes("ALSHIFA1"))) throw new InvalidDataException();
                var length = BitConverter.ToInt64(tail, 0); if (length <= 0 || length > input.Length - 48) throw new InvalidDataException();
                input.Seek(-48 - length, SeekOrigin.End);
                using (var sha = SHA256.Create()) using (var output = File.Create(zip))
                {
                    var buffer = new byte[1024 * 1024]; var left = length;
                    while (left > 0) { var count = input.Read(buffer, 0, (int)Math.Min(buffer.Length, left)); if (count == 0) throw new InvalidDataException(); sha.TransformBlock(buffer, 0, count, null, 0); output.Write(buffer, 0, count); left -= count; }
                    sha.TransformFinalBlock(buffer, 0, 0); if (!sha.Hash.SequenceEqual(tail.Skip(8).Take(32))) throw new InvalidDataException();
                }
            }
            ZipFile.ExtractToDirectory(zip, work); File.Delete(zip); return work;
        }
        private static void ReadExactly(Stream input, byte[] buffer)
        {
            var offset = 0; while (offset < buffer.Length) { var n = input.Read(buffer, offset, buffer.Length - offset); if (n == 0) throw new EndOfStreamException(); offset += n; }
        }
    }
}
