using System.Collections.Concurrent;
using System.Security.Claims;
using AlShifa.Api.Features.Auth;
using AlShifa.Api.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Dept = AlShifa.Api.Infrastructure.Groups;

namespace AlShifa.Api.Features.Realtime;

/// <summary>Live connections per user, so access can be cut the moment an account changes.</summary>
public sealed class ConnectionTracker
{
    private readonly ConcurrentDictionary<string, HubCallerContext> connections = new();

    public void Add(HubCallerContext context) => connections[context.ConnectionId] = context;

    public void Remove(string connectionId) => connections.TryRemove(connectionId, out _);

    public void Disconnect(long userId)
    {
        var id = userId.ToString();
        foreach (var context in connections.Values.Where(c => c.UserIdentifier == id))
        {
            context.Abort();
        }
    }
}

[Authorize]
public sealed class EventsHub(ConnectionTracker tracker) : Hub
{
    public const string Path = "/hubs/events";

    /// <summary>Groups come only from the server-side permissions; clients have no method to join one.</summary>
    private static readonly (string Group, string Permission)[] Membership =
    [
        (Dept.Reception, "visits.create"),
        (Dept.Triage, "preexam.edit"),
        (Dept.Doctor, "consultation.edit"),
        (Dept.Lab, "diagnostics.edit"),
        (Dept.Surgery, "surgery.edit"),
        (Dept.Pharmacy, "pharmacy.create"),
        (Dept.Optical, "optical.create"),
        (Dept.Cashier, "payments.create"),
        (Dept.Finance, "ledger.view"),
        (Dept.Inventory, "inventory.approve"),
        (Dept.Management, "reports.print"),
        (Dept.Audio, "calls.manage"),
    ];

    public override async Task OnConnectedAsync()
    {
        tracker.Add(Context);
        try
        {
            foreach (var (group, permission) in Membership)
            {
                if (Context.User!.HasClaim(RolePermissions.ClaimType, permission))
                {
                    await Groups.AddToGroupAsync(Context.ConnectionId, group);
                }
            }

            await base.OnConnectedAsync();
        }
        catch
        {
            tracker.Remove(Context.ConnectionId);
            throw;
        }
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        tracker.Remove(Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }
}

public sealed class UserIdFromNameIdentifier : IUserIdProvider
{
    public string? GetUserId(HubConnectionContext connection) => connection.User?.FindFirstValue(ClaimTypes.NameIdentifier);
}

/// <summary>Sends committed outbox rows to SignalR. A send failure leaves the row for the next pass.</summary>
public sealed class OutboxDispatcher(IServiceScopeFactory scopes, IHubContext<EventsHub> hub, ILogger<OutboxDispatcher> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan Retention = TimeSpan.FromDays(7);
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromHours(1);
    // Not at startup: a restart after a database failure must not immediately repeat the heaviest statement.
    private static readonly TimeSpan FirstCleanupDelay = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan CleanupPause = TimeSpan.FromMilliseconds(200);
    public const int CleanupBatch = 500;
    private const int MaxCleanupBatches = 200;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var nextCleanup = DateTime.UtcNow + FirstCleanupDelay;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                // Drain bursts without the old 100-events/second ceiling. Keep work and tracked rows
                // bounded so this background task still yields to interactive requests and shutdown.
                var dispatchTime = System.Diagnostics.Stopwatch.StartNew();
                for (var batch = 0; batch < 4; batch++)
                {
                    if (!await DispatchAsync(db, stoppingToken)) break;
                    db.ChangeTracker.Clear();
                    if (dispatchTime.Elapsed >= PollInterval) break;
                }

                if (DateTime.UtcNow >= nextCleanup)
                {
                    // Scheduled before running: a failing cleanup waits a full interval instead of repeating every second.
                    nextCleanup = DateTime.UtcNow + CleanupInterval;
                    await CleanupAsync(db, DateTime.UtcNow - Retention, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                // A database outage must not stop the loop; events stay queued until it recovers.
                logger.LogWarning(e, "Outbox dispatch failed");
            }

            try
            {
                await Task.Delay(PollInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task CleanupAsync(AppDbContext db, DateTime cutoff, CancellationToken ct)
    {
        try
        {
            var events = await PurgeOutboxAsync(db, cutoff, ct);
            var records = await PurgeIdempotencyRecordsAsync(db, cutoff, ct);
            if (events + records > 0)
            {
                logger.LogInformation("Retention cleanup removed {Events} outbox events and {Records} idempotency records older than {Cutoff:o}", events, records, cutoff);
            }
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            // Cleanup is housekeeping only; it must never take dispatch or the hospital down with it.
            logger.LogWarning(e, "Retention cleanup failed; it is retried in {Interval}", CleanupInterval);
        }
    }

    /// <summary>
    /// Removes expired replay records in small batches walked by primary key. A single DELETE ... WHERE CreatedAt &lt; x has no index to use:
    /// it scans and locks the whole table in one transaction, blocking every new save that needs an idempotency row, and leaves a large undo
    /// history for purge. Here each batch reads at most <see cref="CleanupBatch"/> rows and deletes them by key in its own short statement.
    /// Only rows older than the 7-day retention are removed, so a retried request inside that window still replays its stored response.
    /// </summary>
    public static async Task<int> PurgeIdempotencyRecordsAsync(AppDbContext db, DateTime cutoff, CancellationToken ct)
    {
        var deleted = 0;
        long after = 0;
        for (var round = 0; round < MaxCleanupBatches; round++)
        {
            var batch = await db.IdempotencyRecords.AsNoTracking().Where(r => r.Id > after).OrderBy(r => r.Id).Take(CleanupBatch)
                .Select(r => new { r.Id, r.CreatedAt }).ToListAsync(ct);
            // Ids grow with time; stopping at the first row inside retention never removes a live one.
            var expired = batch.TakeWhile(r => r.CreatedAt < cutoff).Select(r => r.Id).ToList();
            if (expired.Count > 0)
            {
                deleted += await db.IdempotencyRecords.Where(r => expired.Contains(r.Id)).ExecuteDeleteAsync(ct);
            }

            if (expired.Count < CleanupBatch) break;
            after = expired[^1];
            await Task.Delay(CleanupPause, ct);
        }

        return deleted;
    }

    /// <summary>Same batching as <see cref="PurgeIdempotencyRecordsAsync"/>. Undelivered events are never removed.</summary>
    public static async Task<int> PurgeOutboxAsync(AppDbContext db, DateTime cutoff, CancellationToken ct)
    {
        var deleted = 0;
        long after = 0;
        for (var round = 0; round < MaxCleanupBatches; round++)
        {
            var batch = await db.OutboxEvents.AsNoTracking().Where(e => e.Id > after).OrderBy(e => e.Id).Take(CleanupBatch)
                .Select(e => new { e.Id, e.CreatedAt, e.ProcessedAt }).ToListAsync(ct);
            var old = batch.TakeWhile(e => e.CreatedAt < cutoff).ToList();
            var expired = old.Where(e => e.ProcessedAt != null && e.ProcessedAt < cutoff).Select(e => e.Id).ToList();
            if (expired.Count > 0)
            {
                deleted += await db.OutboxEvents.Where(e => expired.Contains(e.Id)).ExecuteDeleteAsync(ct);
            }

            if (old.Count < CleanupBatch) break;
            after = old[^1].Id;
            await Task.Delay(CleanupPause, ct);
        }

        return deleted;
    }

    private async Task<bool> DispatchAsync(AppDbContext db, CancellationToken ct)
    {
        var batch = await db.OutboxEvents.Where(e => e.ProcessedAt == null).OrderBy(e => e.Id).Take(100).ToListAsync(ct);
        foreach (var e in batch)
        {
            var groups = e.Groups.Split(',', StringSplitOptions.RemoveEmptyEntries);
            await hub.Clients.Groups(groups).SendAsync("event", new { type = e.Type, payload = e.Payload }, ct);
            e.ProcessedAt = DateTime.UtcNow;
        }

        if (batch.Count > 0)
        {
            await db.SaveChangesAsync(ct);
        }

        return batch.Count == 100;
    }
}
