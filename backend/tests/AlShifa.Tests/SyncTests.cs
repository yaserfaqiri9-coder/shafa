using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AlShifa.Api.Features.Auth;
using AlShifa.Api.Features.Finance;
using AlShifa.Api.Features.Patients;
using AlShifa.Api.Features.Settings;
using AlShifa.Api.Features.Sync;
using AlShifa.Api.Features.Users;
using AlShifa.Api.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MySql.Data.MySqlClient;

namespace AlShifa.Tests;

/// <summary>
/// A hospital server and an online copy, each with its own database. The online copy runs on a database account that may only read,
/// exactly as on the real server, so a write that slipped past the application would fail here too.
/// </summary>
public sealed class SyncFixture : IAsyncLifetime
{
    public const string Key = "test-sync-key-0123456789abcdef-0123456789";
    private const string ReadOnlyUser = "alshifa_mirror_ro";
    private readonly string readOnlyPassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

    public static string LocalDb => WithDatabase(ApiFixture.ConnectionString, "alshifa_synclocal_test");

    public static string MirrorDb => WithDatabase(ApiFixture.ConnectionString, "alshifa_mirror_test");

    public string MirrorReadOnlyDb => new MySqlConnectionStringBuilder(MirrorDb) { UserID = ReadOnlyUser, Password = readOnlyPassword }.ConnectionString;

    public WebApplicationFactory<Program> Mirror { get; private set; } = null!;

    public string PreviousSchema { get; private set; } = "";

    public WebApplicationFactory<Program> Local { get; private set; } = null!;

    /// <summary>Simulates the hospital's internet: off throws before anything is sent, LoseAnswer applies the batch online but loses the reply.</summary>
    public FlakyNetwork Network { get; } = new();

    public async Task InitializeAsync()
    {
        foreach (var connection in new[] { LocalDb, MirrorDb })
        {
            await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseMySQL(connection).Options);
            await db.Database.EnsureDeletedAsync();
        }

        // The deployment migrates the online database with an owner account; the application then reads through its own.
        await using (var owner = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseMySQL(MirrorDb).Options))
        {
            await owner.Database.MigrateAsync();
        }

        await RootSqlAsync($"""
            DROP USER IF EXISTS '{ReadOnlyUser}'@'localhost';
            CREATE USER '{ReadOnlyUser}'@'localhost' IDENTIFIED BY '{readOnlyPassword}';
            GRANT SELECT ON alshifa_mirror_test.* TO '{ReadOnlyUser}'@'localhost';
            GRANT UPDATE (AccessFailedCount, LockoutEnd, ConcurrencyStamp) ON alshifa_mirror_test.AspNetUsers TO '{ReadOnlyUser}'@'localhost';
            """);

        // As in production after deploying online ahead of the hospital: the previous hospital version is still accepted.
        await using (var schema = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseMySQL(MirrorDb).Options))
        {
            PreviousSchema = schema.Database.GetMigrations().SkipLast(1).Last();
        }

        Mirror = new App(MirrorReadOnlyDb, new()
        {
            ["Mirror:Enabled"] = "true",
            ["Mirror:SyncKey"] = Key,
            ["Mirror:SyncConnection"] = MirrorDb,
            ["Mirror:AcceptedSourceSchemas:0"] = PreviousSchema,
        });
        _ = Mirror.Server;

        Local = new App(LocalDb, new()
        {
            ["Sync:Url"] = "https://online.test",
            ["Sync:Key"] = Key,
            ["Sync:IntervalMinutes"] = "600",
            // Tests drive every run themselves. The hosted loop would otherwise start after 20 s and race a test's
            // own run for the same online command (the interval is clamped to 5 minutes, so 600 does not stop it).
            ["Sync:StartDelaySeconds"] = "86400",
        }, services => services.AddHttpClient(SyncWorker.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new FlakyHandler(Network, Mirror.Server.CreateHandler())));
        _ = Local.Server;
    }

    public async Task DisposeAsync()
    {
        await Local.DisposeAsync();
        await Mirror.DisposeAsync();
        await RootSqlAsync($"DROP USER IF EXISTS '{ReadOnlyUser}'@'localhost';");

        // Both databases exist only while the tests run.
        MySqlConnection.ClearAllPools();
        foreach (var connection in new[] { LocalDb, MirrorDb })
        {
            await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseMySQL(connection).Options);
            await db.Database.EnsureDeletedAsync();
        }
    }

    public Task SyncAsync() => Local.Services.GetRequiredService<SyncWorker>().RunOnceAsync(CancellationToken.None);

    public AppDbContext MirrorData() => new(new DbContextOptionsBuilder<AppDbContext>().UseMySQL(MirrorDb).Options);

    public async Task<HttpClient> LocalLoginAsync(string role)
    {
        var name = $"{role[..Math.Min(role.Length, 6)].ToLowerInvariant()}_{Guid.NewGuid():N}"[..20];
        await CreateUserAsync(Local, name, role);
        var client = Client(Local);
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(name, ApiFixture.TestPassword));
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        await ApiFixture.RefreshCsrfAsync(client);
        return client;
    }

    public static async Task<string> CreateUserAsync(WebApplicationFactory<Program> app, string name, string role)
    {
        using var scope = app.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var user = new AppUser { UserName = name, FullName = name };
        Assert.True((await users.CreateAsync(user, ApiFixture.TestPassword)).Succeeded);
        await users.AddToRolesAsync(user, [role]);
        return name;
    }

    public static HttpClient Client(WebApplicationFactory<Program> app)
    {
        var client = app.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), HandleCookies = true });
        ApiFixture.RefreshCsrfAsync(client).GetAwaiter().GetResult();
        return client;
    }

    /// <summary>The --online-access command, run as the operator would: on the online server, with the writing database account.</summary>
    public async Task<string> EnableOnlineAccessAsync(string userName, string password)
    {
        await using var operatorApp = new App(MirrorDb, new() { ["Mirror:Enabled"] = "true" });
        using var scope = operatorApp.Services.CreateScope();
        Environment.SetEnvironmentVariable("ALSHIFA_ONLINE_USER", userName);
        Environment.SetEnvironmentVariable("ALSHIFA_ONLINE_PASSWORD", password);
        try
        {
            await OnlineAccess.RunAsync(scope.ServiceProvider, revoke: false);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ALSHIFA_ONLINE_USER", null);
            Environment.SetEnvironmentVariable("ALSHIFA_ONLINE_PASSWORD", null);
        }

        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        return (await users.GetAuthenticatorKeyAsync((await users.FindByNameAsync(userName))!))!;
    }

    /// <summary>The hospital application on the same database with sync switched off, as after online.ps1 -Disable.</summary>
    public WebApplicationFactory<Program> LocalWithoutSync() => new App(LocalDb, []);

    private static string WithDatabase(string connection, string database) =>
        new MySqlConnectionStringBuilder(connection) { Database = database }.ConnectionString;

    private static async Task RootSqlAsync(string sql)
    {
        var isolatedAdmin = Environment.GetEnvironmentVariable("ALSHIFA_TEST_ADMIN_DB");
        if (!string.IsNullOrWhiteSpace(isolatedAdmin))
        {
            var isolated = new MySqlConnectionStringBuilder(isolatedAdmin) { Database = "" };
            await using var admin = new MySqlConnection(isolated.ConnectionString);
            await admin.OpenAsync();
            foreach (var statement in sql.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                await using (var command = new MySqlCommand(statement, admin)) { await command.ExecuteNonQueryAsync(); }
            return;
        }
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, ".tools", "dev-secrets.json")))
        {
            dir = dir.Parent;
        }

        using var secrets = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir!.FullName, ".tools", "dev-secrets.json")));
        var root = new MySqlConnectionStringBuilder(ApiFixture.ConnectionString) { UserID = "root", Password = secrets.RootElement.GetProperty("root").GetString(), Database = "" };
        await using var connection = new MySqlConnection(root.ConnectionString);
        await connection.OpenAsync();
        foreach (var statement in sql.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            await using var command = new MySqlCommand(statement, connection);
            await command.ExecuteNonQueryAsync();
        }
    }

    private sealed class App(string connection, Dictionary<string, string> settings, Action<IServiceCollection>? services = null) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("ConnectionStrings:Default", connection);
            builder.UseSetting("RateLimit:LoginPerMinute", "100000");
            builder.UseSetting("Storage:Path", Path.Combine(Path.GetTempPath(), "alshifa-sync-test-" + (settings.ContainsKey("Mirror:Enabled") ? "online" : "local")));
            foreach (var (key, value) in settings)
            {
                builder.UseSetting(key, value);
            }

            builder.ConfigureLogging(logging => { logging.ClearProviders(); logging.AddConsole(); logging.SetMinimumLevel(LogLevel.Warning); });
            builder.ConfigureTestServices(s =>
            {
                s.AddSingleton<IBackupTrigger>(new RecordingBackupTrigger());
                services?.Invoke(s);
            });
        }
    }
}

public sealed class FlakyNetwork
{
    public bool Offline { get; set; }

    public bool LoseAnswer { get; set; }

    public bool DropCommandResultOnce { get; set; }
    public bool LegacyProtocolState { get; set; }
}

public sealed class FlakyHandler(FlakyNetwork network, HttpMessageHandler inner) : DelegatingHandler(inner)
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (network.Offline)
        {
            throw new HttpRequestException("No route to host", new System.Net.Sockets.SocketException());
        }

        if (network.DropCommandResultOnce && request.RequestUri!.AbsolutePath.EndsWith("/commands/result"))
        {
            network.DropCommandResultOnce = false;
            throw new HttpRequestException("Result delivery interrupted after the local commit");
        }
        var response = await base.SendAsync(request, ct);
        if (network.LegacyProtocolState && request.RequestUri!.AbsolutePath.EndsWith("/mirror/state"))
        {
            var state = await response.Content.ReadFromJsonAsync<MirrorState>(cancellationToken: ct);
            response.Content.Dispose();
            response.Content = JsonContent.Create(state! with { ProtocolVersion = 1 });
        }
        if (network.LoseAnswer && request.RequestUri!.AbsolutePath.EndsWith("/mirror/apply"))
        {
            throw new HttpRequestException("Connection reset while reading the answer");
        }

        return response;
    }
}

public sealed class SyncTests(SyncFixture sync, Xunit.Abstractions.ITestOutputHelper output) : IClassFixture<SyncFixture>
{
    [Fact]
    public async Task Bounded_pending_check_keeps_the_empty_queue_locked_until_command_commit()
    {
        Assert.Equal("1", Environment.GetEnvironmentVariable("ALSHIFA_ISOLATED_QUEUE_TESTS"));
        await sync.SyncAsync();
        using var scope = sync.Local.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
        Assert.Empty(await db.SyncChanges.FromSqlRaw(CommandExecutor.PendingChangesSql).AsNoTracking().ToListAsync());
        await using var writer = new MySqlConnection(SyncFixture.LocalDb);
        await writer.OpenAsync();
        await using (var limit = new MySqlCommand("SET SESSION innodb_lock_wait_timeout=1", writer)) await limit.ExecuteNonQueryAsync();
        await using var insert = new MySqlCommand("INSERT INTO SyncChanges(TableName,KeyJson,CreatedAt) VALUES('Patients','[-999999]',UTC_TIMESTAMP(6))", writer);
        var blocked = await Assert.ThrowsAsync<MySqlException>(() => insert.ExecuteNonQueryAsync());
        Assert.Equal(1205, blocked.Number);
        await tx.RollbackAsync();
        Assert.Equal(1, await insert.ExecuteNonQueryAsync());
        // Remove only this synthetic sentinel; never reset a queue or its checkpoints.
        Assert.Equal(1, await db.SyncChanges.Where(s => s.TableName == "Patients" && s.KeyJson == "[-999999]").ExecuteDeleteAsync());
    }

    [Fact]
    public async Task Five_hundred_pending_rows_use_bounded_lookups_and_preserve_upserts_and_deletes()
    {
        Assert.Equal("1", Environment.GetEnvironmentVariable("ALSHIFA_ISOLATED_QUEUE_TESTS"));
        await sync.SyncAsync();
        using var scope = sync.Local.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var stamp = "perf-" + Guid.NewGuid().ToString("N")[..8];
        var patients = Enumerable.Range(0, 500).Select(i => new Patient
        {
            FileNumber = stamp + i, FirstName = "Synthetic", LastName = stamp, Gender = Gender.Male,
        }).ToArray();
        db.Patients.AddRange(patients);
        await db.SaveChangesAsync();

        // General logging is enabled only on the runner's positively verified, dedicated instance.
        // No extra application instrumentation or production logging is needed to count raw SQL.
        var connectionString = new MySqlConnectionStringBuilder(Environment.GetEnvironmentVariable("ALSHIFA_TEST_ADMIN_DB")) { Database = "mysql" }.ConnectionString;
        await using var admin = new MySqlConnection(connectionString);
        await admin.OpenAsync();
        async Task Execute(string text) { await using var command = new MySqlCommand(text, admin); await command.ExecuteNonQueryAsync(); }
        await Execute("SET GLOBAL log_output='TABLE'");
        await Execute("TRUNCATE TABLE mysql.general_log");
        await Execute("SET GLOBAL general_log=ON");
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        try { await sync.SyncAsync(); }
        finally { await Execute("SET GLOBAL general_log=OFF"); }
        elapsed.Stop();
        await using var count = new MySqlCommand("SELECT COUNT(*) FROM mysql.general_log WHERE command_type='Query' AND argument LIKE 'SELECT %FROM `Patients`%'", admin);
        var totalLookups = Convert.ToInt32(await count.ExecuteScalarAsync());
        count.CommandText += " AND argument NOT LIKE 'SELECT 1 FROM %'";
        var lookups = Convert.ToInt32(await count.ExecuteScalarAsync());
        output.WriteLine($"SYNC_PERFORMANCE rows=500 totalReaderQueries={totalLookups} localReaderQueries={lookups} elapsedMs={elapsed.ElapsedMilliseconds}");
        await using (var mirror = sync.MirrorData()) Assert.Equal(500, await mirror.Patients.CountAsync(p => p.LastName == stamp));
        Assert.Empty(await db.SyncChanges.AsNoTracking().ToListAsync());

        foreach (var patient in patients.Take(250)) patient.FirstName = "Changed";
        db.Patients.RemoveRange(patients.Skip(250));
        await db.SaveChangesAsync();
        await sync.SyncAsync();
        await using (var mirror = sync.MirrorData())
        {
            Assert.Equal(250, await mirror.Patients.CountAsync(p => p.LastName == stamp));
            Assert.Equal(250, await mirror.Patients.CountAsync(p => p.LastName == stamp && p.FirstName == "Changed"));
        }
        Assert.Empty(await db.SyncChanges.AsNoTracking().ToListAsync());
        Assert.InRange(lookups, 1, 10);
    }

    [Fact]
    public async Task Physical_cards_attendance_and_current_patient_survive_offline_and_lost_sync_reply()
    {
        await sync.SyncAsync();
        var day = KabulClock.Today.AddDays(40);
        const string entry = "v:987654321";
        using var scope = sync.Local.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.QueueCards.Add(new() { Day = day, Number = 100 });
        db.QueueAttendances.Add(new() { Day = day, Desk = AlShifa.Api.Features.Calls.CallDesk.Vision, EntryKey = entry, Present = false, UpdatedAt = DateTime.UtcNow });
        db.QueueDeskLanes.Add(new() { Day = day, Desk = AlShifa.Api.Features.Calls.CallDesk.Vision, LaneId = 0, EntryKey = entry });
        sync.Network.Offline = true;
        try {
            await db.SaveChangesAsync();
            await Assert.ThrowsAsync<HttpRequestException>(sync.SyncAsync);
            Assert.True(await db.QueueCards.AnyAsync(c => c.Day == day && c.Number == 100));
        } finally { sync.Network.Offline = false; }
        sync.Network.LoseAnswer = true;
        try { await Assert.ThrowsAsync<HttpRequestException>(sync.SyncAsync); }
        finally { sync.Network.LoseAnswer = false; }
        await sync.SyncAsync();
        await using (var mirror = sync.MirrorData()) {
            Assert.Single(await mirror.QueueCards.Where(c => c.Day == day && c.Number == 100).ToListAsync());
            Assert.False((await mirror.QueueAttendances.SingleAsync(a => a.Day == day && a.EntryKey == entry)).Present);
            Assert.Equal(entry, (await mirror.QueueDeskLanes.SingleAsync(l => l.Day == day)).EntryKey);
        }
        (await db.QueueAttendances.SingleAsync(a => a.Day == day && a.EntryKey == entry)).Present = true;
        (await db.QueueDeskLanes.SingleAsync(l => l.Day == day)).EntryKey = null;
        await db.SaveChangesAsync();
        await sync.SyncAsync();
        await using var verified = sync.MirrorData();
        Assert.True((await verified.QueueAttendances.SingleAsync(a => a.Day == day && a.EntryKey == entry)).Present);
        Assert.Null((await verified.QueueDeskLanes.SingleAsync(l => l.Day == day)).EntryKey);
    }

    [Fact]
    public async Task A_legacy_online_service_cannot_bypass_source_ownership_guards()
    {
        sync.Network.LegacyProtocolState = true;
        try
        {
            var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => sync.SyncAsync());
            Assert.Contains("sync_protocol_mismatch", refused.Message);
        }
        finally { sync.Network.LegacyProtocolState = false; }
        await sync.SyncAsync();
    }

    private async Task<(HttpClient Online, HttpClient Local)> OnlineUserAsync(string role)
    {
        var name = "cmd_" + Guid.NewGuid().ToString("N")[..16];
        await SyncFixture.CreateUserAsync(sync.Local, name, role);
        var local = SyncFixture.Client(sync.Local);
        (await local.PostAsJsonAsync("/api/auth/login", new LoginRequest(name, ApiFixture.TestPassword))).EnsureSuccessStatusCode();
        await ApiFixture.RefreshCsrfAsync(local);
        await sync.SyncAsync();
        const string password = "Online-Commands-2026";
        var secret = await sync.EnableOnlineAccessAsync(name, password);
        var online = SyncFixture.Client(sync.Mirror);
        (await online.PostAsJsonAsync("/api/auth/login", new LoginRequest(name, password, Totp(secret)))).EnsureSuccessStatusCode();
        await ApiFixture.RefreshCsrfAsync(online);
        return (online, local);
    }

    [Fact]
    public async Task Online_payment_result_can_be_lost_without_posting_money_twice()
    {
        var desk = await sync.LocalLoginAsync(Roles.Receptionist);
        var patient = await CreatePatientAsync(desk, "payment-" + Guid.NewGuid().ToString("N"));
        var admin = await sync.LocalLoginAsync(Roles.SuperAdmin);
        var serviceResponse = await admin.PostAsJsonAsync("/api/services", new SaveServiceRequest("sync-" + Guid.NewGuid().ToString("N"), null, true, 100));
        serviceResponse.EnsureSuccessStatusCode();
        var service = JsonDocument.Parse(await serviceResponse.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetInt32();
        var (online, cashier) = await OnlineUserAsync(Roles.Cashier);
        int registerId;
        using (var scope = sync.Local.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var register = new AlShifa.Api.Features.Finance.CashRegister { Name = Guid.NewGuid().ToString("N") };
            db.CashRegisters.Add(register);
            await db.SaveChangesAsync();
            registerId = register.Id;
        }
        (await cashier.PostAsJsonAsync("/api/cash/sessions", new OpenSessionRequest(registerId, 0))).EnsureSuccessStatusCode();
        var issued = await cashier.SendAsync(ApiFixture.Keyed("/api/invoices", new CreateInvoiceRequest(patient.Id, null, [new InvoiceLineInput(service, 1)])));
        issued.EnsureSuccessStatusCode();
        var invoiceId = JsonDocument.Parse(await issued.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetInt64();
        await sync.SyncAsync();
        var key = Guid.NewGuid().ToString("N");
        HttpRequestMessage Pay() {
            var m = ApiFixture.Keyed("/api/payments", new PaymentRequest(invoiceId, 60, AlShifa.Api.Features.Finance.PaymentMethod.Cash, null));
            m.Headers.Remove("Idempotency-Key");
            m.Headers.Add("Idempotency-Key", key);
            return m;
        }
        var accepted = await online.SendAsync(Pay());
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        var location = accepted.Headers.Location!;
        await CreatePatientAsync(desk, "unrelated-" + Guid.NewGuid().ToString("N"));
        sync.Network.DropCommandResultOnce = true;
        await Assert.ThrowsAsync<HttpRequestException>(sync.SyncAsync);
        Assert.Equal(0, (await online.GetFromJsonAsync<CommandResult>(location))!.StatusCode);
        await sync.SyncAsync();
        Assert.Equal(201, (await online.GetFromJsonAsync<CommandResult>(location))!.StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await online.SendAsync(Pay())).StatusCode);
        await sync.SyncAsync();
        using var verified = sync.Local.Services.CreateScope();
        var localDb = verified.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(1, await localDb.Payments.CountAsync(p => p.InvoiceId == invoiceId));
        Assert.Equal(60, (await localDb.Invoices.AsNoTracking().SingleAsync(i => i.Id == invoiceId)).Paid);
        Assert.True((await AlShifa.Api.Features.Finance.FinanceEndpoints.ReconcileAsync(localDb, default)).Balanced);
    }

    [Fact]
    public async Task Changed_local_data_and_invalid_versions_refuse_online_edits_without_partial_changes()
    {
        var (online, desk) = await OnlineUserAsync(Roles.Receptionist);
        var patient = await CreatePatientAsync(desk, "conflict-" + Guid.NewGuid().ToString("N"));
        await sync.SyncAsync();
        async Task<Uri> QueueEdit(int version) {
            var m = new HttpRequestMessage(HttpMethod.Put, $"/api/patients/{patient.Id}") {
                Content = JsonContent.Create(new SavePatientRequest("online-edited", null, null, Gender.Male, null, 30, null, null, Version: version)) };
            m.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
            var response = await online.SendAsync(m);
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            return response.Headers.Location!;
        }
        var invalid = await QueueEdit(patient.Version + 10);
        await sync.SyncAsync();
        Assert.Equal(409, (await online.GetFromJsonAsync<CommandResult>(invalid))!.StatusCode);
        using (var check = sync.Local.Services.CreateScope())
        {
            var db = check.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Equal(patient.FirstName, (await db.Patients.AsNoTracking().SingleAsync(p => p.Id == patient.Id)).FirstName);
            Assert.False(await db.PatientChangeLogs.AnyAsync(c => c.PatientId == patient.Id));
        }
        // New rows start at Version=0; this is a real concurrency token too.
        var valid = await QueueEdit(patient.Version);
        await sync.SyncAsync();
        Assert.Equal(200, (await online.GetFromJsonAsync<CommandResult>(valid))!.StatusCode);
        patient = (await desk.GetFromJsonAsync<PatientDto>($"/api/patients/{patient.Id}"))!;
        Assert.Equal("online-edited", patient.FirstName);
        var stale = await QueueEdit(patient.Version);
        var changed = await desk.PutAsJsonAsync($"/api/patients/{patient.Id}", new SavePatientRequest("local-edited", null, null, Gender.Male, null, 30, null, null, Version: patient.Version));
        changed.EnsureSuccessStatusCode();
        await sync.SyncAsync();
        var refused = await online.GetFromJsonAsync<CommandResult>(stale);
        Assert.Equal(409, refused!.StatusCode);
        Assert.Contains("concurrency_conflict", refused.ResponseBody);
        await using var mirror = sync.MirrorData();
        Assert.Equal("local-edited", (await mirror.Patients.AsNoTracking().SingleAsync(p => p.Id == patient.Id)).FirstName);
    }

    [Fact]
    public async Task Patients_and_users_reach_the_online_copy_without_passwords()
    {
        var desk = await sync.LocalLoginAsync(Roles.Receptionist);
        var patient = await CreatePatientAsync(desk, "سینک");

        await sync.SyncAsync();

        await using var online = sync.MirrorData();
        var copied = await online.Patients.AsNoTracking().SingleAsync(p => p.Id == patient.Id);
        Assert.Equal(patient.FileNumber, copied.FileNumber);
        Assert.Equal("سینک", copied.FirstName);

        // Passwords and sign-in state never leave the hospital.
        Assert.True(await online.Users.AnyAsync());
        var deskIdentity = (await desk.GetFromJsonAsync<CurrentUserDto>("/api/auth/me"))!;
        Assert.Null((await online.Users.AsNoTracking().SingleAsync(u => u.Id == deskIdentity.Id)).PasswordHash);
        Assert.False(await online.Set<IdentityUserToken<long>>().AnyAsync(t => t.UserId == deskIdentity.Id));

        // Edits and deletions follow.
        var edited = await desk.PutAsJsonAsync($"/api/patients/{patient.Id}", new SavePatientRequest("سینک-۲", null, null, Gender.Female, null, 40, null, null, Version: patient.Version));
        Assert.True(edited.IsSuccessStatusCode, await edited.Content.ReadAsStringAsync());

        long departmentId;
        using (var scope = sync.Local.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var department = new AlShifa.Api.Features.Users.Department { Code = "SY" + Random.Shared.Next(1000, 9999), Name = "Sync test" };
            db.Departments.Add(department);
            await db.SaveChangesAsync();
            departmentId = department.Id;
        }

        await sync.SyncAsync();
        Assert.True(await online.Departments.AnyAsync(d => d.Id == departmentId));
        Assert.Equal("سینک-۲", (await online.Patients.AsNoTracking().SingleAsync(p => p.Id == patient.Id)).FirstName);

        using (var scope = sync.Local.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Departments.Remove(await db.Departments.SingleAsync(d => d.Id == departmentId));
            await db.SaveChangesAsync();
        }

        await sync.SyncAsync();
        Assert.False(await online.Departments.AnyAsync(d => d.Id == departmentId));
        await AssertNothingPendingAsync();
    }

    [Fact]
    public async Task Work_continues_offline_and_nothing_is_lost_or_doubled()
    {
        var desk = await sync.LocalLoginAsync(Roles.Receptionist);
        await sync.SyncAsync();

        // No internet: the hospital keeps working and the change waits.
        sync.Network.Offline = true;
        PatientDto patient;
        try
        {
            patient = await CreatePatientAsync(desk, "آفلاین");
            await Assert.ThrowsAsync<HttpRequestException>(sync.SyncAsync);

            var admin = await sync.LocalLoginAsync(Roles.SuperAdmin);
            var status = await admin.GetFromJsonAsync<SyncStatusDto>("/api/sync/status");
            Assert.True(status!.Pending > 0);
            Assert.False(status.Connected);
            Assert.NotNull(status.LastError);
        }
        finally
        {
            sync.Network.Offline = false;
        }

        // The batch arrives online but the reply is lost, so the hospital sends it again.
        sync.Network.LoseAnswer = true;
        try
        {
            await Assert.ThrowsAsync<HttpRequestException>(sync.SyncAsync);
        }
        finally
        {
            sync.Network.LoseAnswer = false;
        }

        await sync.SyncAsync();

        await using var online = sync.MirrorData();
        Assert.Equal(1, await online.Patients.CountAsync(p => p.FileNumber == patient.FileNumber));
        await AssertNothingPendingAsync();
    }

    [Fact]
    public async Task Online_commands_require_two_factor_and_local_confirmation()
    {
        var director = await SyncFixture.CreateUserAsync(sync.Local, $"dir_{Guid.NewGuid():N}"[..18], Roles.SuperAdmin);
        var desk = await sync.LocalLoginAsync(Roles.Receptionist);
        var patient = await CreatePatientAsync(desk, "مدیر");
        await sync.SyncAsync();

        // A synced account has no password online until the operator enables it.
        var anonymous = SyncFixture.Client(sync.Mirror);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync("/api/auth/login", new LoginRequest(director, ApiFixture.TestPassword))).StatusCode);

        const string onlinePassword = "Online-Password-2026";
        var key = await sync.EnableOnlineAccessAsync(director, onlinePassword);

        var withoutCode = await anonymous.PostAsJsonAsync("/api/auth/login", new LoginRequest(director, onlinePassword));
        Assert.Equal(HttpStatusCode.Unauthorized, withoutCode.StatusCode);
        Assert.Contains("two_factor_required", await withoutCode.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync("/api/auth/login", new LoginRequest(director, onlinePassword, "000000"))).StatusCode);

        var client = SyncFixture.Client(sync.Mirror);
        var login = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(director, onlinePassword, Totp(key)));
        Assert.True(login.IsSuccessStatusCode, await login.Content.ReadAsStringAsync());
        var me = await login.Content.ReadFromJsonAsync<CurrentUserDto>();
        Assert.False(me!.ReadOnly);
        Assert.True(me.Online);
        Assert.NotEmpty(me.Permissions);
        Assert.Contains("patients.create", me.Permissions);
        await ApiFixture.RefreshCsrfAsync(client);

        // Everything can be read.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/patients/{patient.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/reports/dashboard")).StatusCode);

        // Management, not a doctor itself, reads every doctor's lists and any patient's history online.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/clinical/worklist?kind=doctor")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/clinical/worklist?kind=doctor&done=true")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/clinical/patients/{patient.Id}/history")).StatusCode);
        // At the hospital the care-relationship rule is unchanged.
        var localDirector = SyncFixture.Client(sync.Local);
        Assert.True((await localDirector.PostAsJsonAsync("/api/auth/login", new LoginRequest(director, ApiFixture.TestPassword))).IsSuccessStatusCode);
        await ApiFixture.RefreshCsrfAsync(localDirector);
        Assert.Equal(HttpStatusCode.Forbidden, (await localDirector.GetAsync($"/api/clinical/patients/{patient.Id}/history")).StatusCode);
        var status = await client.GetFromJsonAsync<SyncStatusDto>("/api/sync/status");
        Assert.Equal("mirror", status!.Mode);
        Assert.NotNull(status.LastSuccessAt);

        // Intent is durable online and must not change either patient's record before local execution.
        var submissionKey = Guid.NewGuid().ToString("N");
        var request = new SavePatientRequest("online-" + Guid.NewGuid().ToString("N"), null, null, Gender.Male, null, 30, null, null);
        HttpRequestMessage Submit(SavePatientRequest body) {
            var message = new HttpRequestMessage(HttpMethod.Post, "/api/patients") { Content = JsonContent.Create(body) };
            message.Headers.Add("Idempotency-Key", submissionKey);
            return message;
        }
        var simultaneous = await Task.WhenAll(client.SendAsync(Submit(request)), client.SendAsync(Submit(request)));
        Assert.All(simultaneous, r => Assert.Equal(HttpStatusCode.Accepted, r.StatusCode));
        var create = simultaneous[0];
        var location = create.Headers.Location!;
        Assert.Equal(HttpStatusCode.Accepted, (await client.SendAsync(Submit(request))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.SendAsync(Submit(request with { FirstName = "different" }))).StatusCode);
        await using (var before = sync.MirrorData())
        {
            Assert.False(await before.Patients.AnyAsync(p => p.FirstName == request.FirstName));
            Assert.Equal(1, await before.SyncCommands.CountAsync(c => c.Body.Contains(request.FirstName)));
        }
        var queued = await client.GetFromJsonAsync<CommandResult>(location);
        Assert.Equal(0, queued!.StatusCode);
        await sync.SyncAsync();
        var completed = await client.GetFromJsonAsync<CommandResult>(location);
        Assert.Equal(201, completed!.StatusCode);
        using (var localScope = sync.Local.Services.CreateScope())
        {
            var db = localScope.ServiceProvider.GetRequiredService<AppDbContext>();
            var receipt = await db.SyncCommands.AsNoTracking().SingleAsync(c => c.Id == completed.Id);
            var replay = await sync.Local.Services.GetRequiredService<CommandExecutor>().ExecuteAsync(receipt, CancellationToken.None);
            Assert.Equal(completed.ResponseBody, replay.ResponseBody);
            Assert.Equal(1, await db.Patients.CountAsync(p => p.FirstName == request.FirstName));
        }
        await sync.SyncAsync();
        await using (var after = sync.MirrorData())
            Assert.Equal(1, await after.Patients.CountAsync(p => p.FirstName == request.FirstName));
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/auth/change-password", new ChangePasswordRequest(onlinePassword, "Another-Password-1"))).StatusCode);

        // Even past the application, the online database account cannot write.
        await using var readOnly = new MySqlConnection(sync.MirrorReadOnlyDb);
        await readOnly.OpenAsync();
        await using var write = new MySqlCommand($"UPDATE Patients SET FirstName='x' WHERE Id={patient.Id}", readOnly);
        await Assert.ThrowsAsync<MySqlException>(() => write.ExecuteNonQueryAsync());

        // The receptionist was synced too, but was never enabled online.
        var deskName = (await sync.MirrorData().Users.AsNoTracking().Where(u => u.UserName!.StartsWith("recept")).Select(u => u.UserName).FirstAsync())!;
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync("/api/auth/login", new LoginRequest(deskName, ApiFixture.TestPassword))).StatusCode);
    }

    [Fact]
    public async Task Online_copy_accepts_only_signed_batches()
    {
        var client = SyncFixture.Client(sync.Mirror);
        var body = Encoding.UTF8.GetBytes("""{"schema":"x","seq":1,"sentAt":"2026-01-01T00:00:00Z","tables":[]}""");

        var unsigned = await client.PostAsync("/api/sync/mirror/apply", new ByteArrayContent(body));
        Assert.Equal(HttpStatusCode.Unauthorized, unsigned.StatusCode);

        var time = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var forged = new HttpRequestMessage(HttpMethod.Post, "/api/sync/mirror/apply") { Content = new ByteArrayContent(body) };
        forged.Headers.Add(SyncSignature.TimeHeader, time);
        forged.Headers.Add(SyncSignature.SignatureHeader, SyncSignature.Sign("another-key-0123456789abcdef-0123456789", time, "POST", "/api/sync/mirror/apply", body));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(forged)).StatusCode);

        var old = DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeSeconds().ToString();
        var replayed = new HttpRequestMessage(HttpMethod.Post, "/api/sync/mirror/apply") { Content = new ByteArrayContent(body) };
        replayed.Headers.Add(SyncSignature.TimeHeader, old);
        replayed.Headers.Add(SyncSignature.SignatureHeader, SyncSignature.Sign(SyncFixture.Key, old, "POST", "/api/sync/mirror/apply", body));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(replayed)).StatusCode);

        // A correctly signed batch from another version is refused before anything is written.
        var other = new HttpRequestMessage(HttpMethod.Post, "/api/sync/mirror/apply") { Content = new ByteArrayContent(body) };
        other.Headers.Add(SyncSignature.TimeHeader, time);
        other.Headers.Add(SyncSignature.SignatureHeader, SyncSignature.Sign(SyncFixture.Key, time, "POST", "/api/sync/mirror/apply", body));
        Assert.Equal(HttpStatusCode.Conflict, (await client.SendAsync(other)).StatusCode);

        // The hospital server has no such endpoint at all.
        Assert.Equal(HttpStatusCode.NotFound, (await SyncFixture.Client(sync.Local).PostAsync("/api/sync/mirror/apply", new ByteArrayContent(body))).StatusCode);
    }

    [Fact]
    public async Task Online_copy_one_version_ahead_keeps_accepting_the_previous_hospital_version()
    {
        await sync.SyncAsync();
        await using var online = sync.MirrorData();
        var own = SyncSchema.SchemaVersion(online);
        var sequence = await online.SyncStates.AsNoTracking().Where(s => s.Key == "apply.seq").Select(s => s.Value).SingleAsync();
        var source = sync.Local.Services.GetRequiredService<InstallationIdentity>().Id.ToString();
        var client = SyncFixture.Client(sync.Mirror);
        async Task<HttpResponseMessage> Signed(HttpMethod method, string path, byte[] body)
        {
            var time = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
            var request = new HttpRequestMessage(method, path);
            if (body.Length > 0) request.Content = new ByteArrayContent(body);
            request.Headers.Add(SyncSignature.TimeHeader, time);
            request.Headers.Add(SyncSignature.SignatureHeader, SyncSignature.Sign(SyncFixture.Key, time, method.Method, path, body));
            return await client.SendAsync(request);
        }
        byte[] Batch(string schema, long seq) => JsonSerializer.SerializeToUtf8Bytes(new SyncBatch(schema, seq, DateTime.UtcNow, [], SourceId: source), JsonSerializerOptions.Web);

        try
        {
            // The hospital still on the previous version sends its batch; it is applied, and the online copy then reports
            // that version back, which is what the previous version compares with its own.
            Assert.Equal(HttpStatusCode.OK, (await Signed(HttpMethod.Post, "/api/sync/mirror/apply", Batch(sync.PreviousSchema, long.Parse(sequence) + 1))).StatusCode);
            var state = await (await Signed(HttpMethod.Get, "/api/sync/mirror/state", [])).Content.ReadFromJsonAsync<MirrorState>();
            Assert.Equal(sync.PreviousSchema, state!.Schema);
            Assert.Contains(own, state.AcceptedSchemas!);

            // Any other version is still refused before anything is written.
            Assert.Equal(HttpStatusCode.Conflict, (await Signed(HttpMethod.Post, "/api/sync/mirror/apply", Batch("20200101000000_Unknown", long.Parse(sequence) + 2))).StatusCode);
        }
        finally
        {
            await online.Database.ExecuteSqlInterpolatedAsync($"UPDATE SyncStates SET `Value`={sequence} WHERE `Key`='apply.seq'");
        }

        // The upgraded hospital reads AcceptedSchemas, continues, and the online copy reports its own version again.
        await sync.SyncAsync();
        var after = await (await Signed(HttpMethod.Get, "/api/sync/mirror/state", [])).Content.ReadFromJsonAsync<MirrorState>();
        Assert.Equal(own, after!.Schema);
    }

    [Fact]
    public async Task Divergent_history_stops_without_erasing_either_database()
    {
        var desk = await sync.LocalLoginAsync(Roles.Receptionist);
        var patient = await CreatePatientAsync(desk, "بازسازی");
        await sync.SyncAsync();

        await using var online = sync.MirrorData();
        var accounts = await online.Users.CountAsync();
        var previousSequence = await online.SyncStates.AsNoTracking().Where(s => s.Key == "apply.seq").Select(s => s.Value).SingleAsync();

        // Rows the hospital never sent (a test run against the online copy, or a hospital database restored from a backup)
        // and a batch number this hospital never used.
        await online.Database.ExecuteSqlRawAsync("INSERT INTO Departments (Code, Name, IsActive, Version) VALUES ('ZZTEST', 'Test row', 1, 1)");
        await online.Database.ExecuteSqlRawAsync("UPDATE SyncStates SET `Value`='99999999999999' WHERE `Key`='apply.seq'");
        try
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => sync.SyncAsync());
            Assert.Contains("sync_reconciliation_required", error.Message);
            Assert.True(await online.Departments.AnyAsync(d => d.Code == "ZZTEST"));
            Assert.True(await online.Patients.AnyAsync(p => p.Id == patient.Id));

            Assert.Equal(accounts, await online.Users.CountAsync());
            await online.Database.ExecuteSqlRawAsync("UPDATE SyncStates SET `Value`='1' WHERE `Key`='apply.seq'");
            await Assert.ThrowsAsync<InvalidOperationException>(() => sync.SyncAsync());
            Assert.True(await online.Patients.AnyAsync(p => p.Id == patient.Id));
        }
        finally
        {
            await online.Database.ExecuteSqlInterpolatedAsync($"UPDATE SyncStates SET `Value`={previousSequence} WHERE `Key`='apply.seq'");
            await online.Database.ExecuteSqlRawAsync("DELETE FROM Departments WHERE Code='ZZTEST'");
        }
        await sync.SyncAsync();
    }

    [Fact]
    public async Task Another_installation_and_reset_cannot_overwrite_the_bound_mirror()
    {
        await sync.SyncAsync();
        await using var db = sync.MirrorData();
        var schema = SyncSchema.SchemaVersion(db);
        var before = await db.Patients.CountAsync();
        var users = await db.Users.CountAsync();
        var other = new SyncBatch(schema, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 10000, DateTime.UtcNow, [], SourceId: Guid.NewGuid().ToString());
        var conflict = await Assert.ThrowsAsync<AppException>(() => MirrorApply.ApplyAsync(db, SyncFixture.MirrorDb, other, CancellationToken.None));
        Assert.Contains("authoritative source", conflict.Message);
        await Assert.ThrowsAsync<AppException>(() => MirrorApply.ApplyAsync(db, SyncFixture.MirrorDb, other with { Reset = true }, CancellationToken.None));
        Assert.Equal(before, await db.Patients.CountAsync());
        Assert.Equal(users, await db.Users.CountAsync());
    }

    [Fact]
    public async Task Sync_checkbox_retains_offline_work_and_restricts_changes_to_settings_editors()
    {
        var admin = await sync.LocalLoginAsync(Roles.SuperAdmin);
        var desk = await sync.LocalLoginAsync(Roles.Receptionist);
        Assert.Equal(HttpStatusCode.Forbidden, (await desk.PutAsJsonAsync("/api/sync/enabled", new { enabled = false })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync("/api/sync/enabled", new { enabled = false })).StatusCode);
        try
        {
            var patient = await CreatePatientAsync(desk, "Offline checkbox test");
            await sync.SyncAsync();
            await using var online = sync.MirrorData();
            Assert.False(await online.Patients.AnyAsync(p => p.Id == patient.Id));
            Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsync("/api/sync/run", null)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync("/api/sync/enabled", new { enabled = true })).StatusCode);
            await sync.SyncAsync();
            Assert.True(await online.Patients.AnyAsync(p => p.Id == patient.Id));
        }
        finally { await admin.PutAsJsonAsync("/api/sync/enabled", new { enabled = true }); }
    }

    [Fact]
    public async Task Changes_made_while_sync_was_off_are_sent_when_it_is_on_again()
    {
        await sync.SyncAsync();

        long departmentId;
        await using (var off = sync.LocalWithoutSync())
        {
            using var scope = off.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            _ = off.Server;

            var department = new AlShifa.Api.Features.Users.Department { Code = "OF" + Random.Shared.Next(1000, 9999), Name = "Made while sync was off" };
            db.Departments.Add(department);
            await db.SaveChangesAsync();
            departmentId = department.Id;
            Assert.True(await db.SyncChanges.AnyAsync());
        }

        await sync.SyncAsync();
        await using var online = sync.MirrorData();
        Assert.True(await online.Departments.AnyAsync(d => d.Id == departmentId));
    }

    [Fact]
    public async Task The_timed_sync_keeps_running_after_a_round_and_never_stops_the_application()
    {
        // Only the worker under observation may send during this scheduling test.
        // The fixture's default worker otherwise starts after 20 seconds and masks the retry interval.
        await sync.Local.Services.GetRequiredService<SyncWorker>().StopAsync(CancellationToken.None);
        var desk = await sync.LocalLoginAsync(Roles.Receptionist);
        var patient = await CreatePatientAsync(desk, "زمان‌بندی");

        var services = sync.Local.Services;
        var worker = new SyncWorker(services.GetRequiredService<IServiceScopeFactory>(), services.GetRequiredService<IHttpClientFactory>(),
            Microsoft.Extensions.Options.Options.Create(new SyncOptions { Url = "https://online.test", Key = SyncFixture.Key, IntervalMinutes = 1, StartDelaySeconds = 0 }),
            services.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>(), services.GetRequiredService<Microsoft.Extensions.Hosting.IHostEnvironment>(),
            services.GetRequiredService<ILogger<SyncWorker>>());
        await worker.StartAsync(CancellationToken.None);
        try
        {
            await using var online = sync.MirrorData();
            for (var i = 0; i < 100 && !await online.Patients.AnyAsync(p => p.Id == patient.Id); i++)
            {
                await Task.Delay(100);
            }

            Assert.True(await online.Patients.AnyAsync(p => p.Id == patient.Id));

            // A failed automatic round must retry on its timer after internet recovery.
            // This deliberately waits for the production scheduler rather than manually calling RunOnce.
            sync.Network.Offline = true;
            var pendingPatient = await CreatePatientAsync(desk, "Synthetic scheduled offline recovery");
            // A previous test can leave an error in this shared fixture. Observe
            // this worker's failed round before restoring the network; otherwise
            // the wake request can run only after recovery and bypass the timer.
            await LocalAsync(db => db.SyncStates.Where(s => s.Key == "push.lastError").ExecuteDeleteAsync());
            worker.RequestRun();
            var failed = false;
            for (var i = 0; i < 100 && !failed; i++)
            {
                using var scope = services.CreateScope();
                failed = await SyncWorker.GetStateAsync(scope.ServiceProvider.GetRequiredService<AppDbContext>(), "push.lastError", default) is not null;
                if (!failed) await Task.Delay(100);
            }
            Assert.True(failed);
            Assert.False(await online.Patients.AnyAsync(p => p.Id == pendingPatient.Id));
            sync.Network.Offline = false;
            var recovery = System.Diagnostics.Stopwatch.StartNew();
            while (recovery.Elapsed < TimeSpan.FromSeconds(90) && !await online.Patients.AnyAsync(p => p.Id == pendingPatient.Id))
                await Task.Delay(500);
            Assert.True(await online.Patients.AnyAsync(p => p.Id == pendingPatient.Id));
            Assert.True(recovery.Elapsed >= TimeSpan.FromSeconds(40));
            Assert.Equal(1, await online.Patients.CountAsync(p => p.Id == pendingPatient.Id));

            // After the round it waits for the next one instead of failing.
            await Task.Delay(1000);
            Assert.False(worker.ExecuteTask!.IsCompleted, worker.ExecuteTask.Exception?.ToString());
        }
        finally
        {
            sync.Network.Offline = false;
            await worker.StopAsync(CancellationToken.None);
        }

        Assert.Equal(Microsoft.Extensions.Hosting.BackgroundServiceExceptionBehavior.Ignore,
            services.GetRequiredService<Microsoft.Extensions.Options.IOptions<Microsoft.Extensions.Hosting.HostOptions>>().Value.BackgroundServiceExceptionBehavior);
    }

    [Fact]
    public async Task A_unique_value_freed_by_a_deleted_row_can_be_taken_by_a_new_one()
    {
        // As when a refraction is saved again: its lines are deleted and written anew with the same stage and eye.
        var code = "RU" + Random.Shared.Next(1000, 9999);
        await LocalAsync(db => db.Departments.Add(new AlShifa.Api.Features.Users.Department { Code = code, Name = "old" }));
        await sync.SyncAsync();

        await LocalAsync(async db => db.Departments.Remove(await db.Departments.SingleAsync(d => d.Code == code)));
        await LocalAsync(db => db.Departments.Add(new AlShifa.Api.Features.Users.Department { Code = code, Name = "new" }));
        await sync.SyncAsync();

        await using var online = sync.MirrorData();
        Assert.Equal("new", (await online.Departments.AsNoTracking().SingleAsync(d => d.Code == code)).Name);
        await AssertNothingPendingAsync();
    }

    [Fact]
    public async Task Two_rows_that_trade_a_unique_value_both_arrive()
    {
        var (first, second) = ("TA" + Random.Shared.Next(1000, 9999), "TB" + Random.Shared.Next(1000, 9999));
        await LocalAsync(db => db.Departments.AddRange(
            new AlShifa.Api.Features.Users.Department { Code = first, Name = "one" },
            new AlShifa.Api.Features.Users.Department { Code = second, Name = "two" }));
        await sync.SyncAsync();

        // "two" changes first, so it reaches the online copy first and finds "one" still holding the code.
        await LocalAsync(async db => (await db.Departments.SingleAsync(d => d.Name == "two" && d.Code == second)).Code = "TMP" + second);
        await LocalAsync(async db => (await db.Departments.SingleAsync(d => d.Name == "one" && d.Code == first)).Code = second);
        await LocalAsync(async db => (await db.Departments.SingleAsync(d => d.Name == "two" && d.Code == "TMP" + second)).Code = first);
        await sync.SyncAsync();

        await using var online = sync.MirrorData();
        Assert.Equal("two", (await online.Departments.AsNoTracking().SingleAsync(d => d.Code == first)).Name);
        Assert.Equal("one", (await online.Departments.AsNoTracking().SingleAsync(d => d.Code == second)).Name);
        await AssertNothingPendingAsync();
    }

    [Fact]
    public async Task Rows_deleted_while_sync_was_off_disappear_online_when_it_is_on_again()
    {
        var code = "DO" + Random.Shared.Next(1000, 9999);
        await LocalAsync(db => db.Departments.Add(new AlShifa.Api.Features.Users.Department { Code = code, Name = "deleted while off" }));
        await sync.SyncAsync();
        long departmentId;
        using (var scope = sync.Local.Services.CreateScope())
        {
            departmentId = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Departments.Where(d => d.Code == code).Select(d => d.Id).SingleAsync();
        }

        await using (var off = sync.LocalWithoutSync())
        {
            _ = off.Server;
            using var scope = off.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Departments.Remove(await db.Departments.SingleAsync(d => d.Id == departmentId));
            await db.SaveChangesAsync();
            for (var i = 0; i < 50 && await db.SyncStates.AnyAsync(s => s.Key == "push.snapshotOf"); i++)
            {
                await Task.Delay(100);
            }
        }

        await sync.SyncAsync();
        await using var online = sync.MirrorData();
        Assert.False(await online.Departments.AnyAsync(d => d.Id == departmentId));

        // Everything else is still there.
        using var scope2 = sync.Local.Services.CreateScope();
        var local = scope2.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(await local.Departments.CountAsync(), await online.Departments.CountAsync());
        Assert.Equal(await local.Patients.CountAsync(), await online.Patients.CountAsync());
    }

    /// <summary>One change on the hospital database, saved with sync capture on.</summary>
    private Task LocalAsync(Action<AppDbContext> change) => LocalAsync(db =>
    {
        change(db);
        return Task.CompletedTask;
    });

    private async Task LocalAsync(Func<AppDbContext, Task> change)
    {
        using var scope = sync.Local.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await change(db);
        await db.SaveChangesAsync();
    }

    private async Task AssertNothingPendingAsync()
    {
        using var scope = sync.Local.Services.CreateScope();
        Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<AppDbContext>().SyncChanges.CountAsync());
    }

    private static async Task<PatientDto> CreatePatientAsync(HttpClient desk, string name)
    {
        var response = await desk.SendAsync(ApiFixture.Keyed("/api/patients", new SavePatientRequest(name, null, null, Gender.Male, null, 30, null, null, ConfirmNotDuplicate: true)));
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<PatientDto>())!;
    }

    /// <summary>RFC 6238, as an authenticator app computes it.</summary>
    private static string Totp(string base32Key)
    {
        var key = Base32(base32Key);
        var counter = BitConverter.GetBytes(DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30);
        if (BitConverter.IsLittleEndian)
        {
            Array.Reverse(counter);
        }

        var hash = HMACSHA1.HashData(key, counter);
        var offset = hash[^1] & 0x0f;
        var code = ((hash[offset] & 0x7f) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return (code % 1_000_000).ToString("D6");
    }

    private static byte[] Base32(string text)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var bits = string.Concat(text.TrimEnd('=').ToUpperInvariant().Select(c => Convert.ToString(alphabet.IndexOf(c), 2).PadLeft(5, '0')));
        return Enumerable.Range(0, bits.Length / 8).Select(i => Convert.ToByte(bits.Substring(i * 8, 8), 2)).ToArray();
    }
}
