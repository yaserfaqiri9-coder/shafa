using System.Text.Json;
using AlShifa.Api.Features.Settings;
using AlShifa.Api.Features.Sync;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace AlShifa.Tests;

public sealed class AutomationUnitTests
{
    private sealed class EnvironmentStub(string root) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";
        public string ApplicationName { get; set; } = "AutomationTests";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    [Fact]
    public void Installations_have_distinct_durable_host_identities()
    {
        var root = Path.Combine(Path.GetTempPath(), "alshifa-identity-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var configuration = new ConfigurationBuilder().Build();
            var first = new InstallationIdentity(configuration, new EnvironmentStub(Path.Combine(root, "first"))).Id;
            Assert.Equal(first, new InstallationIdentity(configuration, new EnvironmentStub(Path.Combine(root, "first"))).Id);
            Assert.NotEqual(first, new InstallationIdentity(configuration, new EnvironmentStub(Path.Combine(root, "second"))).Id);
            Assert.True(Guid.TryParse(first, out _));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData("http://online.example", false)]
    [InlineData("https://user:password@online.example", false)]
    [InlineData("https://online.example?token=test", false)]
    [InlineData("https://online.example", true)]
    public void Sync_requires_https_without_url_credentials(string url, bool expected)
        => Assert.Equal(expected, SyncWorker.Configured(new SyncOptions { Url = url, Key = new string('t', 32) }));

    [Fact]
    public void Offline_upgrade_holds_online_services_until_compatibility_approval()
        => Assert.False(SyncWorker.Configured(new SyncOptions { Url = "https://shafa.novatechsoft.com", Key = new string('t', 32), RequireCompatibilityApproval = true }));

    [Theory]
    [InlineData("", "", false, "آدرس سرور آنلاین")]
    [InlineData("https://online.example", "", false, "کلید امن")]
    [InlineData("http://online.example", "synthetic-secret", false, "HTTPS")]
    [InlineData("https://user:synthetic-secret@online.example", "synthetic-secret", false, "HTTPS")]
    [InlineData("https://online.example", "synthetic-secret", true, "تأیید سازگاری")]
    public void Configuration_guidance_identifies_the_gate_without_exposing_values(string url, string key, bool hold, string expected)
    {
        var options = new SyncOptions { Url = url, Key = key, RequireCompatibilityApproval = hold };
        Assert.False(SyncWorker.Configured(options));
        var problem = SyncWorker.ConfigurationProblem(options);
        Assert.Contains(expected, problem);
        Assert.DoesNotContain("synthetic-secret", problem);
        Assert.DoesNotContain("online.example", problem);
    }

    [Fact]
    public void Unreadable_update_status_never_claims_success()
    {
        var missing = SoftwareUpdate.Read(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json"));
        Assert.False(missing.Configured);
        Assert.Equal("unknown", missing.State);
        Assert.NotNull(missing.LastError);
    }

    [Fact]
    public void Backup_success_requires_restore_verification()
    {
        var path = Path.Combine(Path.GetTempPath(), "alshifa-backup-test-" + Guid.NewGuid() + ".json");
        var now = DateTime.UtcNow;
        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(new { lastSuccessAt = now }));
            Assert.True(BackupStatus.Read(path, now).NeedsAttention);
            File.WriteAllText(path, JsonSerializer.Serialize(new { lastSuccessAt = now, restoreVerifiedAt = now }));
            Assert.False(BackupStatus.Read(path, now).NeedsAttention);
            Assert.True(BackupStatus.Read(path, now.AddHours(27)).NeedsAttention);
        }
        finally { File.Delete(path); }
    }
}
