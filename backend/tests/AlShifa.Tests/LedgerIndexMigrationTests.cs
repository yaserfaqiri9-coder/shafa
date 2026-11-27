using System.Text.Json;
using AlShifa.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace AlShifa.Tests;

public sealed partial class MigrationSafetyTests
{
    [Fact]
    public async Task Ledger_covering_index_upgrade_and_downgrade_preserve_money_and_pending_sync()
    {
        var operations = new AlShifa.Api.Infrastructure.Migrations.PerformanceIndexes().UpOperations;
        Assert.Equal(3, operations.Count);
        Assert.IsType<CreateIndexOperation>(operations[0]);
        Assert.IsType<DropIndexOperation>(operations[1]);
        Assert.IsType<CreateIndexOperation>(operations[2]);
        var connection = ConnectionString();
        Assert.Equal("1", Environment.GetEnvironmentVariable("ALSHIFA_ISOLATED_QUEUE_TESTS"));
        Assert.True(DatabaseSafety.IsDisposable(connection));
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseMySQL(connection).Options);
        await db.Database.EnsureDeletedAsync();
        try
        {
            var migrator = db.GetService<IMigrator>();
            var previous = db.Database.GetMigrations().TakeWhile(m => !m.EndsWith("_PerformanceIndexes")).Last();
            await migrator.MigrateAsync(previous);
            await db.Database.ExecuteSqlRawAsync("""
                INSERT INTO LedgerAccounts(Code,Name) VALUES('1000','cash'),('3000','opening');
                INSERT INTO LedgerTransactions(Id,Kind,SourceType,SourceId,CreatedByUserId,CreatedAt) VALUES(1,'Opening','Synthetic',1,1,UTC_TIMESTAMP(6));
                INSERT INTO LedgerEntries(TransactionId,AccountCode,Debit,Credit) VALUES(1,'1000',123.45,0),(1,'3000',0,123.45);
                INSERT INTO SyncStates(`Key`,Value,UpdatedAt) VALUES('push.ackedSeq','123',UTC_TIMESTAMP(6)),('push.enabled','true',UTC_TIMESTAMP(6));
                INSERT INTO SyncChanges(TableName,KeyJson,CreatedAt) VALUES('LedgerEntries','[1]',UTC_TIMESTAMP(6));
                """);
            async Task<string> Rows() => JsonSerializer.Serialize(new
            {
                entries = await db.LedgerEntries.AsNoTracking().OrderBy(e => e.Id).ToListAsync(),
                transactions = await db.LedgerTransactions.AsNoTracking().OrderBy(t => t.Id).ToListAsync(),
                state = await db.SyncStates.AsNoTracking().OrderBy(s => s.Key).ToListAsync(),
                queue = await db.SyncChanges.AsNoTracking().OrderBy(s => s.Id).ToListAsync(),
            });
            var before = await Rows();
            await migrator.MigrateAsync(db.Database.GetMigrations().Single(m => m.EndsWith("_PerformanceIndexes")));
            Assert.Equal(before, await Rows());
            await migrator.MigrateAsync(previous);
            Assert.Equal(before, await Rows());
            await migrator.MigrateAsync(db.Database.GetMigrations().Single(m => m.EndsWith("_PerformanceIndexes")));
            Assert.Equal(before, await Rows());
        }
        finally { await db.Database.EnsureDeletedAsync(); }
    }
}
