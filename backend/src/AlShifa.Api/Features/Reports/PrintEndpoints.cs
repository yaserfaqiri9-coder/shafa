using System.Globalization;
using AlShifa.Api.Features.Auth;
using AlShifa.Api.Features.Clinical;
using AlShifa.Api.Features.Finance;
using AlShifa.Api.Features.Inventory;
using AlShifa.Api.Features.Patients;
using AlShifa.Api.Features.Settings;
using AlShifa.Api.Features.Visits;
using AlShifa.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace AlShifa.Api.Features.Reports;

public sealed record PrintField(string Label, string? Value);

/// <param name="Totals">The first entry is the amount band at the foot of the paper; any others print as plain rows under it.</param>
/// <param name="Subtitle">The line under the title naming what was paid for.</param>
/// <param name="NumberLabel">What the document number is called on this paper, such as «شماره رسید».</param>
/// <param name="Highlight">One fact printed on a shaded row under the title, such as the operation on a surgery receipt.</param>
/// <param name="Signer">The doctor who issued a prescription, printed at its foot. Never a typed or drawn signature.</param>
public sealed record PrintDocument(
    string Title, string Number, DateTime Date, string Format, SettingsDto Hospital,
    IReadOnlyList<PrintField> Fields, IReadOnlyList<string>? Headers, IReadOnlyList<IReadOnlyList<string?>>? Rows,
    IReadOnlyList<PrintField>? Totals, string? Note,
    string? Subtitle = null, string? NumberLabel = null, PrintField? Highlight = null, string? Signer = null, string? SignerTitle = null,
    string? Department = null, IReadOnlyList<string>? PurposeItems = null, bool HideNumber = false);

/// <summary>
/// One document shape for every printout, so each document type only states its own content.
/// Each endpoint returns only what that paper needs: financial papers name their section and billed services,
/// never diagnoses. Stored payment, refund and balance values keep a partial, refunded or void paper unambiguous.
/// </summary>
public static class PrintEndpoints
{
    private const string A4 = "a4";
    private const string Receipt = "receipt";

    private sealed record ReceiptKind(string Title, string BillTitle, string Department, IReadOnlyList<string> Purposes, bool IncludesGoods);

    public static void MapPrint(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/print").WithTags("Print");

        group.MapGet("/visit/{id:long}", async (long id, AppDbContext db, CancellationToken ct) =>
        {
            var v = await db.Visits.AsNoTracking()
                .Where(x => x.Id == id)
                .Select(x => new { x.VisitNumber, x.QueueNumber, x.QueueShift, x.QueueRound, x.CreatedAt, x.Patient!.FileNumber, x.Patient.FirstName, x.Patient.LastName, Department = x.Department!.Name, Doctor = x.Doctor != null ? x.Doctor.FullName : null })
                .SingleOrDefaultAsync(ct) ?? throw AppException.NotFound();

            if (v.QueueNumber is null)
            {
                throw new AppException(StatusCodes.Status409Conflict, "no_queue_number", "برای این مراجعه هنوز شماره نوبت صادر نشده است.");
            }

            var invoice = await db.VisitTokens.AsNoTracking().Where(t => t.VisitId == id).Select(t => t.Invoice).SingleOrDefaultAsync(ct);
            return Results.Ok(new PrintDocument("برگه نوبت و پذیرش", v.VisitNumber, v.CreatedAt, Receipt, await SettingsEndpoints.LoadAsync(db, ct),
            [
                new("نام مریض", Name(v.FirstName, v.LastName)),
                new("شماره پرونده", v.FileNumber),
                new("داکتر", v.Doctor),
                new("وضعیت فیس", invoice is null ? null : InvoiceStatusLabel(invoice)),
                new("قابل پرداخت در صندوق", invoice is not null && Balance(invoice) > 0 ? Money(Balance(invoice)) : null),
            ], null, null,
            [new($"شماره نوبت ({QueueShifts.Label(v.QueueShift, v.QueueRound)})", v.QueueNumber.Value.ToString(CultureInfo.InvariantCulture))], null,
            Department: $"پذیرش / {v.Department}",
            Highlight: new($"شماره نوبت ({QueueShifts.Label(v.QueueShift, v.QueueRound)})", v.QueueNumber.Value.ToString(CultureInfo.InvariantCulture)),
            HideNumber: true));
        }).RequirePermission("visits.print");

        group.MapGet("/patient/{id:long}", async (long id, AppDbContext db, CancellationToken ct) =>
        {
            var p = await db.Patients.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw AppException.NotFound();
            return Results.Ok(new PrintDocument("کارت مریض", p.FileNumber, p.CreatedAt, Receipt, await SettingsEndpoints.LoadAsync(db, ct),
            [
                new("نام مریض", Name(p.FirstName, p.LastName)),
                new("نام پدر", p.FatherName),
                new("جنسیت", p.Gender switch { Gender.Male => "مرد", Gender.Female => "زن", _ => "دیگر" }),
                new("تماس", p.Phone),
            ], null, null, null, null,
            NumberLabel: "شماره پرونده"));
        }).RequirePermission("patients.print");

        group.MapGet("/rx/{id:long}", async (long id, AppDbContext db, CancellationToken ct) =>
        {
            var rx = await db.MedicalPrescriptions.AsNoTracking().Include(x => x.Items).Include(x => x.Patient)
                .SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw AppException.NotFound();
            var doctor = await db.Users.AsNoTracking().Where(u => u.Id == rx.DoctorUserId).Select(u => u.FullName).SingleOrDefaultAsync(ct);
            var specialty = await db.Staff.AsNoTracking().Where(s => s.UserId == rx.DoctorUserId).Select(s => s.Specialty).FirstOrDefaultAsync(ct);
            // Each line as the old prescriptions read: "Eye drop Tobradex (Tobramycin + Dexamethasone)".
            var drugs = await PrescriptionEndpoints.DrugDetailsAsync(db, rx.Items.Select(i => i.DrugName).Distinct().ToList(), ct);
            string Line(string name) => drugs.TryGetValue(name, out var d) ? PrescriptionEndpoints.PrescriptionLine(name, d.Form, d.Generic) : name;

            return Results.Ok(new PrintDocument("نسخه داکتر", rx.RxNumber, rx.CreatedAt, A4, await SettingsEndpoints.LoadAsync(db, ct),
            [
                new("نام مریض", Name(rx.Patient!.FirstName, rx.Patient.LastName)),
            ],
            ["دوا", "مقدار", "روش مصرف", "مدت", "توضیح", "تعداد"],
            rx.Items.OrderBy(i => i.Id).Select(i => (IReadOnlyList<string?>)[Line(i.DrugName), i.Dose, i.Route, i.Duration, i.Instructions, Number(i.Quantity)]).ToList(),
            null, rx.Notes,
            NumberLabel: "شماره نسخه", Signer: doctor, SignerTitle: specialty));
        }).RequirePermission("prescriptions.print");

        // Read by the optical slip (OpticalSlipPrint.vue), which has its own layout; its labels are its contract.
        group.MapGet("/optical/{id:long}", async (long id, AppDbContext db, CancellationToken ct) =>
        {
            var rx = await db.OpticalPrescriptions.AsNoTracking().Include(x => x.Patient).SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw AppException.NotFound();
            var doctor = await db.Users.AsNoTracking().Where(u => u.Id == rx.DoctorUserId).Select(u => u.FullName).SingleOrDefaultAsync(ct);

            return Results.Ok(new PrintDocument("نسخه عینک", rx.RxNumber, rx.CreatedAt, A4, await SettingsEndpoints.LoadAsync(db, ct),
            [
                new("مریض", Name(rx.Patient!.FirstName, rx.Patient.LastName)),
                new("شماره پرونده", rx.Patient.FileNumber),
                new("داکتر", doctor),
                new("PD", rx.Pd),
            ],
            ["چشم", "Sphere", "Cylinder", "Axis", "ADD"],
            [
                ["راست (OD)", rx.SphereOd, rx.CylinderOd, rx.AxisOd, rx.AddOd],
                ["چپ (OS)", rx.SphereOs, rx.CylinderOs, rx.AxisOs, rx.AddOs],
            ],
            null, rx.Notes));
        }).RequirePermission("opticalrx.print");

        // This is the sale order handed to the cashier, so both optical and the cashier can reprint it.
        group.MapGet("/optical-order/{id:long}", async (long id, AppDbContext db, HttpContext http, CancellationToken ct) =>
        {
            if (!http.User.HasClaim(RolePermissions.ClaimType, "optical.print") && !http.User.HasClaim(RolePermissions.ClaimType, "invoices.print"))
            {
                throw new AppException(StatusCodes.Status403Forbidden, "forbidden", "شما اجازه چاپ رسید سفارش عینک را ندارید.");
            }

            var order = await db.OpticalOrders.AsNoTracking().Include(o => o.Patient).Include(o => o.Sale).ThenInclude(s => s!.Invoice)
                .SingleOrDefaultAsync(o => o.Id == id, ct) ?? throw AppException.NotFound();
            var invoice = order.Sale!.Invoice!;
            var items = await db.InvoiceItems.AsNoTracking().Where(i => i.InvoiceId == invoice.Id && !i.IsVoid).OrderBy(i => i.Id).ToListAsync(ct);
            var saleItemIds = items.Where(i => i.SourceType == nameof(SaleItem) && i.SourceId.HasValue).Select(i => i.SourceId!.Value).ToList();
            var productIds = await db.Set<SaleItem>().AsNoTracking().Where(i => saleItemIds.Contains(i.Id)).ToDictionaryAsync(i => i.Id, i => i.ProductId, ct);
            var rows = items.GroupBy(i => new { i.Description, i.UnitPrice, i.SourceType, Product = productIds.GetValueOrDefault(i.SourceId ?? 0), Separate = i.SourceType == nameof(SaleItem) ? 0 : i.Id })
                .Select(g => (IReadOnlyList<string?>)[g.Key.Description, g.Key.SourceType == "OpticalNonStockLens" ? "عدسی غیرانباری" : "قلم گدام", Number(g.Sum(i => i.Quantity)), Money(g.Sum(i => i.LineTotal))]).ToList();
            var paymentStatus = order.IsComplimentary ? "تحویل رایگان" : InvoiceStatusLabel(invoice);

            return Results.Ok(new PrintDocument("رسید سفارش فروش عینک", order.OrderNumber, order.CreatedAt, Receipt, await SettingsEndpoints.LoadAsync(db, ct),
            [
                new("نام مریض", order.Patient is null ? order.CustomerName : Name(order.Patient.FirstName, order.Patient.LastName)),
                new("شماره پرونده", order.Patient?.FileNumber),
                new("تلفن", order.Patient is null ? order.CustomerPhone : null),
                new("شماره فاکتور", invoice.InvoiceNumber),
                new("وضعیت پرداخت", paymentStatus),
                new("مانده قابل پرداخت", order.IsComplimentary || Balance(invoice) == 0 ? null : Money(Balance(invoice))),
                new("دلیل تحویل رایگان", order.IsComplimentary ? order.ComplimentaryReason : null),
                new("تأییدکننده", order.IsComplimentary ? order.ComplimentaryApprovedBy : null),
            ], ["قلم", "نوع", "تعداد", "مبلغ"], rows,
            [new("مبلغ قابل پرداخت", Money(order.IsComplimentary ? 0 : invoice.Total - invoice.Discount))], null,
            NumberLabel: "شماره سفارش", Department: "بخش عینک"));
        }).RequireAuthorization();

        group.MapGet("/lab/{id:long}", async (long id, AppDbContext db, CancellationToken ct) =>
        {
            var o = await db.DiagnosticOrders.AsNoTracking()
                .Where(x => x.Id == id)
                .Select(x => new { x.OrderNumber, x.CreatedAt, x.Patient!.FileNumber, x.Patient.FirstName, x.Patient.LastName, Test = x.Test!.Name, Result = x.Result != null ? x.Result.ResultText : null, PerformedAt = x.Result != null ? (DateTime?)x.Result.PerformedAt : null })
                .SingleOrDefaultAsync(ct) ?? throw AppException.NotFound();
            if (o.Result is null)
            {
                throw new AppException(StatusCodes.Status409Conflict, "no_result", "برای این درخواست هنوز نتیجه ثبت نشده است.");
            }

            return Results.Ok(new PrintDocument("جواب آزمایش", o.OrderNumber, o.PerformedAt ?? o.CreatedAt, A4, await SettingsEndpoints.LoadAsync(db, ct),
            [
                new("نام مریض", Name(o.FirstName, o.LastName)),
                new("شماره پرونده", o.FileNumber),
            ], null, null, null, o.Result,
            NumberLabel: "شماره درخواست", Highlight: new("آزمایش", o.Test)));
        }).RequirePermission("diagnostics.print");

        group.MapGet("/invoice/{id:long}", async (long id, AppDbContext db, CancellationToken ct) =>
        {
            var i = await db.Invoices.AsNoTracking().Include(x => x.Patient).SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw AppException.NotFound();
            var items = await db.InvoiceItems.AsNoTracking().Where(x => x.InvoiceId == id).OrderBy(x => x.Id).ToListAsync(ct);
            var activeItems = await BillingLinesAsync(db, items, ct);
            var kind = await ReceiptKindAsync(db, id, ct);
            var withQuantity = kind.IncludesGoods || activeItems.Any(x => x.Quantity != 1);

            return Results.Ok(new PrintDocument(kind.BillTitle, i.InvoiceNumber, i.CreatedAt, A4, await SettingsEndpoints.LoadAsync(db, ct),
            [
                new("نام مریض", i.Patient is null ? null : Name(i.Patient.FirstName, i.Patient.LastName)),
                ..InvoiceStanding(i),
            ],
            withQuantity ? ["شرح خدمت / جنس", "تعداد", "مبلغ"] : ["شرح خدمت / جنس", "مبلغ"],
            activeItems.Select(x => (IReadOnlyList<string?>)(withQuantity
                ? [x.Description, Number(x.Quantity), Money(x.LineTotal)]
                : [x.Description, Money(x.LineTotal)])).ToList(),
            [new("مجموع فاکتور", Money(i.Total - i.Discount))], null,
            NumberLabel: "شماره فاکتور", Department: kind.Department));
        }).RequirePermission("invoices.print");

        group.MapGet("/receipt/{id:long}", async (long id, AppDbContext db, CancellationToken ct) =>
        {
            var p = await db.Payments.AsNoTracking()
                .Where(x => x.Id == id)
                .Select(x => new { x.ReceiptNumber, x.Amount, x.Refunded, x.CreatedAt, x.InvoiceId, x.Invoice!.Status, x.Invoice.Total, x.Invoice.Discount, x.Invoice.Paid, InvoiceRefunded = x.Invoice.Refunded, Patient = x.Invoice.Patient })
                .SingleOrDefaultAsync(ct) ?? throw AppException.NotFound();
            var queue = await db.VisitTokens.AsNoTracking().Where(t => t.InvoiceId == p.InvoiceId)
                .Select(t => new { t.QueueNumber, t.QueueShift, t.QueueRound }).FirstOrDefaultAsync(ct);
            var kind = await ReceiptKindAsync(db, p.InvoiceId, ct);
            var balance = p.Status == InvoiceStatus.Void ? 0 : Balance(p.Total, p.Discount, p.Paid, p.InvoiceRefunded);
            var receiptStatus = p.Status == InvoiceStatus.Void ? "باطل"
                : p.Refunded >= p.Amount ? "برگشت کامل"
                : p.Refunded > 0 ? "برگشت جزئی"
                : balance > 0 ? "پرداخت جزئی"
                : null;

            return Results.Ok(new PrintDocument(kind.Title, p.ReceiptNumber, p.CreatedAt, Receipt, await SettingsEndpoints.LoadAsync(db, ct),
            [
                new("نام مریض", p.Patient is null ? null : Name(p.Patient.FirstName, p.Patient.LastName)),
                new("شماره نوبت", queue?.QueueNumber is { } n ? $"{n} ({QueueShifts.Label(queue.QueueShift, queue.QueueRound)})" : null),
                new("وضعیت", receiptStatus),
                new("برگشت وجه", p.Refunded > 0 ? Money(p.Refunded) : null),
                new("دریافت خالص", p.Refunded > 0 ? Money(p.Amount - p.Refunded) : null),
                new("مانده فاکتور", balance > 0 ? Money(balance) : null),
            ], null, null,
            [new("مبلغ این پرداخت", Money(p.Amount))], null,
            NumberLabel: "شماره رسید", Department: kind.Department, PurposeItems: kind.Purposes));
        }).RequirePermission("payments.print");

        group.MapGet("/refund/{id:long}", async (long id, AppDbContext db, HttpContext http, CancellationToken ct) =>
        {
            if (!http.User.HasClaim(RolePermissions.ClaimType, "payments.print") &&
                !http.User.HasClaim(RolePermissions.ClaimType, "payments.refund"))
            {
                throw new AppException(StatusCodes.Status403Forbidden, "forbidden", "شما اجازه این عملیات را ندارید.");
            }

            var refund = await db.Refunds.AsNoTracking()
                .Where(r => r.Id == id)
                .Select(r => new
                {
                    r.RefundNumber, r.Amount, r.CreatedAt, r.Payment!.ReceiptNumber, r.Payment.InvoiceId,
                    Patient = r.Payment.Invoice!.Patient,
                })
                .SingleOrDefaultAsync(ct) ?? throw AppException.NotFound();

            var kind = await ReceiptKindAsync(db, refund.InvoiceId, ct);
            return Results.Ok(new PrintDocument(kind.Title.Replace("رسید پرداخت", "رسید برگشت وجه"), refund.RefundNumber, refund.CreatedAt, Receipt, await SettingsEndpoints.LoadAsync(db, ct),
            [
                new("نام مریض", refund.Patient is null ? null : Name(refund.Patient.FirstName, refund.Patient.LastName)),
                new("رسید پرداخت", refund.ReceiptNumber),
            ], null, null,
            [new("مبلغ برگشت", Money(refund.Amount))], null,
            NumberLabel: "شماره رسید", Department: kind.Department, PurposeItems: kind.Purposes));
        }).RequireAuthorization();

        group.MapGet("/sale/{id:long}", async (long id, AppDbContext db, HttpContext http, CancellationToken ct) =>
        {
            var s = await db.Sales.AsNoTracking().Include(x => x.Patient).Include(x => x.Invoice).SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw AppException.NotFound();
            var needed = s.Kind == SaleKind.Pharmacy ? "pharmacy.print" : "optical.print";
            if (!http.User.HasClaim(RolePermissions.ClaimType, needed))
            {
                throw new AppException(StatusCodes.Status403Forbidden, "forbidden", "شما اجازه این عملیات را ندارید.");
            }

            var items = await db.InvoiceItems.AsNoTracking().Where(x => x.InvoiceId == s.InvoiceId && !x.IsVoid).OrderBy(x => x.Id).ToListAsync(ct);
            var pharmacy = s.Kind == SaleKind.Pharmacy;
            var invoice = s.Invoice!;
            var printableItems = await BillingLinesAsync(db, items, ct);
            return Results.Ok(new PrintDocument(pharmacy ? "فاکتور دواخانه" : "فاکتور عینک", pharmacy ? invoice.InvoiceNumber : s.SaleNumber, s.CreatedAt, Receipt, await SettingsEndpoints.LoadAsync(db, ct),
            [
                new("نام مریض", s.Patient is null ? null : Name(s.Patient.FirstName, s.Patient.LastName)),
                ..InvoiceStanding(invoice),
            ],
            [pharmacy ? "نام دوا" : "جنس", "تعداد", "مبلغ"],
            printableItems.Select(x => (IReadOnlyList<string?>)[x.Description, Number(x.Quantity), Money(x.LineTotal)]).ToList(),
            [new("مجموع فاکتور", Money(invoice.Total - invoice.Discount))], null,
            NumberLabel: pharmacy ? "شماره فاکتور" : "شماره فروش", Department: pharmacy ? "دواخانه" : "عینک"));
        }).RequireAuthorization();
    }

    private static string Name(string first, string? last) => $"{first} {last}".Trim();

    // Stock batches are an internal fulfillment detail. Combine equivalent
    // billed lines using their stored totals; never recalculate tariffs.
    private static async Task<List<(string Description, decimal Quantity, decimal LineTotal)>> BillingLinesAsync(AppDbContext db, IReadOnlyList<InvoiceItem> items, CancellationToken ct)
    {
        var ids = items.Where(i => !i.IsVoid && i.SourceType == nameof(SaleItem) && i.SourceId.HasValue).Select(i => i.SourceId!.Value).ToList();
        var products = await db.Set<SaleItem>().AsNoTracking().Where(i => ids.Contains(i.Id))
            .ToDictionaryAsync(i => i.Id, i => i.ProductId, ct);
        return items.Where(i => !i.IsVoid)
            .GroupBy(i => (i.Description, i.UnitPrice, i.ServiceId, i.SourceType, i.RevenueAccount,
                Product: i.SourceType == nameof(SaleItem) ? products.GetValueOrDefault(i.SourceId ?? 0) : 0,
                Separate: i.SourceType == nameof(SaleItem) && products.ContainsKey(i.SourceId ?? 0) ? 0 : i.Id))
            .Select(g => (g.Key.Description, g.Sum(i => i.Quantity), g.Sum(i => i.LineTotal))).ToList();
    }

    private static string Number(decimal value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static string Money(decimal value) => value.ToString("#,0.##", CultureInfo.InvariantCulture) + " افغانی";

    private static decimal Balance(Invoice invoice) => invoice.Status == InvoiceStatus.Void ? 0 : Balance(invoice.Total, invoice.Discount, invoice.Paid, invoice.Refunded);

    private static decimal Balance(decimal total, decimal discount, decimal paid, decimal refunded) =>
        Math.Max(0, total - discount - (paid - refunded));

    private static string InvoiceStatusLabel(Invoice invoice)
    {
        if (invoice.Status == InvoiceStatus.Void)
        {
            return "باطل";
        }

        var netPaid = invoice.Paid - invoice.Refunded;
        if (invoice.Refunded > 0 && netPaid <= 0)
        {
            return "برگشت کامل";
        }

        if (netPaid <= 0)
        {
            return "پرداخت‌نشده";
        }

        return Balance(invoice) > 0 ? (invoice.Refunded > 0 ? "پرداخت جزئی پس از برگشت" : "پرداخت جزئی") : "پرداخت‌شده";
    }

    /// <summary>
    /// The band shows the total after discount. Every invoice names its status;
    /// discount, payment, refund and balance rows appear only when they explain the current standing.
    /// </summary>
    private static IEnumerable<PrintField> InvoiceStanding(Invoice invoice)
    {
        var status = InvoiceStatusLabel(invoice);
        var settled = status == "پرداخت‌شده";
        yield return new("وضعیت", status);
        yield return new("جمع پیش از تخفیف", invoice.Discount > 0 ? Money(invoice.Total) : null);
        yield return new("تخفیف", invoice.Discount > 0 ? Money(invoice.Discount) : null);
        yield return new("پرداخت‌شده", settled || invoice.Paid <= 0 ? null : Money(invoice.Paid));
        yield return new("برگشت وجه", invoice.Refunded > 0 ? Money(invoice.Refunded) : null);
        yield return new("مانده", Balance(invoice) > 0 ? Money(Balance(invoice)) : null);
    }

    /// <summary>Identify every billed section from stored relationships, never by guessing from the description.
    /// Mixed bills must not inherit the first service's title. A void/refunded bill keeps its original identity.</summary>
    private static async Task<ReceiptKind> ReceiptKindAsync(AppDbContext db, long invoiceId, CancellationToken ct)
    {
        var all = await db.InvoiceItems.AsNoTracking()
            .Where(i => i.InvoiceId == invoiceId).OrderBy(i => i.Id)
            .Select(i => new { i.SourceType, i.SourceId, i.Description, i.ServiceId, i.RevenueAccount, i.IsVoid })
            .ToListAsync(ct);
        var sources = all.Any(i => !i.IsVoid) ? all.Where(i => !i.IsVoid).ToList() : all;
        var diagnosticIds = sources.Where(s => s.SourceType == nameof(DiagnosticOrder) && s.SourceId.HasValue).Select(s => s.SourceId!.Value).ToList();
        var diagnostics = await db.DiagnosticOrders.AsNoTracking().Where(o => diagnosticIds.Contains(o.Id))
            .Select(o => new { o.Id, o.Test!.Kind }).ToDictionaryAsync(o => o.Id, o => o.Kind, ct);
        var serviceIds = sources.Where(s => s.ServiceId.HasValue).Select(s => s.ServiceId!.Value).ToList();
        var services = await db.Services.AsNoTracking().Where(s => serviceIds.Contains(s.Id))
            .Select(s => new { s.Id, Department = s.Department != null ? s.Department.Name : null })
            .ToDictionaryAsync(s => s.Id, s => s.Department, ct);
        var saleKind = await db.Sales.AsNoTracking().Where(s => s.InvoiceId == invoiceId).Select(s => (SaleKind?)s.Kind).FirstOrDefaultAsync(ct);

        var categories = new List<(string Name, string Department)>();
        foreach (var source in sources)
        {
            (string Name, string Department) category;
            if (source.SourceType is nameof(VisitToken) or nameof(Visit)) category = ("فیس معاینه", "پذیرش و معاینه");
            else if (source.SourceType == nameof(SurgeryCase)) category = ("عملیات", "عملیات");
            else if (source.SourceType == nameof(DiagnosticOrder) && source.SourceId is { } diagnosticId && diagnostics.TryGetValue(diagnosticId, out var kind))
                category = kind switch
                {
                    DiagnosticKind.Refraction => ("رفراکشن", "رفراکشن / بینایی‌سنجی"),
                    DiagnosticKind.Imaging => ("تصویربرداری", "تصویربرداری"),
                    DiagnosticKind.Lab => ("لابراتوار", "لابراتوار / آزمایشگاه"),
                    _ => ("خدمات تشخیصی", "تشخیص"),
                };
            else if (source.RevenueAccount == Accounts.PharmacyRevenue) category = ("دواخانه", "دواخانه");
            else if (source.RevenueAccount == Accounts.OpticalRevenue) category = ("عینک", "عینک");
            else if ((source.SourceType is null or nameof(SaleItem)) && saleKind is { } sale)
                category = sale == SaleKind.Pharmacy ? ("دواخانه", "دواخانه") : ("عینک", "عینک");
            else category = ("خدمات", source.ServiceId is { } serviceId && services.GetValueOrDefault(serviceId) is { } department ? department : "صندوق");
            categories.Add(category);
        }

        var distinct = categories.Distinct().ToList();
        var name = distinct.Count == 1 ? distinct[0].Name : distinct.Count > 1 ? "خدمات چند بخش" : "خدمات";
        var section = distinct.Count > 0 ? string.Join("، ", distinct.Select(c => c.Department).Distinct()) : "صندوق";
        var purposes = sources.Select(s => s.Description.Trim()).Where(s => s.Length > 0).Distinct().ToList();
        return new($"رسید پرداخت {name}", $"فاکتور {name}", section, purposes,
            categories.Any(c => c.Name is "دواخانه" or "عینک"));
    }
}
