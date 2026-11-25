using System.ComponentModel.DataAnnotations;
using AlShifa.Api.Features.Auth;
using AlShifa.Api.Features.Finance;
using AlShifa.Api.Features.Inventory;
using AlShifa.Api.Features.Visits;
using AlShifa.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace AlShifa.Api.Features.Clinical;

public sealed record CreateSurgeryRequest(long VisitId, [Required, MaxLength(200)] string SurgeryType, EyeSide Eye, int? ServiceId = null, int? PackageId = null, int? OperationDefinitionId = null,
    [Range(0.01, 100000000)] decimal? Amount = null, SettlementDiscount? Discount = null);

public sealed record SaveSurgeryRequest(
    DateTime? ScheduledAt,
    [MaxLength(2000)] string? PreOpChecks,
    bool ConsentRecorded,
    [MaxLength(4000)] string? OperationReport,
    [MaxLength(2000)] string? PostOpStatus,
    int Version,
    [MaxLength(200)] string? SurgeryType = null,
    EyeSide? Eye = null,
    [MaxLength(500)] string? AmendReason = null);

public sealed record ChangeSurgeryStatusRequest(SurgeryStatus Status, int Version);

public sealed record SurgeryRow(long Id, string CaseNumber, long PatientId, string PatientFileNumber, string PatientName, long VisitId, string SurgeryType, string Eye, DateTime? ScheduledAt, string Status, int Version, IReadOnlyList<string> NextStatuses, int? QueueNumber = null);

public sealed record SurgeryDetail(SurgeryRow Case, string? PreOpChecks, bool ConsentRecorded, string? OperationReport, string? PostOpStatus, int? PackageId = null, string? PackageName = null, DateTime? SuppliesRecordedAt = null);

public static class SurgeryEndpoints
{
    // Clinical gates such as required consent are intentionally not enforced until confirmed (WORKFLOWS.md M-4).
    private static readonly Dictionary<SurgeryStatus, SurgeryStatus[]> Allowed = new()
    {
        [SurgeryStatus.Proposed] = [SurgeryStatus.Scheduled, SurgeryStatus.Cancelled],
        [SurgeryStatus.Scheduled] = [SurgeryStatus.InProgress, SurgeryStatus.Cancelled],
        [SurgeryStatus.InProgress] = [SurgeryStatus.Completed],
        [SurgeryStatus.Completed] = [SurgeryStatus.FollowUp, SurgeryStatus.Closed],
        [SurgeryStatus.FollowUp] = [SurgeryStatus.Closed],
        [SurgeryStatus.Closed] = [],
        [SurgeryStatus.Cancelled] = [],
    };

    public static void MapSurgeries(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/surgeries").WithTags("Surgery");

        group.MapGet("/", async (AppDbContext db, SurgeryStatus? status, bool? open, DateOnly? date, int page = 1, int pageSize = 50, CancellationToken ct = default) =>
        {
            pageSize = Math.Clamp(pageSize, 1, 100);
            page = Math.Max(page, 1);

            var query = db.SurgeryCases.AsNoTracking();
            if (status is { } s)
            {
                query = query.Where(c => c.Status == s);
            }

            // The desk's lists stay small: open cases, or the cases finished on one day.
            if (open == true)
            {
                query = query.Where(c => c.Status != SurgeryStatus.Completed && c.Status != SurgeryStatus.FollowUp && c.Status != SurgeryStatus.Closed && c.Status != SurgeryStatus.Cancelled);
            }
            else if (open == false)
            {
                query = query.Where(c => c.Status == SurgeryStatus.Completed || c.Status == SurgeryStatus.FollowUp || c.Status == SurgeryStatus.Closed);
            }

            if (date is { } day)
            {
                var (from, to) = DiagnosticEndpoints.KabulDay(day);
                query = query.Where(c => c.ScheduledAt != null ? c.ScheduledAt >= from && c.ScheduledAt < to : c.Visit!.VisitDate == day);
            }

            var total = await query.CountAsync(ct);
            if (open == false)
            {
                var done = await Project(query.OrderByDescending(c => c.Id).Skip((page - 1) * pageSize).Take(pageSize)).ToListAsync(ct);
                return Results.Ok(new PagedResult<SurgeryRow>(done.Select(ToRow).ToList(), total, page, pageSize));
            }

            var rows = await Project(query.OrderByDescending(c => c.Status == SurgeryStatus.InProgress)
                .ThenBy(c => db.QueueAttendances.Any(a => a.Day == c.Visit!.VisitDate && a.Desk == Calls.CallDesk.Surgery && a.EntryKey == "v:" + c.VisitId && !a.Present))
                .ThenBy(c => c.Visit!.QueueShift == QueueShift.Afternoon).ThenBy(c => c.Visit!.QueueNumber).ThenBy(c => c.Id).Skip((page - 1) * pageSize).Take(pageSize)).ToListAsync(ct);
            return Results.Ok(new PagedResult<SurgeryRow>(rows.Select(ToRow).ToList(), total, page, pageSize));
        }).RequirePermission("surgery.view");

        // Clinical detail needs edit rights: viewers such as the manager only see status and schedule.
        group.MapGet("/{id:long}", async (long id, AppDbContext db, CancellationToken ct) =>
        {
            var surgery = await db.SurgeryCases.AsNoTracking().SingleOrDefaultAsync(c => c.Id == id, ct) ?? throw AppException.NotFound();
            var row = await Project(db.SurgeryCases.AsNoTracking().Where(c => c.Id == id)).SingleAsync(ct);
            var packageName = surgery.PackageId is { } packageId ? await db.SurgeryPackages.Where(p => p.Id == packageId).Select(p => p.Name).SingleAsync(ct) : null;
            return Results.Ok(new SurgeryDetail(ToRow(row), surgery.PreOpChecks, surgery.ConsentRecorded, surgery.OperationReport, surgery.PostOpStatus, surgery.PackageId, packageName, surgery.SuppliesRecordedAt));
        }).RequirePermission("surgery.edit");

        group.MapPost("/", async (CreateSurgeryRequest request, AppDbContext db, ClinicalAccess access, Idempotency idempotency, NumberService numbers, InvoiceService invoices, LedgerService ledger, StockService stock, CurrentUser user, AuditService audit, Outbox outbox, CancellationToken ct) =>
            await idempotency.RunAsync(request, async () =>
            {
                var patientId = await ClinicalEndpoints.PatientOfVisitAsync(db, request.VisitId, ct);
                await access.EnsureResponsibleAsync(request.VisitId, ct);

                // A package brings its own fee: the patient pays the package and nothing more for its supplies.
                OperationDefinition? operation = null;
                if (request.OperationDefinitionId is { } operationId)
                {
                    operation = await db.OperationDefinitions.Include(o => o.Packages).ThenInclude(p => p.Package).ThenInclude(p => p!.Items)
                        .SingleOrDefaultAsync(o => o.Id == operationId && o.IsActive, ct)
                        ?? throw new AppException(400, "unknown_operation", "عملیات انتخاب‌شده فعال نیست.");
                    if (request.ServiceId is not null || request.PackageId is not null) throw new AppException(400, "invalid_operation", "فقط عملیات تعریف‌شده را انتخاب کنید.");
                    if (operation.Packages.Any(p => !p.Package!.IsActive)) throw new AppException(409, "inactive_package", "یکی از پکیج‌های عملیات غیرفعال است.");
                }
                var serviceIdOrPackage = operation?.ServiceId ?? request.ServiceId;
                if (request.PackageId is { } packageId)
                {
                    serviceIdOrPackage = await db.SurgeryPackages.Where(p => p.Id == packageId && p.IsActive).Select(p => (int?)p.ServiceId).SingleOrDefaultAsync(ct)
                        ?? throw new AppException(StatusCodes.Status400BadRequest, "unknown_package", "پکیج انتخاب‌شده معتبر یا فعال نیست.");
                }

                // Left without a fee, the case would have nothing to check before it starts. A waiver goes through an approved discount.
                if (serviceIdOrPackage is not { } serviceId)
                {
                    throw new AppException(StatusCodes.Status400BadRequest, "service_required", "برای عملیات باید خدمت قابل پرداخت انتخاب شود.");
                }

                if (!await db.Services.AnyAsync(s => s.Id == serviceId && s.IsActive, ct))
                {
                    throw new AppException(StatusCodes.Status400BadRequest, "unknown_service", "خدمت انتخاب‌شده معتبر نیست.");
                }

                var tariff = await FinanceEndpoints.CurrentPriceAsync(db, serviceId, ct);
                var price = request.Amount ?? tariff;
                Money.EnsureCents(price);
                if (price <= 0)
                {
                    throw new AppException(StatusCodes.Status409Conflict, "no_price", "برای این خدمت قیمت تعیین نشده است.");
                }

                var surgery = new SurgeryCase
                {
                    CaseNumber = await numbers.NextNumberAsync("surgery", ct),
                    PatientId = patientId,
                    VisitId = request.VisitId,
                    SurgeryType = operation?.Name ?? request.SurgeryType.Trim(),
                    OperationDefinitionId = operation?.Id,
                    Eye = request.Eye,
                    ResponsibleUserId = user.Id,
                    PackageId = request.PackageId,
                };
                db.SurgeryCases.Add(surgery);
                await db.SaveChangesAsync(ct);

                if (operation is not null)
                {
                    var plan = operation.Packages.SelectMany(p => p.Package!.Items.Select(i => new { i.ProductId, p.Package.LocationId, Quantity = i.Quantity * p.Quantity }))
                        .GroupBy(p => new { p.ProductId, p.LocationId });
                    foreach (var line in plan)
                    {
                        var quantity = line.Sum(p => p.Quantity);
                        if (quantity <= 0 || decimal.Round(quantity, 3) != quantity)
                            throw new AppException(400, "invalid_package_quantity", "مقدار مصرف پکیج باید با دقت سه رقم اعشار قابل ثبت باشد.");
                        db.SurgerySupplyPlans.Add(new SurgerySupplyPlan { SurgeryCaseId = surgery.Id, ProductId = line.Key.ProductId, LocationId = line.Key.LocationId, Quantity = line.Sum(p => p.Quantity) });
                    }
                }
                audit.Add("SurgeryProposed", "SurgeryCase", surgery.Id.ToString(), $"Amount: {price:F2}; tariff: {tariff:F2}");
                outbox.Add("SurgeryScheduled", new { surgeryId = surgery.Id }, Groups.Surgery, Groups.Doctor);
                await db.SaveChangesAsync(ct);

                // Its own invoice, so its payment can be checked on its own.
                var invoice = await invoices.CreateAsync(patientId, request.VisitId, ct);
                await invoices.AddLineAsync(invoice, new ChargeLine(surgery.SurgeryType, 1, price, Accounts.ServiceRevenue, serviceId, nameof(SurgeryCase), surgery.Id), ct);
                if (request.Discount is { } discount)
                {
                    var amount = FinanceEndpoints.DiscountAmount(invoice, discount.Amount, discount.Mode);
                    await FinanceEndpoints.ApplyDiscountAsync(invoice, amount, discount.Amount, discount.Mode, "تخفیف داکتر هنگام تجویز عملیات", db, ledger, audit, outbox, ct);
                }

                await OperationCatalog.ConsumePaidAsync(invoice, db, stock, ledger, user, audit, outbox, ct);

                var row = await Project(db.SurgeryCases.Where(c => c.Id == surgery.Id)).SingleAsync(ct);
                return (StatusCodes.Status201Created, ToRow(row));
            }, ct)).RequirePermission("surgery.create");

        group.MapPut("/{id:long}", async (long id, SaveSurgeryRequest request, AppDbContext db, ClinicalAccess access, CurrentUser user, AuditService audit, CancellationToken ct) =>
        {
            await using var tx = await db.BeginWorkflowTransactionAsync(ct);
            var surgery = await db.SurgeryCases.SingleOrDefaultAsync(c => c.Id == id, ct) ?? throw AppException.NotFound();
            db.Entry(surgery).Property(c => c.Version).OriginalValue = request.Version;
            await access.EnsureResponsibleAsync(surgery.VisitId, ct, surgery.ResponsibleUserId);
            var amendReason = Text.Clean(request.AmendReason);
            if (surgery.Status == SurgeryStatus.Cancelled || (surgery.Status == SurgeryStatus.Closed && amendReason is null))
            {
                throw new AppException(StatusCodes.Status409Conflict, "case_closed", "این پرونده بسته شده است. برای تغییر پرونده بسته، دلیل اصلاح را بنویسید.");
            }

            // Which operation and which eye can change only until the case begins. A wrong eye after that is an incident, not an edit.
            var type = Text.Clean(request.SurgeryType) ?? surgery.SurgeryType;
            var eye = request.Eye ?? surgery.Eye;
            if (type != surgery.SurgeryType || eye != surgery.Eye)
            {
                if (surgery.Status is not (SurgeryStatus.Proposed or SurgeryStatus.Scheduled))
                {
                    throw new AppException(StatusCodes.Status409Conflict, "case_started", "عملیات آغاز شده است و نوع عملیات یا چشم تغییر نمی‌کند.");
                }

                Amendments.AddChanges(db, nameof(SurgeryCase), id,
                [
                    (nameof(surgery.SurgeryType), surgery.SurgeryType, type),
                    (nameof(surgery.Eye), surgery.Eye.ToString(), eye.ToString()),
                ], amendReason ?? Amendments.BeforeFinal, user.Id);

                // The invoice names the operation it charges for. Only the words change; the amount and ledger stay.
                var charge = await db.InvoiceItems.SingleOrDefaultAsync(i => i.SourceType == nameof(SurgeryCase) && i.SourceId == id && !i.IsVoid, ct);
                if (charge is not null)
                {
                    charge.Description = type;
                }

                surgery.SurgeryType = type;
                surgery.Eye = eye;
            }

            if (amendReason is not null)
            {
                Amendments.AddChanges(db, nameof(SurgeryCase), id,
                [
                    (nameof(surgery.PreOpChecks), surgery.PreOpChecks, Text.Clean(request.PreOpChecks)),
                    (nameof(surgery.OperationReport), surgery.OperationReport, Text.Clean(request.OperationReport)),
                    (nameof(surgery.PostOpStatus), surgery.PostOpStatus, Text.Clean(request.PostOpStatus)),
                ], amendReason, user.Id);
            }

            // Reporting an operation before it started skips the start step, so the same check applies.
            if (surgery.Status is SurgeryStatus.Proposed or SurgeryStatus.Scheduled && (Text.Clean(request.OperationReport) ?? Text.Clean(request.PostOpStatus)) is not null)
            {
                await DiagnosticGate.EnsureChargePaidAsync(db, nameof(SurgeryCase), id, ct);
            }

            surgery.ScheduledAt = request.ScheduledAt;
            surgery.PreOpChecks = Text.Clean(request.PreOpChecks);
            surgery.ConsentRecorded = request.ConsentRecorded;
            surgery.OperationReport = Text.Clean(request.OperationReport);
            surgery.PostOpStatus = Text.Clean(request.PostOpStatus);
            audit.Add("SurgeryEdited", "SurgeryCase", id.ToString(), amendReason);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return Results.NoContent();
        }).RequirePermission("surgery.edit");

        group.MapPost("/{id:long}/status", async (long id, ChangeSurgeryStatusRequest request, AppDbContext db, ClinicalAccess access, InvoiceService invoices, AuditService audit, Outbox outbox, HttpContext http, CancellationToken ct) =>
        {
            var needed = request.Status == SurgeryStatus.Cancelled ? "surgery.cancel" : "surgery.approve";
            if (!http.User.HasClaim(RolePermissions.ClaimType, needed))
            {
                throw new AppException(StatusCodes.Status403Forbidden, "forbidden", "شما اجازه این عملیات را ندارید.");
            }

            // Read before the transaction, so it does not fix the snapshot ahead of the visit lock. A case never changes visit.
            var caseRow = await db.SurgeryCases.AsNoTracking().Where(c => c.Id == id).Select(c => new { c.VisitId }).SingleOrDefaultAsync(ct) ?? throw AppException.NotFound();
            await using var tx = await db.BeginWorkflowTransactionAsync(ct);
            var visit = await VisitService.LockAsync(db, caseRow.VisitId, ct);

            var surgery = await db.SurgeryCases.SingleOrDefaultAsync(c => c.Id == id, ct) ?? throw AppException.NotFound();
            db.Entry(surgery).Property(c => c.Version).OriginalValue = request.Version;
            await access.EnsureResponsibleAsync(surgery.VisitId, ct, surgery.ResponsibleUserId);
            if (!Allowed[surgery.Status].Contains(request.Status))
            {
                throw new AppException(StatusCodes.Status409Conflict, "invalid_transition", "این تغییر وضعیت مجاز نیست.");
            }

            if (request.Status == SurgeryStatus.Scheduled && surgery.ScheduledAt is null)
            {
                throw new AppException(StatusCodes.Status409Conflict, "schedule_required", "ابتدا تاریخ عملیات را ثبت کنید.");
            }

            if (request.Status == SurgeryStatus.InProgress)
            {
                ClinicalEndpoints.EnsureNotCancelled(visit);
                await DiagnosticGate.EnsureChargePaidAsync(db, nameof(SurgeryCase), id, ct);
            }
            else if (request.Status == SurgeryStatus.Completed && surgery.PackageId is not null && surgery.SuppliesRecordedAt is null)
            {
                // The package's supplies must leave stock before the case is finished, or the store would never learn of them.
                throw new AppException(StatusCodes.Status409Conflict, "supplies_required", "ابتدا مواد مصرفی پکیج را در پرونده عملیات ثبت کنید تا از گدام کسر شود.");
            }
            else if (request.Status == SurgeryStatus.Cancelled)
            {
                // Otherwise the fee of a cancelled case stays on the unpaid list and can still be charged.
                await invoices.CancelChargeAsync(nameof(SurgeryCase), id, ct);
            }

            surgery.Status = request.Status;
            audit.Add("SurgeryStatusChanged", "SurgeryCase", id.ToString(), request.Status.ToString());
            outbox.Add("SurgeryScheduled", new { surgeryId = id }, Groups.Surgery, Groups.Doctor);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return Results.NoContent();
        }).RequireAuthorization();
    }

    private sealed record Projection(long Id, string CaseNumber, long PatientId, string FileNumber, string FirstName, string? LastName, long VisitId, string SurgeryType, EyeSide Eye, DateTime? ScheduledAt, SurgeryStatus Status, int Version, int? QueueNumber);

    private static IQueryable<Projection> Project(IQueryable<SurgeryCase> cases) => cases.Select(c => new Projection(
        c.Id, c.CaseNumber, c.PatientId, c.Patient!.FileNumber, c.Patient.FirstName, c.Patient.LastName, c.VisitId, c.SurgeryType, c.Eye, c.ScheduledAt, c.Status, c.Version, c.Visit!.QueueNumber));

    private static SurgeryRow ToRow(Projection c) => new(
        c.Id, c.CaseNumber, c.PatientId, c.FileNumber, $"{c.FirstName} {c.LastName}".Trim(), c.VisitId, c.SurgeryType, c.Eye.ToString(), c.ScheduledAt, c.Status.ToString(), c.Version,
        Allowed[c.Status].Select(s => s.ToString()).ToList(), c.QueueNumber);
}
