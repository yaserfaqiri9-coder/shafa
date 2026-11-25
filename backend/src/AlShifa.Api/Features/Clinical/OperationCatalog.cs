using System.ComponentModel.DataAnnotations;
using AlShifa.Api.Features.Finance;
using AlShifa.Api.Features.Inventory;
using AlShifa.Api.Features.Auth;
using AlShifa.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace AlShifa.Api.Features.Clinical;

public class OperationDefinition
{
    public int Id { get; set; }
    [MaxLength(150)] public string Name { get; set; } = "";
    public int ServiceId { get; set; }
    public Service? Service { get; set; }
    public bool IsActive { get; set; } = true;
    public int Version { get; set; }
    public List<OperationPackage> Packages { get; set; } = [];
}

public class OperationPackage
{
    public long Id { get; set; }
    public int OperationDefinitionId { get; set; }
    public int PackageId { get; set; }
    public SurgeryPackage? Package { get; set; }
    public decimal Quantity { get; set; }
}

// Snapshot at referral: later catalog edits must not change what an existing case consumes.
public class SurgerySupplyPlan
{
    public long Id { get; set; }
    public long SurgeryCaseId { get; set; }
    public SurgeryCase? SurgeryCase { get; set; }
    public int ProductId { get; set; }
    public Inventory.Product? Product { get; set; }
    public int LocationId { get; set; }
    public Inventory.StockLocation? Location { get; set; }
    public decimal Quantity { get; set; }
}

public sealed record OperationPackageInput(int PackageId, [Range(0.001, 100000)] decimal Quantity);
public sealed record SaveOperationRequest([Required, MaxLength(150)] string Name, [Range(0.01, 100000000)] decimal Price,
    OperationPackageInput[] Packages, bool IsActive = true, int? Version = null);

public static class OperationCatalog
{
    public static void MapOperations(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/operations");
        group.MapGet("/", async (AppDbContext db, bool all = false, CancellationToken ct = default) =>
        {
            var now = DateTime.UtcNow;
            return Results.Ok(await db.OperationDefinitions.AsNoTracking().Where(o => all || o.IsActive).OrderBy(o => o.Name)
                .Select(o => new { o.Id, o.Name, o.ServiceId, o.IsActive, o.Version,
                    Price = db.ServicePrices.Where(p => p.ServiceId == o.ServiceId && p.EffectiveFrom <= now).OrderByDescending(p => p.EffectiveFrom).ThenByDescending(p => p.Id).Select(p => (decimal?)p.Price).FirstOrDefault(),
                    Packages = o.Packages.Select(p => new { p.PackageId, Name = p.Package!.Name, p.Quantity }).ToList() }).ToListAsync(ct));
        }).RequirePermission("surgery.view");

        group.MapPost("/", async (SaveOperationRequest r, AppDbContext db, NumberService numbers, CurrentUser user, AuditService audit, CancellationToken ct) =>
        {
            await using var tx = await db.BeginWorkflowTransactionAsync(ct);
            var code = await ShortCodesAsync(db, numbers, ct);
            var service = new Service { Code = code, Name = r.Name.Trim(), IsActive = r.IsActive };
            db.Services.Add(service);
            await db.SaveChangesAsync(ct);
            var operation = new OperationDefinition { ServiceId = service.Id };
            db.OperationDefinitions.Add(operation);
            await ApplyAsync(operation, service, r, db, user, ct);
            audit.Add("OperationCreated", nameof(OperationDefinition), operation.Id.ToString());
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return Results.Created($"/api/operations/{operation.Id}", new { operation.Id });
        }).RequirePermission("surgery.approve");

        group.MapPut("/{id:int}", async (int id, SaveOperationRequest r, AppDbContext db, CurrentUser user, AuditService audit, CancellationToken ct) =>
        {
            await using var tx = await db.BeginWorkflowTransactionAsync(ct);
            var operation = (await db.OperationDefinitions.FromSqlInterpolated($"SELECT * FROM OperationDefinitions WHERE Id = {id} FOR UPDATE").ToListAsync(ct)).SingleOrDefault() ?? throw AppException.NotFound();
            if (r.Version != operation.Version) throw new DbUpdateConcurrencyException();
            db.Entry(operation).Property(o => o.Version).IsModified = true;
            await db.Entry(operation).Collection(o => o.Packages).LoadAsync(ct);
            var service = await db.Services.SingleAsync(s => s.Id == operation.ServiceId, ct);
            await ApplyAsync(operation, service, r, db, user, ct);
            audit.Add("OperationEdited", nameof(OperationDefinition), id.ToString());
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return Results.NoContent();
        }).RequirePermission("surgery.approve");

        // Removes an operation nobody has used, with its fee service and price history; one already referred or invoiced is only deactivated.
        group.MapDelete("/{id:int}", async (int id, int version, AppDbContext db, AuditService audit, CancellationToken ct) =>
        {
            var operation = await db.OperationDefinitions.Include(o => o.Packages).SingleOrDefaultAsync(o => o.Id == id, ct) ?? throw AppException.NotFound();
            db.Entry(operation).Property(o => o.Version).OriginalValue = version;
            var service = await db.Services.SingleAsync(s => s.Id == operation.ServiceId, ct);
            var prices = await db.ServicePrices.Where(p => p.ServiceId == service.Id).ToListAsync(ct);
            var used = await db.SurgeryCases.AnyAsync(c => c.OperationDefinitionId == id, ct) || await db.InvoiceItems.AnyAsync(i => i.ServiceId == service.Id, ct);
            var deleted = !used && await SafeDelete.TryRemoveAsync(db, service, ct, [.. operation.Packages, operation, .. prices]);
            if (!deleted)
            {
                operation.IsActive = service.IsActive = false;
            }

            audit.Add(deleted ? "OperationDeleted" : "OperationDeactivated", nameof(OperationDefinition), id.ToString(), operation.Name);
            await db.SaveChangesAsync(ct);
            return Results.Ok(new DeleteOutcome(deleted));
        }).RequirePermission("surgery.approve");
    }

    private static Task<string> ShortCodesAsync(AppDbContext db, NumberService numbers, CancellationToken ct) =>
        ShortCodes.NextAsync(numbers, "service", c => db.Services.AnyAsync(s => s.Code == c, ct), ct);

    private static async Task ApplyAsync(OperationDefinition operation, Service service, SaveOperationRequest r, AppDbContext db, CurrentUser user, CancellationToken ct)
    {
        Money.EnsureCents(r.Price);
        if (r.Price <= 0 || string.IsNullOrWhiteSpace(r.Name)) throw new AppException(400, "invalid_operation", "نام و قیمت عملیات الزامی است.");
        var packages = r.Packages ?? [];
        if (packages.Any(p => p.Quantity <= 0 || decimal.Round(p.Quantity, 3) != p.Quantity) || packages.Select(p => p.PackageId).Distinct().Count() != packages.Length)
            throw new AppException(400, "invalid_packages", "تعداد پکیج معتبر نیست یا تکراری است.");
        var ids = packages.Select(p => p.PackageId).ToList();
        if (await db.SurgeryPackages.CountAsync(p => ids.Contains(p.Id) && p.IsActive, ct) != ids.Count)
            throw new AppException(400, "unknown_package", "پکیج انتخاب‌شده فعال نیست.");
        operation.Name = service.Name = r.Name.Trim();
        operation.IsActive = service.IsActive = r.IsActive;
        db.OperationPackages.RemoveRange(operation.Packages);
        operation.Packages = packages.Select(p => new OperationPackage { PackageId = p.PackageId, Quantity = p.Quantity }).ToList();
        db.ServicePrices.Add(new ServicePrice { ServiceId = service.Id, Price = r.Price, EffectiveFrom = DateTime.UtcNow, CreatedByUserId = user.Id });
        await db.SaveChangesAsync(ct);
    }

    // Called inside the same locked invoice/payment transaction. Failure rolls back both money and stock.
    public static async Task ConsumePaidAsync(Invoice invoice, AppDbContext db, StockService stock, LedgerService ledger, CurrentUser user, AuditService audit, Outbox outbox, CancellationToken ct)
    {
        if (!InvoiceService.IsSettled(invoice)) return;
        var caseIds = await db.InvoiceItems.Where(i => i.InvoiceId == invoice.Id && !i.IsVoid && i.SourceType == nameof(SurgeryCase)).Select(i => i.SourceId!.Value).ToListAsync(ct);
        foreach (var id in caseIds.Distinct().Order())
        {
            var surgery = (await db.SurgeryCases.FromSqlInterpolated($"SELECT * FROM SurgeryCases WHERE Id = {id} FOR UPDATE").ToListAsync(ct)).Single();
            if (surgery.OperationDefinitionId is null || surgery.SuppliesRecordedAt is not null) continue;
            if (surgery.Status == SurgeryStatus.Cancelled) throw new AppException(409, "case_cancelled", "این عملیات لغو شده است.");
            var plan = await db.SurgerySupplyPlans.Where(p => p.SurgeryCaseId == id).OrderBy(p => p.ProductId).ThenBy(p => p.LocationId).ToListAsync(ct);
            foreach (var p in plan)
                await stock.TakeAsync(p.ProductId, p.LocationId, p.Quantity, nameof(SurgeryCase), id, MovementKind.Consumption, $"مصرف عملیات {surgery.CaseNumber}", ct);
            var taken = db.ChangeTracker.Entries<StockMovement>().Where(e => e.State == EntityState.Added && e.Entity.SourceType == nameof(SurgeryCase) && e.Entity.SourceId == id).Select(e => e.Entity).ToList();
            surgery.SuppliesRecordedAt = DateTime.UtcNow;
            surgery.SuppliesRecordedByUserId = user.Id;
            surgery.SuppliesLocationId = plan.Select(p => (int?)p.LocationId).Distinct().Count() == 1 ? plan[0].LocationId : null;
            await db.SaveChangesAsync(ct);
            foreach (var movement in taken)
            {
                var value = decimal.Round(Math.Abs(movement.Quantity) * movement.Batch!.UnitCost, 2, MidpointRounding.AwayFromZero);
                ledger.Post("Adjustment", "StockMovement", movement.Id, "مصرف پکیج پس از پرداخت عملیات", (Accounts.CostOfGoods, value, 0), (Accounts.Inventory, 0, value));
            }
            audit.Add("SurgerySuppliesRecorded", nameof(SurgeryCase), id.ToString(), "Paid operation packages");
            outbox.Add("InventoryChanged", new { surgeryId = id }, Groups.Inventory, Groups.Surgery);
            outbox.Add("SurgeryScheduled", new { surgeryId = id }, Groups.Surgery, Groups.Doctor);
            await db.SaveChangesAsync(ct);
        }
    }
}
