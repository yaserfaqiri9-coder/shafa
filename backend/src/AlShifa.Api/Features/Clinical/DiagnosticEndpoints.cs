using System.ComponentModel.DataAnnotations;
using AlShifa.Api.Features.Auth;
using AlShifa.Api.Features.Finance;
using AlShifa.Api.Features.Visits;
using AlShifa.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace AlShifa.Api.Features.Clinical;

public sealed record CreateDiagnosticOrderRequest(long PatientId, long? VisitId, int TestId, [MaxLength(500)] string? Note, bool ReturnToDoctor = true, int? ServiceId = null, SettlementDiscount? Discount = null);

public sealed record ChangeOrderStatusRequest(int Version);

public sealed record SaveResultRequest([Required, MaxLength(4000)] string ResultText, int Version);

public sealed record AmendResultRequest([Required, MaxLength(4000)] string ResultText, [Required, MinLength(3), MaxLength(500)] string Reason, int Version);

public sealed record EditOrderNoteRequest([MaxLength(500)] string? Note, int Version);

public sealed record SaveTestRequest([Required, MaxLength(150)] string Name, bool IsActive, int? ServiceId = null, DiagnosticKind Kind = DiagnosticKind.Lab);

public sealed record DiagnosticProjection(
    long Id, string OrderNumber, long PatientId, string FileNumber, string FirstName, string? LastName, long? VisitId, int TestId, string TestName,
    string? Note, DiagnosticStatus Status, DateTime CreatedAt, int Version, string? ResultText, DateTime? PerformedAt, DiagnosticKind Kind, bool ReturnToDoctor, bool? Paid, int? QueueNumber = null);

public sealed record DiagnosticOrderDto(
    long Id, string OrderNumber, long PatientId, string PatientFileNumber, string PatientName, long? VisitId, int TestId, string TestName,
    string? Note, string Status, DateTime CreatedAt, int Version, string? ResultText, DateTime? PerformedAt, string Kind, bool ReturnToDoctor, bool? Paid, int? QueueNumber = null);

/// <summary>The server-side rules around prepaid requests. Screens only reflect them.</summary>
public static class DiagnosticGate
{
    /// <summary>Open requests whose answer the doctor is waiting for.</summary>
    public static IQueryable<DiagnosticOrder> Awaited(AppDbContext db) =>
        db.DiagnosticOrders.Where(o => (o.Status == DiagnosticStatus.Requested || o.Status == DiagnosticStatus.InProgress)
            && (o.Test!.Kind == DiagnosticKind.Refraction || (o.Test!.Kind == DiagnosticKind.Imaging && o.ReturnToDoctor)));

    private static IQueryable<DiagnosticOrder> Pending(AppDbContext db, long visitId) =>
        Awaited(db).Where(o => o.VisitId == visitId);

    /// <summary>The doctor still needs these answers, so the exam and the visit cannot close over them.</summary>
    public static async Task EnsureNonePendingAsync(AppDbContext db, long visitId, CancellationToken ct)
    {
        var names = await Pending(db, visitId).Select(o => o.Test!.Name).ToListAsync(ct);
        if (names.Count > 0)
        {
            throw new AppException(StatusCodes.Status409Conflict, "pending_requests", $"این درخواست‌ها هنوز تکمیل نشده‌اند: {string.Join("، ", names)}. ابتدا تکمیل یا لغو کنید.");
        }
    }

    public static async Task EnsurePaidAsync(AppDbContext db, DiagnosticOrder order, CancellationToken ct)
    {
        var settled = order.InvoiceId is { } invoiceId && InvoiceService.IsSettled(await db.Invoices.AsNoTracking().SingleAsync(i => i.Id == invoiceId, ct));
        if (!settled)
        {
            throw new AppException(StatusCodes.Status409Conflict, "payment_required", "پیش از شروع، فیس این درخواست باید کامل پرداخت شود.");
        }

        if (order.VisitId is { } visitId && await db.Visits.AnyAsync(v => v.Id == visitId && v.Status == VisitStatus.Cancelled, ct))
        {
            throw new AppException(StatusCodes.Status409Conflict, "visit_closed", "این مراجعه لغو شده است.");
        }
    }

    /// <summary>For a service charged through its own line. No line means no fee. A line that shares an older invoice needs that whole invoice settled.</summary>
    public static async Task EnsureChargePaidAsync(AppDbContext db, string sourceType, long sourceId, CancellationToken ct)
    {
        var unpaid = await db.InvoiceItems.Where(i => i.SourceType == sourceType && i.SourceId == sourceId && !i.IsVoid)
            .Join(db.Invoices, i => i.InvoiceId, i => i.Id, (_, invoice) => invoice)
            .AnyAsync(i => i.Status == InvoiceStatus.Void || i.Total - i.Discount > i.Paid - i.Refunded, ct);
        if (unpaid)
        {
            throw new AppException(StatusCodes.Status409Conflict, "payment_required", "پیش از شروع، فیس این خدمت باید کامل پرداخت شود.");
        }
    }

    /// <summary>Withdraws the unpaid charged services of a visit being cancelled. A paid one stops the cancellation until it is refunded.</summary>
    public static async Task CloseOpenPrepaidAsync(AppDbContext db, InvoiceService invoices, AuditService audit, long visitId, CancellationToken ct)
    {
        var open = await db.DiagnosticOrders
            .Where(o => o.VisitId == visitId && o.InvoiceId != null && (o.Status == DiagnosticStatus.Requested || o.Status == DiagnosticStatus.InProgress))
            .ToListAsync(ct);

        foreach (var order in open)
        {
            var charge = await db.InvoiceItems.SingleOrDefaultAsync(i => i.SourceType == nameof(DiagnosticOrder) && i.SourceId == order.Id && !i.IsVoid, ct);
            if (charge is not null)
            {
                await invoices.ClosePrepaidAsync(charge, ct);
            }

            order.Status = DiagnosticStatus.Cancelled;
            audit.Add("DiagnosticCancelled", "DiagnosticOrder", order.Id.ToString());
        }

        var surgeries = await db.SurgeryCases.Where(c => c.VisitId == visitId && (c.Status == SurgeryStatus.Proposed || c.Status == SurgeryStatus.Scheduled)).ToListAsync(ct);
        foreach (var surgery in surgeries)
        {
            await invoices.CancelChargeAsync(nameof(SurgeryCase), surgery.Id, ct);
            surgery.Status = SurgeryStatus.Cancelled;
            audit.Add("SurgeryStatusChanged", "SurgeryCase", surgery.Id.ToString(), nameof(SurgeryStatus.Cancelled));
        }

        await invoices.CancelChargeAsync(nameof(Visit), visitId, ct);
    }

    /// <summary>Call after the order's change is saved. The patient returns once nothing the doctor is waiting for remains.</summary>
    public static async Task ReturnToDoctorAsync(AppDbContext db, long visitId, CurrentUser user, Outbox outbox, CancellationToken ct)
    {
        if (await Pending(db, visitId).AnyAsync(ct))
        {
            return;
        }

        // A request finished before Vision must not carry the patient past it.
        var visit = await db.Visits.SingleAsync(v => v.Id == visitId, ct);
        if (visit.Status is not (VisitStatus.InConsultation or VisitStatus.Registered or VisitStatus.Waiting or VisitStatus.InPreExam) && VisitWorkflow.CanMove(visit.Status, VisitStatus.ReadyForDoctor))
        {
            db.VisitStatusHistory.Add(new VisitStatusHistory { VisitId = visitId, FromStatus = visit.Status, ToStatus = VisitStatus.ReadyForDoctor, ChangedByUserId = user.Id });
            visit.Status = VisitStatus.ReadyForDoctor;
            outbox.Add("VisitStatusChanged", new { visitId }, VisitService.EventGroups);
            await db.SaveChangesAsync(ct);
        }
    }
}

public static class DiagnosticEndpoints
{
    public static void MapDiagnostics(this IEndpointRouteBuilder app)
    {
        var tests = app.MapGroup("/api/diagnostic-tests").WithTags("Diagnostics");

        tests.MapGet("/", async (AppDbContext db, CancellationToken ct) =>
            Results.Ok(await db.DiagnosticTests.AsNoTracking().OrderBy(t => t.Name).Select(t => new { t.Id, t.Name, t.IsActive, Kind = t.Kind.ToString(), t.ServiceId }).ToListAsync(ct)))
            .RequirePermission("diagnostics.view");

        // Settings managers see every test, without the clinical permission the list above asks for.
        tests.MapGet("/manage", async (AppDbContext db, CancellationToken ct) =>
            Results.Ok((await db.DiagnosticTests.AsNoTracking().OrderBy(t => t.Name).Select(t => new { t.Id, t.Name, t.IsActive, Kind = t.Kind.ToString(), t.ServiceId }).ToListAsync(ct))
                .Select(t => new { t.Id, t.Name, t.IsActive, t.Kind, t.ServiceId, IsSystem = Auth.Seeder.IsSystemTest(t.Name) })))
            .RequirePermission("settings.view");

        tests.MapPost("/", async (SaveTestRequest request, AppDbContext db, AuditService audit, CancellationToken ct) =>
        {
            if (request.Kind != DiagnosticKind.Lab && request.ServiceId is null)
            {
                throw new AppException(StatusCodes.Status400BadRequest, "service_required", "رفرکشن و عکس‌برداری باید تعرفه داشته باشند.");
            }

            if (request.ServiceId is { } linkedService && !await db.Services.AnyAsync(s => s.Id == linkedService && s.IsActive, ct))
            {
                throw new AppException(StatusCodes.Status400BadRequest, "unknown_service", "خدمت انتخاب‌شده معتبر نیست.");
            }

            var test = new DiagnosticTest { Name = request.Name.Trim(), IsActive = request.IsActive, ServiceId = request.ServiceId, Kind = request.Kind };
            db.DiagnosticTests.Add(test);
            audit.Add("DiagnosticTestCreated", "DiagnosticTest", null, test.Name);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/diagnostic-tests/{test.Id}", new { test.Id });
        }).RequirePermission("settings.edit");

        tests.MapPut("/{id:int}", async (int id, SaveTestRequest request, AppDbContext db, AuditService audit, CancellationToken ct) =>
        {
            var test = await db.DiagnosticTests.SingleOrDefaultAsync(t => t.Id == id, ct) ?? throw AppException.NotFound();
            if (Auth.Seeder.IsSystemTest(test.Name))
            {
                throw new AppException(StatusCodes.Status409Conflict, "system_test", "این مورد سیستمی است و قابل تغییر نیست.");
            }

            // Open requests were charged and routed by this kind.
            if (request.Kind != test.Kind)
            {
                throw new AppException(StatusCodes.Status400BadRequest, "immutable_kind", "نوع آزمایش پس از ساخت قابل تغییر نیست.");
            }

            if (test.Kind != DiagnosticKind.Lab && request.ServiceId is null)
            {
                throw new AppException(StatusCodes.Status400BadRequest, "service_required", "رفرکشن و عکس‌برداری باید تعرفه داشته باشند.");
            }

            if (request.ServiceId is { } linkedService && linkedService != test.ServiceId && !await db.Services.AnyAsync(s => s.Id == linkedService && s.IsActive, ct))
            {
                throw new AppException(StatusCodes.Status400BadRequest, "unknown_service", "خدمت انتخاب‌شده معتبر نیست.");
            }

            test.Name = request.Name.Trim();
            test.IsActive = request.IsActive;
            test.ServiceId = request.ServiceId;
            audit.Add("DiagnosticTestEdited", "DiagnosticTest", id.ToString());
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        }).RequirePermission("settings.edit");

        tests.MapDelete("/{id:int}", async (int id, AppDbContext db, AuditService audit, CancellationToken ct) =>
        {
            var test = await db.DiagnosticTests.SingleOrDefaultAsync(t => t.Id == id, ct) ?? throw AppException.NotFound();
            if (Auth.Seeder.IsSystemTest(test.Name))
            {
                throw new AppException(StatusCodes.Status409Conflict, "system_test", "این مورد سیستمی است و حذف نمی‌شود.");
            }

            return await SafeDelete.RemoveOrDeactivateAsync(db, audit, test, "DiagnosticTest", id.ToString(), test.Name, () => test.IsActive = false, ct);
        }).RequirePermission("settings.edit");

        var orders = app.MapGroup("/api/diagnostic-orders").WithTags("Diagnostics");

        orders.MapGet("/", async (AppDbContext db, CurrentUser user, DiagnosticStatus? status, long? visitId, bool? mine, bool? excludeRefraction, bool? open, DateOnly? date, int page = 1, int pageSize = 50, CancellationToken ct = default) =>
        {
            pageSize = Math.Clamp(pageSize, 1, 100);
            page = Math.Max(page, 1);

            var query = db.DiagnosticOrders.AsNoTracking();
            if (mine == true)
            {
                var userId = user.Id;
                query = query.Where(o => o.RequestedByUserId == userId);
            }

            if (excludeRefraction == true)
            {
                query = query.Where(o => o.Test!.Kind != DiagnosticKind.Refraction);
            }

            if (status is { } s)
            {
                query = query.Where(o => o.Status == s);
            }

            if (visitId is { } v)
            {
                query = query.Where(o => o.VisitId == v);
            }

            // A desk's lists stay small however long the hospital runs: the open work, or one day's results.
            if (open == true)
            {
                query = query.Where(o => o.Status == DiagnosticStatus.Requested || o.Status == DiagnosticStatus.InProgress);
            }
            else if (open == false)
            {
                query = query.Where(o => o.Status == DiagnosticStatus.ResultReady);
            }

            if (date is { } day)
            {
                var (from, to) = KabulDay(day);
                query = query.Where(o => o.Visit != null ? o.Visit.VisitDate == day : o.CreatedAt >= from && o.CreatedAt < to);
            }

            var total = await query.CountAsync(ct);
            if (open == false)
            {
                var done = await QueryOrders(query.OrderByDescending(o => o.Id).Skip((page - 1) * pageSize).Take(pageSize)).ToListAsync(ct);
                return Results.Ok(new PagedResult<DiagnosticOrderDto>(done.Select(ToDto).ToList(), total, page, pageSize));
            }

            var rows = await QueryOrders(query.OrderByDescending(o => o.Status == DiagnosticStatus.InProgress)
                .ThenBy(o => db.QueueAttendances.Any(a => a.Day == o.Visit!.VisitDate && a.EntryKey == "v:" + o.VisitId && !a.Present
                    && (o.Test!.Kind == DiagnosticKind.Refraction ? a.Desk == Calls.CallDesk.Refraction : o.Test.Kind == DiagnosticKind.Imaging ? a.Desk == Calls.CallDesk.Imaging : a.Desk == Calls.CallDesk.Lab)))
                .ThenBy(o => o.Visit != null && o.Visit.QueueShift == QueueShift.Afternoon).ThenBy(o => o.Visit != null ? o.Visit.QueueNumber ?? int.MaxValue : int.MaxValue).ThenBy(o => o.Id).Skip((page - 1) * pageSize).Take(pageSize)).ToListAsync(ct);
            return Results.Ok(new PagedResult<DiagnosticOrderDto>(rows.Select(ToDto).ToList(), total, page, pageSize));
        }).RequirePermission("diagnostics.view");

        orders.MapPost("/", async (CreateDiagnosticOrderRequest request, AppDbContext db, ClinicalAccess access, Idempotency idempotency, NumberService numbers, InvoiceService invoices, LedgerService ledger, CurrentUser user, AuditService audit, Outbox outbox, HttpContext http, CancellationToken ct) =>
            await idempotency.RunAsync(request, async () =>
            {
                DiagnosticTest? test;
                if (request.ServiceId is { } selectedService)
                {
                    if (request.TestId != 0) throw new AppException(400, "invalid_test", "فقط یک درخواست انتخاب کنید.");
                    // Lock before any snapshot read: a waiting request must see the first request's new test
                    // in both the link lookup and the joined response under MySQL repeatable-read isolation.
                    var service = (await db.Services.FromSqlInterpolated($"SELECT * FROM Services WHERE Id = {selectedService} FOR UPDATE").ToListAsync(ct)).SingleOrDefault();
                    if (service is null || !service.IsActive || ServiceCodes.PaidFlow.Contains(service.Code)
                        || await db.SurgeryPackages.AnyAsync(p => p.ServiceId == selectedService, ct)
                        || await db.OperationDefinitions.AnyAsync(o => o.ServiceId == selectedService, ct)
                        || await db.Departments.AnyAsync(d => d.Id == service.DepartmentId && d.Code == "surgery", ct))
                        throw new AppException(400, "unknown_service", "خدمت تشخیصی انتخاب‌شده معتبر نیست.");
                    test = (await db.DiagnosticTests.FromSqlInterpolated($"SELECT * FROM DiagnosticTests WHERE ServiceId = {selectedService} ORDER BY Id FOR UPDATE").ToListAsync(ct)).FirstOrDefault();
                    if (test is null)
                    {
                        test = new DiagnosticTest { Name = service.Name, IsActive = true, ServiceId = service.Id, Kind = DiagnosticKind.Lab };
                        db.DiagnosticTests.Add(test);
                        await db.SaveChangesAsync(ct);
                        audit.Add("DiagnosticTestCreated", "DiagnosticTest", test.Id.ToString());
                    }
                }
                else test = await db.DiagnosticTests.AsNoTracking().SingleOrDefaultAsync(t => t.Id == request.TestId, ct);
                if (test is null || !test.IsActive) throw new AppException(StatusCodes.Status400BadRequest, "unknown_test", "آزمایش انتخاب‌شده معتبر نیست.");
                if (test.ServiceId is { } activeService && !await db.Services.AnyAsync(s => s.Id == activeService && s.IsActive, ct))
                    throw new AppException(400, "unknown_service", "خدمت این درخواست غیرفعال است.");
                if (!await db.Patients.AnyAsync(p => p.Id == request.PatientId, ct))
                {
                    throw new AppException(StatusCodes.Status400BadRequest, "unknown_patient", "مریض پیدا نشد.");
                }

                if (request.VisitId is { } visitId && !await db.Visits.AnyAsync(v => v.Id == visitId && v.PatientId == request.PatientId, ct))
                {
                    throw new AppException(StatusCodes.Status400BadRequest, "visit_mismatch", "این مراجعه به این مریض تعلق ندارد.");
                }

                if (request.VisitId is { } orderedVisit)
                {
                    await access.EnsureResponsibleAsync(orderedVisit, ct);
                    if (await db.Visits.AnyAsync(v => v.Id == orderedVisit && v.Status == VisitStatus.AwaitingPayment, ct))
                    {
                        throw new AppException(StatusCodes.Status409Conflict, "payment_required", "فیس معاینه این مراجعه هنوز پرداخت نشده است.");
                    }
                }

                var prepaid = test.Kind != DiagnosticKind.Lab;

                if (test.Kind == DiagnosticKind.Refraction)
                {
                    if (!http.User.HasClaim(RolePermissions.ClaimType, "consultation.edit"))
                    {
                        throw new AppException(StatusCodes.Status403Forbidden, "forbidden", "رفرکشن را فقط داکتر درخواست می‌کند.");
                    }

                    if (request.VisitId is null)
                    {
                        throw new AppException(StatusCodes.Status400BadRequest, "visit_required", "رفرکشن باید به یک مراجعه وصل باشد.");
                    }
                }

                // The visit row is locked so two simultaneous clicks cannot both pass the duplicate check and charge twice.
                Visit? lockedVisit = null;
                if (prepaid && request.VisitId is { } lockId)
                {
                    lockedVisit = await db.Visits.FromSqlInterpolated($"SELECT * FROM Visits WHERE Id = {lockId} FOR UPDATE").SingleAsync(ct);
                    if (lockedVisit.Status is VisitStatus.Completed or VisitStatus.Cancelled)
                    {
                        throw new AppException(StatusCodes.Status409Conflict, "visit_closed", "این مراجعه بسته شده است.");
                    }

                    // Must be a locking read: a plain read here sees the data from before the wait for the visit lock.
                    var earlier = await db.DiagnosticOrders.FromSqlInterpolated($"SELECT * FROM DiagnosticOrders WHERE VisitId = {lockId} AND TestId = {test.Id} FOR UPDATE").AsNoTracking().ToListAsync(ct);
                    if (earlier.Any(o => o.Status is DiagnosticStatus.Requested or DiagnosticStatus.InProgress || (test.Kind == DiagnosticKind.Refraction && o.Status == DiagnosticStatus.ResultReady)))
                    {
                        throw new AppException(StatusCodes.Status409Conflict, "duplicate_request", "این درخواست برای همین مراجعه قبلاً ثبت شده است.");
                    }
                }

                var order = new DiagnosticOrder
                {
                    OrderNumber = await numbers.NextNumberAsync("lab", ct),
                    PatientId = request.PatientId,
                    VisitId = request.VisitId,
                    TestId = test.Id,
                    Note = Text.Clean(request.Note),
                    RequestedByUserId = user.Id,
                    ReturnToDoctor = lockedVisit is not null && (test.Kind == DiagnosticKind.Refraction || request.ReturnToDoctor),
                };
                db.DiagnosticOrders.Add(order);
                await db.SaveChangesAsync(ct);

                audit.Add("DiagnosticOrderCreated", "DiagnosticOrder", order.Id.ToString());
                outbox.Add("DiagnosticOrderCreated", new { orderId = order.Id }, Groups.Lab, Groups.Triage);
                await db.SaveChangesAsync(ct);

                if (test.ServiceId is { } serviceId)
                {
                    var price = await FinanceEndpoints.CurrentPriceAsync(db, serviceId, ct);
                    var line = new ChargeLine(test.Name, 1, price, Accounts.ServiceRevenue, serviceId, nameof(DiagnosticOrder), order.Id);
                    if (prepaid && price <= 0)
                    {
                        throw new AppException(StatusCodes.Status409Conflict, "no_price", "برای این خدمت قیمت تعیین نشده است.");
                    }

                    // Every charged request keeps its own invoice, so its payment can be checked on its own.
                    var invoice = await invoices.CreateAsync(request.PatientId, request.VisitId, ct);
                    await invoices.AddLineAsync(invoice, line, ct);
                    if (request.Discount is { } discount)
                    {
                        var amount = FinanceEndpoints.DiscountAmount(invoice, discount.Amount, discount.Mode);
                        await FinanceEndpoints.ApplyDiscountAsync(invoice, amount, discount.Amount, discount.Mode, "تخفیف داکتر هنگام تجویز خدمت تشخیصی", db, ledger, audit, outbox, ct);
                    }
                    order.InvoiceId = invoice.Id;
                }
                else if (request.Discount is not null)
                {
                    throw new AppException(StatusCodes.Status409Conflict, "discount_without_charge", "برای این درخواست مبلغی برای تخفیف وجود ندارد.");
                }
                else if (prepaid)
                {
                    throw new AppException(StatusCodes.Status409Conflict, "no_price", "برای این خدمت قیمت تعیین نشده است.");
                }

                if (order.ReturnToDoctor && VisitWorkflow.CanMove(lockedVisit!.Status, VisitStatus.AwaitingDiagnostic))
                {
                    db.VisitStatusHistory.Add(new VisitStatusHistory { VisitId = lockedVisit.Id, FromStatus = lockedVisit.Status, ToStatus = VisitStatus.AwaitingDiagnostic, ChangedByUserId = user.Id });
                    lockedVisit.Status = VisitStatus.AwaitingDiagnostic;
                    outbox.Add("VisitStatusChanged", new { visitId = lockedVisit.Id }, VisitService.EventGroups);
                }

                // As with a saved exam: the doctor who refers an unassigned patient keeps them, so the result comes back to this doctor.
                if (order.ReturnToDoctor && lockedVisit!.DoctorStaffId is null && http.User.HasClaim(RolePermissions.ClaimType, "consultation.edit"))
                {
                    var userId = user.Id;
                    lockedVisit.DoctorStaffId = await db.Staff.Where(s => s.UserId == userId && s.IsDoctor && s.IsActive).Select(s => (long?)s.Id).FirstOrDefaultAsync(ct);
                }

                await db.SaveChangesAsync(ct);

                if (request.ServiceId is not null)
                {
                    // The catalog link can have been committed by the request we waited for. Use the
                    // locked test instead of joining it through an older repeatable-read snapshot.
                    var patient = await db.Patients.AsNoTracking().SingleAsync(p => p.Id == order.PatientId, ct);
                    var invoice = order.InvoiceId is { } invoiceId ? await db.Invoices.SingleAsync(i => i.Id == invoiceId, ct) : null;
                    var paid = invoice is null ? (bool?)null : invoice.Status != InvoiceStatus.Void && invoice.Total - invoice.Discount <= invoice.Paid - invoice.Refunded;
                    return (StatusCodes.Status201Created, new DiagnosticOrderDto(order.Id, order.OrderNumber, order.PatientId,
                        patient.FileNumber, $"{patient.FirstName} {patient.LastName}".Trim(), order.VisitId, test.Id, test.Name,
                        order.Note, order.Status.ToString(), order.CreatedAt, order.Version, null, null, test.Kind.ToString(), order.ReturnToDoctor, paid));
                }
                var row = await QueryOrders(db.DiagnosticOrders.Where(o => o.Id == order.Id)).SingleAsync(ct);
                return (StatusCodes.Status201Created, ToDto(row));
            }, ct)).RequirePermission("diagnostics.create");

        orders.MapGet("/{id:long}", async (long id, AppDbContext db, CancellationToken ct) =>
        {
            var row = await QueryOrders(db.DiagnosticOrders.AsNoTracking().Where(o => o.Id == id)).SingleOrDefaultAsync(ct) ?? throw AppException.NotFound();
            return Results.Ok(ToDto(row));
        }).RequirePermission("diagnostics.view");

        // The test itself stays: it was charged and routed as that test. A wrong one is cancelled and requested again.
        orders.MapPut("/{id:long}/note", async (long id, EditOrderNoteRequest request, AppDbContext db, ClinicalAccess access, AuditService audit, CancellationToken ct) =>
        {
            var order = await db.DiagnosticOrders.SingleOrDefaultAsync(o => o.Id == id, ct) ?? throw AppException.NotFound();
            db.Entry(order).Property(o => o.Version).OriginalValue = request.Version;
            if (order.VisitId is { } visitId)
            {
                await access.EnsureResponsibleAsync(visitId, ct);
            }

            if (order.Status is DiagnosticStatus.ResultReady or DiagnosticStatus.Cancelled)
            {
                throw new AppException(StatusCodes.Status409Conflict, "invalid_transition", "برای این درخواست نتیجه ثبت شده یا لغو شده است.");
            }

            audit.Add("DiagnosticOrderEdited", "DiagnosticOrder", id.ToString(), order.Note);
            order.Note = Text.Clean(request.Note);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        }).RequirePermission("diagnostics.create");

        // A result the doctor may already have read, so the old text and the reason stay on the record, and the doctor is told.
        orders.MapPut("/{id:long}/result", async (long id, AmendResultRequest request, AppDbContext db, CurrentUser user, AuditService audit, Outbox outbox, CancellationToken ct) =>
        {
            await using var tx = await db.BeginWorkflowTransactionAsync(ct);
            var order = await db.DiagnosticOrders.SingleOrDefaultAsync(o => o.Id == id, ct) ?? throw AppException.NotFound();
            db.Entry(order).Property(o => o.Version).OriginalValue = request.Version;
            if (await KindOfAsync(db, order, ct) == DiagnosticKind.Refraction)
            {
                throw UseRefractionForm();
            }

            var result = await db.DiagnosticResults.SingleOrDefaultAsync(r => r.OrderId == id, ct);
            if (order.Status != DiagnosticStatus.ResultReady || result is null)
            {
                throw new AppException(StatusCodes.Status409Conflict, "invalid_transition", "برای این درخواست هنوز نتیجه‌ای ثبت نشده است.");
            }

            var reason = request.Reason.Trim();
            Amendments.Add(db, nameof(DiagnosticOrder), id, nameof(result.ResultText), result.ResultText, request.ResultText.Trim(), reason, user.Id);
            result.ResultText = request.ResultText.Trim();

            // The text lives in its own table: the version must still move on for the next editor's check.
            db.Entry(order).State = EntityState.Modified;
            audit.Add("DiagnosticResultAmended", "DiagnosticOrder", id.ToString(), reason);
            outbox.Add("DiagnosticResultReady", new { orderId = id, visitId = order.VisitId }, Groups.Doctor, Groups.Lab);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return Results.NoContent();
        }).RequirePermission("diagnostics.approve");

        orders.MapPost("/{id:long}/start", async (long id, ChangeOrderStatusRequest request, AppDbContext db, AuditService audit, CancellationToken ct) =>
        {
            var order = await db.DiagnosticOrders.SingleOrDefaultAsync(o => o.Id == id, ct) ?? throw AppException.NotFound();
            db.Entry(order).Property(o => o.Version).OriginalValue = request.Version;
            if (order.Status != DiagnosticStatus.Requested)
            {
                throw new AppException(StatusCodes.Status409Conflict, "invalid_transition", "این درخواست قبلاً شروع شده یا بسته است.");
            }

            await EnsureCanStartAsync(db, order, ct);

            order.Status = DiagnosticStatus.InProgress;
            audit.Add("DiagnosticStarted", "DiagnosticOrder", id.ToString());
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        }).RequirePermission("diagnostics.edit");

        orders.MapPost("/{id:long}/result", async (long id, SaveResultRequest request, AppDbContext db, CurrentUser user, AuditService audit, Outbox outbox, CancellationToken ct) =>
        {
            var orderVisit = await VisitOfOrderAsync(db, id, ct);
            await using var tx = await db.BeginWorkflowTransactionAsync(ct);

            // Two results finishing together must not each see the other as pending, or the patient never returns to the doctor.
            if (orderVisit is { } lockedVisit)
            {
                await VisitService.LockAsync(db, lockedVisit, ct);
            }

            var order = await db.DiagnosticOrders.SingleOrDefaultAsync(o => o.Id == id, ct) ?? throw AppException.NotFound();
            db.Entry(order).Property(o => o.Version).OriginalValue = request.Version;
            if (order.Status is DiagnosticStatus.ResultReady or DiagnosticStatus.Cancelled)
            {
                throw new AppException(StatusCodes.Status409Conflict, "invalid_transition", "برای این درخواست نتیجه ثبت شده یا لغو شده است.");
            }

            // Saving a result from Requested skips the start step, so the same check applies.
            if (order.Status == DiagnosticStatus.Requested)
            {
                await EnsureCanStartAsync(db, order, ct);
            }
            else if (await KindOfAsync(db, order, ct) == DiagnosticKind.Refraction)
            {
                throw UseRefractionForm();
            }

            db.DiagnosticResults.Add(new DiagnosticResult { OrderId = id, ResultText = request.ResultText.Trim(), PerformedByUserId = user.Id });
            order.Status = DiagnosticStatus.ResultReady;
            audit.Add("DiagnosticResultSaved", "DiagnosticOrder", id.ToString());
            outbox.Add("DiagnosticResultReady", new { orderId = id, visitId = order.VisitId }, Groups.Doctor, Groups.Lab);
            await db.SaveChangesAsync(ct);
            if (order.ReturnToDoctor && order.VisitId is { } visitId)
            {
                await DiagnosticGate.ReturnToDoctorAsync(db, visitId, user, outbox, ct);
            }

            await tx.CommitAsync(ct);
            return Results.NoContent();
        }).RequirePermission("diagnostics.approve");

        orders.MapPost("/{id:long}/cancel", async (long id, ChangeOrderStatusRequest request, AppDbContext db, ClinicalAccess access, InvoiceService invoices, CurrentUser user, AuditService audit, Outbox outbox, CancellationToken ct) =>
        {
            var orderVisit = await VisitOfOrderAsync(db, id, ct);
            await using var tx = await db.BeginWorkflowTransactionAsync(ct);
            if (orderVisit is { } lockedVisit)
            {
                await VisitService.LockAsync(db, lockedVisit, ct);
                await access.EnsureResponsibleAsync(lockedVisit, ct);
            }

            var order = await db.DiagnosticOrders.SingleOrDefaultAsync(o => o.Id == id, ct) ?? throw AppException.NotFound();
            db.Entry(order).Property(o => o.Version).OriginalValue = request.Version;
            if (order.Status is DiagnosticStatus.ResultReady or DiagnosticStatus.Cancelled)
            {
                throw new AppException(StatusCodes.Status409Conflict, "invalid_transition", "این درخواست قابل لغو نیست.");
            }

            var charge = await db.InvoiceItems.SingleOrDefaultAsync(i => i.SourceType == nameof(DiagnosticOrder) && i.SourceId == id && !i.IsVoid, ct);
            if (charge is not null)
            {
                if (order.InvoiceId is not null)
                {
                    await invoices.ClosePrepaidAsync(charge, ct);
                }
                else
                {
                    await invoices.VoidLineAsync(charge, ct);
                }
            }

            order.Status = DiagnosticStatus.Cancelled;
            audit.Add("DiagnosticCancelled", "DiagnosticOrder", id.ToString());
            await db.SaveChangesAsync(ct);
            if (order.ReturnToDoctor && order.VisitId is { } visitId)
            {
                await DiagnosticGate.ReturnToDoctorAsync(db, visitId, user, outbox, ct);
            }

            await tx.CommitAsync(ct);
            return Results.NoContent();
        }).RequirePermission("diagnostics.cancel");
    }

    /// <summary>Read before the transaction starts, so it does not fix the snapshot ahead of the visit lock. An order never changes visit.</summary>
    private static async Task<long?> VisitOfOrderAsync(AppDbContext db, long id, CancellationToken ct)
    {
        var row = await db.DiagnosticOrders.AsNoTracking().Where(o => o.Id == id).Select(o => new { o.VisitId }).SingleOrDefaultAsync(ct);
        return row is null ? throw AppException.NotFound() : row.VisitId;
    }

    private static Task<DiagnosticKind> KindOfAsync(AppDbContext db, DiagnosticOrder order, CancellationToken ct) =>
        db.DiagnosticTests.Where(t => t.Id == order.TestId).Select(t => t.Kind).SingleAsync(ct);

    private static AppException UseRefractionForm() =>
        new(StatusCodes.Status409Conflict, "use_refraction_form", "رفرکشن از فرم بینایی‌سنجی ثبت می‌شود.");

    private static async Task EnsureCanStartAsync(AppDbContext db, DiagnosticOrder order, CancellationToken ct)
    {
        var kind = await KindOfAsync(db, order, ct);
        if (kind == DiagnosticKind.Refraction)
        {
            throw UseRefractionForm();
        }

        if (kind == DiagnosticKind.Imaging || order.InvoiceId is not null)
        {
            await DiagnosticGate.EnsurePaidAsync(db, order, ct);
        }
        else
        {
            await DiagnosticGate.EnsureChargePaidAsync(db, nameof(DiagnosticOrder), order.Id, ct);
        }
    }

    public static IQueryable<DiagnosticProjection> QueryOrders(IQueryable<DiagnosticOrder> orders) =>
        orders.Select(o => new DiagnosticProjection(
            o.Id, o.OrderNumber, o.PatientId, o.Patient!.FileNumber, o.Patient.FirstName, o.Patient.LastName, o.VisitId, o.TestId, o.Test!.Name,
            o.Note, o.Status, o.CreatedAt, o.Version, o.Result != null ? o.Result.ResultText : null, o.Result != null ? (DateTime?)o.Result.PerformedAt : null,
            o.Test!.Kind, o.ReturnToDoctor,
            o.Test!.Kind == DiagnosticKind.Lab && o.InvoiceId == null ? null : (bool?)(o.Invoice != null && o.Invoice.Status != InvoiceStatus.Void && o.Invoice.Total - o.Invoice.Discount <= o.Invoice.Paid - o.Invoice.Refunded), o.Visit != null ? o.Visit.QueueNumber : null));

    public static DiagnosticOrderDto ToDto(DiagnosticProjection o) => new(
        o.Id, o.OrderNumber, o.PatientId, o.FileNumber, $"{o.FirstName} {o.LastName}".Trim(), o.VisitId, o.TestId, o.TestName,
        o.Note, o.Status.ToString(), o.CreatedAt, o.Version, o.ResultText, o.PerformedAt, o.Kind.ToString(), o.ReturnToDoctor, o.Paid, o.QueueNumber);

    /// <summary>A Kabul calendar day as a UTC range, for timestamps stored in UTC. Afghanistan keeps no daylight saving.</summary>
    public static (DateTime From, DateTime To) KabulDay(DateOnly day)
    {
        var from = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc).AddHours(-4.5);
        return (from, from.AddDays(1));
    }
}
