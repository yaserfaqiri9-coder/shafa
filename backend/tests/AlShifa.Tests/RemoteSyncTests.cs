using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
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

/// <summary>Opt-in two-host test. Only scripts/test-contabo-sync.ps1 provisions its isolated server and private settings.</summary>
public sealed class RemoteSyncTests
{
    private sealed class RemoteLabFactAttribute : FactAttribute
    {
        public RemoteLabFactAttribute()
        {
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ALSHIFA_REMOTE_LAB_FILE")))
                Skip = "Requires the dedicated two-host lab provisioned by test-contabo-sync.ps1.";
        }
    }
    private sealed record Lab(string RemoteBase, int ApiTunnelPort, int DbTunnelPort, string ServerUuid, string WriterConnection, string SyncKey);
    private sealed class Network { public volatile bool Offline; public int OfflineFailures; }
    private sealed class Transport(Network network, HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (network.Offline)
            {
                Interlocked.Increment(ref network.OfflineFailures);
                throw new HttpRequestException("Synthetic internet interruption");
            }
            return base.SendAsync(request, ct);
        }
    }

    private static SocketsHttpHandler Tunnel(Lab lab) => new()
    {
        ConnectCallback = async (context, ct) =>
        {
            if (context.DnsEndPoint.Host != "shafa.novatechsoft.com" || context.DnsEndPoint.Port != 33443 || lab.ApiTunnelPort != 33443)
                throw new InvalidOperationException("Unexpected remote test API target.");
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
            try { await socket.ConnectAsync(IPAddress.Loopback, lab.ApiTunnelPort, ct); return new NetworkStream(socket, ownsSocket: true); }
            catch { socket.Dispose(); throw; }
        },
        // Normal certificate chain and hostname validation stay enabled. The SSH tunnel changes only the TCP route.
        UseProxy = false,
    };

    private sealed class App(string connection, string root, Lab lab, Network network, bool onlineOperator = false, bool automatic = false) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("ConnectionStrings:Default", connection);
            builder.UseSetting("Mirror:Enabled", onlineOperator ? "true" : "false");
            builder.UseSetting("Storage:Path", Path.Combine(root, "storage"));
            builder.UseSetting("DataProtection:KeysPath", Path.Combine(root, "keys"));
            builder.UseSetting("Sync:IdentityFile", Path.Combine(root, "installation-id"));
            if (!onlineOperator)
            {
                builder.UseSetting("Sync:Url", lab.RemoteBase);
                builder.UseSetting("Sync:Key", lab.SyncKey);
                builder.UseSetting("Sync:StartDelaySeconds", automatic ? "0" : "3600");
                builder.UseSetting("Sync:IntervalMinutes", "1");
            }
            builder.ConfigureLogging(logs => logs.ClearProviders());
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<IBackupTrigger>(new RecordingBackupTrigger());
                services.AddSingleton<IUpdateTrigger>(new RecordingUpdateTrigger());
                if (!onlineOperator) services.AddHttpClient(SyncWorker.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => new Transport(network, Tunnel(lab)));
            });
        }
    }

    [RemoteLabFact]
    public async Task Local_and_Contabo_exchange_initial_rows_offline_work_and_idempotent_online_commands_over_validated_tls()
    {
        var labPath = Environment.GetEnvironmentVariable("ALSHIFA_REMOTE_LAB_FILE")
            ?? throw new InvalidOperationException("Use test-contabo-sync.ps1; this test must never target a configured production mirror.");
        var lab = JsonSerializer.Deserialize<Lab>(await File.ReadAllTextAsync(labPath), JsonSerializerOptions.Web)!;
        Assert.Equal("https://shafa.novatechsoft.com:33443", lab.RemoteBase);
        var remoteConnection = new MySqlConnectionStringBuilder(lab.WriterConnection);
        Assert.Equal("127.0.0.1", remoteConnection.Server);
        Assert.Equal(33480u, remoteConnection.Port);
        Assert.Equal("alshifa_remote_test", remoteConnection.Database);
        Assert.Equal("lab_owner", remoteConnection.UserID);
        await using (var remote = new MySqlConnection(lab.WriterConnection))
        {
            await remote.OpenAsync();
            await using var identity = new MySqlCommand("SELECT @@server_uuid", remote);
            Assert.Equal(lab.ServerUuid, (string)(await identity.ExecuteScalarAsync())!);
        }
        var localConnection = new MySqlConnectionStringBuilder(ApiFixture.ConnectionString);
        Assert.Equal("127.0.0.1", localConnection.Server);
        Assert.InRange(localConnection.Port, 33379u, 33399u);
        localConnection.Database = "alshifa_remote_source_test";
        var root = Path.Combine(Path.GetDirectoryName(labPath)!, "local-api");
        Directory.CreateDirectory(root);
        var network = new Network();
        await using var local = new App(localConnection.ConnectionString, root, lab, network);
        _ = local.Server;
        const string userName = "synthetic_remote_admin";
        const string localPassword = "Synthetic-Local-2026";
        const string onlinePassword = "Synthetic-Online-2026";
        using (var scope = local.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
            var user = new AppUser { UserName = userName, FullName = "Synthetic integration operator" };
            Assert.True((await users.CreateAsync(user, localPassword)).Succeeded);
            Assert.True((await users.AddToRoleAsync(user, Roles.SuperAdmin)).Succeeded);
        }
        using var localClient = SyncFixture.Client(local);
        (await localClient.PostAsJsonAsync("/api/auth/login", new LoginRequest(userName, localPassword))).EnsureSuccessStatusCode();
        await ApiFixture.RefreshCsrfAsync(localClient);
        var source = await CreatePatient(localClient, "Synthetic-local-initial");
        var worker = local.Services.GetRequiredService<SyncWorker>();
        var status = await localClient.GetFromJsonAsync<SyncStatusDto>("/api/sync/status");
        Assert.True(status!.Configured);
        Assert.Null(status.ConfigurationProblem);
        // Persist activation once; subsequent host instances must retain it.
        (await localClient.PutAsJsonAsync("/api/sync/enabled", new SyncEnabledRequest(false))).EnsureSuccessStatusCode();
        (await localClient.PutAsJsonAsync("/api/sync/enabled", new SyncEnabledRequest(true))).EnsureSuccessStatusCode();
        await worker.RunOnceAsync(default);
        var installationId = local.Services.GetRequiredService<InstallationIdentity>().Id;

        // Same operator command as deployment, but confined to the checked remote test UUID/schema.
        await using var operatorApp = new App(lab.WriterConnection, Path.Combine(root, "operator"), lab, network, onlineOperator: true);
        string authenticator;
        using (var scope = operatorApp.Services.CreateScope())
        {
            var beforeOut = Console.Out;
            var oldName = Environment.GetEnvironmentVariable("ALSHIFA_ONLINE_USER");
            var oldPassword = Environment.GetEnvironmentVariable("ALSHIFA_ONLINE_PASSWORD");
            try
            {
                Console.SetOut(TextWriter.Null);
                Environment.SetEnvironmentVariable("ALSHIFA_ONLINE_USER", userName);
                Environment.SetEnvironmentVariable("ALSHIFA_ONLINE_PASSWORD", onlinePassword);
                await OnlineAccess.RunAsync(scope.ServiceProvider, revoke: false);
            }
            finally
            {
                Console.SetOut(beforeOut);
                Environment.SetEnvironmentVariable("ALSHIFA_ONLINE_USER", oldName);
                Environment.SetEnvironmentVariable("ALSHIFA_ONLINE_PASSWORD", oldPassword);
            }
            var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
            authenticator = (await users.GetAuthenticatorKeyAsync((await users.FindByNameAsync(userName))!))!;
        }
        using var online = new HttpClient(Tunnel(lab)) { BaseAddress = new Uri(lab.RemoteBase) };
        await ApiFixture.RefreshCsrfAsync(online);
        (await online.PostAsJsonAsync("/api/auth/login", new LoginRequest(userName, onlinePassword, Totp(authenticator)))).EnsureSuccessStatusCode();
        await ApiFixture.RefreshCsrfAsync(online);
        var received = await online.GetFromJsonAsync<PatientDto>($"/api/patients/{source.Id}");
        Assert.Equal(source.FileNumber, received!.FileNumber);
        Assert.Equal("Synthetic-local-initial", received.FirstName);

        // The remote server queues an intent; local processing remains authoritative.
        var key = Guid.NewGuid().ToString("N");
        HttpRequestMessage OnlinePatient() => ApiFixture.Keyed("/api/patients", new SavePatientRequest("Synthetic-online-intent", null, null, Gender.Male, null, 30, null, null), key);
        var accepted = await online.SendAsync(OnlinePatient());
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        Assert.NotNull(accepted.Headers.Location);
        network.Offline = true;
        await Assert.ThrowsAsync<HttpRequestException>(() => worker.RunOnceAsync(default));
        var offlinePatient = await CreatePatient(localClient, "Synthetic-local-offline");
        string? failedAttempt;
        using (var scope = local.Services.CreateScope())
        {
            Assert.True(await scope.ServiceProvider.GetRequiredService<AppDbContext>().SyncChanges.AnyAsync());
            failedAttempt = await SyncWorker.GetStateAsync(scope.ServiceProvider.GetRequiredService<AppDbContext>(), "push.lastAttemptAt", default);
        }

        // Restart the source host against its own same scratch database and durable identity.
        // Let the real one-minute worker retry; do not click Run or signal RequestRun after internet recovery.
        var failuresBeforeRestart = Volatile.Read(ref network.OfflineFailures);
        await using (var restarted = new App(localConnection.ConnectionString, root, lab, network, automatic: true))
        {
            _ = restarted.Server;
            Assert.Equal(installationId, restarted.Services.GetRequiredService<InstallationIdentity>().Id);
            await WaitUntilAsync(async () =>
            {
                using var scope = restarted.Services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                return Volatile.Read(ref network.OfflineFailures) > failuresBeforeRestart
                    && await SyncWorker.GetStateAsync(db, "push.lastAttemptAt", default) != failedAttempt
                    && await SyncWorker.GetStateAsync(db, "push.lastError", default) is not null;
            }, TimeSpan.FromSeconds(15));
            network.Offline = false;
            var recoveryWait = System.Diagnostics.Stopwatch.StartNew();
            await WaitUntilAsync(async () =>
            {
                using var scope = restarted.Services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                return await SyncWorker.EnabledAsync(db, default)
                    && !await db.SyncChanges.AnyAsync()
                    && await SyncWorker.GetStateAsync(db, "push.lastError", default) is null;
            }, TimeSpan.FromSeconds(100));
            Assert.True(recoveryWait.Elapsed >= TimeSpan.FromSeconds(40), "Recovery must use the real scheduled retry after the failed automatic attempt.");
        }
        var result = await online.GetFromJsonAsync<CommandResult>(accepted.Headers.Location);
        Assert.Equal(201, result!.StatusCode);
        var mirroredOffline = await online.GetFromJsonAsync<PatientDto>($"/api/patients/{offlinePatient.Id}");
        Assert.Equal("Synthetic-local-offline", mirroredOffline!.FirstName);
        Assert.Equal(HttpStatusCode.Created, (await online.SendAsync(OnlinePatient())).StatusCode);

        // A real online payment is received and posted once, then mirrored back.
        var service = await localClient.PostAsJsonAsync("/api/services", new SaveServiceRequest("Synthetic remote service", null, true, 100));
        service.EnsureSuccessStatusCode();
        var serviceId = (await service.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
        var register = await localClient.PostAsJsonAsync("/api/cash/registers", new SaveRegisterRequest("Synthetic remote cash register"));
        register.EnsureSuccessStatusCode();
        var registerId = (await register.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
        (await localClient.PostAsJsonAsync("/api/cash/sessions", new OpenSessionRequest(registerId, 0))).EnsureSuccessStatusCode();
        var invoiceKey = Guid.NewGuid().ToString("N");
        HttpRequestMessage Invoice() => ApiFixture.Keyed("/api/invoices", new CreateInvoiceRequest(source.Id, null, [new InvoiceLineInput(serviceId, 1)]), invoiceKey);
        var invoice = await localClient.SendAsync(Invoice());
        invoice.EnsureSuccessStatusCode();
        var invoiceId = (await invoice.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();
        var invoiceReplay = await localClient.SendAsync(Invoice());
        invoiceReplay.EnsureSuccessStatusCode();
        Assert.Equal(invoiceId, (await invoiceReplay.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64());
        await worker.RunOnceAsync(default);
        var paymentKey = Guid.NewGuid().ToString("N");
        HttpRequestMessage Payment() => ApiFixture.Keyed("/api/payments", new PaymentRequest(invoiceId, 60, PaymentMethod.Cash, null), paymentKey);
        var queued = await online.SendAsync(Payment());
        Assert.Equal(HttpStatusCode.Accepted, queued.StatusCode);
        await worker.RunOnceAsync(default);
        var paid = await online.GetFromJsonAsync<CommandResult>(queued.Headers.Location);
        Assert.Equal(201, paid!.StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await online.SendAsync(Payment())).StatusCode);

        // An online edit queued against an old version must never overwrite a subsequent local edit.
        var current = (await online.GetFromJsonAsync<PatientDto>($"/api/patients/{source.Id}"))!;
        var edit = new HttpRequestMessage(HttpMethod.Put, $"/api/patients/{source.Id}")
        {
            Content = JsonContent.Create(new SavePatientRequest("Synthetic-stale-online-edit", null, null, Gender.Male, null, 30, null, null, Version: current.Version)),
        };
        edit.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        var pendingEdit = await online.SendAsync(edit);
        Assert.Equal(HttpStatusCode.Accepted, pendingEdit.StatusCode);
        (await localClient.PutAsJsonAsync($"/api/patients/{source.Id}", new SavePatientRequest("Synthetic-local-authoritative-edit", null, null, Gender.Male, null, 30, null, null, Version: current.Version))).EnsureSuccessStatusCode();
        await worker.RunOnceAsync(default);
        var conflict = await online.GetFromJsonAsync<CommandResult>(pendingEdit.Headers.Location);
        Assert.Equal(409, conflict!.StatusCode);
        Assert.Contains("concurrency_conflict", conflict.ResponseBody);
        using (var scope = local.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Equal(1, await db.Patients.CountAsync(p => p.FirstName == "Synthetic-online-intent"));
            Assert.Equal(1, await db.Payments.CountAsync(p => p.InvoiceId == invoiceId));
            Assert.Equal(1, await db.Invoices.CountAsync(i => i.Id == invoiceId));
            Assert.Equal(60m, await db.Payments.Where(p => p.InvoiceId == invoiceId).SumAsync(p => p.Amount));
            Assert.Equal(0, await db.SyncChanges.CountAsync());
            var reconciliation = await FinanceEndpoints.ReconcileAsync(db, default);
            Assert.True(reconciliation.Balanced);
            Assert.True(await scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>().CheckPasswordAsync((await db.Users.SingleAsync(u => u.UserName == userName)), localPassword));
        }
        await using var mirrorDb = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseMySQL(lab.WriterConnection).Options);
        Assert.Equal(1, await mirrorDb.Payments.CountAsync(p => p.InvoiceId == invoiceId));
        Assert.Equal(1, await mirrorDb.Patients.CountAsync(p => p.FirstName == "Synthetic-online-intent"));
        Assert.Equal("Synthetic-local-authoritative-edit", (await mirrorDb.Patients.SingleAsync(p => p.Id == source.Id)).FirstName);
        Assert.Equal(1, await mirrorDb.Invoices.CountAsync(i => i.Id == invoiceId));
        Assert.Equal(installationId, await mirrorDb.SyncStates.Where(s => s.Key == "apply.sourceId").Select(s => s.Value).SingleAsync());
        var proof = new
        {
            completedAt = DateTime.UtcNow, remoteSchema = SyncSchema.SchemaVersion(mirrorDb),
            localToOnline = true, onlineCommandToLocalAndBack = true,
            offlineLocalSave = true, scheduledRecoveryWithoutUserAction = true,
            activationAndIdentitySurviveHostRestart = true,
            patientPaymentAndInvoiceReplayDeduplicated = true,
            staleOnlineEditRejected = true, localOutboxEmpty = true, ledgerBalanced = true,
            validatedTlsOverSshTunnel = true, productionMirrorModified = false,
        };
        await File.WriteAllTextAsync(Path.Combine(Path.GetDirectoryName(labPath)!, "sync-proof.json"), JsonSerializer.Serialize(proof, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await condition()) return;
            await Task.Delay(500);
        }
        Assert.True(await condition(), "The automatic Sync deadline elapsed.");
    }

    private static async Task<PatientDto> CreatePatient(HttpClient client, string name)
    {
        var response = await client.SendAsync(ApiFixture.Keyed("/api/patients", new SavePatientRequest(name, null, null, Gender.Male, null, 30, null, null)));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<PatientDto>())!;
    }

    private static string Totp(string secret)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var bytes = new List<byte>(); var buffer = 0; var bits = 0;
        foreach (var c in secret.TrimEnd('=').ToUpperInvariant()) { buffer = (buffer << 5) | alphabet.IndexOf(c); bits += 5; if (bits >= 8) { bits -= 8; bytes.Add((byte)(buffer >> bits)); } }
        var counter = BitConverter.GetBytes(IPAddress.HostToNetworkOrder(DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30));
        var hash = HMACSHA1.HashData(bytes.ToArray(), counter); var offset = hash[^1] & 15;
        var value = ((hash[offset] & 127) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return (value % 1_000_000).ToString("D6");
    }
}
