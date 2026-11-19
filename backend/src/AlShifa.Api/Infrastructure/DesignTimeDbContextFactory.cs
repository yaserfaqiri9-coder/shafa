using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace AlShifa.Api.Infrastructure;

/// <summary>Schema tooling must never start the web host or its background workers.</summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseMySQL("Server=127.0.0.1;Port=1;Database=alshifa_schema_test;User=schema;Password=unused;").Options);
}
