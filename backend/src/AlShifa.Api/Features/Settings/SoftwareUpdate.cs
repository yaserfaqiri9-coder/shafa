using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using AlShifa.Api.Features.Auth;
using AlShifa.Api.Infrastructure;

namespace AlShifa.Api.Features.Settings;

public sealed record SoftwareUpdateStatus(bool Configured, string State, string? CurrentVersion = null,
    string? AvailableVersion = null, DateTime? CheckedAt = null, DateTime? InstalledAt = null, string? LastError = null,
    string? AutoInstallWindow = null, string? FailedVersion = null, int? FailedAttempts = null,
    bool PackageVerified = false, string? RunningVersion = null);
public sealed record ApproveSoftwareUpdate(string Version);

public interface IUpdateTrigger { void Approve(string version); }
public sealed class EventLogUpdateTrigger(IConfiguration configuration) : IUpdateTrigger
{
    public void Approve(string version)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var path = configuration["Update:ApprovalFile"];
        if (string.IsNullOrWhiteSpace(path)) throw new InvalidOperationException("Update approval storage is not configured.");
        var temporary = path + "." + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(new { version, approvedAt = DateTime.UtcNow }, JsonSerializerOptions.Web));
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        EventLog.WriteEntry(EventLogBackupTrigger.Source, $"AlShifa update approval {version}", EventLogEntryType.Information, 4101);
    }
}

public static class SoftwareUpdate
{
    // A version waiting for the night window (or for a retry after a failure) may also be installed now by an administrator.
    static readonly HashSet<string> ApprovableStates = ["awaitingApproval", "scheduled", "retryScheduled", "failed"];

    public static string? RunningVersion()
    {
        try
        {
            using var marker = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "release.json")));
            return marker.RootElement.GetProperty("version").GetString();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException)
        {
            return null;
        }
    }

    public static SoftwareUpdateStatus Read(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return new(false, "unconfigured");
        try
        {
            return JsonSerializer.Deserialize<SoftwareUpdateStatus>(File.ReadAllText(path), JsonSerializerOptions.Web)
                ?? new(false, "unknown", LastError: "وضعیت آپدیت خوانده نشد.");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return new(false, "unknown", LastError: "وضعیت آپدیت خوانده نشد.");
        }
    }

    public static void MapSoftwareUpdate(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/settings/software-update", (IConfiguration config) => Results.Ok(Read(config["Update:StatusFile"]) with { RunningVersion = RunningVersion() }))
            .RequirePermission("settings.view");
        app.MapPost("/api/settings/software-update/approve", async (ApproveSoftwareUpdate request, IConfiguration config,
            IUpdateTrigger trigger, AuditService audit, AppDbContext db, CancellationToken ct) =>
        {
            if (config.GetValue("Mirror:Enabled", false))
                throw new AppException(409, "local_update_required", "نصب نسخهٔ شفاخانه باید از سیستم محلی درخواست شود.");
            var status = Read(config["Update:StatusFile"]);
            if (!Regex.IsMatch(request.Version ?? "", @"^\d+\.\d+\.\d+\.\d+$", RegexOptions.CultureInvariant)
                || !status.Configured || !ApprovableStates.Contains(status.State) || status.AvailableVersion != request.Version
                || !Version.TryParse(status.CurrentVersion, out var current) || !Version.TryParse(request.Version, out var available) || available <= current
                || (status.State == "failed" && !status.PackageVerified)
                || status.CheckedAt is null || DateTime.UtcNow - status.CheckedAt.Value.ToUniversalTime() > TimeSpan.FromMinutes(10)
                || status.CheckedAt.Value.ToUniversalTime() > DateTime.UtcNow.AddSeconds(30))
                throw new AppException(409, "update_not_ready", "نسخه آماده و تأییدشده‌ای برای نصب وجود ندارد.");
            // Persist the approval before emitting a host event that can stop this process.
            audit.Add("SoftwareUpdateApproved", "SoftwareUpdate", null, request.Version);
            await db.SaveChangesAsync(ct);
            try { trigger.Approve(request.Version!); }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                throw new AppException(503, "update_trigger_failed", "درخواست نصب به ویندوز نرسید؛ به مسئول سیستم اطلاع دهید.");
            }
            return Results.Accepted(value: new { version = request.Version, state = "requested" });
        }).RequirePermission("settings.edit");
    }
}
