using AlShifa.Api.Features.Auth;
using AlShifa.Api.Features.Clinical;
using AlShifa.Api.Features.Finance;
using AlShifa.Api.Features.Patients;
using AlShifa.Api.Features.Visits;
using AlShifa.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace AlShifa.Api.Features.Reports;

public sealed record CountRow(string Name, int Count);

public sealed record CashierRow(string Cashier, decimal Total);

/// <summary>Home page money. Revenue fields are money received less money paid back, on the day it changed hands; Refunds is money paid back.</summary>
public sealed record FinancialReport(
    DateOnly From, DateOnly To, decimal ServiceRevenue, decimal PharmacyRevenue, decimal OpticalRevenue, decimal Discounts, decimal Refunds,
    decimal NetRevenue, decimal CostOfGoods, decimal Expenses, decimal InventoryAdjustments, decimal Receipts, IReadOnlyList<CashierRow> ReceiptsByCashier,
    decimal OutstandingNow, int OpenInvoicesNow, decimal StockValueNow);

public sealed record DashboardDay(DateOnly Date, int Completed, int Active, int Cancelled, int Male, int Female);

public sealed record DashboardReport(
    DateOnly Today, int YesterdayVisits, IReadOnlyList<DashboardDay> Days, IReadOnlyList<CountRow> VisitsByDepartment, IReadOnlyList<CountRow> TodayByStatus, int LowStockProducts);

public static class ReportEndpoints
{
    public static void MapReports(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/reports").WithTags("Reports");

        group.MapReportDocuments();

        // Counts only: no patient names or clinical detail, so it is safe for every role that can see reports.
        group.MapGet("/dashboard", async (AppDbContext db, CancellationToken ct) =>
        {
            var today = KabulClock.Today;
            var firstDay = today.AddDays(-5);
            var rows = await db.Visits.AsNoTracking()
                .Where(v => v.VisitDate >= firstDay && v.VisitDate <= today && v.Status != VisitStatus.AwaitingPayment)
                .GroupBy(v => new { v.VisitDate, v.Status, v.Patient!.Gender })
                .Select(g => new { g.Key.VisitDate, g.Key.Status, g.Key.Gender, Count = g.Count() })
                .ToListAsync(ct);

            var days = Enumerable.Range(0, 6).Select(offset =>
            {
                var date = firstDay.AddDays(offset);
                var day = rows.Where(r => r.VisitDate == date).ToList();
                var cancelled = day.Where(r => r.Status == VisitStatus.Cancelled).Sum(r => r.Count);
                var completed = day.Where(r => r.Status == VisitStatus.Completed).Sum(r => r.Count);
                var attended = day.Where(r => r.Status != VisitStatus.Cancelled).ToList();
                return new DashboardDay(
                    date, completed, attended.Sum(r => r.Count) - completed, cancelled,
                    attended.Where(r => r.Gender == Gender.Male).Sum(r => r.Count),
                    attended.Where(r => r.Gender == Gender.Female).Sum(r => r.Count));
            }).ToList();

            var monthStart = today.AddDays(-29);
            var counts = await db.Visits.AsNoTracking()
                .Where(v => v.VisitDate >= monthStart && v.VisitDate <= today && v.Status != VisitStatus.Cancelled && v.Status != VisitStatus.AwaitingPayment)
                .GroupBy(v => v.DepartmentId)
                .Select(g => new { DepartmentId = g.Key, Count = g.Count() })
                .ToListAsync(ct);
            var departments = await db.Departments.AsNoTracking().OrderBy(d => d.Id).Select(d => new { d.Id, d.Name }).ToListAsync(ct);

            return Results.Ok(new DashboardReport(
                today,
                rows.Where(r => r.VisitDate == today.AddDays(-1)).Sum(r => r.Count),
                days,
                departments.Select(d => new CountRow(d.Name, counts.FirstOrDefault(c => c.DepartmentId == d.Id)?.Count ?? 0)).ToList(),
                rows.Where(r => r.VisitDate == today).GroupBy(r => r.Status).Select(g => new CountRow(g.Key.ToString(), g.Sum(r => r.Count))).ToList(),
                await db.Products.AsNoTracking().CountAsync(p => p.IsActive && p.ReorderLevel > 0 && (db.StockBatches.Where(b => b.ProductId == p.Id).Sum(b => (decimal?)b.Quantity) ?? 0) <= p.ReorderLevel, ct)));
        }).RequirePermission("reports.view");

        group.MapGet("/financial", async (DateOnly? from, DateOnly? to, AppDbContext db, CancellationToken ct) =>
        {
            var (start, end) = Range(from, to);
            return Results.Ok(await LoadFinancialAsync(start, end, db, ct));
        }).RequirePermission("ledger.view");
    }

    /// <summary>The home page figures, from the same calculation as the report tabs, so the two can never disagree.</summary>
    internal static async Task<FinancialReport> LoadFinancialAsync(DateOnly start, DateOnly end, AppDbContext db, CancellationToken ct)
    {
        var (startUtc, endUtc) = UtcRange(start, end);
        var f = await ReportFigures.LoadAsync(startUtc, endUtc, db, ct);
        decimal Revenue(string name) => f.Sections.Where(x => x.Name == name).Sum(x => x.Net);

        var byCashier = await (
            from p in db.Payments.AsNoTracking()
            join u in db.Users on p.ReceivedByUserId equals u.Id
            where p.CreatedAt >= startUtc && p.CreatedAt < endUtc
            group p by u.FullName into g
            select new CashierRow(g.Key, g.Sum(x => x.Amount))).ToListAsync(ct);

        var open = db.Invoices.AsNoTracking().Where(i => (i.Status == InvoiceStatus.Issued || i.Status == InvoiceStatus.PartiallyPaid) && i.Total - i.Discount - i.Paid + i.Refunded > 0);
        var pharmacy = Revenue(ReportSections.Pharmacy);
        var optical = Revenue(ReportSections.Optical);

        return new FinancialReport(
            start, end, f.NetRevenue - pharmacy - optical, pharmacy, optical, f.Discounts, f.RefundsPaid,
            f.NetRevenue,
            f.Cost,
            f.Expenses,
            f.StockAdjustments,
            f.Receipts,
            byCashier,
            await open.SumAsync(i => (decimal?)(i.Total - i.Discount - i.Paid + i.Refunded), ct) ?? 0,
            await open.CountAsync(ct),
            await db.StockBatches.AsNoTracking().Where(b => b.Quantity > 0).SumAsync(b => (decimal?)(b.Quantity * b.UnitCost), ct) ?? 0);
    }

    internal static (DateOnly Start, DateOnly End) Range(DateOnly? from, DateOnly? to)
    {
        var start = from ?? KabulClock.Today;
        var end = to ?? start;
        if (end < start)
        {
            throw new AppException(StatusCodes.Status400BadRequest, "invalid_range", "تاریخ پایان نمی‌تواند پیش از تاریخ شروع باشد.");
        }

        return (start, end);
    }

    /// <summary>Kabul is UTC+4:30 all year, so a Kabul calendar day starts at 19:30 UTC the day before.</summary>
    internal static (DateTime Start, DateTime End) UtcRange(DateOnly start, DateOnly end) =>
        (start.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc).AddHours(-4.5), end.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc).AddHours(-4.5));
}
