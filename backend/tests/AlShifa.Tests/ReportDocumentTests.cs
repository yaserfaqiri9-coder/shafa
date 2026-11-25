using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AlShifa.Api.Features.Auth;
using AlShifa.Api.Features.Finance;
using AlShifa.Api.Features.Inventory;
using AlShifa.Api.Features.Reports;
using AlShifa.Api.Features.Settings;
using AlShifa.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AlShifa.Tests;

[Collection("api")]
public sealed class ReportDocumentTests(ApiFixture api)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static readonly string[] Tabs = ["overview", "finance", "pharmacy"];

    private static string Name(string prefix) => $"{prefix}_{Guid.NewGuid():N}"[..20];

    private async Task<HttpClient> UserAsync(string role)
    {
        var name = Name("u");
        await api.CreateUserAsync(name, role);
        return await api.LoginAsync(name);
    }

    private async Task<T> InDbAsync<T>(Func<AppDbContext, Task<T>> read)
    {
        using var scope = api.Services.CreateScope();
        return await read(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    private static HttpRequestMessage Post(string url, object body)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
        message.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        return message;
    }

    private static async Task<T> Read<T>(HttpResponseMessage response, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, text);
        return JsonSerializer.Deserialize<T>(text, JsonOptions)!;
    }

    private static async Task<JsonElement> ReportAsync(HttpClient client, string key, string query = "") =>
        await Read<JsonElement>(await client.GetAsync($"/api/reports/{key}{query}"));

    private static JsonElement? FindTile(JsonElement report, string label) =>
        report.GetProperty("tiles").EnumerateArray().Where(t => t.GetProperty("label").GetString() == label).Select(t => (JsonElement?)t).SingleOrDefault();

    /// <summary>A figure that must be known.</summary>
    private static decimal Tile(JsonElement report, string label) =>
        FindTile(report, label)?.GetProperty("value").GetDecimal() ?? throw new Xunit.Sdk.XunitException($"No tile {label}");

    /// <summary>A figure that may be «unknown»: null then.</summary>
    private static decimal? MaybeTile(JsonElement report, string label)
    {
        var value = (FindTile(report, label) ?? throw new Xunit.Sdk.XunitException($"No tile {label}")).GetProperty("value");
        return value.ValueKind == JsonValueKind.Null ? null : value.GetDecimal();
    }

    private static JsonElement Table(JsonElement report, string title) =>
        report.GetProperty("tables").EnumerateArray().Single(t => t.GetProperty("title").GetString() == title);

    private static List<JsonElement[]> Rows(JsonElement table) =>
        table.GetProperty("rows").EnumerateArray().Select(r => r.EnumerateArray().ToArray()).ToList();

    private static JsonElement[] Total(JsonElement table) => table.GetProperty("total").EnumerateArray().ToArray();

    /// <summary>A section outside the fixed list is shown only when it has something, so a missing row reads as zero.</summary>
    private static decimal SectionNet(JsonElement overview, string section) =>
        Rows(Table(overview, "عواید هر بخش")).SingleOrDefault(r => r[0].GetString() == section)?[4].GetDecimal() ?? 0;

    private static int SectionCount(JsonElement overview, string section) =>
        Rows(Table(overview, "عواید هر بخش")).SingleOrDefault(r => r[0].GetString() == section)?[1].GetInt32() ?? 0;

    private static string Today => KabulClock.Today.ToString("yyyy-MM-dd");

    private async Task<HttpClient> CashierWithOpenSessionAsync()
    {
        var cashier = await UserAsync(Roles.Cashier);
        var registerId = await InDbAsync(async db =>
        {
            var register = new CashRegister { Name = Name("صندوق") };
            db.CashRegisters.Add(register);
            await db.SaveChangesAsync();
            return register.Id;
        });
        var opened = await cashier.PostAsJsonAsync("/api/cash/sessions", new OpenSessionRequest(registerId, 0));
        Assert.True(opened.IsSuccessStatusCode, await opened.Content.ReadAsStringAsync());
        return cashier;
    }

    private async Task<int> NewProductAsync(decimal salePrice)
    {
        var manager = await UserAsync(Roles.Inventory);
        var response = await manager.PostAsJsonAsync("/api/inventory/products", new SaveProductRequest("جنس " + Name("n"), ProductKind.Drug, null, "عدد", 0, salePrice));
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetInt32();
    }

    private async Task StockAsync(int productId, decimal quantity, decimal cost)
    {
        var manager = await UserAsync(Roles.Inventory);
        var response = await manager.SendAsync(Post("/api/inventory/purchases", new CreatePurchaseRequest("تأمین‌کننده", "pharmacy",
            [new PurchaseLineInput(productId, Name("L"), DateOnly.FromDateTime(DateTime.UtcNow.AddYears(1)), quantity, cost)])));
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Figures_of_any_period_load_and_add_up_to_the_money_received()
    {
        // A Kabul day starts at 19:30 UTC the day before.
        var endUtc = KabulClock.Today.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc).AddHours(-4.5);
        var startUtc = endUtc.AddDays(-401);
        var f = await InDbAsync(db => ReportFigures.LoadAsync(startUtc, endUtc, db, default));
        var received = await InDbAsync(async db => await db.Payments.Where(p => p.CreatedAt >= startUtc && p.CreatedAt < endUtc).SumAsync(p => (decimal?)p.Amount) ?? 0);
        var paidBack = await InDbAsync(async db => await db.Refunds.Where(r => r.CreatedAt >= startUtc && r.CreatedAt < endUtc).SumAsync(r => (decimal?)r.Amount) ?? 0);
        Assert.Equal(received, f.Receipts);
        Assert.Equal(paidBack, f.RefundsPaid);
        // Receipts split between sections add up to the cent.
        Assert.Equal(received, f.Sections.Sum(s => s.Received));
        Assert.Equal(paidBack, f.Sections.Sum(s => s.Refunded));
        Assert.Equal(received - paidBack, f.NetRevenue);
        Assert.Equal(f.NetRevenue - f.Expenses, f.NetReceived);
        Assert.All(ReportSections.Core, name => Assert.Contains(f.Sections, s => s.Name == name));
    }

    [Fact]
    public async Task Each_tab_answers_over_one_day_and_a_long_period_with_its_main_figures_first()
    {
        var manager = await UserAsync(Roles.Manager);
        foreach (var key in Tabs)
        {
            var report = await ReportAsync(manager, key);
            Assert.False(string.IsNullOrEmpty(report.GetProperty("title").GetString()));
            Assert.False(string.IsNullOrEmpty(report.GetProperty("description").GetString()));
            Assert.InRange(report.GetProperty("tiles").GetArrayLength(), 1, 8);
            Assert.True(report.GetProperty("tables").GetArrayLength() >= 1);
            // The page shows at most four figures on top; every report below has its own name, given once.
            Assert.InRange(report.GetProperty("tiles").EnumerateArray().Count(t => t.GetProperty("main").GetBoolean()), 1, 4);
            var keys = report.GetProperty("tables").EnumerateArray().Select(t => t.GetProperty("key").GetString()).ToList();
            Assert.All(keys, k => Assert.False(string.IsNullOrEmpty(k)));
            Assert.Equal(keys.Count, keys.Distinct().Count());
            // A figure is shown once on a page.
            var labels = report.GetProperty("tiles").EnumerateArray().Select(t => t.GetProperty("label").GetString()).ToList();
            Assert.Equal(labels.Count, labels.Distinct().Count());
            Assert.Equal(Today, report.GetProperty("from").GetString());

            var longer = await ReportAsync(manager, key, $"?from={KabulClock.Today.AddDays(-400):yyyy-MM-dd}&to={Today}");
            Assert.Equal(JsonValueKind.Array, longer.GetProperty("tiles").ValueKind);
            Assert.Equal(HttpStatusCode.BadRequest, (await manager.GetAsync($"/api/reports/{key}?from=2001-01-03&to=2001-01-02")).StatusCode);
        }
    }

    [Fact]
    public async Task Each_role_reaches_only_the_tabs_it_may_see_and_money_only_with_the_books()
    {
        // Clinical staff and the counts-only viewer see the hospital's work, never its money.
        foreach (var role in new[] { Roles.Doctor, Roles.DoctorReportsViewer })
        {
            var client = await UserAsync(role);
            var overview = await ReportAsync(client, "overview");
            Assert.NotNull(FindTile(overview, "مریضان"));
            Assert.All(overview.GetProperty("tiles").EnumerateArray(), t => Assert.NotEqual("money", t.GetProperty("kind").GetString()));
            Assert.DoesNotContain(overview.GetProperty("tables").EnumerateArray(), t => t.GetProperty("columns").EnumerateArray().Any(c => c.GetProperty("kind").GetString() == "money"));
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/reports/finance")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/reports/pharmacy")).StatusCode);
        }

        var accountant = await UserAsync(Roles.Accountant);
        foreach (var key in Tabs)
        {
            Assert.Equal(HttpStatusCode.OK, (await accountant.GetAsync($"/api/reports/{key}")).StatusCode);
        }
        Assert.NotNull(FindTile(await ReportAsync(accountant, "overview"), "عواید"));

        var pharmacist = await UserAsync(Roles.Pharmacist);
        Assert.Equal(HttpStatusCode.OK, (await pharmacist.GetAsync("/api/reports/pharmacy")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await pharmacist.GetAsync("/api/reports/finance")).StatusCode);

        var triage = await UserAsync(Roles.Triage);
        foreach (var key in Tabs)
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await triage.GetAsync($"/api/reports/{key}")).StatusCode);
        }

        // The scattered reports the three tabs replaced answer no more.
        var admin = await UserAsync(Roles.SuperAdmin);
        foreach (var key in new[] { "patients", "doctors", "cash", "outstanding", "expenses", "profit-loss", "revenue-sources", "daily-finance", "cash-flow", "sales", "stock" })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/reports/{key}")).StatusCode);
        }
    }

    [Fact]
    public async Task A_paid_and_partly_refunded_service_moves_revenue_receipts_refunds_and_cash_each_by_its_own_amount()
    {
        var accountant = await UserAsync(Roles.Accountant);
        var admin = await UserAsync(Roles.SuperAdmin);
        var service = await admin.PostAsJsonAsync("/api/services", new SaveServiceRequest("خدمت " + Name("n"), null, true, 1000));
        var serviceId = JsonDocument.Parse(await service.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetInt32();
        var cashier = await CashierWithOpenSessionAsync();

        var finance = await ReportAsync(accountant, "finance");
        var overview = await ReportAsync(accountant, "overview");
        var home = (await accountant.GetFromJsonAsync<FinancialReport>("/api/reports/financial", JsonOptions))!;

        var invoice = await Read<InvoiceRow>(await cashier.SendAsync(Post("/api/invoices", new CreateInvoiceRequest(null, null, [new InvoiceLineInput(serviceId, 1)]))), HttpStatusCode.Created);
        var payment = await Read<PaymentRow>(await cashier.SendAsync(Post("/api/payments", new PaymentRequest(invoice.Id, 600, PaymentMethod.Cash, null))), HttpStatusCode.Created);
        await Read<JsonElement>(await accountant.SendAsync(Post("/api/refunds", new RefundRequest(payment.Id, 100, "برگشت قسمی"))), HttpStatusCode.Created);
        await Read<JsonElement>(await accountant.SendAsync(Post("/api/expenses", new CreateExpenseRequest("برق " + Name("n"), null, 50, KabulClock.Today))), HttpStatusCode.Created);

        var financeAfter = await ReportAsync(accountant, "finance");
        var overviewAfter = await ReportAsync(accountant, "overview");
        var homeAfter = (await accountant.GetFromJsonAsync<FinancialReport>("/api/reports/financial", JsonOptions))!;
        decimal Moved(string label) => Tile(financeAfter, label) - Tile(finance, label);

        // Revenue is only the money received; the 400 still owed is not revenue, and the money paid back is shown on its own.
        Assert.Equal(600, Moved("عواید"));
        Assert.Equal(100, Moved("پول برگشت‌داده‌شده"));
        Assert.Equal(50, Moved("مصارف"));
        Assert.Equal(450, Moved("خالص دریافتی"));
        Assert.Equal(400, Moved("طلبات از مریضان"));
        Assert.Equal(0, Moved("تخفیفات"));
        // Cash moves by the money that changed hands: 600 in, 100 back, 50 spent.
        Assert.Equal(450, Moved("پول نقد دفتری"));

        // The same figures on every tab and on the home page.
        foreach (var label in new[] { "عواید", "پول برگشت‌داده‌شده", "مصارف", "خالص دریافتی" })
        {
            Assert.Equal(Tile(financeAfter, label), Tile(overviewAfter, label));
        }
        Assert.Equal(Tile(financeAfter, "عواید"), homeAfter.Receipts);
        Assert.Equal(Tile(financeAfter, "پول برگشت‌داده‌شده"), homeAfter.Refunds);
        Assert.Equal(Tile(financeAfter, "عواید") - Tile(financeAfter, "پول برگشت‌داده‌شده"), homeAfter.NetRevenue);
        Assert.Equal(Tile(financeAfter, "مصارف"), homeAfter.Expenses);
        Assert.Equal(500, homeAfter.NetRevenue - home.NetRevenue);

        // The receipt and the refund go on the section of the invoice, and the sections add up to the whole to the cent.
        Assert.Equal(500, SectionNet(overviewAfter, ReportSections.Other) - SectionNet(overview, ReportSections.Other));
        Assert.Equal(1, SectionCount(overviewAfter, ReportSections.Other) - SectionCount(overview, ReportSections.Other));
        var sections = Table(overviewAfter, "عواید هر بخش");
        Assert.Equal(Tile(overviewAfter, "عواید"), Rows(sections).Sum(r => r[2].GetDecimal()));
        Assert.Equal(Tile(overviewAfter, "عواید") - Tile(overviewAfter, "پول برگشت‌داده‌شده"), Rows(sections).Sum(r => r[4].GetDecimal()));
        Assert.Equal(Tile(overviewAfter, "عواید") - Tile(overviewAfter, "پول برگشت‌داده‌شده"), Total(sections)[4].GetDecimal());

        // The net calculation and the cash account each close on their own tile.
        Assert.Equal(Tile(financeAfter, "خالص دریافتی"), Total(Table(financeAfter, "محاسبه خالص دریافتی"))[1].GetDecimal());
        Assert.Equal(Tile(financeAfter, "خالص دریافتی"), Rows(Table(financeAfter, "محاسبه خالص دریافتی")).Sum(r => r[1].GetDecimal()));
        var cash = Table(financeAfter, "پول نقد شفاخانه");
        Assert.Equal(Tile(financeAfter, "پول نقد دفتری"), Rows(cash).Sum(r => r[1].GetDecimal()));
        Assert.Equal(Tile(financeAfter, "پول نقد دفتری"), Total(cash)[1].GetDecimal());

        var open = Rows(Table(financeAfter, "طلبات: فاکتورهای پرداخت‌نشده")).Single(r => r[1].GetString() == invoice.InvoiceNumber);
        Assert.Equal(400, open[7].GetDecimal());
    }

    [Fact]
    public async Task A_pharmacy_sale_shows_its_sales_cost_and_profit_and_its_return_takes_them_all_back()
    {
        var accountant = await UserAsync(Roles.Accountant);
        var pharmacist = await UserAsync(Roles.Pharmacist);
        var productId = await NewProductAsync(100);
        await StockAsync(productId, 3, 40);
        var cashier = await CashierWithOpenSessionAsync();

        var pharmacy = await ReportAsync(accountant, "pharmacy");
        var finance = await ReportAsync(accountant, "finance");
        var overview = await ReportAsync(accountant, "overview");

        // An unpaid bill is not a sale: nothing moves until money is received.
        var sale = await Read<SaleRow>(await pharmacist.SendAsync(Post("/api/pharmacy/sales", new CreateSaleRequest(null, null, [new SaleLineInput(productId, 2)]))), HttpStatusCode.Created);
        var billed = await ReportAsync(accountant, "pharmacy");
        Assert.Equal(0, Tile(billed, "فروشات") - Tile(pharmacy, "فروشات"));
        Assert.Equal(0, Tile(billed, "قیمت تمام‌شده") - Tile(pharmacy, "قیمت تمام‌شده"));
        Assert.Equal(0, Tile(await ReportAsync(accountant, "finance"), "عواید") - Tile(finance, "عواید"));

        await Read<PaymentRow>(await cashier.SendAsync(Post("/api/payments", new PaymentRequest(sale.InvoiceId, 200, PaymentMethod.Cash, null))), HttpStatusCode.Created);
        Assert.Equal(HttpStatusCode.NoContent, (await pharmacist.PostAsJsonAsync($"/api/pharmacy/sales/{sale.Id}/deliver", new DeliverSaleRequest(sale.Version))).StatusCode);

        // Paid and handed over: the money is the sale and the purchase price of the goods its cost.
        var delivered = await ReportAsync(accountant, "pharmacy");
        Assert.Equal(200, Tile(delivered, "فروشات") - Tile(pharmacy, "فروشات"));
        Assert.Equal(80, Tile(delivered, "قیمت تمام‌شده") - Tile(pharmacy, "قیمت تمام‌شده"));
        if (MaybeTile(pharmacy, "سود فروشات") is { } before && MaybeTile(delivered, "سود فروشات") is { } after)
        {
            Assert.Equal(120, after - before);
        }
        Assert.Equal(-80, Tile(delivered, "ارزش موجودی گدام") - Tile(billed, "ارزش موجودی گدام"));

        var deliveredFinance = await ReportAsync(accountant, "finance");
        Assert.Equal(200, Tile(deliveredFinance, "عواید") - Tile(finance, "عواید"));
        var deliveredOverview = await ReportAsync(accountant, "overview");
        Assert.Equal(200, SectionNet(deliveredOverview, ReportSections.Pharmacy) - SectionNet(overview, ReportSections.Pharmacy));
        Assert.Equal(1, SectionCount(deliveredOverview, ReportSections.Pharmacy) - SectionCount(overview, ReportSections.Pharmacy));
        // The pharmacy tab and the overview give the pharmacy the same sales.
        Assert.Equal(SectionNet(deliveredOverview, ReportSections.Pharmacy),
            Rows(Table(delivered, "فروشات")).Single(r => r[0].GetString() == ReportSections.Pharmacy)[2].GetDecimal());

        // A paid return gives the money back and takes back the cost: the money received stays, the money paid back is shown on its own.
        var version = await InDbAsync(db => db.Sales.Where(s => s.Id == sale.Id).Select(s => s.Version).SingleAsync());
        Assert.Equal(HttpStatusCode.NoContent, (await pharmacist.PostAsJsonAsync($"/api/pharmacy/sales/{sale.Id}/return", new ReturnSaleRequest(version, "برگشت دوا"))).StatusCode);
        var returned = await ReportAsync(accountant, "pharmacy");
        Assert.Equal(0, Tile(returned, "فروشات") - Tile(pharmacy, "فروشات"));
        Assert.Equal(0, Tile(returned, "قیمت تمام‌شده") - Tile(pharmacy, "قیمت تمام‌شده"));
        var returnedFinance = await ReportAsync(accountant, "finance");
        Assert.Equal(200, Tile(returnedFinance, "عواید") - Tile(finance, "عواید"));
        Assert.Equal(0, Tile(returnedFinance, "خالص دریافتی") - Tile(finance, "خالص دریافتی"));
        Assert.Equal(200, Tile(returnedFinance, "پول برگشت‌داده‌شده") - Tile(finance, "پول برگشت‌داده‌شده"));
        Assert.Equal(0, Tile(returnedFinance, "پول نقد دفتری") - Tile(finance, "پول نقد دفتری"));
    }

    [Fact]
    public async Task Goods_sold_without_a_purchase_price_make_the_profit_unknown_instead_of_wrong()
    {
        var accountant = await UserAsync(Roles.Accountant);
        var pharmacist = await UserAsync(Roles.Pharmacist);
        var productId = await NewProductAsync(70);
        await StockAsync(productId, 2, 0);

        await Read<SaleRow>(await pharmacist.SendAsync(Post("/api/pharmacy/sales", new CreateSaleRequest(null, null, [new SaleLineInput(productId, 1)]))), HttpStatusCode.Created);

        var pharmacy = await ReportAsync(accountant, "pharmacy");
        Assert.Null(MaybeTile(pharmacy, "سود فروشات"));
        Assert.Equal(ReportText.Unknown, Rows(Table(pharmacy, "فروشات")).Single(r => r[0].GetString() == ReportSections.Pharmacy)[4].GetString());
        Assert.Contains(pharmacy.GetProperty("warnings").EnumerateArray(), w => w.GetString()!.Contains("قیمت خرید"));

        // The days before still have a known profit.
        Assert.NotNull(MaybeTile(await ReportAsync(accountant, "pharmacy", "?from=1999-01-01&to=1999-01-01"), "سود فروشات"));
    }

    [Fact]
    public async Task Revenue_is_money_received_on_its_day_and_an_unpaid_service_is_never_revenue()
    {
        var accountant = await UserAsync(Roles.Accountant);
        var marker = Name("unpaid");
        await InDbAsync(async db =>
        {
            var lab = await db.Departments.SingleAsync(d => d.Code == "lab");
            var service = new Service { Code = marker, Name = "خدمت آزمایشی عواید نقدی", DepartmentId = lab.Id };
            db.Services.Add(service);
            var register = new CashRegister { Name = Name("صندوق") };
            db.CashRegisters.Add(register);
            await db.SaveChangesAsync();
            var user = await db.Users.Select(u => u.Id).FirstAsync();
            var session = new CashSession { RegisterId = register.Id, OpenedByUserId = user, OpenedAt = new DateTime(2003, 3, 4, 5, 0, 0, DateTimeKind.Utc), ClosedByUserId = user, ClosedAt = new DateTime(2003, 3, 8, 5, 0, 0, DateTimeKind.Utc) };
            db.CashSessions.Add(session);

            // Charged on 5 March (Kabul): one service cancelled unpaid on 6 March, one left unpaid, one half-paid on 7 March.
            InvoiceItem Line() => new() { Description = "خدمت", UnitPrice = 700, LineTotal = 700, RevenueAccount = Accounts.ServiceRevenue, ServiceId = service.Id };
            var cancelled = new Invoice { InvoiceNumber = marker, Status = InvoiceStatus.Void, Items = [Line()] };
            cancelled.Items[0].IsVoid = true;
            var unpaid = new Invoice { InvoiceNumber = "a" + marker[1..], Total = 700, Items = [Line()] };
            var paid = new Invoice { InvoiceNumber = "b" + marker[1..], Total = 700, Paid = 300, Status = InvoiceStatus.PartiallyPaid, Items = [Line()] };
            db.Invoices.AddRange(cancelled, unpaid, paid);
            await db.SaveChangesAsync();

            var chargedAt = new DateTime(2003, 3, 5, 5, 0, 0, DateTimeKind.Utc);
            foreach (var invoice in new[] { cancelled, unpaid, paid })
            {
                db.LedgerTransactions.Add(new LedgerTransaction
                {
                    Kind = "Charge", SourceType = "InvoiceItem", SourceId = invoice.Items[0].Id, CreatedAt = chargedAt,
                    Entries = [new LedgerEntry { AccountCode = Accounts.Receivable, Debit = 700 }, new LedgerEntry { AccountCode = Accounts.ServiceRevenue, Credit = 700 }],
                });
            }
            db.LedgerTransactions.Add(new LedgerTransaction
            {
                Kind = "ChargeReversal", SourceType = "InvoiceItem", SourceId = cancelled.Items[0].Id, CreatedAt = chargedAt.AddDays(1),
                Entries = [new LedgerEntry { AccountCode = Accounts.ServiceRevenue, Debit = 700 }, new LedgerEntry { AccountCode = Accounts.Receivable, Credit = 700 }],
            });
            await db.SaveChangesAsync();
            db.Payments.Add(new Payment { ReceiptNumber = marker, InvoiceId = paid.Id, Amount = 300, Method = PaymentMethod.Cash, CashSessionId = session.Id, ReceivedByUserId = user, CreatedAt = chargedAt.AddDays(2) });
            await db.SaveChangesAsync();
            return true;
        });

        // Neither the day of the charges nor the day of the cancellation has revenue, and nothing is negative.
        foreach (var day in new[] { "2003-03-05", "2003-03-06" })
        {
            var overview = await ReportAsync(accountant, "overview", $"?from={day}&to={day}");
            Assert.Equal(0, SectionNet(overview, ReportSections.Lab));
            Assert.Equal(0, Tile(overview, "عواید"));
            Assert.All(Rows(Table(overview, "عواید هر بخش")), r => Assert.True(r[4].GetDecimal() >= 0));
        }

        // The money counts on the day it was received, in the section of the service it paid for.
        var paidDay = await ReportAsync(accountant, "overview", "?from=2003-03-07&to=2003-03-07");
        Assert.Equal(300, Tile(paidDay, "عواید"));
        Assert.Equal(300, SectionNet(paidDay, ReportSections.Lab));
        Assert.Equal(1, SectionCount(paidDay, ReportSections.Lab));
        Assert.Equal(300, Tile(await ReportAsync(accountant, "overview", "?from=2003-03-05&to=2003-03-08"), "عواید"));
    }

    [Fact]
    public async Task A_receipt_for_several_sections_is_split_by_their_prices_to_the_cent()
    {
        var marker = Name("split");
        var f = await InDbAsync(async db =>
        {
            var lab = await db.Departments.SingleAsync(d => d.Code == "lab");
            var surgery = await db.Departments.SingleAsync(d => d.Code == "surgery");
            var labService = new Service { Code = marker, Name = "خدمت لابراتوار آزمایشی", DepartmentId = lab.Id };
            var surgeryService = new Service { Code = "x" + marker[1..], Name = "خدمت جراحی آزمایشی", DepartmentId = surgery.Id };
            db.Services.AddRange(labService, surgeryService);
            var register = new CashRegister { Name = Name("صندوق") };
            db.CashRegisters.Add(register);
            await db.SaveChangesAsync();
            var user = await db.Users.Select(u => u.Id).FirstAsync();
            var session = new CashSession { RegisterId = register.Id, OpenedByUserId = user, OpenedAt = new DateTime(2004, 4, 4, 5, 0, 0, DateTimeKind.Utc), ClosedByUserId = user, ClosedAt = new DateTime(2004, 4, 6, 5, 0, 0, DateTimeKind.Utc) };
            db.CashSessions.Add(session);
            var invoice = new Invoice
            {
                InvoiceNumber = marker, Total = 300, Paid = 100, Status = InvoiceStatus.PartiallyPaid,
                Items =
                [
                    new InvoiceItem { Description = "لابراتوار", UnitPrice = 100, LineTotal = 100, RevenueAccount = Accounts.ServiceRevenue, ServiceId = labService.Id },
                    new InvoiceItem { Description = "جراحی", UnitPrice = 200, LineTotal = 200, RevenueAccount = Accounts.ServiceRevenue, ServiceId = surgeryService.Id },
                ],
            };
            db.Invoices.Add(invoice);
            await db.SaveChangesAsync();
            db.Payments.Add(new Payment { ReceiptNumber = marker, InvoiceId = invoice.Id, Amount = 100, Method = PaymentMethod.Cash, CashSessionId = session.Id, ReceivedByUserId = user, CreatedAt = new DateTime(2004, 4, 5, 5, 0, 0, DateTimeKind.Utc) });
            await db.SaveChangesAsync();
            return await ReportFigures.LoadAsync(new DateTime(2004, 4, 4, 19, 30, 0, DateTimeKind.Utc), new DateTime(2004, 4, 5, 19, 30, 0, DateTimeKind.Utc), db, default);
        });

        Assert.Equal(100, f.Receipts);
        Assert.Equal(33.33m, f.Sections.Single(s => s.Name == ReportSections.Lab).Received);
        Assert.Equal(66.67m, f.Sections.Single(s => s.Name == ReportSections.Surgery).Received);
        Assert.Equal(f.Receipts, f.Sections.Sum(s => s.Received));
    }

    [Fact]
    public async Task Ledger_figures_reconcile_reversals_costs_and_Kabul_day_boundaries()
    {
        var accountant = await UserAsync(Roles.Accountant);
        var marker = Name("finrep");
        // Invoice numbers hold 20 characters, so the sale invoice gets its own marker of the same length.
        var saleMarker = "s" + marker[1..];
        try
        {
            await InDbAsync(async db =>
            {
                var lab = await db.Departments.SingleAsync(d => d.Code == "lab");
                var service = new Service { Code = marker, Name = "خدمت گزارش", DepartmentId = lab.Id };
                var invoice = new Invoice { InvoiceNumber = marker, Total = 1000, Items = [new InvoiceItem { Description = "خدمت", UnitPrice = 1000, LineTotal = 1000, RevenueAccount = Accounts.ServiceRevenue }] };
                db.Services.Add(service);
                db.Invoices.Add(invoice);
                var expense = new Expense { ExpenseNumber = marker, Category = "مصرف آزمایشی گزارش", Amount = 80, Status = ExpenseStatus.Cancelled, ExpenseDate = new DateOnly(2000, 1, 1) };
                db.Expenses.Add(expense);

                // A pharmacy bill made on 5 January (Kabul) and handed over on 6 January.
                var user = await db.Users.Select(u => u.Id).FirstAsync();
                var location = await db.StockLocations.Select(l => l.Id).FirstAsync();
                var product = new Product { Code = marker, Name = marker, Kind = ProductKind.Drug, Unit = "عدد", IsActive = false };
                db.Products.Add(product);
                await db.SaveChangesAsync();
                invoice.Items[0].ServiceId = service.Id;
                var batch = new StockBatch { ProductId = product.Id, LocationId = location, LotNumber = marker, Quantity = 0, UnitCost = 40 };
                db.StockBatches.Add(batch);
                var saleInvoice = new Invoice { InvoiceNumber = saleMarker, Total = 100 };
                db.Invoices.Add(saleInvoice);
                await db.SaveChangesAsync();
                var sale = new Sale
                {
                    SaleNumber = marker, Kind = SaleKind.Pharmacy, InvoiceId = saleInvoice.Id, CreatedByUserId = user,
                    CreatedAt = new DateTime(2001, 1, 5, 5, 0, 0, DateTimeKind.Utc), DeliveredAt = new DateTime(2001, 1, 6, 5, 0, 0, DateTimeKind.Utc),
                    Items = [new SaleItem { ProductId = product.Id, BatchId = batch.Id, Quantity = 1, UnitPrice = 100, UnitCost = 40 }],
                };
                db.Sales.Add(sale);
                await db.SaveChangesAsync();
                var saleLine = new InvoiceItem { InvoiceId = saleInvoice.Id, Description = marker, Quantity = 1, UnitPrice = 100, LineTotal = 100, RevenueAccount = Accounts.PharmacyRevenue, SourceType = nameof(SaleItem), SourceId = sale.Items[0].Id };
                db.InvoiceItems.Add(saleLine);
                await db.SaveChangesAsync();

                var start = new DateTime(2001, 1, 1, 19, 30, 0, DateTimeKind.Utc); // 2 January in Kabul.
                LedgerTransaction Add(DateTime at, string debit, string credit, decimal amount, string source = "ReportTest", long id = 0, string? kind = null)
                {
                    var transaction = new LedgerTransaction
                    {
                        Kind = kind ?? Name("test"), SourceType = source, SourceId = id, CreatedAt = at, Description = marker,
                        Entries = [new LedgerEntry { AccountCode = debit, Debit = amount }, new LedgerEntry { AccountCode = credit, Credit = amount }],
                    };
                    db.LedgerTransactions.Add(transaction);
                    return transaction;
                }
                Add(start.AddSeconds(-1), Accounts.Cash, Accounts.OpeningBalance, 100);
                Add(start, Accounts.Receivable, Accounts.ServiceRevenue, 1000, "InvoiceItem", invoice.Items[0].Id, "Charge");
                Add(start.AddHours(1), Accounts.Receivable, Accounts.PharmacyRevenue, 400);
                Add(start.AddHours(1), Accounts.Receivable, Accounts.OpticalRevenue, 200);
                Add(start.AddHours(1), Accounts.Discounts, Accounts.Receivable, 100);
                Add(start.AddHours(1), Accounts.Refunds, Accounts.Cash, 50);
                Add(start.AddHours(1), Accounts.CostOfGoods, Accounts.Inventory, 250);
                var original = Add(start.AddHours(1), Accounts.Expenses, Accounts.Cash, 80, "Expense", expense.Id);
                Add(start.AddHours(1), Accounts.InventoryAdjustments, Accounts.Inventory, 20);
                Add(start.AddHours(1), Accounts.Cash, Accounts.Receivable, 900);
                await db.SaveChangesAsync();
                var reversal = Add(start.AddDays(1), Accounts.Cash, Accounts.Expenses, 80, "Expense", expense.Id);
                reversal.ReversalOfId = original.Id;
                Add(start.AddDays(1), Accounts.Expenses, Accounts.Cash, 40);
                Add(start.AddDays(2), Accounts.Receivable, Accounts.ServiceRevenue, 999);
                Add(sale.CreatedAt, Accounts.Receivable, Accounts.PharmacyRevenue, 100, "InvoiceItem", saleLine.Id, "Charge");
                Add(sale.DeliveredAt.Value, Accounts.CostOfGoods, Accounts.Inventory, 40, nameof(Sale), sale.Id, "Cogs");
                await db.SaveChangesAsync();
                return true;
            });

            const string range = "?from=2001-01-02&to=2001-01-03";
            // Ledger charges, discounts and refund journals without a receipt or a refund paid are never revenue.
            var finance = await ReportAsync(accountant, "finance", range);
            Assert.Equal(0, Tile(finance, "عواید"));
            Assert.Equal(0, Tile(finance, "پول برگشت‌داده‌شده"));
            Assert.Equal(40, Tile(finance, "مصارف"));
            Assert.Equal(-40, Tile(finance, "خالص دریافتی"));
            Assert.Equal(100, Tile(finance, "تخفیفات"));
            Assert.Equal(910, Tile(finance, "پول نقد دفتری"));
            Assert.Equal(-40, Total(Table(finance, "محاسبه خالص دریافتی"))[1].GetDecimal());
            var cash = Table(finance, "پول نقد شفاخانه");
            Assert.Equal(100, Rows(cash)[0][1].GetDecimal());
            Assert.Equal(910, Total(cash)[1].GetDecimal());
            var categories = Rows(Table(finance, "مصارف به تفکیک نوع"));
            Assert.Equal(0, categories.Single(r => r[0].GetString() == "مصرف آزمایشی گزارش")[1].GetDecimal());

            var overview = await ReportAsync(accountant, "overview", range);
            Assert.All(ReportSections.Core, name => Assert.Equal(0, SectionNet(overview, name)));
            Assert.Equal(-40, Tile(overview, "خالص دریافتی"));

            // The expense cancelled the next day still belongs to the day it was posted.
            var firstDay = await ReportAsync(accountant, "finance", "?from=2001-01-02&to=2001-01-02");
            Assert.Equal(80, Tile(firstDay, "مصارف"));
            Assert.Equal(-80, Tile(firstDay, "خالص دریافتی"));
            // A Kabul day starts at 19:30 UTC the day before: the opening cash posted one second earlier is not in it.
            Assert.Equal(100, Rows(Table(firstDay, "پول نقد شفاخانه"))[0][1].GetDecimal());
            Assert.Equal(0, Tile(await ReportAsync(accountant, "finance", "?from=1999-01-01&to=1999-01-01"), "خالص دریافتی"));

            // An unpaid bill is no sale; the cost of goods counts on the day they were handed over.
            var billDay = await ReportAsync(accountant, "pharmacy", "?from=2001-01-05&to=2001-01-05");
            Assert.Equal(0, Tile(billDay, "فروشات"));
            Assert.Equal(0, Tile(billDay, "قیمت تمام‌شده"));
            var handDay = await ReportAsync(accountant, "pharmacy", "?from=2001-01-06&to=2001-01-06");
            Assert.Equal(0, Tile(handDay, "فروشات"));
            Assert.Equal(40, Tile(handDay, "قیمت تمام‌شده"));
            Assert.Equal(-40, MaybeTile(handDay, "سود فروشات"));

            foreach (var key in Tabs)
            {
                foreach (var format in new[] { "xlsx", "pdf" })
                {
                    var response = await accountant.GetAsync($"/api/reports/{key}/export{range}&format={format}");
                    Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
                    var bytes = await response.Content.ReadAsByteArrayAsync();
                    Assert.True(bytes.Length > 100);
                    Assert.Equal(format == "pdf" ? "%PDF" : "PK", System.Text.Encoding.ASCII.GetString(bytes[..(format == "pdf" ? 4 : 2)]));
                }
            }
        }
        finally
        {
            // Synthetic fixtures must not change later tests' whole-ledger reconciliation.
            await InDbAsync(async db =>
            {
                var transactions = await db.LedgerTransactions.Include(t => t.Entries).Where(t => t.Description == marker).ToListAsync();
                foreach (var transaction in transactions) transaction.ReversalOfId = null;
                await db.SaveChangesAsync();
                db.LedgerEntries.RemoveRange(transactions.SelectMany(t => t.Entries));
                db.LedgerTransactions.RemoveRange(transactions);
                var invoices = await db.Invoices.Include(i => i.Items).Where(i => i.InvoiceNumber == marker || i.InvoiceNumber == saleMarker).ToListAsync();
                db.InvoiceItems.RemoveRange(invoices.SelectMany(i => i.Items));
                var sales = await db.Sales.Include(s => s.Items).Where(s => s.SaleNumber == marker).ToListAsync();
                db.Set<SaleItem>().RemoveRange(sales.SelectMany(s => s.Items));
                db.Sales.RemoveRange(sales);
                await db.SaveChangesAsync();
                db.Invoices.RemoveRange(invoices);
                db.Expenses.RemoveRange(await db.Expenses.Where(e => e.ExpenseNumber == marker).ToListAsync());
                db.StockBatches.RemoveRange(await db.StockBatches.Where(b => b.LotNumber == marker).ToListAsync());
                await db.SaveChangesAsync();
                db.Products.RemoveRange(await db.Products.Where(p => p.Code == marker).ToListAsync());
                db.Services.RemoveRange(await db.Services.Where(s => s.Code == marker).ToListAsync());
                await db.SaveChangesAsync();
                return true;
            });
        }
    }

    [Fact]
    public async Task Store_lists_what_is_gone_short_or_expiring_and_its_value()
    {
        var inventory = await UserAsync(Roles.Inventory);
        var low = "کم " + Name("n");
        var expired = "گذشته " + Name("n");
        var fresh = "تازه " + Name("n");
        await InDbAsync(async db =>
        {
            var location = await db.StockLocations.Select(l => l.Id).FirstAsync();
            var products = new[]
            {
                new Product { Code = Name("L"), Name = low, Kind = ProductKind.Drug, Unit = "عدد", ReorderLevel = 10 },
                new Product { Code = Name("E"), Name = expired, Kind = ProductKind.Drug, Unit = "عدد" },
                new Product { Code = Name("F"), Name = fresh, Kind = ProductKind.Drug, Unit = "عدد" },
            };
            db.Products.AddRange(products);
            await db.SaveChangesAsync();
            db.StockBatches.AddRange(
                new StockBatch { ProductId = products[0].Id, LocationId = location, LotNumber = "L1", Quantity = 4, UnitCost = 2 },
                new StockBatch { ProductId = products[1].Id, LocationId = location, LotNumber = "E1", Quantity = 3, UnitCost = 2, ExpiryDate = KabulClock.Today.AddDays(-1) },
                new StockBatch { ProductId = products[2].Id, LocationId = location, LotNumber = "F1", Quantity = 3, UnitCost = 2, ExpiryDate = KabulClock.Today.AddDays(365) });
            return await db.SaveChangesAsync();
        });

        var report = await ReportAsync(inventory, "pharmacy");
        var shortages = Rows(Table(report, "اجناس تمام‌شده و کمبود"));
        Assert.Equal("کمبود", shortages.Single(r => r[0].GetString() == low)[5].GetString());
        Assert.DoesNotContain(shortages, r => r[0].GetString() == fresh);
        var expiring = Rows(Table(report, "اجناس منقضی و نزدیک به انقضا"));
        Assert.Equal("تاریخ گذشته", expiring.Single(r => r[0].GetString() == expired)[6].GetString());
        Assert.DoesNotContain(expiring, r => r[0].GetString() == fresh);
        var all = Rows(Table(report, "موجودی همه اجناس"));
        Assert.Equal("عادی", all.Single(r => r[0].GetString() == fresh)[7].GetString());
        Assert.Equal(6, all.Single(r => r[0].GetString() == fresh)[6].GetDecimal());
        Assert.True(Tile(report, "منقضی یا نزدیک به انقضا") >= 1);
        Assert.True(Tile(report, "اجناس تمام‌شده یا کمبود") >= 1);
    }

    [Fact]
    public async Task A_section_sees_only_its_own_sales()
    {
        var optical = Rows(Table(await ReportAsync(await UserAsync(Roles.Optical), "pharmacy"), "فروشات"));
        Assert.Equal([ReportSections.Optical], optical.Select(r => r[0].GetString()));

        var pharmacy = Rows(Table(await ReportAsync(await UserAsync(Roles.Pharmacist), "pharmacy"), "فروشات"));
        Assert.Equal([ReportSections.Pharmacy], pharmacy.Select(r => r[0].GetString()));
    }

    [Fact]
    public async Task Excel_and_pdf_files_hold_every_list_and_only_who_may_export_gets_them()
    {
        var accountant = await UserAsync(Roles.Accountant);
        var what = Name("کرایه");
        await Read<JsonElement>(await accountant.SendAsync(Post("/api/expenses", new CreateExpenseRequest(what, null, 40, KabulClock.Today))), HttpStatusCode.Created);

        var excel = await accountant.GetAsync("/api/reports/finance/export?format=xlsx");
        Assert.Equal(HttpStatusCode.OK, excel.StatusCode);
        Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", excel.Content.Headers.ContentType?.MediaType);
        Assert.EndsWith(".xlsx", excel.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
        using (var zip = new System.IO.Compression.ZipArchive(new MemoryStream(await excel.Content.ReadAsByteArrayAsync())))
        {
            using var reader = new StreamReader(zip.GetEntry("xl/worksheets/sheet1.xml")!.Open());
            var sheet = await reader.ReadToEndAsync();
            Assert.Contains("rightToLeft=\"1\"", sheet);
            // Every list of the tab is in the file, not only the first.
            Assert.Contains("محاسبه خالص دریافتی", sheet);
            Assert.Contains("مصارف به تفکیک نوع", sheet);
            Assert.Contains(what, sheet);
            Assert.Contains("طلبات: فاکتورهای پرداخت‌نشده", sheet);
            // Text is never a formula.
            Assert.DoesNotContain("<f>", sheet);
        }

        var pdf = await accountant.GetAsync("/api/reports/finance/export?format=pdf");
        Assert.Equal(HttpStatusCode.OK, pdf.StatusCode);
        Assert.Equal("application/pdf", pdf.Content.Headers.ContentType?.MediaType);
        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString((await pdf.Content.ReadAsByteArrayAsync())[..4]));

        Assert.Equal(HttpStatusCode.BadRequest, (await accountant.GetAsync("/api/reports/finance/export?format=csv")).StatusCode);

        // One report alone: its file holds that report and none of the others of the tab.
        var single = await accountant.GetAsync("/api/reports/finance/export?format=xlsx&item=expenses");
        Assert.Equal(HttpStatusCode.OK, single.StatusCode);
        Assert.Contains("expenses", single.Content.Headers.ContentDisposition?.FileName ?? "");
        using (var zip = new System.IO.Compression.ZipArchive(new MemoryStream(await single.Content.ReadAsByteArrayAsync())))
        {
            using var reader = new StreamReader(zip.GetEntry("xl/worksheets/sheet1.xml")!.Open());
            var sheet = await reader.ReadToEndAsync();
            Assert.Contains("مصارف به تفکیک نوع", sheet);
            Assert.Contains(what, sheet);
            Assert.DoesNotContain("محاسبه خالص دریافتی", sheet);
            Assert.DoesNotContain("طلبات: فاکتورهای پرداخت‌نشده", sheet);
        }

        var singlePdf = await accountant.GetAsync("/api/reports/finance/export?format=pdf&item=net");
        Assert.Equal(HttpStatusCode.OK, singlePdf.StatusCode);
        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString((await singlePdf.Content.ReadAsByteArrayAsync())[..4]));
        Assert.Equal(HttpStatusCode.NotFound, (await accountant.GetAsync("/api/reports/finance/export?format=pdf&item=nothing")).StatusCode);

        // A doctor may read the overview but not take it away as a file.
        var doctor = await UserAsync(Roles.Doctor);
        Assert.Equal(HttpStatusCode.Forbidden, (await doctor.GetAsync("/api/reports/overview/export?format=xlsx")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await doctor.GetAsync("/api/reports/overview/export?format=pdf")).StatusCode);
    }


    [Fact]
    public async Task Seeding_a_database_in_use_takes_the_register_permission_back_from_reception()
    {
        using var scope = api.Services.CreateScope();
        var roles = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.RoleManager<AlShifa.Api.Features.Users.AppRole>>();
        var role = (await roles.FindByNameAsync(Roles.Receptionist))!;
        var claim = new System.Security.Claims.Claim(RolePermissions.ClaimType, "cash.create");
        await roles.AddClaimAsync(role, claim);

        await Seeder.SeedReferenceDataAsync(scope.ServiceProvider);

        Assert.DoesNotContain((await roles.GetClaimsAsync(role)).Select(c => c.Value), value => value == "cash.create");
    }

    [Fact]
    public async Task A_register_opened_before_fees_moved_can_still_be_closed_by_reception()
    {
        var name = Name("r");
        await api.CreateUserAsync(name, Roles.Receptionist);
        var reception = await api.LoginAsync(name);
        var sessionId = await InDbAsync(async db =>
        {
            var user = await db.Users.Where(u => u.UserName == name).Select(u => u.Id).SingleAsync();
            var register = new CashRegister { Name = name };
            db.CashRegisters.Add(register);
            await db.SaveChangesAsync();
            var session = new CashSession { RegisterId = register.Id, OpenedByUserId = user, OpeningAmount = 5 };
            db.CashSessions.Add(session);
            await db.SaveChangesAsync();
            return session.Id;
        });

        var sessions = await Read<JsonElement>(await reception.GetAsync("/api/cash/sessions?mine=true"));
        Assert.Equal(sessionId, sessions.EnumerateArray().Single().GetProperty("id").GetInt64());
        Assert.Equal(HttpStatusCode.OK, (await reception.PostAsJsonAsync($"/api/cash/sessions/{sessionId}/close", new CloseSessionRequest(5, 0))).StatusCode);
    }
}
