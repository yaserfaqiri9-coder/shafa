using AlShifa.Api.Features.Clinical;
using AlShifa.Api.Features.Finance;
using AlShifa.Api.Features.Inventory;
using AlShifa.Api.Features.Visits;
using AlShifa.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace AlShifa.Api.Features.Reports;

/// <summary>The names money is reported under, in the fixed order the reports list them.</summary>
public static class ReportSections
{
    public const string DoctorExam = "معاینه داکتر";
    public const string Refraction = "رفرکشن";
    public const string Imaging = "عکس‌برداری";
    public const string Lab = "لابراتوار";
    public const string Surgery = "جراحی";
    public const string Pharmacy = "دواخانه";
    public const string Optical = "عینک";
    public const string Other = "سایر خدمات";

    /// <summary>Always listed, even with nothing in the period, so the owner sees the same rows every day.</summary>
    public static readonly string[] Core = [DoctorExam, Refraction, Imaging, Lab, Surgery, Pharmacy, Optical];
}

/// <summary>
/// One section's money in a period, by the day it was actually received or paid back. Net = Received − Refunded.
/// A charge that is never paid is never in it, whether or not it is cancelled.
/// </summary>
/// <param name="Count">Receipts that paid for this section in the period.</param>
public sealed record SectionFigures(string Name, int Count, decimal Received, decimal Refunded)
{
    public decimal Net => Received - Refunded;
}

/// <summary>What one sales section took in and the purchase price of the goods it handed over. Profit is null when the cost of some goods sold is not known.</summary>
public sealed record SalesFigures(string Name, int Count, decimal Net, decimal Cost, decimal UnknownCostRevenue, int UnknownCostLines)
{
    public decimal? Profit => UnknownCostLines > 0 ? null : Net - Cost;
}

/// <summary>
/// Every money figure of a period, from one set of queries, so no two reports can disagree.
/// Revenue is money received from patients, on the day it was received; money paid back is shown on its own, on the day it was paid.
/// Expenses, costs and the cash account come from the ledger by the time each entry was posted (Kabul day).
/// </summary>
public sealed class FinanceFigures
{
    public required IReadOnlyList<SectionFigures> Sections { get; init; }

    /// <summary>Money received from patients in the period: the hospital's revenue.</summary>
    public required decimal Receipts { get; init; }

    public required decimal ReceiptsCash { get; init; }

    public required int ReceiptCount { get; init; }

    /// <summary>Money actually paid back to patients in the period.</summary>
    public required decimal RefundsPaid { get; init; }

    /// <summary>Revenue less money paid back.</summary>
    public decimal NetRevenue => Receipts - RefundsPaid;

    public required decimal Expenses { get; init; }

    /// <summary>What is left of the money received after paying patients back and paying the expenses.</summary>
    public decimal NetReceived => NetRevenue - Expenses;

    /// <summary>Discounts given in the period. Never revenue nor a deduction from it: only money received counts.</summary>
    public required decimal Discounts { get; init; }

    /// <summary>Purchase price of goods handed over or used in surgery in the period.</summary>
    public required decimal Cost { get; init; }

    public required decimal StockAdjustments { get; init; }

    public required decimal CashOpening { get; init; }

    public required decimal CashClosing { get; init; }

    /// <summary>Money in the cash account that is neither a receipt, a refund nor an expense, such as an opening balance.</summary>
    public decimal CashOther => CashClosing - CashOpening - Receipts + RefundsPaid + Expenses;

    public required decimal Purchases { get; init; }

    public required IReadOnlyList<SalesFigures> Sales { get; init; }
}

public static class ReportFigures
{
    /// <summary>Which section an invoice line's money belongs to. The one place that decides it for every report.</summary>
    internal sealed class ItemSection
    {
        public long ItemId { get; init; }

        public long InvoiceId { get; init; }

        public string Section { get; init; } = "";

        public decimal LineTotal { get; init; }

        public bool IsVoid { get; init; }
    }

    internal static IQueryable<ItemSection> ItemSections(AppDbContext db) =>
        from item in db.InvoiceItems.AsNoTracking()
        join service in db.Services.AsNoTracking() on item.ServiceId equals (int?)service.Id into services
        from service in services.DefaultIfEmpty()
        join department in db.Departments.AsNoTracking() on service.DepartmentId equals (int?)department.Id into departments
        from department in departments.DefaultIfEmpty()
        join diagnostic in db.DiagnosticOrders.AsNoTracking() on new { item.SourceType, item.SourceId } equals new { SourceType = (string?)nameof(DiagnosticOrder), SourceId = (long?)diagnostic.Id } into diagnostics
        from diagnostic in diagnostics.DefaultIfEmpty()
        select new ItemSection
        {
            ItemId = item.Id,
            InvoiceId = item.InvoiceId,
            LineTotal = item.LineTotal,
            IsVoid = item.IsVoid,
            Section = item.RevenueAccount == Accounts.PharmacyRevenue ? ReportSections.Pharmacy
                : item.RevenueAccount == Accounts.OpticalRevenue ? ReportSections.Optical
                : item.SourceType == nameof(SurgeryCase) ? ReportSections.Surgery
                : diagnostic != null ? (diagnostic.Test!.Kind == DiagnosticKind.Lab ? ReportSections.Lab : diagnostic.Test.Kind == DiagnosticKind.Refraction ? ReportSections.Refraction : ReportSections.Imaging)
                : item.SourceType == nameof(VisitToken) || item.SourceType == nameof(Visit) ? ReportSections.DoctorExam
                : service != null && service.Code == ServiceCodes.VisitFee ? ReportSections.DoctorExam
                : service != null && service.Code == ServiceCodes.Refraction ? ReportSections.Refraction
                : service != null && service.Code == ServiceCodes.Imaging ? ReportSections.Imaging
                : department == null ? ReportSections.Other
                : department.Code == "doctor" || department.Code == "triage" ? ReportSections.DoctorExam
                : department.Code == "lab" ? ReportSections.Lab
                : department.Code == "surgery" ? ReportSections.Surgery
                : department.Code == "pharmacy" ? ReportSections.Pharmacy
                : department.Code == "optical" ? ReportSections.Optical
                : department.Name,
        };

    public static async Task<FinanceFigures> LoadAsync(DateTime startUtc, DateTime endUtc, AppDbContext db, CancellationToken ct)
    {
        var movement = await (
            from e in db.LedgerEntries.AsNoTracking()
            join t in db.LedgerTransactions.AsNoTracking() on e.TransactionId equals t.Id
            where t.CreatedAt >= startUtc && t.CreatedAt < endUtc
            group e by e.AccountCode into g
            select new { Code = g.Key, Debit = g.Sum(x => x.Debit), Credit = g.Sum(x => x.Credit) }).ToListAsync(ct);
        decimal DebitNet(string code) => movement.Where(m => m.Code == code).Sum(m => m.Debit - m.Credit);

        var cashOpening = await (
            from e in db.LedgerEntries.AsNoTracking()
            join t in db.LedgerTransactions.AsNoTracking() on e.TransactionId equals t.Id
            where e.AccountCode == Accounts.Cash && t.CreatedAt < startUtc
            select (decimal?)(e.Debit - e.Credit)).SumAsync(ct) ?? 0;

        var payments = await db.Payments.AsNoTracking().Where(p => p.CreatedAt >= startUtc && p.CreatedAt < endUtc)
            .Select(p => new Received(p.Id, p.InvoiceId, p.Amount, p.Method == PaymentMethod.Cash)).ToListAsync(ct);
        var refunds = await (
            from r in db.Refunds.AsNoTracking()
            join p in db.Payments.AsNoTracking() on r.PaymentId equals p.Id
            where r.CreatedAt >= startUtc && r.CreatedAt < endUtc
            select new Received(r.Id, p.InvoiceId, r.Amount, false)).ToListAsync(ct);

        var sections = await SectionsAsync(startUtc, endUtc, payments, refunds, db, ct);
        var sales = await SalesAsync(startUtc, endUtc, sections, db, ct);

        return new FinanceFigures
        {
            Sections = sections,
            Receipts = payments.Sum(p => p.Amount),
            ReceiptsCash = payments.Where(p => p.Cash).Sum(p => p.Amount),
            ReceiptCount = payments.Count,
            RefundsPaid = refunds.Sum(r => r.Amount),
            Expenses = DebitNet(Accounts.Expenses),
            Discounts = DebitNet(Accounts.Discounts),
            Cost = DebitNet(Accounts.CostOfGoods),
            StockAdjustments = DebitNet(Accounts.InventoryAdjustments),
            CashOpening = cashOpening,
            CashClosing = cashOpening + DebitNet(Accounts.Cash),
            Purchases = -DebitNet(Accounts.Payable),
            Sales = sales,
        };
    }

    private sealed record Received(long Id, long InvoiceId, decimal Amount, bool Cash);

    /// <summary>
    /// The money each section received and paid back. A receipt or refund on an invoice of one section is all that section's;
    /// on an invoice of several sections it is split by what each section's lines charged, to the cent, so the rows add up to the whole.
    /// </summary>
    private static async Task<List<SectionFigures>> SectionsAsync(DateTime startUtc, DateTime endUtc, List<Received> payments, List<Received> refunds, AppDbContext db, CancellationToken ct)
    {
        var lines = await ItemSections(db)
            .Where(s => db.Payments.Any(p => p.InvoiceId == s.InvoiceId && p.CreatedAt >= startUtc && p.CreatedAt < endUtc)
                || db.Refunds.Any(r => r.CreatedAt >= startUtc && r.CreatedAt < endUtc && db.Payments.Any(p => p.Id == r.PaymentId && p.InvoiceId == s.InvoiceId)))
            .Select(s => new { s.InvoiceId, s.Section, s.LineTotal, s.IsVoid }).ToListAsync(ct);

        // Each invoice's share per section: its lines still charged, or, when all were taken back, the lines it had.
        var shares = lines.GroupBy(l => l.InvoiceId).ToDictionary(g => g.Key, g =>
        {
            var charged = g.Where(l => l.LineTotal > 0 && !l.IsVoid).ToList();
            if (charged.Count == 0) charged = g.Where(l => l.LineTotal > 0).ToList();
            return charged.GroupBy(l => l.Section).Select(x => (Section: x.Key, Weight: x.Sum(l => l.LineTotal)))
                .OrderByDescending(x => x.Weight).ThenBy(x => x.Section).ToList();
        });

        IEnumerable<(string Section, decimal Amount)> Split(Received money)
        {
            if (!shares.TryGetValue(money.InvoiceId, out var parts) || parts.Count == 0)
            {
                return [(ReportSections.Other, money.Amount)];
            }

            var total = parts.Sum(x => x.Weight);
            var rest = parts.Skip(1).Select(x => (x.Section, Amount: decimal.Round(money.Amount * x.Weight / total, 2, MidpointRounding.AwayFromZero))).ToList();
            // The largest share takes the rounding, so the parts add up to the receipt exactly.
            return [(parts[0].Section, money.Amount - rest.Sum(x => x.Amount)), .. rest];
        }

        var received = payments.SelectMany(p => Split(p).Select(x => (x.Section, x.Amount, p.Id))).ToList();
        var paidBack = refunds.SelectMany(Split).ToList();

        var names = ReportSections.Core
            .Concat(received.Select(r => r.Section).Concat(paidBack.Select(r => r.Section)).Distinct().Where(n => !ReportSections.Core.Contains(n)).OrderBy(n => n))
            .ToList();

        return names.Select(name => new SectionFigures(name,
            received.Where(r => r.Section == name).Select(r => r.Id).Distinct().Count(),
            received.Where(r => r.Section == name).Sum(r => r.Amount),
            paidBack.Where(r => r.Section == name).Sum(r => r.Amount))).ToList();
    }

    /// <summary>
    /// What pharmacy and optical took in, against the purchase price of the goods they handed over in the period.
    /// Goods whose purchase price is not recorded make the profit unknown.
    /// </summary>
    private static async Task<List<SalesFigures>> SalesAsync(DateTime startUtc, DateTime endUtc, List<SectionFigures> sections, AppDbContext db, CancellationToken ct)
    {
        var posted = await (
            from e in db.LedgerEntries.AsNoTracking()
            join t in db.LedgerTransactions.AsNoTracking() on e.TransactionId equals t.Id
            join s in db.Sales.AsNoTracking() on t.SourceId equals s.Id
            where t.SourceType == nameof(Sale) && e.AccountCode == Accounts.CostOfGoods && t.CreatedAt >= startUtc && t.CreatedAt < endUtc
            select new { s.Kind, Amount = e.Debit - e.Credit }).ToListAsync(ct);

        // Goods sold with no purchase price: from a batch entered without cost, or a made-to-order lens.
        var noCost = await db.Sales.AsNoTracking()
            .Where(s => s.CreatedAt >= startUtc && s.CreatedAt < endUtc && !s.IsReturned)
            .SelectMany(s => s.Items.Where(i => i.UnitCost <= 0 && i.UnitPrice > 0).Select(i => new { s.Kind, Amount = i.Quantity * i.UnitPrice }))
            .ToListAsync(ct);
        var lenses = await (
            from i in db.InvoiceItems.AsNoTracking()
            join t in db.LedgerTransactions.AsNoTracking() on new { SourceType = "InvoiceItem", SourceId = i.Id } equals new { t.SourceType, t.SourceId }
            where i.SourceType == "OpticalNonStockLens" && !i.IsVoid && i.LineTotal > 0 && t.Kind == "Charge" && t.CreatedAt >= startUtc && t.CreatedAt < endUtc
            select i.LineTotal).ToListAsync(ct);

        SalesFigures Row(string name, SaleKind kind)
        {
            var section = sections.Single(s => s.Name == name);
            var unknown = noCost.Where(n => n.Kind == kind).ToList();
            var cost = posted.Where(p => p.Kind == kind).Sum(p => p.Amount);
            var unknownAmount = unknown.Sum(u => u.Amount) + (kind == SaleKind.Optical ? lenses.Sum() : 0);
            var unknownLines = unknown.Count + (kind == SaleKind.Optical ? lenses.Count : 0);
            return new SalesFigures(name, section.Count, section.Net, cost, unknownAmount, unknownLines);
        }

        return [Row(ReportSections.Pharmacy, SaleKind.Pharmacy), Row(ReportSections.Optical, SaleKind.Optical)];
    }
}
