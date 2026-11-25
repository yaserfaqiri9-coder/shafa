using AlShifa.Api.Features.Auth;
using AlShifa.Api.Features.Clinical;
using AlShifa.Api.Features.Finance;
using AlShifa.Api.Features.Inventory;
using AlShifa.Api.Features.Settings;
using AlShifa.Api.Features.Visits;
using AlShifa.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace AlShifa.Api.Features.Reports;

/// <summary>How a value is shown: count, money, quantity, text, date or datetime.</summary>
public static class ReportKind
{
    public const string Count = "count";
    public const string Money = "money";
    public const string Quantity = "quantity";
    public const string Text = "text";
    public const string Date = "date";
    public const string DateTime = "datetime";
}

/// <param name="Value">Null when the recorded data cannot give a true figure; it is then shown as «نامعلوم», never as a guess.</param>
/// <param name="Note">One short line under the figure that says what it is, such as what it is not to be confused with.</param>
/// <param name="Main">One of the few (at most four) figures the page shows on top of the tab. The others stay in the document for
/// the home page and the checks that tie the tabs together, and each is also a line of one of the reports below.</param>
/// <param name="Item">Key of the report table that details this figure; the page opens it on one click.</param>
public sealed record ReportTile(string Label, decimal? Value, string Kind, string? Note = null, bool Main = false, string? Item = null);

public sealed record ReportColumn(string Label, string Kind);

/// <param name="Note">One plain sentence, only when a reader could otherwise misread the list, such as a list cut short.</param>
/// <param name="Key">The report's own name in the page address and the export link, so one report opens, prints and exports alone.</param>
public sealed record ReportTable(string Title, IReadOnlyList<ReportColumn> Columns, IReadOnlyList<IReadOnlyList<object?>> Rows, IReadOnlyList<object?>? Total = null, string? Note = null, string Key = "");

public sealed record ReportPoint(DateOnly Date, decimal Value);

public sealed record ReportTrend(string Title, string Kind, IReadOnlyList<ReportPoint> Points);

/// <summary>
/// One report tab in the single shape the page shows and the Excel and PDF files are made from: the main figures on top,
/// then the lists behind them, which the page opens only on request.
/// </summary>
/// <param name="Warnings">Plain sentences on what the figures cannot show, such as a profit that cannot be known.</param>
public sealed record ReportDocument(
    string Title, string Description, DateOnly? From, DateOnly? To, IReadOnlyList<ReportTile> Tiles, ReportTrend? Trend, IReadOnlyList<ReportTable> Tables,
    IReadOnlyList<string>? Warnings = null);

/// <summary>
/// The three reports: the hospital at a glance, money and accounts, pharmacy and store. Every money figure comes from
/// <see cref="ReportFigures"/>, so a figure shown on two tabs is always the same number.
/// </summary>
public static class ReportDocuments
{
    /// <summary>A list longer than this is cut, with a sentence saying so; a narrower period shows the rest.</summary>
    private const int MaxListRows = 3000;

    private const int NearExpiryDays = 90;

    private sealed record Period(DateOnly Start, DateOnly End, DateTime StartUtc, DateTime EndUtc);

    private delegate Task<ReportDocument> Builder(Period period, AppDbContext db, HttpContext http, CancellationToken ct);

    /// <param name="Permission">Needed to reach the report at all.</param>
    private sealed record Definition(string Permission, Builder Build);

    private static readonly Dictionary<string, Definition> Reports = new()
    {
        // Counts for everyone who may see reports; its money only for those who may see the books.
        ["overview"] = new("reports.view", OverviewAsync),
        ["finance"] = new("ledger.view", FinanceAsync),
        // Sales are shown to each section that may see them; the store's state is the same for all.
        ["pharmacy"] = new("inventory.view", PharmacyAsync),
    };

    public static void MapReportDocuments(this IEndpointRouteBuilder group)
    {
        foreach (var (key, definition) in Reports)
        {
            group.MapGet($"/{key}", async (DateOnly? from, DateOnly? to, AppDbContext db, HttpContext http, CancellationToken ct) =>
                Results.Ok(await BuildAsync(definition, from, to, db, http, ct))).RequirePermission(definition.Permission);

            // The same document as the page, written as an Excel workbook or a PDF page by page.
            group.MapGet($"/{key}/export", async (string? format, string? item, DateOnly? from, DateOnly? to, AppDbContext db, HttpContext http, CancellationToken ct) =>
            {
                var excel = string.Equals(format, "xlsx", StringComparison.OrdinalIgnoreCase);
                if (!excel && !string.Equals(format, "pdf", StringComparison.OrdinalIgnoreCase))
                {
                    throw new AppException(StatusCodes.Status400BadRequest, "invalid_format", "نوع فایل باید xlsx یا pdf باشد.");
                }

                Need(http, excel ? ["reports.export"] : ["reports.print", "reports.export"]);
                var document = await BuildAsync(definition, from, to, db, http, ct);
                var name = $"report-{key}-{ReportText.FileDate(document.From ?? KabulClock.Today)}";
                if (!string.IsNullOrEmpty(item))
                {
                    document = Single(document, item);
                    name = $"report-{key}-{item}-{ReportText.FileDate(document.From ?? KabulClock.Today)}";
                }

                var hospital = (await SettingsEndpoints.LoadAsync(db, ct)).HospitalName;
                return excel
                    ? Results.File(ReportExcel.Build(document, hospital), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"{name}.xlsx")
                    : Results.File(ReportPdf.Build(document, hospital), "application/pdf", $"{name}.pdf");
            }).RequirePermission(definition.Permission);
        }
    }

    /// <summary>Reports whose figures rest on the tab's definitions and warnings, such as a profit that cannot be known; they carry both into their file.</summary>
    private static readonly HashSet<string> ExplainedReports = ["sections", "profit", "cash", "sales"];

    /// <summary>
    /// One report of a tab as a document of its own: its title, days, rows and total, built from the very same document the page shows,
    /// so the file always holds exactly the numbers on screen and nothing of the other reports.
    /// </summary>
    private static ReportDocument Single(ReportDocument document, string item)
    {
        var table = document.Tables.FirstOrDefault(t => t.Key == item)
            ?? throw new AppException(StatusCodes.Status404NotFound, "report_not_found", "این گزارش پیدا نشد یا شما به آن دسترسی ندارید.");
        var explained = ExplainedReports.Contains(item);
        // The tab's name heads the file, and the report's own title stands over its list, so the two are never printed twice.
        return new ReportDocument(document.Title, explained ? document.Description : "", document.From, document.To, [], null, [table], explained ? document.Warnings : null);
    }

    private static Task<ReportDocument> BuildAsync(Definition definition, DateOnly? from, DateOnly? to, AppDbContext db, HttpContext http, CancellationToken ct)
    {
        var (start, end) = ReportEndpoints.Range(from, to);
        var (startUtc, endUtc) = ReportEndpoints.UtcRange(start, end);
        return definition.Build(new Period(start, end, startUtc, endUtc), db, http, ct);
    }

    private static bool Has(HttpContext http, string permission) => http.User.HasClaim(RolePermissions.ClaimType, permission);

    private static void Need(HttpContext http, string[] any)
    {
        if (!any.Any(permission => Has(http, permission)))
        {
            throw new AppException(StatusCodes.Status403Forbidden, "forbidden", "شما به این گزارش دسترسی ندارید.");
        }
    }

    private static ReportColumn Text(string label) => new(label, ReportKind.Text);

    private static ReportColumn Count(string label) => new(label, ReportKind.Count);

    private static ReportColumn Money(string label) => new(label, ReportKind.Money);

    private static ReportColumn Quantity(string label) => new(label, ReportKind.Quantity);

    private static ReportColumn Date(string label) => new(label, ReportKind.Date);

    private static ReportTile CountTile(string label, decimal value, string? note = null, bool main = false) => new(label, value, ReportKind.Count, note, main);

    private static ReportTile MoneyTile(string label, decimal? value, string? note = null, bool main = false, string? item = null) => new(label, value, ReportKind.Money, note, main, item);

    private static object?[] Row(params object?[] cells) => cells;

    /// <summary>A figure that cannot be known is written as a word in a list, the same as on a tile.</summary>
    private static object Known(decimal? value) => value is { } v ? v : ReportText.Unknown;

    private static string? Cut(int shown, int total, string what) => total > shown ? $"فقط {shown} {what} اول نشان داده شده است. برای دیدن بقیه، دوره کوتاه‌تری انتخاب کنید." : null;

    private static string FullName(string first, string? last) => $"{first} {last}".Trim();

    private static DateOnly KabulDay(DateTime utc) => DateOnly.FromDateTime(utc.AddHours(4.5));

    private static string ProfitNote(decimal? profit) => profit switch
    {
        null => "قیمت خرید بعضی اجناس ثبت نیست",
        < 0 => "زیان",
        _ => "سود",
    };

    /// <summary>Why a profit cannot be known, in words the owner can act on.</summary>
    private static string UnknownCostWarning(int lines, decimal amount) =>
        $"قیمت خرید {ReportText.Number(lines)} قلم جنس فروخته‌شده (به ارزش فروش {ReportText.Money(amount)}) در سیستم ثبت نیست؛ " +
        "این‌ها اجناسی‌اند که بدون قیمت خرید وارد گدام شده‌اند یا شیشه عینکی که از بیرون سفارش شده است. " +
        "تا قیمت خرید آن‌ها معلوم نباشد، سود واقعی حساب نمی‌شود و به‌جای رقم «نامعلوم» نوشته می‌شود.";

    private const string MoneyDefinition =
        "عواید فقط پولی است که واقعاً از مریضان گرفته شده، به تاریخ دریافت؛ خدمت پرداخت‌نشده عاید نیست، چه لغو شود چه نه. " +
        "پول برگشت‌داده‌شده به تاریخ برگشت جدا نشان داده می‌شود. خالص دریافتی = عواید − پول برگشت‌داده‌شده − مصارف. " +
        "پول رسیدی که برای چند بخش بوده، به نسبت قیمت خدمات هر بخش تقسیم شده است.";

    /// <summary>The same four money figures on the overview and the money tab; the overview keeps two of them on top, beside its patients.</summary>
    private static ReportTile[] MoneyTiles(FinanceFigures f, bool allMain = true) =>
    [
        MoneyTile("عواید", f.Receipts, $"پول دریافت‌شده · {ReportText.Number(f.ReceiptCount)} رسید", main: true, item: "sections"),
        MoneyTile("پول برگشت‌داده‌شده", f.RefundsPaid, "پولی که به مریضان پس داده شد", main: allMain, item: "sections"),
        MoneyTile("مصارف", f.Expenses, main: allMain, item: "expenses"),
        MoneyTile("خالص دریافتی", f.NetReceived, "عواید − برگشت پول − مصارف", main: true, item: "net"),
    ];

    private static ReportTable SectionsTable(FinanceFigures f) =>
        new("عواید هر بخش",
            [Text("بخش"), Count("تعداد رسید"), Money("پول دریافت‌شده"), Money("پول برگشت‌داده‌شده"), Money("عواید خالص بخش")],
            f.Sections.Select(s => (IReadOnlyList<object?>)Row(s.Name, s.Count, s.Received, -s.Refunded, s.Net)).ToList(),
            Row("مجموع", f.ReceiptCount, f.Receipts, -f.RefundsPaid, f.NetRevenue),
            "فقط پول واقعاً دریافت‌شده، به تاریخ دریافت. مصارف شفاخانه به بخش‌ها تقسیم نمی‌شود.",
            "sections");

    // ───────────────────────────── 1. The hospital at a glance ─────────────────────────────

    private static async Task<ReportDocument> OverviewAsync(Period p, AppDbContext db, HttpContext http, CancellationToken ct)
    {
        var money = Has(http, "ledger.view");
        var visits = db.Visits.AsNoTracking().Where(v => v.VisitDate >= p.Start && v.VisitDate <= p.End && v.Status != VisitStatus.AwaitingPayment && v.Status != VisitStatus.Cancelled);
        var seen = ClinicalEndpoints.SeenByDoctor(db, visits);

        var visitCount = await visits.CountAsync(ct);
        var patients = await visits.Select(v => v.PatientId).Distinct().CountAsync(ct);
        var examined = await seen.Select(v => v.PatientId).Distinct().CountAsync(ct);
        var cancelled = await db.Visits.AsNoTracking().CountAsync(v => v.VisitDate >= p.Start && v.VisitDate <= p.End && v.Status == VisitStatus.Cancelled, ct);

        var byDoctor = await visits.GroupBy(v => v.Doctor != null ? v.Doctor.FullName : null).Select(g => new { Name = g.Key, Count = g.Count() }).ToListAsync(ct);
        var seenByDoctor = await seen.GroupBy(v => v.Doctor != null ? v.Doctor.FullName : null).Select(g => new { Name = g.Key, Count = g.Count() }).ToListAsync(ct);
        var doctors = byDoctor.Select(d => d.Name).Concat(seenByDoctor.Select(d => d.Name)).Distinct()
            .Select(name => new
            {
                Name = name ?? "داکتر تعیین نشده",
                Visits = byDoctor.Where(d => d.Name == name).Sum(d => d.Count),
                Seen = seenByDoctor.Where(d => d.Name == name).Sum(d => d.Count),
            })
            .OrderBy(d => d.Name == "داکتر تعیین نشده").ThenByDescending(d => d.Seen).ThenBy(d => d.Name).ToList();

        var f = await ReportFigures.LoadAsync(p.StartUtc, p.EndUtc, db, ct);

        var tiles = new List<ReportTile>
        {
            CountTile("مریضان", patients, $"{ReportText.Number(visitCount)} مراجعه", main: true),
            CountTile("مریضان معاینه‌شده", examined, "داکتر معاینه یا راجع کرده است", main: true),
        };
        var tables = new List<ReportTable>();
        var warnings = new List<string>();

        if (money)
        {
            tiles.AddRange(MoneyTiles(f, allMain: false));
            tables.Add(SectionsTable(f));
        }
        else
        {
            tables.Add(new ReportTable("رسیدهای هر بخش", [Text("بخش"), Count("تعداد رسید")],
                f.Sections.Select(s => (IReadOnlyList<object?>)Row(s.Name, s.Count)).ToList(),
                Row("مجموع", f.ReceiptCount), "رسیدهایی که در این دوره برای خدمات هر بخش گرفته شد.", "sections"));
        }

        tables.Add(new ReportTable("مریضان هر داکتر", [Text("داکتر"), Count("مراجعات"), Count("معاینه‌شده")],
            doctors.Select(d => (IReadOnlyList<object?>)Row(d.Name, d.Visits, d.Seen)).ToList(),
            doctors.Count > 1 ? Row("مجموع", doctors.Sum(d => d.Visits), doctors.Sum(d => d.Seen)) : null,
            cancelled > 0 ? $"{ReportText.Number(cancelled)} مراجعه لغو شده در این دوره شمرده نشده است." : null, "doctors"));

        return new ReportDocument(
            "گزارش عمومی شفاخانه",
            "مریضان: هر مریض یک بار شمرده می‌شود، حتی اگر چند بار آمده باشد؛ مراجعات لغوشده شمرده نمی‌شوند. " +
            "معاینه‌شده: داکتر معاینه را ثبت کرده یا مریض را به رفراکشن، عکس‌برداری یا لابراتوار راجع کرده است." +
            (money ? " " + MoneyDefinition + " همه ارقام به وقت کابل." : " همه ارقام به وقت کابل."),
            p.Start, p.End, tiles, null, tables, warnings);
    }

    // ───────────────────────────── 2. Money and accounts ─────────────────────────────

    private static async Task<ReportDocument> FinanceAsync(Period p, AppDbContext db, HttpContext http, CancellationToken ct)
    {
        var f = await ReportFigures.LoadAsync(p.StartUtc, p.EndUtc, db, ct);

        var open = db.Invoices.AsNoTracking().Where(i => (i.Status == InvoiceStatus.Issued || i.Status == InvoiceStatus.PartiallyPaid) && i.Total - i.Discount - i.Paid > 0);
        var openCount = await open.CountAsync(ct);
        var outstanding = await open.SumAsync(i => (decimal?)(i.Total - i.Discount - i.Paid), ct) ?? 0;
        var openRows = await open.OrderBy(i => i.Id).Take(MaxListRows)
            .Select(i => new
            {
                i.InvoiceNumber,
                i.CreatedAt,
                First = i.Patient != null ? i.Patient.FirstName : null,
                Last = i.Patient != null ? i.Patient.LastName : null,
                Item = i.Items.Where(x => !x.IsVoid).OrderBy(x => x.Id).Select(x => x.Description).FirstOrDefault(),
                Payable = i.Total - i.Discount,
                i.Paid,
                Balance = i.Total - i.Discount - i.Paid,
            }).ToListAsync(ct);
        var today = KabulClock.Today;

        var categories = await (
            from e in db.LedgerEntries.AsNoTracking()
            join t in db.LedgerTransactions.AsNoTracking() on e.TransactionId equals t.Id
            join expense in db.Expenses.AsNoTracking() on new { t.SourceType, t.SourceId } equals new { SourceType = nameof(Expense), SourceId = expense.Id } into expenses
            from expense in expenses.DefaultIfEmpty()
            where t.CreatedAt >= p.StartUtc && t.CreatedAt < p.EndUtc && e.AccountCode == Accounts.Expenses
            group e by expense.Category ?? "بدون نوع" into g
            select new { Category = g.Key, Amount = g.Sum(e => e.Debit - e.Credit) }).ToListAsync(ct);

        var registers = await RegistersAsync(p, db, ct);

        var calculation = new List<IReadOnlyList<object?>>
        {
            Row("عواید: پول دریافت‌شده از مریضان", f.Receipts),
            Row("کسر: پول برگشت‌داده‌شده به مریضان", -f.RefundsPaid),
            Row("کسر: مصارف", -f.Expenses),
        };

        var cash = new List<IReadOnlyList<object?>>
        {
            Row("پول نقد دفتری در آغاز دوره", f.CashOpening),
            Row("دریافت از مریضان — نقد", f.ReceiptsCash),
            Row("دریافت از مریضان — غیرنقد", f.Receipts - f.ReceiptsCash),
            Row("پول برگشت‌داده‌شده به مریضان", -f.RefundsPaid),
            Row("مصارف پرداخت‌شده", -f.Expenses),
        };
        if (f.CashOther != 0)
        {
            cash.Add(Row("سایر ثبت‌های صندوق (مانند موجودی آغاز سیستم)", f.CashOther));
        }

        var warnings = new List<string>();
        // The system records purchases on credit but not the money later paid to the companies, so the debt itself is not known.
        warnings.Add($"پرداخت پول به شرکت‌ها در سیستم ثبت نمی‌شود؛ بنابراین بدهی شفاخانه به شرکت‌ها حساب نمی‌شود. خرید اجناس از شرکت‌ها در این دوره: {ReportText.Money(f.Purchases)}.");

        return new ReportDocument(
            "گزارش مالی و حسابداری",
            MoneyDefinition + " طلبات و تخفیفات عاید نیستند و جدا نشان داده می‌شوند. " +
            "تحویل و انتقال پول میان صندوق‌ها عاید یا مصرف نیست و در هیچ رقم این گزارش نمی‌آید. پول نقد دفتری، پولی است که طبق اسناد باید نزد شفاخانه باشد؛ پولی که از صندوق بیرون برده شده و ثبت نشده در آن نیست. همه ارقام به وقت کابل.",
            p.Start, p.End,
            [
                .. MoneyTiles(f),
                MoneyTile("طلبات از مریضان", outstanding, $"همین حالا · {ReportText.Number(openCount)} فاکتور · عاید نیست"),
                MoneyTile("تخفیفات", f.Discounts, "عاید نیست"),
                MoneyTile("پول نقد دفتری", f.CashClosing, "در پایان دوره"),
            ],
            null,
            [
                SectionsTable(f),
                new ReportTable("محاسبه خالص دریافتی", [Text("شرح"), Money("مبلغ")], calculation, Row("خالص دریافتی", f.NetReceived), Key: "net"),
                new ReportTable("پول نقد شفاخانه", [Text("شرح"), Money("مبلغ")], cash, Row("پول نقد دفتری در پایان دوره", f.CashClosing), Key: "cash"),
                registers,
                new ReportTable("مصارف به تفکیک نوع", [Text("نوع مصرف"), Money("مبلغ")],
                    categories.OrderByDescending(c => c.Amount).ThenBy(c => c.Category).Select(c => (IReadOnlyList<object?>)Row(c.Category, c.Amount)).ToList(),
                    Row("مجموع", f.Expenses), "مصرف لغوشده در روزی که لغو شده کم می‌شود.", "expenses"),
                new ReportTable("طلبات: فاکتورهای پرداخت‌نشده", [Date("تاریخ"), Text("فاکتور"), Text("نام مریض"), Text("بابت"), Count("چند روز"), Money("مبلغ فاکتور"), Money("پرداخت‌شده"), Money("باقی")],
                    openRows.Select(r => (IReadOnlyList<object?>)Row(KabulDay(r.CreatedAt), r.InvoiceNumber, r.First is null ? "بدون مریض" : FullName(r.First, r.Last), r.Item ?? "—",
                        today.DayNumber - KabulDay(r.CreatedAt).DayNumber, r.Payable, r.Paid, r.Balance)).ToList(),
                    openCount > 1 && openCount <= MaxListRows ? Row("مجموع", null, null, null, null, openRows.Sum(r => r.Payable), openRows.Sum(r => r.Paid), outstanding) : null,
                    Cut(openRows.Count, openCount, "فاکتور") ?? "وضعیت همین حالا، از کهنه‌ترین به تازه‌ترین.", "outstanding"),
            ],
            warnings);
    }

    /// <summary>Each cash register: who holds it now, the cash it took in the period, and any shortage or excess found at closing.</summary>
    private static async Task<ReportTable> RegistersAsync(Period p, AppDbContext db, CancellationToken ct)
    {
        var registers = await db.CashRegisters.AsNoTracking().OrderBy(r => r.Id).Select(r => new { r.Id, r.Name, r.IsActive }).ToListAsync(ct);
        var open = await (
            from s in db.CashSessions.AsNoTracking()
            join u in db.Users on s.OpenedByUserId equals u.Id
            where s.ClosedAt == null
            select new
            {
                s.RegisterId,
                s.OpenedAt,
                Cashier = u.FullName,
                Expected = s.OpeningAmount + (db.Payments.Where(x => x.CashSessionId == s.Id && x.Method == PaymentMethod.Cash).Sum(x => (decimal?)x.Amount) ?? 0),
            }).ToListAsync(ct);
        var received = await db.Payments.AsNoTracking().Where(x => x.CreatedAt >= p.StartUtc && x.CreatedAt < p.EndUtc && x.Method == PaymentMethod.Cash)
            .GroupBy(x => x.CashSession!.RegisterId).Select(g => new { RegisterId = g.Key, Amount = g.Sum(x => x.Amount) }).ToListAsync(ct);
        var closed = await db.CashSessions.AsNoTracking()
            .Where(s => s.ClosedAt >= p.StartUtc && s.ClosedAt < p.EndUtc && s.CountedAmount != null && s.ExpectedAmount != null)
            .GroupBy(s => s.RegisterId).Select(g => new { RegisterId = g.Key, Difference = g.Sum(s => s.CountedAmount!.Value - s.ExpectedAmount!.Value) }).ToListAsync(ct);

        var rows = registers
            .Where(r => r.IsActive || open.Any(o => o.RegisterId == r.Id) || received.Any(x => x.RegisterId == r.Id) || closed.Any(c => c.RegisterId == r.Id))
            .Select(r =>
            {
                var session = open.Where(o => o.RegisterId == r.Id).OrderBy(o => o.OpenedAt).FirstOrDefault();
                return (IReadOnlyList<object?>)Row(
                    r.Name,
                    session is null ? "بسته" : $"باز از {ReportText.Time(session.OpenedAt)}",
                    session?.Cashier ?? "—",
                    received.Where(x => x.RegisterId == r.Id).Sum(x => x.Amount),
                    session?.Expected,
                    closed.Where(c => c.RegisterId == r.Id).Sum(c => c.Difference));
            }).ToList();

        return new ReportTable("صندوق‌ها",
            [Text("صندوق"), Text("وضعیت"), Text("صندوق‌دار"), Money("دریافت نقد در این دوره"), Money("پول نقد که اکنون باید در صندوق باشد"), Money("کمی یا زیادی هنگام بستن")],
            rows,
            rows.Count > 1 ? Row("مجموع", null, null, received.Sum(x => x.Amount), open.Count > 0 ? open.Sum(o => o.Expected) : null, closed.Sum(c => c.Difference)) : null,
            "پول باز صندوق = پول آغاز صندوق + پول نقد دریافت‌شده در همان نوبت. کمی با منفی و زیادی با مثبت نشان داده می‌شود.", "registers");
    }

    // ───────────────────────────── 3. Pharmacy and store ─────────────────────────────

    private static async Task<ReportDocument> PharmacyAsync(Period p, AppDbContext db, HttpContext http, CancellationToken ct)
    {
        var seesPharmacy = Has(http, "pharmacy.view");
        var seesOptical = Has(http, "optical.view");
        var f = await ReportFigures.LoadAsync(p.StartUtc, p.EndUtc, db, ct);
        var sales = f.Sales.Where(s => (s.Name == ReportSections.Pharmacy && seesPharmacy) || (s.Name == ReportSections.Optical && seesOptical)).ToList();
        decimal? salesProfit = sales.Any(s => s.Profit is null) ? null : sales.Sum(s => s.Net - s.Cost);

        var today = KabulClock.Today;
        var soon = today.AddDays(NearExpiryDays);
        var products = await db.Products.AsNoTracking().Where(x => x.IsActive)
            .Select(x => new
            {
                x.Name,
                x.Code,
                x.Kind,
                x.Unit,
                x.ReorderLevel,
                OnHand = db.StockBatches.Where(b => b.ProductId == x.Id).Sum(b => (decimal?)b.Quantity) ?? 0,
                Value = db.StockBatches.Where(b => b.ProductId == x.Id && b.Quantity > 0).Sum(b => (decimal?)(b.Quantity * b.UnitCost)) ?? 0,
                Expiry = db.StockBatches.Where(b => b.ProductId == x.Id && b.Quantity > 0 && b.ExpiryDate != null).Min(b => b.ExpiryDate),
            }).ToListAsync(ct);
        var batches = await db.StockBatches.AsNoTracking()
            .Where(b => b.Quantity > 0 && b.ExpiryDate != null && b.ExpiryDate <= soon && b.Product!.IsActive)
            .OrderBy(b => b.ExpiryDate).ThenBy(b => b.Product!.Name)
            .Select(b => new { b.Product!.Name, b.LotNumber, b.ExpiryDate, b.Quantity, b.Product.Unit, Value = b.Quantity * b.UnitCost }).ToListAsync(ct);

        static bool Short(decimal onHand, decimal reorder) => onHand <= 0 || (reorder > 0 && onHand <= reorder);
        string State(decimal onHand, decimal reorder, DateOnly? expiry) =>
            onHand <= 0 ? "تمام شده"
            : expiry < today ? "تاریخ گذشته"
            : reorder > 0 && onHand <= reorder ? "کمبود"
            : expiry <= soon ? "نزدیک به تاریخ انقضا"
            : "عادی";
        static string KindName(ProductKind kind) => kind switch
        {
            ProductKind.Drug => "دوا",
            ProductKind.Frame => "فریم عینک",
            ProductKind.Lens => "شیشه عینک",
            _ => "عمومی",
        };

        var shortages = products.Where(x => Short(x.OnHand, x.ReorderLevel)).OrderBy(x => x.OnHand > 0).ThenBy(x => x.Name).ToList();
        var expiring = batches.Select(b => b.Name).Distinct().Count();
        var stock = products.OrderBy(x => x.Name).Take(MaxListRows).ToList();

        var tiles = new List<ReportTile>();
        var tables = new List<ReportTable>();
        var warnings = new List<string>();
        if (sales.Count > 0)
        {
            tiles.Add(MoneyTile("فروشات", sales.Sum(s => s.Net), "پول دریافت‌شده، پس از کسر برگشت پول", main: true, item: "sales"));
            tiles.Add(MoneyTile("قیمت تمام‌شده", sales.Sum(s => s.Cost), "قیمت خرید اجناس تحویل‌شده"));
            tiles.Add(MoneyTile("سود فروشات", salesProfit, ProfitNote(salesProfit), main: true, item: "sales"));
            tables.Add(new ReportTable("فروشات", [Text("بخش"), Count("تعداد رسید"), Money("فروشات"), Money("قیمت تمام‌شده"), Money("سود")],
                sales.Select(s => (IReadOnlyList<object?>)Row(s.Name, s.Count, s.Net, s.Cost, Known(s.Profit))).ToList(),
                sales.Count > 1 ? Row("مجموع", sales.Sum(s => s.Count), sales.Sum(s => s.Net), sales.Sum(s => s.Cost), Known(salesProfit)) : null, Key: "sales"));
            if (salesProfit is null)
            {
                warnings.Add(UnknownCostWarning(sales.Sum(s => s.UnknownCostLines), sales.Sum(s => s.UnknownCostRevenue)));
            }
        }

        tiles.Add(MoneyTile("ارزش موجودی گدام", products.Sum(x => x.Value), "به قیمت خرید · همین حالا", main: true));
        tiles.Add(CountTile("اجناس تمام‌شده یا کمبود", shortages.Count, "همین حالا", main: true));
        // A section that sells already fills four figures; without sales the store's expiry joins the top row.
        tiles.Add(CountTile("منقضی یا نزدیک به انقضا", expiring, $"تا {ReportText.Number(NearExpiryDays)} روز آینده", main: sales.Count == 0));

        tables.Add(new ReportTable("اجناس تمام‌شده و کمبود", [Text("جنس"), Text("نوع"), Quantity("موجودی"), Text("واحد"), Quantity("حد سفارش"), Text("وضعیت")],
            shortages.Select(x => (IReadOnlyList<object?>)Row(x.Name, KindName(x.Kind), x.OnHand, x.Unit, x.ReorderLevel > 0 ? x.ReorderLevel : null, x.OnHand <= 0 ? "تمام شده" : "کمبود")).ToList(),
            Key: "shortages"));
        tables.Add(new ReportTable("اجناس منقضی و نزدیک به انقضا", [Text("جنس"), Text("نمبر لات"), Date("تاریخ انقضا"), Quantity("موجودی"), Text("واحد"), Money("ارزش"), Text("وضعیت")],
            batches.Select(b => (IReadOnlyList<object?>)Row(b.Name, string.IsNullOrWhiteSpace(b.LotNumber) ? "—" : b.LotNumber, b.ExpiryDate, b.Quantity, b.Unit, b.Value,
                b.ExpiryDate < today ? "تاریخ گذشته" : "نزدیک به انقضا")).ToList(),
            batches.Count > 1 ? Row("مجموع", null, null, null, null, batches.Sum(b => b.Value), null) : null, Key: "expiring"));
        tables.Add(new ReportTable("موجودی همه اجناس", [Text("جنس"), Text("کد"), Text("نوع"), Quantity("موجودی"), Text("واحد"), Date("نزدیک‌ترین تاریخ انقضا"), Money("ارزش"), Text("وضعیت")],
            stock.Select(x => (IReadOnlyList<object?>)Row(x.Name, x.Code, KindName(x.Kind), x.OnHand, x.Unit, x.Expiry, x.Value, State(x.OnHand, x.ReorderLevel, x.Expiry))).ToList(),
            stock.Count > 1 && stock.Count == products.Count ? Row("مجموع", null, null, null, null, null, products.Sum(x => x.Value), null) : null,
            Cut(stock.Count, products.Count, "جنس"), "stock"));

        return new ReportDocument(
            "گزارش دواخانه و گدام",
            "فروشات: پولی که در این دوره بابت دوا و عینک واقعاً دریافت شده، پس از کسر پول برگشت‌داده‌شده؛ بل پرداخت‌نشده فروش نیست. قیمت تمام‌شده: قیمت خرید اجناسی که در این دوره تحویل شده یا برگشته است. سود فروشات = فروشات − قیمت تمام‌شده. " +
            "ارزش، کمبود و انقضای گدام وضعیت همین حالا را نشان می‌دهند، نه تاریخ انتخاب‌شده. همه ارقام به وقت کابل.",
            p.Start, p.End, tiles, null, tables, warnings);
    }
}
