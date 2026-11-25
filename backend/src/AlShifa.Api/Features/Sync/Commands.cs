using System.ComponentModel.DataAnnotations;
using System.Data;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AlShifa.Api.Features.Users;
using AlShifa.Api.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AlShifa.Api.Features.Sync;

/// <summary>Online intent, and local execution receipt. Never replicated or pruned by a snapshot.</summary>
public sealed class SyncCommand
{
    [MaxLength(64)] public string Id { get; set; } = "";
    [MaxLength(64)] public string RequestHash { get; set; } = "";
    [MaxLength(64)] public string IdempotencyKey { get; set; } = "";
    [MaxLength(150)] public string Schema { get; set; } = "";
    public long UserId { get; set; }
    [MaxLength(10)] public string Method { get; set; } = "";
    [MaxLength(500)] public string Path { get; set; } = "";
    [MaxLength(2000)] public string Query { get; set; } = "";
    public string Body { get; set; } = "";
    public long BaseSeq { get; set; }
    public int StatusCode { get; set; }
    public string ResponseBody { get; set; } = "";
    [MaxLength(100)] public string ContentType { get; set; } = "application/json";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
}

public sealed record CommandResult(string Id, int StatusCode, string ResponseBody, string ContentType);

public static class CommandPolicy
{
    // Only database workflows. Files, credentials, host configuration and physical call/audio actions stay local.
    private static readonly HashSet<string> Roots = new(StringComparer.Ordinal)
    {
        "patients", "visits", "admissions", "appointments", "visit-fees", "reception",
        "finance", "invoices", "cashier", "cash-sessions", "inventory", "pharmacy", "sales",
        "clinical", "diagnostics", "surgeries", "surgery-packages", "refractions", "prescriptions",
        "consultations", "pre-examinations", "triage", "follow-ups", "optical", "directory",
        "services", "payments", "cash", "ledger", "expenses", "departments", "suppliers", "staff", "operations",
        "diagnostic-tests", "diagnostic-orders", "optical-prescriptions", "visit-tokens", "chat",
    };

    private const string QueueResetPath = "/api/settings/queue-numbers/reset";

    public static bool Allowed(string method, string path) =>
        method is "POST" or "PUT" or "PATCH" or "DELETE"
        && path.StartsWith("/api/", StringComparison.Ordinal)
        && !path.Contains('%') && !path.Contains('\\') && !path.Contains("..", StringComparison.Ordinal)
        && (Roots.Contains(path[5..].Split('/')[0]) || method == "POST" && path == QueueResetPath)
        && !path.Contains("/attachments", StringComparison.Ordinal);

    public static string Hash(string method, string path, string query, string body) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{method}\n{path}\n{query}\n{body}")));

    public static string Id(long user, string key) => Hash(user.ToString(System.Globalization.CultureInfo.InvariantCulture), key, "", "");

    /// <summary>These creates already enforce duplicates/balances/stock in their local workflow.
    /// Versioned edits retain their existing optimistic concurrency checks. Other commands need a fresh snapshot.</summary>
    public static bool ValidatesCurrentState(SyncCommand command)
    {
        var path = command.Path.TrimEnd('/');
        if (command.Method == "POST" && path is "/api/patients" or "/api/appointments" or "/api/admissions"
            or "/api/visits" or "/api/visit-tokens" or "/api/invoices" or "/api/payments"
            or "/api/inventory/purchases" or "/api/pharmacy/sales" or "/api/optical/orders"
            or "/api/cash/sessions" or "/api/expenses" or "/api/diagnostic-orders" or "/api/surgeries"
            or "/api/chat/messages" or "/api/chat/public/messages")
            return true;
        if (command.Method == "DELETE" && Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(command.Query)
            .TryGetValue("version", out var version) && int.TryParse(version, out var number) && number >= 0)
            return true;
        try
        {
            using var json = JsonDocument.Parse(command.Body);
            return json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.EnumerateObject()
                .Any(p => p.Name.Equals("version", StringComparison.OrdinalIgnoreCase)
                    && p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetInt32(out var v) && v >= 0);
        }
        catch (JsonException) { return false; }
    }
}

/// <summary>Only this store uses the online writer account, and only for the command queue.</summary>
public sealed class CommandStore(IOptions<MirrorOptions> options)
{
    public AppDbContext Open() => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseMySQL(options.Value.SyncConnection).Options);
}

public static class CommandEndpoints
{
    public static void MapCommands(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/sync/commands").RequireAuthorization();
        group.MapGet("/", async (HttpContext http, AppDbContext db, CancellationToken ct) =>
        {
            var user = long.Parse(http.User.FindFirstValue(ClaimTypes.NameIdentifier)!);
            return Results.Ok(await db.SyncCommands.AsNoTracking().Where(c => c.UserId == user)
                .OrderByDescending(c => c.CreatedAt).Take(50)
                .Select(c => new { c.Id, c.Path, c.CreatedAt, c.CompletedAt, c.StatusCode, c.ResponseBody }).ToListAsync(ct));
        });
        group.MapGet("/{id}", async (string id, HttpContext http, AppDbContext db, CancellationToken ct) =>
        {
            var user = long.Parse(http.User.FindFirstValue(ClaimTypes.NameIdentifier)!);
            var c = await db.SyncCommands.AsNoTracking().SingleOrDefaultAsync(c => c.Id == id && c.UserId == user, ct)
                ?? throw AppException.NotFound();
            return Results.Ok(new CommandResult(c.Id, c.StatusCode, c.ResponseBody, c.ContentType));
        });

        var signed = app.MapGroup("/api/sync/mirror/commands").AllowAnonymous().DisableAntiforgery();
        // POST: parameters and results are covered by the existing HMAC, including the body.
        signed.MapPost("/pending", async (HttpContext http, CommandStore store, IOptions<MirrorOptions> options, CancellationToken ct) =>
        {
            await SyncSignature.ReadVerifiedAsync(http, options.Value.SyncKey, 0, ct);
            await using var db = store.Open();
            return Results.Ok(await db.SyncCommands.AsNoTracking().Where(c => c.StatusCode == 0)
                .OrderBy(c => c.CreatedAt).ThenBy(c => c.Id).Take(25).ToListAsync(ct));
        });
        signed.MapPost("/result", async (HttpContext http, CommandStore store, IOptions<MirrorOptions> options, CancellationToken ct) =>
        {
            var bytes = await SyncSignature.ReadVerifiedAsync(http, options.Value.SyncKey, 4 * 1024 * 1024, ct);
            var result = JsonSerializer.Deserialize<CommandResult>(bytes, JsonSerializerOptions.Web)
                ?? throw AppException.NotFound();
            if (result.StatusCode is < 200 or > 499 || result.StatusCode == 202)
                throw new AppException(400, "invalid_result", "Invalid command result.");
            await using var db = store.Open();
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
            var c = await db.SyncCommands.FromSqlInterpolated($"SELECT * FROM SyncCommands WHERE Id = {result.Id} FOR UPDATE")
                .SingleOrDefaultAsync(ct) ?? throw AppException.NotFound();
            if (c.StatusCode != 0)
            {
                if (c.StatusCode != result.StatusCode || c.ResponseBody != result.ResponseBody)
                    throw new AppException(409, "result_mismatch", "Command already has a different result.");
                return Results.NoContent();
            }
            c.StatusCode = result.StatusCode;
            c.ResponseBody = result.ResponseBody;
            c.ContentType = result.ContentType;
            c.CompletedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return Results.NoContent();
        });
    }

    /// <summary>Runs after authentication, authorization and CSRF. Business endpoints never write online.</summary>
    public static IApplicationBuilder UseOnlineCommandQueue(this IApplicationBuilder app) => app.Use(async (http, next) =>
    {
        var r = http.Request;
        if (HttpMethods.IsGet(r.Method) || HttpMethods.IsHead(r.Method) || HttpMethods.IsOptions(r.Method)
            || r.Path.StartsWithSegments("/api/sync/mirror") || r.Path == "/api/auth/login" || r.Path == "/api/auth/logout"
            || r.Path.StartsWithSegments("/hubs"))
        {
            await next();
            return;
        }
        if (http.User.Identity?.IsAuthenticated != true) { http.Response.StatusCode = 401; return; }
        if (!CommandPolicy.Allowed(r.Method, r.Path.Value ?? "") || (r.ContentLength ?? 0) > 1024 * 1024
            || (r.ContentLength > 0 && r.ContentType?.StartsWith("application/json", StringComparison.OrdinalIgnoreCase) != true))
            throw new AppException(409, "local_operation_required", "این عملیات باید در سرور محلی انجام شود.");
        var key = r.Headers["Idempotency-Key"].ToString();
        if (key.Length is < 1 or > 64) throw new AppException(400, "idempotency_key_required", "شناسه درخواست ضروری است.");
        // Bounded read also covers chunked bodies.
        using var body = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await r.Body.ReadAsync(buffer, http.RequestAborted)) > 0)
        {
            if (body.Length + read > 1024 * 1024) throw new AppException(413, "too_large", "Request too large.");
            body.Write(buffer, 0, read);
        }
        var text = Encoding.UTF8.GetString(body.ToArray());
        var user = long.Parse(http.User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var id = CommandPolicy.Id(user, key);
        var hash = CommandPolicy.Hash(r.Method, r.Path, r.QueryString.Value ?? "", text);
        var store = http.RequestServices.GetRequiredService<CommandStore>();
        await using var db = store.Open();
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, http.RequestAborted);
        var existing = await db.SyncCommands.AsNoTracking().SingleOrDefaultAsync(c => c.Id == id, http.RequestAborted);
        if (existing is null)
        {
            var seq = await SyncWorker.GetStateAsync(db, "apply.seq", http.RequestAborted);
            if (!long.TryParse(seq, out var revision) || revision == 0)
                throw new AppException(409, "sync_not_ready", "نسخه آنلاین هنوز با سرور محلی همگام نشده است.");
            var schema = SyncSchema.SchemaVersion(db);
            var path = r.Path.Value ?? "";
            var query = r.QueryString.Value ?? "";
            // The unique key serializes simultaneous submissions without two shared-lock readers
            // trying to upgrade to insert locks. A duplicate never overwrites the original intent.
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO SyncCommands
                    (`Id`,`RequestHash`,`IdempotencyKey`,`Schema`,`UserId`,`Method`,`Path`,`Query`,`Body`,`BaseSeq`,`StatusCode`,`ResponseBody`,`ContentType`,`CreatedAt`)
                VALUES ({id},{hash},{key},{schema},{user},{r.Method},{path},{query},{text},{revision},0,'','application/json',{DateTime.UtcNow})
                ON DUPLICATE KEY UPDATE `Id`=`Id`
                """, http.RequestAborted);
            existing = await db.SyncCommands.AsNoTracking().SingleAsync(c => c.Id == id, http.RequestAborted);
        }
        if (existing.RequestHash != hash)
            throw new AppException(409, "idempotency_key_reused", "شناسه برای درخواست دیگری استفاده شده است.");
        await tx.CommitAsync(http.RequestAborted);
        if (existing is { StatusCode: > 0 })
        {
            // A form retry with the same key receives the locally confirmed original response.
            http.Response.StatusCode = existing.StatusCode;
            http.Response.ContentType = existing.ContentType;
            await http.Response.WriteAsync(existing.ResponseBody, http.RequestAborted);
            return;
        }
        http.Response.StatusCode = StatusCodes.Status202Accepted;
        http.Response.Headers.Location = "/api/sync/commands/" + id;
        await http.Response.WriteAsJsonAsync(new { commandId = id, pending = existing?.StatusCode is null or 0 }, http.RequestAborted);
    });
}

/// <summary>Re-enters the actual local routing/auth/validation pipeline with a freshly resolved local identity.
/// The business transaction, local receipt and replication outbox commit together.</summary>
public sealed class CommandExecutor(IServiceScopeFactory scopes, IHttpContextAccessor accessor)
{
    private sealed class BodyDetection(bool hasBody) : IHttpRequestBodyDetectionFeature
    {
        public bool CanHaveBody => hasBody;
    }
    internal static readonly object InternalRequest = new();
    public RequestDelegate? Pipeline { private get; set; }

    public async Task<CommandResult> ExecuteAsync(SyncCommand command, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        // Lock the receipt key and the outbox range. Concurrent local commits either precede validation or wait.
        var stored = await db.SyncCommands.FromSqlInterpolated($"SELECT * FROM SyncCommands WHERE Id = {command.Id} FOR UPDATE").SingleOrDefaultAsync(ct);
        if (stored is not null)
        {
            if (stored.RequestHash != command.RequestHash) throw new InvalidOperationException("Command identity reused.");
            return new(stored.Id, stored.StatusCode, stored.ResponseBody, stored.ContentType);
        }
        var validatesCurrent = CommandPolicy.ValidatesCurrentState(command);
        var pending = !validatesCurrent && (await db.SyncChanges.FromSqlRaw("SELECT * FROM SyncChanges FOR UPDATE").ToListAsync(ct)).Count != 0;
        var ack = await SyncWorker.GetStateAsync(db, "push.ackedSeq", ct);
        CommandResult Failure(int status, string code, string detail) => new(command.Id, status,
            JsonSerializer.Serialize(new { code, title = detail }), "application/problem+json");
        CommandResult result;
        if (!CommandPolicy.Allowed(command.Method, command.Path) || command.IdempotencyKey.Length is < 1 or > 64
            || command.Id != CommandPolicy.Id(command.UserId, command.IdempotencyKey)
            || command.RequestHash != CommandPolicy.Hash(command.Method, command.Path, command.Query, command.Body))
            result = Failure(400, "invalid_command", "Invalid command envelope.");
        else if (command.Schema != SyncSchema.SchemaVersion(db) || (!validatesCurrent &&
            (pending || ack != command.BaseSeq.ToString(System.Globalization.CultureInfo.InvariantCulture))))
            result = Failure(409, "sync_conflict", "اطلاعات محلی تغییر کرده است؛ پس از همگام‌سازی، اطلاعات تازه را بررسی و درخواست جدید ثبت کنید.");
        else
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
            var user = await users.FindByIdAsync(command.UserId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (user is null || !user.IsActive || await users.IsLockedOutAsync(user))
                result = Failure(403, "user_inactive", "دسترسی کاربر در سرور محلی تأیید نشد.");
            else
            {
                var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider, RequestAborted = ct };
                context.User = await scope.ServiceProvider.GetRequiredService<SignInManager<AppUser>>().CreateUserPrincipalAsync(user);
                context.Items[InternalRequest] = true;
                context.Request.Method = command.Method;
                context.Request.Path = command.Path;
                context.Request.QueryString = new QueryString(command.Query);
                context.Request.Scheme = "https";
                context.Request.Host = new HostString("localhost");
                var bytes = Encoding.UTF8.GetBytes(command.Body);
                context.Request.Body = new MemoryStream(bytes);
                context.Request.ContentLength = bytes.Length;
                context.Request.ContentType = "application/json";
                context.Features.Set<IHttpRequestBodyDetectionFeature>(new BodyDetection(bytes.Length != 0));
                context.Request.Headers["Idempotency-Key"] = command.IdempotencyKey;
                await using var response = new MemoryStream();
                context.Response.Body = response;
                var previous = accessor.HttpContext;
                db.CommandExecution = true;
                await tx.CreateSavepointAsync("command_action", ct);
                try
                {
                    accessor.HttpContext = context;
                    await (Pipeline ?? throw new InvalidOperationException("Command pipeline unavailable."))(context);
                    if (context.Response.StatusCode >= 500 || context.Response.StatusCode == 202)
                        throw new InvalidOperationException("Local operation did not finish; command will be retried.");
                    result = new(command.Id, context.Response.StatusCode, Encoding.UTF8.GetString(response.ToArray()),
                        context.Response.ContentType ?? "application/json");
                    if (result.StatusCode >= 400) await tx.RollbackToSavepointAsync("command_action", ct);
                }
                finally { accessor.HttpContext = previous; db.CommandExecution = false; }
                db.ChangeTracker.Clear();
            }
        }
        // Never accept a remote 'completed' flag: only this local transaction can create the receipt.
        command.StatusCode = result.StatusCode;
        command.ResponseBody = result.ResponseBody;
        command.ContentType = result.ContentType;
        command.CompletedAt = DateTime.UtcNow;
        db.SyncCommands.Add(command);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return result;
    }
}
