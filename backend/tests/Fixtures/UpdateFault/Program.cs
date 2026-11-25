using System.Text.Json;
using System.Text.RegularExpressions;
using MySql.Data.MySqlClient;

// Fault injection is compiled separately and never included in a normal release.
// It cannot mutate a database unless all physical lab identity checks succeed.
if (!args.Contains("--migrate")) return 86;
var app = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
var parent = Directory.GetParent(app);
while (parent is not null && !Regex.IsMatch(parent.Name, "^windows-service-lab-[a-f0-9]{32}$")) parent = parent.Parent;
var root = parent?.FullName ?? "";
if (!Regex.IsMatch(Path.GetFileName(root), "^windows-service-lab-[a-f0-9]{32}$") || !File.Exists(Path.Combine(root, "fixture.marker"))) return 87;
var value = Environment.GetEnvironmentVariable("ConnectionStrings__Default");
if (string.IsNullOrEmpty(value))
{
    using var settings = JsonDocument.Parse(File.ReadAllText(Path.Combine(app, "appsettings.Production.json")));
    value = settings.RootElement.GetProperty("ConnectionStrings").GetProperty("Default").GetString();
}
var connectionString = new MySqlConnectionStringBuilder(value);
if (connectionString.Server != "127.0.0.1" || connectionString.Port != 33490 || (connectionString.Database != "alshifa" && !Regex.IsMatch(connectionString.Database, "^alshifa_restore_[a-f0-9]{32}$"))) return 88;
await using var connection = new MySqlConnection(value);
await connection.OpenAsync();
await using (var identity = new MySqlCommand("SELECT @@port, @@datadir", connection))
await using (var reader = await identity.ExecuteReaderAsync())
{
    if (!await reader.ReadAsync() || reader.GetInt32(0) != 33490 || Path.GetFullPath(reader.GetString(1)).TrimEnd(Path.DirectorySeparatorChar) != Path.Combine(root, "data")) return 89;
}
await using (var command = new MySqlCommand("ALTER TABLE Patients ADD COLUMN LabRollbackProbe int NULL; UPDATE Patients SET FirstName='Synthetic failed update probe' WHERE FirstName='Synthetic Windows preservation';", connection))
    await command.ExecuteNonQueryAsync();
using var fault = JsonDocument.Parse(File.ReadAllText(Path.Combine(app, "lab-fault.json")));
return fault.RootElement.GetProperty("phase").GetString() == "migration" ? 42 : 0;
