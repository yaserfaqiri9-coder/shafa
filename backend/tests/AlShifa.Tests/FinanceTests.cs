using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AlShifa.Api.Features.Auth;
using AlShifa.Api.Features.Clinical;
using AlShifa.Api.Features.Finance;
using AlShifa.Api.Features.Inventory;
using AlShifa.Api.Features.Patients;
using AlShifa.Api.Features.Reports;
using AlShifa.Api.Features.Users;
using AlShifa.Api.Features.Visits;
using AlShifa.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AlShifa.Tests;

[Collection("api")]
public sealed class FinanceTests(ApiFixture api) : PhysicalQueueTestScope(api)
{
    [Fact]
    public void Shamsi_payroll_prorates_start_end_and_rate_changes_without_charging_an_unfinished_month()
    {
        var calendar = new System.Globalization.PersianCalendar();
        DateOnly Day(int y, int m, int d) => DateOnly.FromDateTime(calendar.ToDateTime(y, m, d, 0, 0, 0, 0));
        var staff = new Staff { EmploymentStartDate = Day(1405, 1, 15), EmploymentEndDate = Day(1405, 2, 10) };
        var terms = new[] { new StaffSalaryTerm { EffectiveFrom = staff.EmploymentStartDate.Value, MonthlySalary = 3100 } };
        var beforeEnd = StaffPayroll.Earned(staff, terms, Day(1405, 1, 31));
        Assert.Empty(beforeEnd);
        var months = StaffPayroll.Earned(staff, terms, Day(1405, 3, 1));
        Assert.Equal(new decimal[] { 1700, 1000 }, months.Select(m => m.Amount));
        Assert.Equal(new[] { 17, 10 }, months.Select(m => m.WorkingDays));
        Assert.Equal(2700m, months.Sum(m => m.Amount));
        var changedRate = StaffPayroll.Earned(staff, [terms[0], new StaffSalaryTerm { EffectiveFrom = Day(1405, 2, 1), MonthlySalary = 6200 }], Day(1405, 3, 1));
        Assert.Equal(3700m, changedRate.Sum(m => m.Amount));
        staff.EmploymentStartDate = Day(1403, 12, 1);
        staff.EmploymentEndDate = Day(1403, 12, 30);
        Assert.Equal(3000m, Assert.Single(StaffPayroll.Earned(staff,
            [new StaffSalaryTerm { EffectiveFrom = staff.EmploymentStartDate.Value, MonthlySalary = 3000 }], Day(1404, 1, 1))).Amount);
    }

    [Fact]
    public async Task Staff_profile_tracks_monthly_debt_withdrawals_salary_retries_and_reversals()
    {
        var admin = await UserAsync(Roles.SuperAdmin);
        var start = StaffPayroll.MonthStart(StaffPayroll.MonthStart(KabulClock.Today).AddDays(-1));
        var created = await admin.PostAsJsonAsync("/api/staff", new SaveStaffRequest(Name("staff"), null, false, true, null, null,
            MonthlySalary: 3000, EmploymentStartDate: start));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();
        async Task<JsonElement> Profile() => await admin.GetFromJsonAsync<JsonElement>($"/api/staff/{id}/payroll");
        Assert.Equal(3000m, (await Profile()).GetProperty("owed").GetDecimal());
        var key = Guid.NewGuid().ToString("N");
        var withdrawal = new StaffPayoutRequest(1000, KabulClock.Today, "Withdrawal", "synthetic advance");
        var first = await Created<JsonElement>(await admin.SendAsync(Post($"/api/staff/{id}/payouts", withdrawal, key)));
        var retry = await Created<JsonElement>(await admin.SendAsync(Post($"/api/staff/{id}/payouts", withdrawal, key)));
        Assert.Equal(first.GetProperty("id").GetInt64(), retry.GetProperty("id").GetInt64());
        Assert.Equal(2000m, (await Profile()).GetProperty("owed").GetDecimal());
        var over = await admin.SendAsync(Post($"/api/staff/{id}/payouts", new StaffPayoutRequest(2001, KabulClock.Today, "Salary", null)));
        Assert.Equal(HttpStatusCode.Conflict, over.StatusCode);
        await Created<JsonElement>(await admin.SendAsync(Post($"/api/staff/{id}/payouts", new StaffPayoutRequest(2000, KabulClock.Today, "Salary", null))));
        Assert.Equal(0m, (await Profile()).GetProperty("owed").GetDecimal());
        using (var scope = api.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Equal(2, await db.Expenses.CountAsync(e => e.StaffId == id));
            var expense = await db.Expenses.SingleAsync(e => e.Id == first.GetProperty("id").GetInt64());
            var canceled = await admin.PostAsJsonAsync($"/api/expenses/{expense.Id}/cancel", new VoidRequest("synthetic correction", expense.Version));
            Assert.Equal(HttpStatusCode.NoContent, canceled.StatusCode);
        }
        Assert.Equal(1000m, (await Profile()).GetProperty("owed").GetDecimal());
        var reception = await UserAsync(Roles.Receptionist);
        Assert.Equal(HttpStatusCode.Forbidden, (await reception.GetAsync($"/api/staff/{id}/payroll")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reception.SendAsync(Post($"/api/staff/{id}/payouts", withdrawal))).StatusCode);
    }

    [Fact]
    public async Task Operation_price_and_package_snapshot_are_charged_and_consumed_once_at_final_payment()
    {
        var admin = await UserAsync(Roles.SuperAdmin);
        var package = await Created<PackageRow>(await admin.PostAsJsonAsync("/api/surgery-packages", new SavePackageRequest(Name("kit"), 900, 100, 5, "surgery")));
        var operationResponse = await Created<JsonElement>(await admin.PostAsJsonAsync("/api/operations", new SaveOperationRequest(Name("operation"), 5000, [new OperationPackageInput(package.Id, 2)])));
        var operationId = operationResponse.GetProperty("id").GetInt32();
        var patient = await NewPatientAsync();
        long visitId;
        using (var scope = api.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var visit = new Visit { PatientId = patient.Id, VisitNumber = Name("visit"), DepartmentId = await db.Departments.Select(d => d.Id).FirstAsync(), Status = VisitStatus.InConsultation };
            db.Visits.Add(visit);
            await db.SaveChangesAsync();
            visitId = visit.Id;
        }
        var surgery = await Created<SurgeryRow>(await admin.SendAsync(Post("/api/surgeries", new CreateSurgeryRequest(visitId, "ignored client name", EyeSide.OD, OperationDefinitionId: operationId))));
        long invoiceId;
        using (var scope = api.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var item = await db.InvoiceItems.SingleAsync(i => i.SourceType == nameof(SurgeryCase) && i.SourceId == surgery.Id);
            invoiceId = item.InvoiceId;
            Assert.Equal(5000m, item.LineTotal);
            Assert.Equal(5m, await db.StockBatches.Where(b => b.ProductId == package.Items[0].ProductId).SumAsync(b => b.Quantity));
        }
        var catalog = await admin.GetFromJsonAsync<JsonElement>("/api/operations?all=true");
        var op = catalog.EnumerateArray().Single(o => o.GetProperty("id").GetInt32() == operationId);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsJsonAsync($"/api/operations/{operationId}", new SaveOperationRequest("Changed operation", 6000, [new OperationPackageInput(package.Id, 1)], Version: op.GetProperty("version").GetInt32()))).StatusCode);
        var cashier = await CashierWithOpenSessionAsync();
        await Created<PaymentRow>(await cashier.SendAsync(Post("/api/payments", new PaymentRequest(invoiceId, 2000, PaymentMethod.Cash, null))));
        using (var scope = api.Services.CreateScope())
            Assert.Equal(5m, await scope.ServiceProvider.GetRequiredService<AppDbContext>().StockBatches.Where(b => b.ProductId == package.Items[0].ProductId).SumAsync(b => b.Quantity));
        var key = Guid.NewGuid().ToString("N");
        var final = new PaymentRequest(invoiceId, 3000, PaymentMethod.Cash, null);
        await Created<PaymentRow>(await cashier.SendAsync(Post("/api/payments", final, key)));
        await Created<PaymentRow>(await cashier.SendAsync(Post("/api/payments", final, key)));
        using (var scope = api.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Equal(3m, await db.StockBatches.Where(b => b.ProductId == package.Items[0].ProductId).SumAsync(b => b.Quantity));
            Assert.NotNull((await db.SurgeryCases.SingleAsync(c => c.Id == surgery.Id)).SuppliesRecordedAt);
            Assert.Equal(2, await db.Payments.CountAsync(p => p.InvoiceId == invoiceId));
            Assert.Equal(2m, -(await db.StockMovements.Where(m => m.SourceType == nameof(SurgeryCase) && m.SourceId == surgery.Id).SumAsync(m => m.Quantity)));
        }
        var latest = (await admin.GetFromJsonAsync<JsonElement>("/api/operations?all=true")).EnumerateArray().Single(o => o.GetProperty("id").GetInt32() == operationId);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsJsonAsync($"/api/operations/{operationId}",
            new SaveOperationRequest("Four kits", 6000, [new OperationPackageInput(package.Id, 4)], Version: latest.GetProperty("version").GetInt32()))).StatusCode);
        var secondCase = await Created<SurgeryRow>(await admin.SendAsync(Post("/api/surgeries", new CreateSurgeryRequest(visitId, "four kits", EyeSide.OD, OperationDefinitionId: operationId))));
        long secondInvoice;
        using (var scope = api.Services.CreateScope())
            secondInvoice = (await scope.ServiceProvider.GetRequiredService<AppDbContext>().InvoiceItems.SingleAsync(i => i.SourceType == nameof(SurgeryCase) && i.SourceId == secondCase.Id)).InvoiceId;
        var refused = await cashier.SendAsync(Post("/api/payments", new PaymentRequest(secondInvoice, 6000, PaymentMethod.Cash, null)));
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        using (var scope = api.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.False(await db.Payments.AnyAsync(p => p.InvoiceId == secondInvoice));
            Assert.Equal(0m, (await db.Invoices.SingleAsync(i => i.Id == secondInvoice)).Paid);
            Assert.Equal(3m, await db.StockBatches.Where(b => b.ProductId == package.Items[0].ProductId).SumAsync(b => b.Quantity));
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

    private static string Name(string prefix) => $"{prefix}_{Guid.NewGuid():N}"[..20];

    private async Task<HttpClient> UserAsync(params string[] roles)
    {
        var name = Name("u");
        await api.CreateUserAsync(name, roles);
        return await api.LoginAsync(name);
    }

    private static HttpRequestMessage Post(string url, object body, string? key = null)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
        message.Headers.Add("Idempotency-Key", key ?? Guid.NewGuid().ToString("N"));
        return message;
    }

    private static async Task<T> Created<T>(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.Created, text);
        return JsonSerializer.Deserialize<T>(text, JsonOptions)!;
    }

    private async Task<int> NewServiceAsync(decimal price)
    {
        var admin = await UserAsync(Roles.SuperAdmin);
        var response = await admin.PostAsJsonAsync("/api/services", new SaveServiceRequest("خدمت " + Name("n"), null, true, price));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetInt32();
    }

    private async Task<PatientDto> NewPatientAsync()
    {
        var reception = await UserAsync(Roles.Receptionist);
        return await Created<PatientDto>(await reception.SendAsync(Post("/api/patients", new SavePatientRequest(Name("مریض"), null, null, Gender.Male, null, 30, null, null))));
    }

    private async Task<HttpClient> CashierWithOpenSessionAsync()
    {
        var cashier = await UserAsync(Roles.Cashier);
        await OpenFreshSessionAsync(cashier);
        return cashier;
    }

    /// <summary>Each test cashier gets a register of their own, since a register has one open session at a time.</summary>
    private async Task OpenFreshSessionAsync(HttpClient cashier)
    {
        int registerId;
        using (var scope = api.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var register = new CashRegister { Name = Name("صندوق") };
            db.CashRegisters.Add(register);
            await db.SaveChangesAsync();
            registerId = register.Id;
        }

        var opened = await cashier.PostAsJsonAsync("/api/cash/sessions", new OpenSessionRequest(registerId, 0));
        Assert.True(opened.StatusCode == HttpStatusCode.Created, await opened.Content.ReadAsStringAsync());
    }

    private async Task<InvoiceRow> NewInvoiceAsync(long patientId, int serviceId)
    {
        var cashier = await UserAsync(Roles.Cashier);
        return await Created<InvoiceRow>(await cashier.SendAsync(Post("/api/invoices", new CreateInvoiceRequest(patientId, null, [new InvoiceLineInput(serviceId, 1)]))));
    }

    private async Task<int> NewProductAsync(decimal salePrice = 100, ProductKind kind = ProductKind.Drug)
    {
        var manager = await UserAsync(Roles.Inventory);
        var response = await manager.PostAsJsonAsync("/api/inventory/products", new SaveProductRequest("جنس " + Name("n"), kind, null, "عدد", 0, salePrice));
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetInt32();
    }

    private async Task StockAsync(int productId, decimal quantity, string location = "pharmacy", decimal cost = 40)
    {
        var manager = await UserAsync(Roles.Inventory);
        var response = await manager.SendAsync(Post("/api/inventory/purchases", new CreatePurchaseRequest("تأمین‌کننده", location,
            [new PurchaseLineInput(productId, Name("L"), DateOnly.FromDateTime(DateTime.UtcNow.AddYears(1)), quantity, cost)])));
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
    }

    private async Task AssertLedgerReconciles()
    {
        using var scope = api.Services.CreateScope();
        var result = await FinanceEndpoints.ReconcileAsync(scope.ServiceProvider.GetRequiredService<AppDbContext>(), default);
        Assert.True(result.Balanced, JsonSerializer.Serialize(result));
    }

    [Fact]
    public async Task Patient_file_lists_only_that_patients_invoices()
    {
        var serviceId = await NewServiceAsync(100);
        var first = await NewPatientAsync();
        var second = await NewPatientAsync();
        var own = await NewInvoiceAsync(first.Id, serviceId);
        await NewInvoiceAsync(second.Id, serviceId);

        var cashier = await UserAsync(Roles.Cashier);
        var page = await cashier.GetFromJsonAsync<PagedResult<InvoiceRow>>($"/api/invoices?patientId={first.Id}&pageSize=100", JsonOptions);
        Assert.Equal(own.Id, Assert.Single(page!.Items).Id);
        Assert.Equal(1, page.Total);
    }

    [Fact]
    public async Task Visit_service_charge_appears_at_the_cashier_with_a_price_snapshot()
    {
        var serviceId = await NewServiceAsync(500);
        var patient = await NewPatientAsync();
        var reception = await UserAsync(Roles.Receptionist);
        var departments = await reception.GetFromJsonAsync<List<LookupItem>>("/api/lookups/departments");
        var visit = await Created<VisitRow>(await reception.SendAsync(Post("/api/visits", new CreateVisitRequest(patient.Id, (int)departments![0].Id, null, null, serviceId, await api.PaidTokenAsync()))));

        var cashier = await UserAsync(Roles.Cashier);
        var unpaid = await cashier.GetFromJsonAsync<PagedResult<InvoiceRow>>($"/api/invoices?unpaid=true&q={patient.FileNumber}", JsonOptions);
        // Search also matches invoice numbers, so unrelated invoices can share a numeric file-number fragment.
        var invoice = Assert.Single(unpaid!.Items, i => i.VisitId == visit.Id);
        Assert.Equal(visit.Id, invoice.VisitId);
        Assert.Equal(500, invoice.Balance);

        var admin = await UserAsync(Roles.SuperAdmin);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsJsonAsync($"/api/services/{serviceId}/price", new SetPriceRequest(900))).StatusCode);

        var detail = await cashier.GetFromJsonAsync<InvoiceDetail>($"/api/invoices/{invoice.Id}", JsonOptions);
        Assert.Equal(500, Assert.Single(detail!.Items).UnitPrice);
        await AssertLedgerReconciles();
    }

    [Fact]
    public async Task Same_source_cannot_be_charged_twice()
    {
        var serviceId = await NewServiceAsync(100);
        var patient = await NewPatientAsync();
        var reception = await UserAsync(Roles.Receptionist);
        var departments = await reception.GetFromJsonAsync<List<LookupItem>>("/api/lookups/departments");
        var visit = await Created<VisitRow>(await reception.SendAsync(Post("/api/visits", new CreateVisitRequest(patient.Id, (int)departments![0].Id, null, null, serviceId, await api.PaidTokenAsync()))));

        using var scope = api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var item = await db.InvoiceItems.AsNoTracking().SingleAsync(i => i.SourceType == nameof(Visit) && i.SourceId == visit.Id);

        db.InvoiceItems.Add(new InvoiceItem { InvoiceId = item.InvoiceId, Description = "تکراری", UnitPrice = 100, LineTotal = 100, RevenueAccount = Accounts.ServiceRevenue, SourceType = nameof(Visit), SourceId = visit.Id });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Partial_payment_leaves_the_correct_balance_and_overpayment_is_refused()
    {
        var serviceId = await NewServiceAsync(1000);
        var patient = await NewPatientAsync();
        var invoice = await NewInvoiceAsync(patient.Id, serviceId);
        var cashier = await CashierWithOpenSessionAsync();

        await Created<PaymentRow>(await cashier.SendAsync(Post("/api/payments", new PaymentRequest(invoice.Id, 400, PaymentMethod.Cash, null))));

        var row = await cashier.GetFromJsonAsync<InvoiceDetail>($"/api/invoices/{invoice.Id}", JsonOptions);
        Assert.Equal(600, row!.Invoice.Balance);
        Assert.Equal(nameof(InvoiceStatus.PartiallyPaid), row.Invoice.Status);

        var over = await cashier.SendAsync(Post("/api/payments", new PaymentRequest(invoice.Id, 601, PaymentMethod.Cash, null)));
        Assert.Equal(HttpStatusCode.Conflict, over.StatusCode);
        Assert.Contains("overpayment", await over.Content.ReadAsStringAsync());

        await Created<PaymentRow>(await cashier.SendAsync(Post("/api/payments", new PaymentRequest(invoice.Id, 600, PaymentMethod.Cash, null))));
        row = await cashier.GetFromJsonAsync<InvoiceDetail>($"/api/invoices/{invoice.Id}", JsonOptions);
        Assert.Equal(0, row!.Invoice.Balance);
        Assert.Equal(nameof(InvoiceStatus.Paid), row.Invoice.Status);
        await AssertLedgerReconciles();
    }

    [Fact]
    public async Task Cashier_settlement_applies_one_fixed_or_percent_discount_before_payment()
    {
        var serviceId = await NewServiceAsync(500);
        var patient = await NewPatientAsync();
        var fixedInvoice = await NewInvoiceAsync(patient.Id, serviceId);
        var cashier = await CashierWithOpenSessionAsync();

        var fixedReceipt = await Created<PaymentRow>(await cashier.SendAsync(Post("/api/payments/settle",
            new CashierSettlementRequest(fixedInvoice.Id, 100, fixedInvoice.Version, new SettlementDiscount(400, DiscountMode.Amount)))));
        Assert.Equal(100m, fixedReceipt.Amount);
        var fixedDetail = await cashier.GetFromJsonAsync<InvoiceDetail>($"/api/invoices/{fixedInvoice.Id}", JsonOptions);
        Assert.Equal(400m, fixedDetail!.Invoice.Discount);
        Assert.Equal(0m, fixedDetail.Invoice.Balance);
        Assert.Equal(nameof(InvoiceStatus.Paid), fixedDetail.Invoice.Status);
        Assert.Single(fixedDetail.Discounts);

        var percentInvoice = await NewInvoiceAsync(patient.Id, serviceId);
        await Created<PaymentRow>(await cashier.SendAsync(Post("/api/payments/settle",
            new CashierSettlementRequest(percentInvoice.Id, 400, percentInvoice.Version, new SettlementDiscount(20, DiscountMode.Percent)))));
        var percentDetail = await cashier.GetFromJsonAsync<InvoiceDetail>($"/api/invoices/{percentInvoice.Id}", JsonOptions);
        Assert.Equal(100m, percentDetail!.Invoice.Discount);
        Assert.Equal(0m, percentDetail.Invoice.Balance);
        Assert.Single(percentDetail.Discounts);
        await AssertLedgerReconciles();
    }

    [Fact]
    public async Task Same_payment_sent_concurrently_is_recorded_once()
    {
        var serviceId = await NewServiceAsync(1000);
        var patient = await NewPatientAsync();
        var invoice = await NewInvoiceAsync(patient.Id, serviceId);
        var cashier = await CashierWithOpenSessionAsync();
        var key = Guid.NewGuid().ToString("N");

        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            cashier.SendAsync(Post("/api/payments", new PaymentRequest(invoice.Id, 300, PaymentMethod.Cash, null), key))));
        var receipts = await Task.WhenAll(responses.Select(r => r.Content.ReadFromJsonAsync<PaymentRow>(JsonOptions)));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.Created, r.StatusCode));
        Assert.Single(receipts.Select(r => r!.ReceiptNumber).Distinct());

        var row = await cashier.GetFromJsonAsync<InvoiceDetail>($"/api/invoices/{invoice.Id}", JsonOptions);
        Assert.Single(row!.Payments);
        Assert.Equal(700, row.Invoice.Balance);
    }

    [Fact]
    public async Task Two_cashiers_cannot_overpay_one_invoice_together()
    {
        var serviceId = await NewServiceAsync(1000);
        var patient = await NewPatientAsync();
        var invoice = await NewInvoiceAsync(patient.Id, serviceId);
        var first = await CashierWithOpenSessionAsync();
        var second = await CashierWithOpenSessionAsync();

        var responses = await Task.WhenAll(
            first.SendAsync(Post("/api/payments", new PaymentRequest(invoice.Id, 800, PaymentMethod.Cash, null))),
            second.SendAsync(Post("/api/payments", new PaymentRequest(invoice.Id, 800, PaymentMethod.Cash, null))));

        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Created);
        var row = await first.GetFromJsonAsync<InvoiceDetail>($"/api/invoices/{invoice.Id}", JsonOptions);
        Assert.Equal(200, row!.Invoice.Balance);
        await AssertLedgerReconciles();
    }

    [Fact]
    public async Task Payment_needs_an_open_cash_session_and_a_cashier_cannot_refund()
    {
        var serviceId = await NewServiceAsync(100);
        var patient = await NewPatientAsync();
        var invoice = await NewInvoiceAsync(patient.Id, serviceId);

        var cashier = await UserAsync(Roles.Cashier);
        var closed = await cashier.SendAsync(Post("/api/payments", new PaymentRequest(invoice.Id, 100, PaymentMethod.Cash, null)));
        Assert.Contains("no_open_session", await closed.Content.ReadAsStringAsync());

        await OpenFreshSessionAsync(cashier);
        var payment = await Created<PaymentRow>(await cashier.SendAsync(Post("/api/payments", new PaymentRequest(invoice.Id, 100, PaymentMethod.Cash, null))));

        var denied = await cashier.SendAsync(Post("/api/refunds", new RefundRequest(payment.Id, 50, "تلاش غیرمجاز")));
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
    }

    [Fact]
    public async Task Refund_is_limited_to_the_payment_and_the_ledger_stays_balanced()
    {
        var serviceId = await NewServiceAsync(1000);
        var patient = await NewPatientAsync();
        var invoice = await NewInvoiceAsync(patient.Id, serviceId);
        var cashier = await CashierWithOpenSessionAsync();
        var payment = await Created<PaymentRow>(await cashier.SendAsync(Post("/api/payments", new PaymentRequest(invoice.Id, 1000, PaymentMethod.Cash, null))));

        var accountant = await UserAsync(Roles.Accountant);
        await Created<object>(await accountant.SendAsync(Post("/api/refunds", new RefundRequest(payment.Id, 300, "برگشت جزئی"))));

        var over = await accountant.SendAsync(Post("/api/refunds", new RefundRequest(payment.Id, 701, "بیش از حد")));
        Assert.Contains("refund_too_large", await over.Content.ReadAsStringAsync());

        var row = await accountant.GetFromJsonAsync<InvoiceDetail>($"/api/invoices/{invoice.Id}", JsonOptions);
        Assert.Equal(300, row!.Invoice.Refunded);
        await AssertLedgerReconciles();
    }

    [Fact]
    public async Task Invoice_with_a_payment_cannot_be_voided_and_unpaid_void_reverses_the_ledger()
    {
        var serviceId = await NewServiceAsync(100);
        var patient = await NewPatientAsync();
        var paid = await NewInvoiceAsync(patient.Id, serviceId);
        var cashier = await CashierWithOpenSessionAsync();
        await Created<PaymentRow>(await cashier.SendAsync(Post("/api/payments", new PaymentRequest(paid.Id, 50, PaymentMethod.Cash, null))));

        var current = await cashier.GetFromJsonAsync<InvoiceDetail>($"/api/invoices/{paid.Id}", JsonOptions);
        var denied = await cashier.PostAsJsonAsync($"/api/invoices/{paid.Id}/void", new VoidRequest("اشتباه", current!.Invoice.Version));
        Assert.Contains("invoice_has_payments", await denied.Content.ReadAsStringAsync());

        var unpaid = await NewInvoiceAsync(patient.Id, serviceId);
        var fresh = await cashier.GetFromJsonAsync<InvoiceDetail>($"/api/invoices/{unpaid.Id}", JsonOptions);
        Assert.Equal(HttpStatusCode.NoContent, (await cashier.PostAsJsonAsync($"/api/invoices/{unpaid.Id}/void", new VoidRequest("اشتباه", fresh!.Invoice.Version))).StatusCode);

        var after = await cashier.GetFromJsonAsync<InvoiceDetail>($"/api/invoices/{unpaid.Id}", JsonOptions);
        Assert.Equal(nameof(InvoiceStatus.Void), after!.Invoice.Status);
        await AssertLedgerReconciles();
    }

    [Fact]
    public async Task Concurrent_sales_never_oversell_limited_stock()
    {
        var productId = await NewProductAsync();
        await StockAsync(productId, 5);
        var pharmacist = await UserAsync(Roles.Pharmacist);

        var responses = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ =>
            pharmacist.SendAsync(Post("/api/pharmacy/sales", new CreateSaleRequest(null, null, [new SaleLineInput(productId, 1)])))));

        var sold = responses.Count(r => r.StatusCode == HttpStatusCode.Created);
        var codes = await Task.WhenAll(responses.Where(r => r.StatusCode != HttpStatusCode.Created).Select(r => r.Content.ReadAsStringAsync()));
        Assert.All(codes, body => Assert.True(body.Contains("insufficient_stock") || body.Contains("busy"), body));
        Assert.InRange(sold, 1, 5);

        // Bills only reserve; the stock leaves when each paid bill is handed over.
        foreach (var bill in await Task.WhenAll(responses.Where(r => r.StatusCode == HttpStatusCode.Created).Select(r => r.Content.ReadFromJsonAsync<SaleRow>(JsonOptions))))
        {
            await api.PayAndDeliverAsync(pharmacist, bill!.Id);
        }

        using var scope = api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var onHand = await db.StockBatches.Where(b => b.ProductId == productId).SumAsync(b => b.Quantity);
        Assert.Equal(5 - sold, onHand);

        var moved = await db.StockMovements.Where(m => m.Batch!.ProductId == productId).SumAsync(m => m.Quantity);
        Assert.Equal(onHand, moved);
        await AssertLedgerReconciles();
    }

    [Fact]
    public async Task Prescription_sale_updates_dispensing_and_cannot_exceed_the_prescribed_amount()
    {
        var productId = await NewProductAsync();
        await StockAsync(productId, 10);

        var patient = await NewPatientAsync();
        var reception = await UserAsync(Roles.Receptionist);
        var departments = await reception.GetFromJsonAsync<List<LookupItem>>("/api/lookups/departments");
        var visit = await Created<VisitRow>(await reception.SendAsync(Post("/api/visits", new CreateVisitRequest(patient.Id, (int)departments![0].Id, null, null, null, await api.PaidTokenAsync()))));

        var doctor = await UserAsync(Roles.Doctor);
        var drug = await api.InDbAsync(db => db.Products.Where(p => p.Id == productId).Select(p => p.Name).SingleAsync());
        var rx = await Created<PrescriptionDto>(await doctor.SendAsync(Post("/api/prescriptions", new CreatePrescriptionRequest(visit.Id, null, [new RxItemInput(drug, null, null, null, null, 2)]))));

        var pharmacist = await UserAsync(Roles.Pharmacist);
        var itemId = rx.Items[0].Id;
        var over = await pharmacist.SendAsync(Post("/api/pharmacy/sales", new CreateSaleRequest(patient.Id, rx.Id, [new SaleLineInput(productId, 3, itemId)])));
        Assert.Contains("over_dispense", await over.Content.ReadAsStringAsync());

        var first = await Created<SaleRow>(await pharmacist.SendAsync(Post("/api/pharmacy/sales", new CreateSaleRequest(patient.Id, rx.Id, [new SaleLineInput(productId, 1, itemId)]))));
        Assert.Equal(nameof(PrescriptionStatus.Issued), (await pharmacist.GetFromJsonAsync<List<PrescriptionDto>>("/api/prescriptions?q=" + rx.RxNumber, JsonOptions))!.Single(r => r.Id == rx.Id).Status);
        await api.PayAndDeliverAsync(pharmacist, first.Id);
        var partial = (await pharmacist.GetFromJsonAsync<List<PrescriptionDto>>("/api/prescriptions?q=" + rx.RxNumber, JsonOptions))!.Single(r => r.Id == rx.Id);
        Assert.Equal(nameof(PrescriptionStatus.PartiallyDispensed), partial.Status);

        var second = await Created<SaleRow>(await pharmacist.SendAsync(Post("/api/pharmacy/sales", new CreateSaleRequest(patient.Id, rx.Id, [new SaleLineInput(productId, 1, itemId)]))));
        await api.PayAndDeliverAsync(pharmacist, second.Id);
        var full = (await pharmacist.GetFromJsonAsync<List<PrescriptionDto>>("/api/prescriptions?q=" + rx.RxNumber, JsonOptions))!.Single(r => r.Id == rx.Id);
        Assert.Equal(nameof(PrescriptionStatus.Dispensed), full.Status);
    }

    [Fact]
    public async Task Standalone_paid_pharmacy_sale_can_be_cancelled_and_refunded_without_delivery()
    {
        var productId = await NewProductAsync(salePrice: 250);
        await StockAsync(productId, 4);
        var pharmacist = await UserAsync(Roles.Pharmacist);

        var sale = await Created<SaleRow>(await pharmacist.SendAsync(Post("/api/pharmacy/sales", new CreateSaleRequest(null, null, [new SaleLineInput(productId, 2)]))));
        Assert.Null(sale.PatientId);
        Assert.Equal(500, sale.Balance);

        var cashier = await CashierWithOpenSessionAsync();
        await Created<PaymentRow>(await cashier.SendAsync(Post("/api/payments", new PaymentRequest(sale.InvoiceId, 500, PaymentMethod.Cash, null))));

        var returned = await pharmacist.PostAsJsonAsync($"/api/pharmacy/sales/{sale.Id}/return", new ReturnSaleRequest(sale.Version, "برگشت"));
        Assert.Equal(HttpStatusCode.NoContent, returned.StatusCode);
        Assert.Equal(500, await api.InDbAsync(db => db.Invoices.Where(i => i.Id == sale.InvoiceId).Select(i => i.Refunded).SingleAsync()));
        Assert.Equal(4, await api.InDbAsync(db => db.StockBatches.Where(b => b.ProductId == productId).SumAsync(b => b.Quantity)));
        await AssertLedgerReconciles();
    }

    [Theory]
    [InlineData(false, 0, 80, 0)]
    [InlineData(false, 20, 180, 0)]
    [InlineData(true, 20, 180, 50)]
    [InlineData(true, 0, 200, 200)]
    public async Task Pharmacy_return_refunds_only_money_still_held_and_reconciles(bool delivered, decimal discount, decimal paid, decimal earlierRefund)
    {
        var productId = await NewProductAsync();
        await StockAsync(productId, 3);
        var pharmacist = await UserAsync(Roles.Pharmacist);
        var accountant = await UserAsync(Roles.Accountant);
        var cashier = await CashierWithOpenSessionAsync();
        var sale = await Created<SaleRow>(await pharmacist.SendAsync(Post("/api/pharmacy/sales", new CreateSaleRequest(null, null, [new SaleLineInput(productId, 2)]))));
        if (discount > 0)
        {
            var version = await api.InDbAsync(db => db.Invoices.Where(i => i.Id == sale.InvoiceId).Select(i => i.Version).SingleAsync());
            Assert.Equal(HttpStatusCode.NoContent, (await accountant.PostAsJsonAsync($"/api/invoices/{sale.InvoiceId}/discount", new DiscountRequest(discount, "تخفیف آزمایشی", version))).StatusCode);
        }
        var payment = await Created<PaymentRow>(await cashier.SendAsync(Post("/api/payments", new PaymentRequest(sale.InvoiceId, paid, PaymentMethod.Cash, null))));
        if (delivered)
            Assert.Equal(HttpStatusCode.NoContent, (await pharmacist.PostAsJsonAsync($"/api/pharmacy/sales/{sale.Id}/deliver", new DeliverSaleRequest(sale.Version))).StatusCode);
        if (earlierRefund > 0)
            await Created<JsonElement>(await accountant.SendAsync(Post("/api/refunds", new RefundRequest(payment.Id, earlierRefund, "برگشت قبلی"))));

        var saleVersion = await api.InDbAsync(db => db.Sales.Where(s => s.Id == sale.Id).Select(s => s.Version).SingleAsync());
        var url = $"/api/pharmacy/sales/{sale.Id}/return";
        var response = await pharmacist.PostAsJsonAsync(url, new ReturnSaleRequest(saleVersion, "برگشت دوا و وجه"));
        Assert.True(response.StatusCode == HttpStatusCode.NoContent, await response.Content.ReadAsStringAsync());
        var detail = await cashier.GetFromJsonAsync<InvoiceDetail>($"/api/invoices/{sale.InvoiceId}", JsonOptions);
        Assert.Equal(("Void", paid, paid, 0m), (detail!.Invoice.Status, detail.Invoice.Paid, detail.Invoice.Refunded, detail.Invoice.Balance));
        Assert.Equal(payment.ReceiptNumber, Assert.Single(detail.Payments).ReceiptNumber);
        var receiptPrint = (await cashier.GetFromJsonAsync<PrintDocument>($"/api/print/receipt/{payment.Id}", JsonOptions))!;
        Assert.DoesNotContain(receiptPrint.Fields, f => f.Label == "مانده فاکتور" && f.Value is not null);
        var invoicePrint = (await cashier.GetFromJsonAsync<PrintDocument>($"/api/print/invoice/{sale.InvoiceId}", JsonOptions))!;
        Assert.DoesNotContain(invoicePrint.Fields, f => f.Label == "مانده" && f.Value is not null);
        Assert.Equal(paid, await api.InDbAsync(db => db.Refunds.Where(r => r.PaymentId == payment.Id).SumAsync(r => r.Amount)));
        Assert.Equal(3, await api.InDbAsync(db => db.StockBatches.Where(b => b.ProductId == productId).SumAsync(b => b.Quantity)));
        Assert.Equal(HttpStatusCode.Conflict, (await pharmacist.PostAsJsonAsync(url, new ReturnSaleRequest(saleVersion, "تلاش دوباره"))).StatusCode);
        Assert.Equal(paid, await api.InDbAsync(db => db.Refunds.Where(r => r.PaymentId == payment.Id).SumAsync(r => r.Amount)));
        await AssertLedgerReconciles();
    }

    [Fact]
    public async Task Returning_a_fully_discounted_pharmacy_bill_closes_it_without_a_refund()
    {
        var productId = await NewProductAsync();
        await StockAsync(productId, 3);
        var pharmacist = await UserAsync(Roles.Pharmacist);
        var accountant = await UserAsync(Roles.Accountant);
        var sale = await Created<SaleRow>(await pharmacist.SendAsync(Post("/api/pharmacy/sales", new CreateSaleRequest(null, null, [new SaleLineInput(productId, 2)]))));
        var version = await api.InDbAsync(db => db.Invoices.Where(i => i.Id == sale.InvoiceId).Select(i => i.Version).SingleAsync());
        Assert.Equal(HttpStatusCode.NoContent, (await accountant.PostAsJsonAsync($"/api/invoices/{sale.InvoiceId}/discount", new DiscountRequest(200, "تخفیف کامل", version))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await pharmacist.PostAsJsonAsync($"/api/pharmacy/sales/{sale.Id}/return", new ReturnSaleRequest(sale.Version, "انصراف مریض"))).StatusCode);
        var invoice = await api.InDbAsync(db => db.Invoices.SingleAsync(i => i.Id == sale.InvoiceId));
        Assert.Equal((InvoiceStatus.Void, 200m, 200m, 0m), (invoice.Status, invoice.Total, invoice.Discount, invoice.Refunded));
        await AssertLedgerReconciles();
    }

    [Fact]
    public async Task Returning_an_unpaid_sale_restores_stock_and_reverses_the_money()
    {
        var productId = await NewProductAsync();
        await StockAsync(productId, 3);
        var pharmacist = await UserAsync(Roles.Pharmacist);

        var sale = await Created<SaleRow>(await pharmacist.SendAsync(Post("/api/pharmacy/sales", new CreateSaleRequest(null, null, [new SaleLineInput(productId, 2)]))));
        var returned = await pharmacist.PostAsJsonAsync($"/api/pharmacy/sales/{sale.Id}/return", new ReturnSaleRequest(sale.Version, "اشتباه در فروش"));
        Assert.True(returned.StatusCode == HttpStatusCode.NoContent, await returned.Content.ReadAsStringAsync());

        using var scope = api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(3, await db.StockBatches.Where(b => b.ProductId == productId).SumAsync(b => b.Quantity));
        Assert.Equal(InvoiceStatus.Void, await db.Invoices.Where(i => i.Id == sale.InvoiceId).Select(i => i.Status).SingleAsync());
        await AssertLedgerReconciles();
    }

    [Fact]
    public async Task Expired_batch_is_not_sold_and_earliest_expiry_goes_first()
    {
        var productId = await NewProductAsync();
        var manager = await UserAsync(Roles.Inventory);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var purchase = await manager.SendAsync(Post("/api/inventory/purchases", new CreatePurchaseRequest(null, "pharmacy",
        [
            new PurchaseLineInput(productId, "OLD", today.AddDays(-5), 10, 10),
            new PurchaseLineInput(productId, "LATE", today.AddYears(2), 2, 10),
            new PurchaseLineInput(productId, "SOON", today.AddMonths(2), 2, 10),
        ])));
        Assert.Equal(HttpStatusCode.Created, purchase.StatusCode);

        var pharmacist = await UserAsync(Roles.Pharmacist);
        var sale = await Created<SaleRow>(await pharmacist.SendAsync(Post("/api/pharmacy/sales", new CreateSaleRequest(null, null, [new SaleLineInput(productId, 3)]))));
        await api.PayAndDeliverAsync(pharmacist, sale.Id);

        using var scope = api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var byLot = await db.StockBatches.Where(b => b.ProductId == productId).ToDictionaryAsync(b => b.LotNumber, b => b.Quantity);
        Assert.Equal(10, byLot["OLD"]);
        Assert.Equal(0, byLot["SOON"]);
        Assert.Equal(1, byLot["LATE"]);

        var tooMany = await pharmacist.SendAsync(Post("/api/pharmacy/sales", new CreateSaleRequest(null, null, [new SaleLineInput(productId, 2)])));
        Assert.Contains("insufficient_stock", await tooMany.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Stock_adjustment_needs_approval_right_and_a_reason()
    {
        var productId = await NewProductAsync();
        await StockAsync(productId, 5);

        using var scope = api.Services.CreateScope();
        var batchId = await scope.ServiceProvider.GetRequiredService<AppDbContext>().StockBatches.Where(b => b.ProductId == productId).Select(b => b.Id).SingleAsync();

        var pharmacist = await UserAsync(Roles.Pharmacist);
        var denied = await pharmacist.SendAsync(Post("/api/inventory/adjustments", new AdjustStockRequest(batchId, -1, MovementKind.Adjustment, "شمارش")));
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);

        var manager = await UserAsync(Roles.Inventory);
        var below = await manager.SendAsync(Post("/api/inventory/adjustments", new AdjustStockRequest(batchId, -6, MovementKind.Adjustment, "شمارش")));
        Assert.Contains("insufficient_stock", await below.Content.ReadAsStringAsync());

        var ok = await manager.SendAsync(Post("/api/inventory/adjustments", new AdjustStockRequest(batchId, -2, MovementKind.Adjustment, "شمارش فیزیکی")));
        Assert.Equal(HttpStatusCode.Created, ok.StatusCode);
    }

    [Fact]
    public async Task Standalone_optical_order_moves_to_delivery_and_tracks_the_balance()
    {
        var frameId = await NewProductAsync(salePrice: 1500, kind: ProductKind.Frame);
        await StockAsync(frameId, 1, "optical", 800);
        var optical = await UserAsync(Roles.Optical);

        var order = await Created<OpticalOrderRow>(await optical.SendAsync(Post("/api/optical/orders", new CreateOpticalOrderRequest(null, null, "عینک مطالعه",
            [new SaleLineInput(frameId, 1)], [new CustomLineInput("عدسی", 700)]))));
        Assert.Equal(2200, order.Total);
        Assert.Equal(nameof(OpticalOrderStatus.Ordered), order.Status);

        var cashier = await CashierWithOpenSessionAsync();
        await Created<PaymentRow>(await cashier.SendAsync(Post("/api/payments", new PaymentRequest(order.InvoiceId, 1000, PaymentMethod.Cash, null))));

        var list = await optical.GetFromJsonAsync<PagedResult<OpticalOrderRow>>("/api/optical/orders", JsonOptions);
        var seen = list!.Items.Single(o => o.Id == order.Id);
        Assert.Equal(1200, seen.Balance);

        var moved = await optical.PostAsJsonAsync($"/api/optical/orders/{order.Id}/status", new ChangeOpticalStatusRequest(OpticalOrderStatus.Delivered, seen.Version));
        Assert.Equal(HttpStatusCode.NoContent, moved.StatusCode);
        await AssertLedgerReconciles();
    }

    [Fact]
    public async Task Complimentary_optical_order_requires_approval_takes_stock_and_never_creates_revenue_or_a_receivable()
    {
        var frameId = await NewProductAsync(salePrice: 1500, kind: ProductKind.Frame);
        await StockAsync(frameId, 1, "optical", 800);
        var optical = await UserAsync(Roles.Optical);

        var refused = await optical.SendAsync(Post("/api/optical/orders", new CreateOpticalOrderRequest(null, null, "تحویل خیرخواهانه", [new SaleLineInput(frameId, 1)], [], true)));
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);

        var order = await Created<OpticalOrderRow>(await optical.SendAsync(Post("/api/optical/orders", new CreateOpticalOrderRequest(null, null, "تحویل خیرخواهانه", [new SaleLineInput(frameId, 1)], [], true, "تأیید اداره", "مسئول مالی"))));
        Assert.True(order.IsComplimentary);
        Assert.Equal(0, order.Total);
        Assert.Equal(0, order.Balance);

        using (var scope = api.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Equal(0, await db.StockBatches.Where(b => b.ProductId == frameId && b.Location!.Code == "optical").SumAsync(b => b.Quantity));
            var freeLineIds = db.InvoiceItems.Where(i => i.InvoiceId == order.InvoiceId).Select(i => i.Id);
            Assert.False(await db.LedgerTransactions.AnyAsync(t => t.Kind == "Charge" && t.SourceType == nameof(InvoiceItem) && freeLineIds.Contains(t.SourceId)));
        }

        var cashier = await CashierWithOpenSessionAsync();
        var duplicate = await cashier.SendAsync(Post("/api/payments", new PaymentRequest(order.InvoiceId, 1, PaymentMethod.Cash, null)));
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        var list = await optical.GetFromJsonAsync<PagedResult<OpticalOrderRow>>("/api/optical/orders", JsonOptions);
        var seen = list!.Items.Single(o => o.Id == order.Id);
        Assert.Equal(HttpStatusCode.NoContent, (await optical.PostAsJsonAsync($"/api/optical/orders/{order.Id}/status", new ChangeOpticalStatusRequest(OpticalOrderStatus.Cancelled, seen.Version, "لغو تحویل رایگان"))).StatusCode);
        Assert.Equal(1, await api.InDbAsync(db => db.StockBatches.Where(b => b.ProductId == frameId && b.Location!.Code == "optical").SumAsync(b => b.Quantity)));

        var delivered = await Created<OpticalOrderRow>(await optical.SendAsync(Post("/api/optical/orders", new CreateOpticalOrderRequest(null, null, "تحویل رایگان گزارش", [new SaleLineInput(frameId, 1)], [], true, "تأیید اداره", "مسئول مالی"))));
        var reportList = await optical.GetFromJsonAsync<PagedResult<OpticalOrderRow>>("/api/optical/orders", JsonOptions);
        var reportVersion = reportList!.Items.Single(row => row.Id == delivered.Id).Version;
        Assert.Equal(HttpStatusCode.NoContent, (await optical.PostAsJsonAsync($"/api/optical/orders/{delivered.Id}/status", new ChangeOpticalStatusRequest(OpticalOrderStatus.Delivered, reportVersion))).StatusCode);
        var accountant = await UserAsync(Roles.Accountant);
        var report = await accountant.GetFromJsonAsync<List<ComplimentaryOpticalReportRow>>("/api/optical/reports/complimentary", JsonOptions);
        Assert.Contains(report!, row => row.Id == delivered.Id && row.Items.Contains("جنس", StringComparison.Ordinal));
        await AssertLedgerReconciles();
    }

    [Fact]
    public async Task Walk_in_optical_order_keeps_the_customer_name_and_is_found_by_name_or_phone()
    {
        var optical = await UserAsync(Roles.Optical);
        var name = $"مشتری {Guid.NewGuid():N}"[..20];
        var phone = $"07{Random.Shared.Next(10000000, 99999999)}";
        var order = await Created<OpticalOrderRow>(await optical.SendAsync(Post("/api/optical/orders", new CreateOpticalOrderRequest(null, null, "عینک آفتابی", [], [new CustomLineInput("عدسی", 300, true)],
            CustomerName: $"  {name}  ", CustomerPhone: phone))));
        Assert.Equal(name, order.PatientName);
        Assert.Equal(phone, order.CustomerPhone);

        // Found among all orders, not only the first page.
        var byName = await optical.GetFromJsonAsync<PagedResult<OpticalOrderRow>>($"/api/optical/orders?q={Uri.EscapeDataString(name)}", JsonOptions);
        Assert.Equal(order.Id, Assert.Single(byName!.Items).Id);
        var byPhone = await optical.GetFromJsonAsync<PagedResult<OpticalOrderRow>>($"/api/optical/orders?q={phone}", JsonOptions);
        Assert.Contains(byPhone!.Items, row => row.Id == order.Id);

        var printed = System.Text.RegularExpressions.Regex.Unescape(await optical.GetStringAsync($"/api/print/optical-order/{order.Id}"));
        Assert.Contains(name, printed, StringComparison.Ordinal);
        Assert.Contains(phone, printed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Nonstock_lens_is_billed_without_a_warehouse_movement_and_order_receipt_can_be_reprinted()
    {
        var optical = await UserAsync(Roles.Optical);
        var order = await Created<OpticalOrderRow>(await optical.SendAsync(Post("/api/optical/orders", new CreateOpticalOrderRequest(null, null, "عدسی سفارشی", [], [new CustomLineInput("عدسی غیرانباری", 700, true)]))));
        Assert.Equal(700, order.Total);

        using (var scope = api.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var saleId = await db.Sales.Where(s => s.InvoiceId == order.InvoiceId).Select(s => s.Id).SingleAsync();
            Assert.Empty(await db.Set<SaleItem>().Where(i => i.SaleId == saleId).ToListAsync());
            Assert.Empty(await db.StockMovements.Where(m => m.SourceType == nameof(Sale) && m.SourceId == saleId).ToListAsync());
        }
        var printed = await optical.GetAsync($"/api/print/optical-order/{order.Id}");
        Assert.Equal(HttpStatusCode.OK, printed.StatusCode);
        Assert.Contains("OpticalNonStockLens", await api.InDbAsync(db => db.InvoiceItems.Where(i => i.InvoiceId == order.InvoiceId).Select(i => i.SourceType).SingleAsync()));

        var cashier = await CashierWithOpenSessionAsync();
        await Created<PaymentRow>(await cashier.SendAsync(Post("/api/payments", new PaymentRequest(order.InvoiceId, 700, PaymentMethod.Cash, null))));
        var unpaid = await cashier.GetFromJsonAsync<List<OpticalOrderRow>>("/api/optical/orders/unpaid", JsonOptions);
        Assert.DoesNotContain(unpaid!, row => row.Id == order.Id);
        await AssertLedgerReconciles();
    }

    [Fact]
    public async Task Counted_stock_adjustment_records_before_after_reason_and_approver()
    {
        var productId = await NewProductAsync();
        await StockAsync(productId, 5, "optical", 40);
        var batchId = await api.InDbAsync(db => db.StockBatches.Where(b => b.ProductId == productId).Select(b => b.Id).SingleAsync());
        var pharmacist = await UserAsync(Roles.Pharmacist);
        Assert.Equal(HttpStatusCode.Forbidden, (await pharmacist.SendAsync(Post("/api/inventory/adjustments/set-quantity", new SetStockQuantityRequest(batchId, 3, "شمارش بخش عینک")))).StatusCode);

        var inventory = await UserAsync(Roles.Inventory);
        var saved = await inventory.SendAsync(Post("/api/inventory/adjustments/set-quantity", new SetStockQuantityRequest(batchId, 3, "شمارش بخش عینک")));
        Assert.Equal(HttpStatusCode.Created, saved.StatusCode);
        using var scope = api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var document = await db.StockAdjustments.SingleAsync(a => a.BatchId == batchId);
        Assert.Equal((5m, 3m, "شمارش بخش عینک"), (document.PreviousQuantity, document.NewQuantity, document.Reason));
        Assert.True(await db.StockMovements.AnyAsync(m => m.SourceType == nameof(StockAdjustment) && m.SourceId == document.Id && m.Quantity == -2));
        await AssertLedgerReconciles();
    }

    [Fact]
    public async Task Batch_expiry_correction_changes_only_the_date_and_is_audited()
    {
        var productId = await NewProductAsync();
        await StockAsync(productId, 5, "optical", 40);
        var batchId = await api.InDbAsync(db => db.StockBatches.Where(b => b.ProductId == productId).Select(b => b.Id).SingleAsync());
        var expiry = new DateOnly(2030, 6, 30);
        var pharmacist = await UserAsync(Roles.Pharmacist);
        Assert.Equal(HttpStatusCode.Forbidden, (await pharmacist.PutAsJsonAsync($"/api/inventory/batches/{batchId}/expiry", new SetBatchExpiryRequest(expiry, "اصلاح تاریخ"))).StatusCode);

        var inventory = await UserAsync(Roles.Inventory);
        Assert.Equal(HttpStatusCode.OK, (await inventory.PutAsJsonAsync($"/api/inventory/batches/{batchId}/expiry", new SetBatchExpiryRequest(expiry, "اصلاح تاریخ"))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await inventory.PutAsJsonAsync($"/api/inventory/batches/{batchId}/expiry", new SetBatchExpiryRequest(expiry, "اصلاح تاریخ"))).StatusCode);

        using var scope = api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var batch = await db.StockBatches.SingleAsync(b => b.Id == batchId);
        Assert.Equal((expiry, 5m, 40m), (batch.ExpiryDate, batch.Quantity, batch.UnitCost));
        Assert.True(await db.AuditLogs.AnyAsync(a => a.Action == "BatchExpiryChanged" && a.EntityId == batchId.ToString()));
        await AssertLedgerReconciles();
    }

    [Fact]
    public async Task Cash_session_reports_what_was_collected()
    {
        var serviceId = await NewServiceAsync(1000);
        var patient = await NewPatientAsync();
        var invoice = await NewInvoiceAsync(patient.Id, serviceId);
        var cashier = await CashierWithOpenSessionAsync();
        await Created<PaymentRow>(await cashier.SendAsync(Post("/api/payments", new PaymentRequest(invoice.Id, 400, PaymentMethod.Cash, null))));

        var sessions = await cashier.GetFromJsonAsync<List<SessionRow>>("/api/cash/sessions?mine=true", JsonOptions);
        var session = Assert.Single(sessions!);
        Assert.Equal(400, session.CashCollected);

        var closed = await cashier.PostAsJsonAsync($"/api/cash/sessions/{session.Id}/close", new CloseSessionRequest(390, session.Version));
        var after = (await closed.Content.ReadFromJsonAsync<SessionRow>(JsonOptions))!;
        Assert.Equal(400, after.ExpectedAmount);
        Assert.Equal(390, after.CountedAmount);

        var noSession = await cashier.SendAsync(Post("/api/payments", new PaymentRequest(invoice.Id, 10, PaymentMethod.Cash, null)));
        Assert.Contains("no_open_session", await noSession.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Expense_cancel_reverses_and_ledger_reconciles_after_mixed_concurrent_activity()
    {
        var serviceId = await NewServiceAsync(100);
        var patient = await NewPatientAsync();
        var cashier = await CashierWithOpenSessionAsync();
        var accountant = await UserAsync(Roles.Accountant);

        var expense = JsonDocument.Parse(await (await accountant.SendAsync(Post("/api/expenses",
            new CreateExpenseRequest("برق", null, 75.50m, DateOnly.FromDateTime(DateTime.UtcNow))))).Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetInt64();
        Assert.Equal(HttpStatusCode.NoContent, (await accountant.PostAsJsonAsync($"/api/expenses/{expense}/cancel", new VoidRequest("اشتباه", 0))).StatusCode);

        var invoices = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => NewInvoiceAsync(patient.Id, serviceId)));
        await Task.WhenAll(invoices.Select(i => cashier.SendAsync(Post("/api/payments", new PaymentRequest(i.Id, 100, PaymentMethod.Cash, null)))));

        await AssertLedgerReconciles();

        var view = await accountant.GetFromJsonAsync<ReconciliationResult>("/api/ledger/reconciliation", JsonOptions);
        Assert.True(view!.Balanced);
    }

    [Fact]
    public async Task Clinical_roles_cannot_touch_finance_and_cashier_cannot_read_the_chart()
    {
        var doctor = await UserAsync(Roles.Doctor);
        Assert.Equal(HttpStatusCode.Forbidden, (await doctor.GetAsync("/api/invoices")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await doctor.GetAsync("/api/ledger/balances")).StatusCode);

        var lab = await UserAsync(Roles.Lab);
        Assert.Equal(HttpStatusCode.Forbidden, (await lab.GetAsync("/api/expenses")).StatusCode);
    }

    [Fact]
    public async Task Payments_and_expenses_can_be_listed_for_a_range_of_Kabul_days()
    {
        var serviceId = await NewServiceAsync(100);
        var patient = await NewPatientAsync();
        var invoice = await NewInvoiceAsync(patient.Id, serviceId);
        var cashier = await CashierWithOpenSessionAsync();
        var payment = await Created<PaymentRow>(await cashier.SendAsync(Post("/api/payments", new PaymentRequest(invoice.Id, 100, PaymentMethod.Cash, null))));

        var today = KabulClock.Today;
        var accountant = await UserAsync(Roles.Accountant);
        var todays = await accountant.GetFromJsonAsync<PagedResult<PaymentRow>>($"/api/payments?from={today:yyyy-MM-dd}&to={today:yyyy-MM-dd}&pageSize=100", JsonOptions);
        Assert.Contains(todays!.Items, p => p.Id == payment.Id);
        var yesterdays = await accountant.GetFromJsonAsync<PagedResult<PaymentRow>>($"/api/payments?from={today.AddDays(-1):yyyy-MM-dd}&to={today.AddDays(-1):yyyy-MM-dd}&pageSize=100", JsonOptions);
        Assert.DoesNotContain(yesterdays!.Items, p => p.Id == payment.Id);

        var category = Name("دسته");
        var lastWeek = today.AddDays(-7);
        Assert.Equal(HttpStatusCode.Created, (await accountant.SendAsync(Post("/api/expenses", new CreateExpenseRequest(category, null, 10, lastWeek)))).StatusCode);
        var week = await accountant.GetFromJsonAsync<JsonElement>($"/api/expenses?from={lastWeek:yyyy-MM-dd}&to={lastWeek:yyyy-MM-dd}&pageSize=100", JsonOptions);
        Assert.Contains(week.GetProperty("items").EnumerateArray(), e => e.GetProperty("category").GetString() == category);
        var todayOnly = await accountant.GetFromJsonAsync<JsonElement>($"/api/expenses?from={today:yyyy-MM-dd}&to={today:yyyy-MM-dd}&pageSize=100", JsonOptions);
        Assert.DoesNotContain(todayOnly.GetProperty("items").EnumerateArray(), e => e.GetProperty("category").GetString() == category);

        // A reversed range is refused, as in the reports.
        Assert.Equal(HttpStatusCode.BadRequest, (await accountant.GetAsync($"/api/expenses?from={today:yyyy-MM-dd}&to={lastWeek:yyyy-MM-dd}")).StatusCode);
    }
}
