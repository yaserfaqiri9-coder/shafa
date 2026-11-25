using System.ComponentModel.DataAnnotations;
using System.Globalization;
using AlShifa.Api.Features.Finance;
using AlShifa.Api.Features.Auth;
using AlShifa.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace AlShifa.Api.Features.Users;

public class StaffSalaryTerm
{
    public long Id { get; set; }
    public long StaffId { get; set; }
    public Staff? Staff { get; set; }
    public DateOnly EffectiveFrom { get; set; }
    public decimal MonthlySalary { get; set; }
}

public sealed record StaffMonthPay(int Year, int Month, DateOnly From, DateOnly Through, int WorkingDays, int MonthDays, decimal Amount);
public sealed record StaffPayoutRequest([Range(0.01, 100000000)] decimal Amount, DateOnly Date,
    [Required, MaxLength(20)] string Kind, [MaxLength(300)] string? Note);

public static class StaffPayroll
{
    private static readonly PersianCalendar Calendar = new();
    public static DateOnly MonthStart(DateOnly day)
    {
        var date = day.ToDateTime(TimeOnly.MinValue);
        return DateOnly.FromDateTime(Calendar.ToDateTime(Calendar.GetYear(date), Calendar.GetMonth(date), 1, 0, 0, 0, 0));
    }
    public static DateOnly NextMonth(DateOnly first) => DateOnly.FromDateTime(Calendar.AddMonths(first.ToDateTime(TimeOnly.MinValue), 1));

    // This is a payroll subledger of earned salary. Cash is posted once, by the existing expense ledger,
    // when salary or a withdrawal is paid. Reads never create expenses or change financial records.
    public static List<StaffMonthPay> Earned(Staff staff, IReadOnlyList<StaffSalaryTerm> terms, DateOnly today)
    {
        var result = new List<StaffMonthPay>();
        if (staff.EmploymentStartDate is not { } start || terms.Count == 0) return result;
        var cutoff = MonthStart(today);
        var sorted = terms.OrderBy(t => t.EffectiveFrom).ToList();
        for (var first = MonthStart(start); first < cutoff && first <= (staff.EmploymentEndDate ?? today); first = NextMonth(first))
        {
            var end = NextMonth(first).AddDays(-1);
            decimal amount = 0;
            var days = 0;
            var monthDays = end.DayNumber - first.DayNumber + 1;
            for (var day = first; day <= end; day = day.AddDays(1))
            {
                if (day < start || day > staff.EmploymentEndDate) continue;
                var rate = sorted.LastOrDefault(t => t.EffectiveFrom <= day);
                if (rate is null) continue;
                amount += rate.MonthlySalary / monthDays;
                days++;
            }
            var dt = first.ToDateTime(TimeOnly.MinValue);
            result.Add(new StaffMonthPay(Calendar.GetYear(dt), Calendar.GetMonth(dt), first, end, days, monthDays,
                decimal.Round(amount, 2, MidpointRounding.AwayFromZero)));
        }
        return result;
    }

    public static async Task ApplyTermsAsync(Staff staff, SaveStaffRequest r, AppDbContext db, HttpContext http, AuditService audit, CancellationToken ct)
    {
        if (r.MonthlySalary is null && r.EmploymentStartDate is null && r.EmploymentEndDate is null) return;
        if (!http.User.HasClaim(Auth.RolePermissions.ClaimType, "expenses.create")) throw new AppException(403, "forbidden", "برای تعیین معاش صلاحیت مالی لازم است.");
        var terms = await db.StaffSalaryTerms.Where(t => t.StaffId == staff.Id).OrderBy(t => t.EffectiveFrom).ToListAsync(ct);
        var start = r.EmploymentStartDate ?? staff.EmploymentStartDate;
        if (start is null || start < new DateOnly(1950, 1, 1)) throw new AppException(400, "start_required", "تاریخ شروع کار را وارد کنید.");
        if (r.EmploymentEndDate < start) throw new AppException(400, "invalid_end_date", "تاریخ ختم پیش از شروع کار است.");
        if (terms.Count > 0 && staff.EmploymentStartDate != start)
            throw new AppException(409, "salary_history", "برای حفظ سابقه معاش، تاریخ شروع کار پس از تعیین معاش تغییر نمی‌کند.");
        // Do not silently recalculate completed payroll periods by moving the end date backwards.
        if (terms.Count > 0 && r.EmploymentEndDate != staff.EmploymentEndDate
            && (r.EmploymentEndDate < MonthStart(KabulClock.Today) || staff.EmploymentEndDate < MonthStart(KabulClock.Today)))
            throw new AppException(409, "salary_history", "تاریخ ختم جدید نباید ماه‌های بسته‌شده را تغییر دهد.");
        staff.EmploymentStartDate = start;
        staff.EmploymentEndDate = r.EmploymentEndDate;
        if (r.MonthlySalary is not { } salary) return;
        Money.EnsureCents(salary);
        if (salary < 0 || salary > 100000000) throw new AppException(400, "invalid_salary", "معاش معتبر نیست.");
        if (terms.LastOrDefault()?.MonthlySalary == salary) return;
        db.Entry(staff).Property(s => s.Version).IsModified = true;
        var effective = terms.Count == 0 ? start.Value : NextMonth(MonthStart(KabulClock.Today));
        if (effective < start) effective = start.Value;
        var scheduled = terms.SingleOrDefault(t => t.EffectiveFrom == effective);
        if (scheduled is not null) scheduled.MonthlySalary = salary;
        else db.StaffSalaryTerms.Add(new StaffSalaryTerm { StaffId = staff.Id, EffectiveFrom = effective, MonthlySalary = salary });
        audit.Add("StaffSalaryChanged", nameof(Staff), staff.Id.ToString(), effective.ToString("yyyy-MM-dd"));
    }

    public static void MapStaffPayroll(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/staff");
        group.MapGet("/payouts", async (AppDbContext db, CancellationToken ct) =>
            Results.Ok(await db.Expenses.AsNoTracking().Where(e => e.StaffId != null).OrderByDescending(e => e.Id).Take(500)
                .Select(e => new { e.Id, e.StaffId, StaffName = e.Staff!.FullName, e.ExpenseNumber, e.Amount,
                    Date = e.ExpenseDate, Kind = e.StaffPaymentKind, Status = e.Status.ToString(), Note = e.Description }).ToListAsync(ct)))
            .RequirePermission("expenses.view");
        group.MapGet("/{id:long}/payroll", async (long id, AppDbContext db, CancellationToken ct) =>
        {
            var staff = await db.Staff.AsNoTracking().SingleOrDefaultAsync(s => s.Id == id, ct) ?? throw AppException.NotFound();
            var terms = await db.StaffSalaryTerms.AsNoTracking().Where(t => t.StaffId == id).OrderBy(t => t.EffectiveFrom).ToListAsync(ct);
            var months = Earned(staff, terms, KabulClock.Today);
            var payments = await db.Expenses.AsNoTracking().Where(e => e.StaffId == id).OrderByDescending(e => e.Id)
                .Select(e => new { e.Id, e.ExpenseNumber, e.Amount, Date = e.ExpenseDate, Kind = e.StaffPaymentKind, Note = e.Description, Status = e.Status.ToString(), e.Version }).ToListAsync(ct);
            var paid = payments.Where(p => p.Status == nameof(ExpenseStatus.Recorded)).Sum(p => p.Amount);
            var earned = months.Sum(m => m.Amount);
            return Results.Ok(new { staff.Id, staff.FullName, staff.Phone, staff.IsActive, staff.EmploymentStartDate, staff.EmploymentEndDate,
                MonthlySalary = terms.LastOrDefault(t => t.EffectiveFrom <= KabulClock.Today)?.MonthlySalary ?? 0,
                Terms = terms.Select(t => new { t.EffectiveFrom, t.MonthlySalary }), Months = months, Payments = payments,
                Earned = earned, Paid = paid, Balance = earned - paid, Owed = Math.Max(0, earned - paid), Advance = Math.Max(0, paid - earned) });
        }).RequirePermission("expenses.view");

        group.MapPost("/{id:long}/payouts", async (long id, StaffPayoutRequest r, AppDbContext db, Idempotency idempotency, NumberService numbers,
            LedgerService ledger, CurrentUser user, AuditService audit, CancellationToken ct) =>
            await idempotency.RunAsync(new { id, request = r }, async () =>
            {
                var staff = (await db.Staff.FromSqlInterpolated($"SELECT * FROM Staff WHERE Id = {id} FOR UPDATE").ToListAsync(ct)).SingleOrDefault() ?? throw AppException.NotFound();
                Money.EnsureCents(r.Amount);
                if (r.Amount <= 0 || r.Date > KabulClock.Today || r.Kind is not ("Salary" or "Withdrawal"))
                    throw new AppException(400, "invalid_payout", "مبلغ، تاریخ یا نوع پرداخت معتبر نیست.");
                if (staff.EmploymentStartDate is null || r.Date < staff.EmploymentStartDate)
                    throw new AppException(400, "salary_not_configured", "ابتدا تاریخ شروع کار و معاش را تعیین کنید.");
                var terms = await db.StaffSalaryTerms.Where(t => t.StaffId == id).ToListAsync(ct);
                var earned = Earned(staff, terms, r.Date).Sum(m => m.Amount);
                // A current locking read is required even after the staff lock: a repeatable-read snapshot
                // can predate the payment committed by the request we waited for.
                var payouts = await db.Expenses.FromSqlInterpolated($"SELECT * FROM Expenses WHERE StaffId = {id} AND Status = 'Recorded' FOR UPDATE").AsNoTracking().ToListAsync(ct);
                var paid = payouts.Sum(e => e.Amount);
                if (r.Kind == "Salary" && r.Amount > Math.Max(0, earned - paid))
                    throw new AppException(409, "salary_overpayment", "مبلغ از معاش باقی‌مانده بیشتر است. پیش‌پرداخت را به‌عنوان برداشت ثبت کنید.");
                var expense = new Expense { ExpenseNumber = await numbers.NextNumberAsync("expense", ct), StaffId = id,
                    StaffPaymentKind = r.Kind, Amount = r.Amount, ExpenseDate = r.Date, Category = r.Kind == "Salary" ? "معاش کارمندان" : "برداشت کارمندان",
                    Description = Text.Clean(r.Note), CreatedByUserId = user.Id };
                db.Expenses.Add(expense);
                await db.SaveChangesAsync(ct);
                ledger.Post("Expense", nameof(Expense), expense.Id, expense.Category, (Accounts.Expenses, r.Amount, 0), (Accounts.Cash, 0, r.Amount));
                audit.Add("StaffPayout", nameof(Staff), id.ToString(), expense.ExpenseNumber);
                await db.SaveChangesAsync(ct);
                return (StatusCodes.Status201Created, new { expense.Id, expense.ExpenseNumber });
            }, ct)).RequirePermission("expenses.create");
    }
}
