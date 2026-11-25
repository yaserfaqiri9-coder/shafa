using System.Data.Common;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Claims;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AlShifa.Api.Features.Settings;

public sealed class DatabaseDownload(IConfiguration config, ILogger<DatabaseDownload> logger)
{
    private readonly SemaphoreSlim running = new(1, 1);

    public async Task Handle(HttpContext http)
    {
        http.Response.Headers.CacheControl = "no-store, private";
        http.Response.Headers.Pragma = "no-cache";
        http.Response.Headers.XContentTypeOptions = "nosniff";
        http.Response.Headers["Referrer-Policy"] = "no-referrer";
        try
        {
            var auth = await http.AuthenticateAsync();
            if (!auth.Succeeded || auth.Principal is null) { await Problem(http, 401, "unauthenticated", "ابتدا وارد شوید."); return; }
            http.User = auth.Principal;
            var permitted = await http.RequestServices.GetRequiredService<IAuthorizationService>()
                .AuthorizeAsync(http.User, null, "perm:settings.edit");
            if ((!http.User.IsInRole("Manager") && !http.User.IsInRole("SuperAdmin")) || !permitted.Succeeded)
            {
                await Problem(http, 403, "forbidden", "فقط مدیر دارای صلاحیت تنظیمات می‌تواند بکاپ دیتابیس دریافت کند."); return;
            }
            if (!config.GetValue<bool>("Mirror:Enabled")) { await Problem(http, 404, "not_found", "این بخش در نسخه آنلاین در دسترس است."); return; }
            if (http.Request.Path == "/api/settings/database-backup/status" && HttpMethods.IsGet(http.Request.Method))
            {
                await http.Response.WriteAsJsonAsync(new { available = File.Exists("/usr/bin/mysqldump"), databaseOnly = true }); return;
            }
            if (http.Request.Path != "/api/settings/database-backup/download" || !HttpMethods.IsPost(http.Request.Method))
            {
                await Problem(http, 404, "not_found", "مسیر درخواستی موجود نیست."); return;
            }
            try { await http.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(http); }
            catch (AntiforgeryValidationException) { await Problem(http, 400, "csrf_invalid", "صفحه را تازه کنید و دوباره تلاش کنید."); return; }
            if (!await running.WaitAsync(0, http.RequestAborted)) { await Problem(http, 409, "backup_running", "یک بکاپ در حال آماده‌سازی است؛ پس از تکمیل دوباره تلاش کنید."); return; }
            try { await Download(http); }
            finally { running.Release(); }
        }
        catch (OperationCanceledException) when (http.RequestAborted.IsCancellationRequested) { }
        catch (Exception error)
        {
            // Never log connection strings, credentials, SQL, dump output, or a user's records.
            var safeCode = error is InvalidOperationException && new[] { "Database configuration missing", "Database name invalid", "Port invalid", "Dump unavailable", "Dump incomplete" }.Contains(error.Message) ? error.Message : error.GetType().Name;
            logger.LogWarning("Manual database backup failed ({FailureType}, {FailureCode}) for user {UserId}", error.GetType().Name, safeCode, http.User.FindFirstValue(ClaimTypes.NameIdentifier));
            if (!http.Response.HasStarted) await Problem(http, 503, "backup_failed", "بکاپ آماده نشد. کمی بعد دوباره تلاش کنید یا به مسئول سیستم اطلاع دهید.");
            else http.Abort();
        }
    }

    private async Task Download(HttpContext http)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
        var connection = new DbConnectionStringBuilder { ConnectionString = config["DatabaseBackup:Connection"] ?? config.GetConnectionString("Default") ?? throw new InvalidOperationException("Database configuration missing") };
        string Value(params string[] names)
        {
            foreach (var name in names) if (connection.TryGetValue(name, out var value)) return Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "";
            return "";
        }
        var database = Value("Database", "Initial Catalog");
        if (!Regex.IsMatch(database, "^[A-Za-z0-9_]+$")) throw new InvalidOperationException("Database name invalid");
        var port = Value("Port");
        if (port == "") port = "3306";
        if (!int.TryParse(port, out var numericPort) || numericPort is < 1 or > 65535) throw new InvalidOperationException("Port invalid");
        static string Option(string value) => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n") + "\"";
        var root = config["DatabaseBackup:TempRoot"] ?? "/var/lib/alshifa/storage/manual-db-backups";
        Directory.CreateDirectory(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var id = Guid.NewGuid().ToString("N");
        var credentials = Path.Combine(root, id + ".cnf");
        var output = Path.Combine(root, id + ".sql.gz");
        static FileStream PrivateFile(string path) => new(path, new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, Options = FileOptions.Asynchronous, UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite });
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted);
        deadline.CancelAfter(TimeSpan.FromMinutes(10));
        var token = deadline.Token;
        try
        {
            await using (var credentialFile = PrivateFile(credentials))
            await using (var writer = new StreamWriter(credentialFile, new UTF8Encoding(false)))
            {
                await writer.WriteAsync($"[client]\nhost={Option(Value("Server", "Host", "Data Source"))}\nport={port}\nuser={Option(Value("User ID", "UserID", "Uid", "User", "Username"))}\npassword={Option(Value("Password", "Pwd"))}\nprotocol=tcp\n");
            }
            var info = new ProcessStartInfo("/usr/bin/mysqldump") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var argument in new[] { "--defaults-file=" + credentials, "--single-transaction", "--skip-lock-tables", "--no-tablespaces", "--quick", "--hex-blob", "--routines", "--events", "--default-character-set=utf8mb4", "--skip-comments", "--databases", database }) info.ArgumentList.Add(argument);
            using (var process = new Process { StartInfo = info })
            {
                if (!process.Start()) throw new InvalidOperationException("Dump unavailable");
                var errorTask = process.StandardError.ReadToEndAsync(token);
                try
                {
                    await using (var file = PrivateFile(output))
                    await using (var gzip = new GZipStream(file, CompressionLevel.Fastest))
                    {
                        await process.StandardOutput.BaseStream.CopyToAsync(gzip, token);
                    }
                    await process.WaitForExitAsync(token);
                    var diagnostic = await errorTask;
                    if (process.ExitCode != 0)
                        logger.LogWarning("Database backup process exited with code {ExitCode}; database error codes {ErrorCodes}", process.ExitCode, string.Join(',', Regex.Matches(diagnostic, @"\b(?:1044|1045|1142|1227|1356|2026)\b").Select(match => match.Value).Distinct()));
                    if (process.ExitCode != 0 || new FileInfo(output).Length < 30) throw new InvalidOperationException("Dump incomplete");
                }
                finally
                {
                    if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(CancellationToken.None); }
                }
            }
            File.Delete(credentials);
            var userId = http.User.FindFirstValue(ClaimTypes.NameIdentifier);
            logger.LogWarning("Online audit: DatabaseBackupPrepared Database by user {UserId}; bytes {Bytes}", userId, new FileInfo(output).Length);
            var now = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("Asia/Kabul"));
            http.Response.ContentType = "application/gzip";
            http.Response.ContentLength = new FileInfo(output).Length;
            http.Response.Headers.ContentDisposition = $"attachment; filename=\"alshifa-database-{now:yyyyMMdd-HHmmss}.sql.gz\"";
            await using var download = new FileStream(output, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous);
            await download.CopyToAsync(http.Response.Body, token);
            logger.LogWarning("Online audit: DatabaseBackupDownloaded Database by user {UserId}", userId);
        }
        finally
        {
            if (File.Exists(credentials)) File.Delete(credentials);
            if (File.Exists(output)) File.Delete(output);
        }
    }

    private static async Task Problem(HttpContext context, int status, string code, string title)
    {
        context.Response.StatusCode = status;
        await context.Response.WriteAsJsonAsync(new { code, title });
    }
}
