using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AlShifa.Api.Features.Auth;
using AlShifa.Api.Features.Settings;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AlShifa.Tests;

[Collection("api")]
public sealed class SoftwareUpdateTests(ApiFixture api)
{
    private async Task<HttpClient> UserAsync(string role)
    {
        var name = "update_" + Guid.NewGuid().ToString("N")[..12];
        await api.CreateUserAsync(name, role);
        return await api.LoginAsync(name);
    }

    [Fact]
    public async Task Only_a_settings_editor_can_approve_the_exact_fresh_verified_version()
    {
        var doctor = await UserAsync(Roles.Doctor);
        var admin = await UserAsync(Roles.SuperAdmin);
        var configuration = api.Services.GetRequiredService<IConfiguration>();
        var path = Path.Combine(Path.GetTempPath(), "alshifa-update-status-" + Guid.NewGuid() + ".json");
        var oldPath = configuration["Update:StatusFile"];
        var before = api.UpdateTrigger.Versions.Count;
        configuration["Update:StatusFile"] = path;
        try
        {
            var status = new SoftwareUpdateStatus(true, "awaitingApproval", "2026.11.20.1", "2026.11.21.1", DateTime.UtcNow);
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(status, JsonSerializerOptions.Web));
            Assert.Equal(HttpStatusCode.Forbidden, (await doctor.GetAsync("/api/settings/software-update")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await doctor.PostAsJsonAsync("/api/settings/software-update/approve", new { version = status.AvailableVersion })).StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync("/api/settings/software-update/approve", new { version = "2026.11.22.1" })).StatusCode);
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(status with { CheckedAt = DateTime.UtcNow.AddHours(-3) }, JsonSerializerOptions.Web));
            Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync("/api/settings/software-update/approve", new { version = status.AvailableVersion })).StatusCode);
            Assert.Equal(before, api.UpdateTrigger.Versions.Count);
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(status, JsonSerializerOptions.Web));
            var accepted = await admin.PostAsJsonAsync("/api/settings/software-update/approve", new { version = status.AvailableVersion });
            Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
            using var body = JsonDocument.Parse(await accepted.Content.ReadAsStringAsync());
            Assert.Equal("requested", body.RootElement.GetProperty("state").GetString());
            Assert.Equal(before + 1, api.UpdateTrigger.Versions.Count);
            Assert.Equal(status.AvailableVersion, api.UpdateTrigger.Versions.Last());
        }
        finally
        {
            configuration["Update:StatusFile"] = oldPath;
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("downloading", false, -1, "2026.11.20.1")]
    [InlineData("failed", false, -1, "2026.11.20.1")]
    [InlineData("awaitingApproval", true, -11, "2026.11.20.1")]
    [InlineData("awaitingApproval", true, 2, "2026.11.20.1")]
    [InlineData("awaitingApproval", true, -1, "2026.11.21.1")]
    [InlineData("awaitingApproval", true, -1, "2026.11.22.1")]
    public async Task Unready_stale_future_or_non_newer_versions_never_trigger_installation(string state, bool verified, int age, string current)
    {
        var admin = await UserAsync(Roles.SuperAdmin);
        var configuration = api.Services.GetRequiredService<IConfiguration>();
        var oldPath = configuration["Update:StatusFile"];
        var path = Path.Combine(Path.GetTempPath(), "alshifa-update-guard-" + Guid.NewGuid() + ".json");
        var before = api.UpdateTrigger.Versions.Count;
        configuration["Update:StatusFile"] = path;
        try
        {
            var status = new SoftwareUpdateStatus(true, state, current, "2026.11.21.1", DateTime.UtcNow.AddMinutes(age), PackageVerified: verified);
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(status, JsonSerializerOptions.Web));
            Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync("/api/settings/software-update/approve", new { version = status.AvailableVersion })).StatusCode);
            Assert.Equal(before, api.UpdateTrigger.Versions.Count);
        }
        finally { configuration["Update:StatusFile"] = oldPath; File.Delete(path); }
    }
}
