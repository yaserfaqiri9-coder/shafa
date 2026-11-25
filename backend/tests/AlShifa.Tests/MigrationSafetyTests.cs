using System.Text.Json;
using AlShifa.Api.Features.Clinical;
using AlShifa.Api.Features.Visits;
using AlShifa.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AlShifa.Tests;

/// <summary>Rehearses the upgrade to Refractions and VisitTokens on a throwaway database holding records from before them.</summary>
public sealed class MigrationSafetyTests
{
    [Fact]
    public async Task Physical_card_migration_preserves_legacy_duplicate_rounds_and_sync_progress()
    {
        var connection = ConnectionString();
        Assert.True(DatabaseSafety.IsDisposable(connection));
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseMySQL(connection).Options);
        await db.Database.EnsureDeletedAsync();
        try {
            await db.GetService<IMigrator>().MigrateAsync("20261122151006_OperationsAndStaffPayroll");
            var patient = new AlShifa.Api.Features.Patients.Patient { FileNumber = "legacy-card", FirstName = "Synthetic", CreatedByUserId = 1 };
            var department = new AlShifa.Api.Features.Users.Department { Code = "card-test", Name = "Synthetic", IsActive = true };
            db.AddRange(patient, department);
            await db.SaveChangesAsync();
            db.Visits.AddRange(
                new Visit { VisitNumber = "legacy-card-a", PatientId = patient.Id, DepartmentId = department.Id, VisitDate = KabulClock.Today, QueueNumber = 3, QueueShift = QueueShift.Morning, QueueRound = 1, Status = VisitStatus.Completed, CreatedByUserId = 1 },
                new Visit { VisitNumber = "legacy-card-b", PatientId = patient.Id, DepartmentId = department.Id, VisitDate = KabulClock.Today, QueueNumber = 3, QueueShift = QueueShift.Afternoon, QueueRound = 2, Status = VisitStatus.Completed, CreatedByUserId = 1 });
            db.SyncStates.AddRange(
                new AlShifa.Api.Features.Sync.SyncState { Key = "push.seq", Value = "12", UpdatedAt = DateTime.UtcNow },
                new AlShifa.Api.Features.Sync.SyncState { Key = "push.ackedSeq", Value = "10", UpdatedAt = DateTime.UtcNow },
                new AlShifa.Api.Features.Sync.SyncState { Key = "push.enabled", Value = "true", UpdatedAt = DateTime.UtcNow });
            db.SyncChanges.Add(new() { TableName = "Patients", KeyJson = $"[{patient.Id}]", CreatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
            var before = JsonSerializer.Serialize(await db.Visits.AsNoTracking().OrderBy(v => v.Id).ToListAsync());
            var progress = JsonSerializer.Serialize(await db.SyncStates.AsNoTracking().OrderBy(v => v.Key).ToListAsync());
            await db.Database.MigrateAsync();
            db.ChangeTracker.Clear();
            Assert.Equal(before, JsonSerializer.Serialize(await db.Visits.AsNoTracking().OrderBy(v => v.Id).ToListAsync()));
            Assert.Equal(progress, JsonSerializer.Serialize(await db.SyncStates.AsNoTracking().OrderBy(v => v.Key).ToListAsync()));
            Assert.Single(await db.QueueCards.Where(c => c.Day == KabulClock.Today && c.Number == 3).ToListAsync());
            Assert.Equal(1, await db.SyncChanges.CountAsync());
            Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        } finally { await db.Database.EnsureDeletedAsync(); }
    }

    private const string BeforeNewFeatures = "20261101213721_SettingsAttachments";

    /// <summary>A database of its own, so the shared test database is not disturbed. Creating one needs the local root account.</summary>
    private static string ConnectionString()
    {
        var fromEnv = Environment.GetEnvironmentVariable("ALSHIFA_TEST_ADMIN_DB");
        if (!string.IsNullOrEmpty(fromEnv))
        {
            return fromEnv;
        }

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, ".tools", "dev-secrets.json")))
        {
            dir = dir.Parent;
        }

        if (dir is null)
        {
            throw new InvalidOperationException("Set ALSHIFA_TEST_ADMIN_DB or run scripts/dev-db.ps1 first.");
        }

        using var secrets = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir.FullName, ".tools", "dev-secrets.json")));
        return $"Server=127.0.0.1;Port=3307;Database=alshifa_migration_test;User=root;Password={secrets.RootElement.GetProperty("root").GetString()};";
    }

    private static Task<int> CountAsync(AppDbContext db, string table) =>
        db.Database.SqlQueryRaw<int>($"SELECT COUNT(*) AS Value FROM {table}").SingleAsync();

    [Fact]
    public async Task Current_online_schema_upgrades_without_losing_the_patient_or_replication_history()
    {
        var connection = ConnectionString();
        Assert.True(DatabaseSafety.IsDisposable(connection));
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseMySQL(connection).Options);
        await db.Database.EnsureDeletedAsync();
        try
        {
            await db.GetService<IMigrator>().MigrateAsync("20261120153000_QueueShiftNumbering");
            await db.Database.ExecuteSqlRawAsync("INSERT INTO Patients (Id, FileNumber, FirstName, Gender, CreatedAt, CreatedByUserId, Version) VALUES (1, '1', 'Synthetic legacy mirror patient', 'Male', '2026-11-20 11:00:00', 1, 7)");
            await db.Database.ExecuteSqlRawAsync("INSERT INTO SyncStates (`Key`,`Value`,UpdatedAt) VALUES ('apply.seq','1795172809252',UTC_TIMESTAMP(6)),('apply.lastAt','2026-11-20T11:06:49.8327637Z',UTC_TIMESTAMP(6))");
            await db.Database.MigrateAsync();
            Assert.Empty(await db.Database.GetPendingMigrationsAsync());
            Assert.Null(await DatabaseSafety.SchemaProblemAsync(db));
            var patient = await db.Patients.SingleAsync();
            Assert.Equal(("1", "Synthetic legacy mirror patient", 7), (patient.FileNumber, patient.FirstName, patient.Version));
            Assert.Equal("1795172809252", await db.SyncStates.Where(s => s.Key == "apply.seq").Select(s => s.Value).SingleAsync());
            Assert.Equal("2026-11-20T11:06:49.8327637Z", await db.SyncStates.Where(s => s.Key == "apply.lastAt").Select(s => s.Value).SingleAsync());
            Assert.False(await db.SyncStates.AnyAsync(s => s.Key == "apply.sourceId"));
        }
        finally { await db.Database.EnsureDeletedAsync(); }
    }

    [Fact]
    public async Task Records_survive_the_upgrade_and_a_hand_run_needs_the_backup_flag()
    {
        var connection = ConnectionString();
        Assert.True(DatabaseSafety.IsDisposable(connection));
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseMySQL(connection).Options);
        await db.Database.EnsureDeletedAsync();

        try
        {
            var migrator = db.GetService<IMigrator>();
            await migrator.MigrateAsync(BeforeNewFeatures);

            await db.Database.ExecuteSqlRawAsync("INSERT INTO Departments (Id, Code, Name, IsActive) VALUES (1, 'doctor', 'داکتر', 1)");
            await db.Database.ExecuteSqlRawAsync("INSERT INTO Patients (Id, FileNumber, FirstName, Gender, CreatedAt, CreatedByUserId, Version) VALUES (1, 'P-000001', 'مریض قدیمی', 'Male', UTC_TIMESTAMP(6), 1, 3)");
            await db.Database.ExecuteSqlRawAsync("INSERT INTO Staff (Id, FullName, IsDoctor, IsActive, Version) VALUES (1, 'داکتر قدیمی', 1, 1, 0)");
            await db.Database.ExecuteSqlRawAsync("INSERT INTO Visits (Id, VisitNumber, PatientId, DepartmentId, DoctorStaffId, Status, VisitDate, QueueNumber, CreatedAt, CreatedByUserId, Version) VALUES (1, 'V-0000001', 1, 1, 1, 'InConsultation', '2026-11-01', 7, UTC_TIMESTAMP(6), 1, 2)");
            await db.Database.ExecuteSqlRawAsync("INSERT INTO Visits (Id, VisitNumber, PatientId, DepartmentId, DoctorId, Status, VisitDate, QueueNumber, CreatedAt, CreatedByUserId, Version) VALUES (2, 'V-0000002', 1, 1, 1, 'Completed', '2026-11-01', 8, UTC_TIMESTAMP(6), 1, 0)");
            await db.Database.ExecuteSqlRawAsync("INSERT INTO Appointments (Id, PatientId, DoctorStaffId, ScheduledAt, Status, CreatedByUserId, CreatedAt, Version) VALUES (1, 1, 1, '2026-12-01 05:00:00', 'Scheduled', 1, UTC_TIMESTAMP(6), 0)");
            await db.Database.ExecuteSqlRawAsync("INSERT INTO DiagnosticTests (Id, Name, IsActive) VALUES (1, 'آزمایش قدیمی', 1)");
            await db.Database.ExecuteSqlRawAsync("INSERT INTO DiagnosticOrders (Id, OrderNumber, PatientId, VisitId, TestId, RequestedByUserId, Status, CreatedAt, Version) VALUES (1, 'LAB-0000001', 1, 1, 1, 1, 'Requested', UTC_TIMESTAMP(6), 0)");
            await db.Database.ExecuteSqlRawAsync("""
                INSERT INTO Products (Id, Code, Name, Kind, Unit, ReorderLevel, SalePrice, IsActive, Version) VALUES
                (1, '1', 'دوای کوددار', 'Drug', 'دانه', 0, 10, 1, 0),
                (2, '10', 'دوای خارج دواخانه', 'Drug', 'دانه', 0, 10, 1, 0),
                (3, 'OS-0a1b2c3d4e5f', 'دوای برگه', 'Drug', 'دانه', 0, 10, 1, 0),
                (4, '3', 'دوای دواخانه دیگر', 'Drug', 'دانه', 0, 10, 1, 0)
                """);
            await db.Database.ExecuteSqlRawAsync("INSERT INTO StockLocations (Id, Code, Name) VALUES (99, 'pharmacy', 'دواخانه')");
            await db.Database.ExecuteSqlRawAsync("""
                INSERT INTO StockBatches (Id, ProductId, LocationId, LotNumber, ExpiryDate, Quantity, UnitCost) VALUES
                (1, 1, 99, 'test-1', NULL, 1, 1),
                (3, 3, 99, 'test-3', NULL, 1, 1),
                (4, 4, 99, 'test-4', NULL, 1, 1)
                """);

            // A database with records is not changed by a hand run, and the running application would say why.
            await Assert.ThrowsAsync<InvalidOperationException>(() => DatabaseSafety.EnsureOperatorMayMigrateAsync(db, backupTaken: false));
            Assert.Contains("missing", await DatabaseSafety.SchemaProblemAsync(db));
            Assert.Contains(BeforeNewFeatures, await db.Database.GetAppliedMigrationsAsync());
            Assert.NotEmpty(await db.Database.GetPendingMigrationsAsync());

            await DatabaseSafety.EnsureOperatorMayMigrateAsync(db, backupTaken: true);
            await db.Database.MigrateAsync();
            Assert.Null(await DatabaseSafety.SchemaProblemAsync(db));

            var patient = await db.Patients.AsNoTracking().SingleAsync();
            Assert.Equal(("1", "مریض قدیمی", 3), (patient.FileNumber, patient.FirstName, patient.Version));

            // Pharmacy stock gets codes from 10; a non-pharmacy product occupying one of those codes moves after the sequence.
            Assert.Equal(["1", "2"], await db.Visits.OrderBy(v => v.Id).Select(v => v.VisitNumber).ToListAsync());
            Assert.Equal("1", await db.DiagnosticOrders.Select(o => o.OrderNumber).SingleAsync());
            Assert.Equal(["10", "13", "11", "12"], await db.Products.OrderBy(p => p.Id).Select(p => p.Code).ToListAsync());
            Assert.Equal([1, 3, 4], await db.StockBatches.OrderBy(b => b.Id).Select(b => b.ProductId).ToListAsync());
            Assert.Equal(13L, await db.Database.SqlQueryRaw<long>("SELECT Value AS Value FROM NumberSequences WHERE Scope = 'code:product' AND Period = ''").SingleAsync());

            var visit = await db.Visits.AsNoTracking().SingleAsync(v => v.Id == 1);
            Assert.Equal((VisitStatus.InConsultation, 7, 2), (visit.Status, visit.QueueNumber, visit.Version));

            // Both old ways of naming a doctor end up on the one real link, which now resolves to the person.
            Assert.Equal("داکتر قدیمی", await db.Visits.Where(v => v.Id == 1).Select(v => v.Doctor!.FullName).SingleAsync());
            Assert.Equal(1, await db.Visits.Where(v => v.Id == 2).Select(v => v.DoctorStaffId).SingleAsync());
            Assert.Equal("داکتر قدیمی", await db.Appointments.Select(a => a.Doctor!.FullName).SingleAsync());

            // What existed before keeps meaning what it meant: an ordinary lab test, with no prepaid gate and no invoice of its own.
            Assert.Equal(DiagnosticKind.Lab, (await db.DiagnosticTests.AsNoTracking().SingleAsync()).Kind);
            var order = await db.DiagnosticOrders.AsNoTracking().SingleAsync();
            Assert.Equal((DiagnosticStatus.Requested, false, (long?)null), (order.Status, order.ReturnToDoctor, order.InvoiceId));
            Assert.Equal(0, await CountAsync(db, "VisitTokens"));
            Assert.Equal(0, await CountAsync(db, "Refractions"));

            // Queue-round migrations intentionally refuse a lossy Down. Preserve that protection;
            // recovery uses the updater's verified backup rather than collapsing distinct rounds.
            // Inspect the migration's actual operation builder before asking EF to acquire its
            // server-wide lock. The provider leaks that lock when building a rejected Down.
            Assert.Throws<NotSupportedException>(() =>
                new AlShifa.Api.Infrastructure.Migrations.QueueRoundColumns().DownOperations);
            Assert.Equal(1, await CountAsync(db, "Patients"));
            Assert.Equal(2, await CountAsync(db, "Visits"));
            Assert.Equal(1, await CountAsync(db, "DiagnosticOrders"));

            await db.Database.MigrateAsync();
            Assert.Null(await DatabaseSafety.SchemaProblemAsync(db));
            Assert.Equal(1, await CountAsync(db, "Patients"));
        }
        finally
        {
            await db.Database.EnsureDeletedAsync();
        }
    }
}
