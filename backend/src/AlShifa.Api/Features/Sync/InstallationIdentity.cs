namespace AlShifa.Api.Features.Sync;

/// <summary>Host identity is outside the replicated database. Never copy it to another installation.</summary>
public sealed class InstallationIdentity(IConfiguration configuration, IHostEnvironment environment)
{
    private readonly Lazy<string> identity = new(() => Load(configuration, environment));
    public string Id => identity.Value;

    private static string Load(IConfiguration configuration, IHostEnvironment environment)
    {
        var path = configuration["Sync:IdentityFile"]
            ?? Path.Combine(configuration["DataProtection:KeysPath"] ?? Path.Combine(environment.ContentRootPath, "keys"), "sync-installation-id");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        if (!File.Exists(path))
        {
            var temporary = path + "." + Guid.NewGuid().ToString("N");
            File.WriteAllText(temporary, Guid.NewGuid().ToString("D"));
            try { File.Move(temporary, path); }
            catch (IOException) when (File.Exists(path)) { File.Delete(temporary); }
        }
        return Guid.TryParse(File.ReadAllText(path).Trim(), out var id)
            ? id.ToString("D") : throw new InvalidOperationException("Invalid installation identity; technical review is required.");
    }
}
