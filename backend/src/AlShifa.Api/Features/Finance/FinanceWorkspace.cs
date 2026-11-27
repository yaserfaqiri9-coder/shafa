using System.ComponentModel.DataAnnotations;
using AlShifa.Api.Features.Auth;
using AlShifa.Api.Features.Reports;
using AlShifa.Api.Features.Visits;
using AlShifa.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace AlShifa.Api.Features.Finance;

public sealed record CorrectPaymentRequest([Range(0.01, 100000000)] decimal Amount, [Required, MinLength(3), MaxLength(300)] string Reason, int Version);
public sealed record CancelPaymentRequest([Required, MinLength(3), MaxLength(300)] string Reason, int Version);
public sealed record CashTransferRequest([Range(0.01, 100000000)] decimal Amount, [Required] string Destination, [Required, MinLength(3), MaxLength(300)] string Reason, int Version);

public static class FinanceWorkspace
{
    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapPost("/api/cash/sessions/{id:long}/transfer", async (long id, CashTransferRequest request, AppDbContext db, CurrentUser user,
            Idempotency idempotency, NumberService numbers, LedgerService ledger, AuditService audit, Outbox outbox, CancellationToken ct) =>
            await idempotency.RunAsync(request, async () =>
            {
                Money.EnsureCents(request.Amount);
                var destination = request.Destination == "Bank" ? Accounts.Bank : request.Destination == "Vault" ? Accounts.Vault
                    : throw new AppException(400, "invalid_destination", "مقصد باید بانک یا گاوصندوق باشد.");
                var session = await db.CashSessions.FromSqlInterpolated($"SELECT * FROM CashSessions WHERE Id={id} FOR UPDATE").SingleOrDefaultAsync(ct) ?? throw AppException.NotFound();
                if (session.OpenedByUserId != user.Id) throw new AppException(403, "not_session_owner", "تحویل پول از صندوق شخص دیگر مجاز نیست.");
                if (session.ClosedAt != null || session.Version != request.Version) throw new DbUpdateConcurrencyException();
                var row = await FinanceEndpoints.QuerySessions(db, db.CashSessions.Where(s => s.Id == id)).SingleAsync(ct);
                if (request.Amount > row.CurrentExpected) throw new AppException(409, "insufficient_cash", "مبلغ از موجودی مورد انتظار صندوق بیشتر است.");
                var number = await numbers.NextAsync("cash-transfer", ct: ct);
                db.CashMovements.Add(new CashMovement { CashSessionId = id, Kind = "TransferOut", SourceType = "CashTransfer", SourceId = number,
                    Amount = -request.Amount, CreatedByUserId = user.Id });
                ledger.Post("CashTransfer", "CashTransfer", number, request.Reason.Trim(), (destination, request.Amount, 0), (Accounts.Cash, 0, request.Amount));
                // Bump the session version so two transfer dialogs cannot silently act on the same figures.
                db.Entry(session).Property(s => s.CloseNote).IsModified = true;
                audit.Add("CashTransferred", "CashSession", id.ToString(), $"{request.Destination}: {request.Amount:F2}; {request.Reason.Trim()}");
                outbox.Add("CashChanged", new { sessionId = id }, Groups.Cashier, Groups.Finance, Groups.Management);
                await db.SaveChangesAsync(ct);
                return (StatusCodes.Status201Created, new { id = number });
            }, ct)).RequirePermission("cash.edit").WithTags("Finance");

        app.MapGet("/api/cash/outstanding-at", async (DateOnly date, string? q, AppDbContext db, int page = 1, int pageSize = 25, CancellationToken ct = default) =>
        {
            var (_, cutoff) = ReportEndpoints.UtcRange(date, date);
            // Reconstruct from immutable journals, including charges/discounts/cancellations posted after invoice issue.
            var journal = db.LedgerTransactions.Where(t => t.CreatedAt < cutoff).Select(t => new
            {
                t.Id,
                InvoiceId = t.SourceType == nameof(Invoice) ? (long?)t.SourceId
                    : t.SourceType == nameof(InvoiceItem) ? db.InvoiceItems.Where(i => i.Id == t.SourceId).Select(i => (long?)i.InvoiceId).FirstOrDefault()
                    : t.SourceType == nameof(Payment) ? db.Payments.Where(p => p.Id == t.SourceId).Select(p => (long?)p.InvoiceId).FirstOrDefault()
                    : t.SourceType == nameof(Refund) ? db.Refunds.Where(r => r.Id == t.SourceId).Select(r => (long?)r.Payment!.InvoiceId).FirstOrDefault()
                    : t.SourceType == "Sale" ? db.Sales.Where(s => s.Id == t.SourceId).Select(s => (long?)s.InvoiceId).FirstOrDefault() : null,
            });
            var debts = from t in journal
                        join e in db.LedgerEntries on t.Id equals e.TransactionId
                        where e.AccountCode == Accounts.Receivable && t.InvoiceId != null
                        group e by t.InvoiceId into g
                        select new { InvoiceId = g.Key, Balance = g.Sum(e => e.Debit - e.Credit) };
            var query = from i in db.Invoices.AsNoTracking()
                        join d in debts on (long?)i.Id equals d.InvoiceId
                        where i.CreatedAt < cutoff && d.Balance > 0
                        select new { Invoice = i, d.Balance };
            if (!string.IsNullOrWhiteSpace(q))
            {
                q = OldNumbers.Plain(q.Trim());
                query = query.Where(x => x.Invoice.InvoiceNumber.Contains(q) || (x.Invoice.Patient != null &&
                    (x.Invoice.Patient.FirstName.Contains(q) || (x.Invoice.Patient.LastName ?? "").Contains(q) || x.Invoice.Patient.FileNumber.Contains(q))));
            }
            page = Math.Max(1, page); pageSize = Math.Clamp(pageSize, 1, 100);
            var total = await query.CountAsync(ct);
            var items = await query.OrderByDescending(x => x.Invoice.Id).Skip((page - 1) * pageSize).Take(pageSize)
                .Select(x => new { x.Invoice.Id, x.Invoice.InvoiceNumber,
                    PatientName = x.Invoice.Patient == null ? null : x.Invoice.Patient.FirstName + " " + (x.Invoice.Patient.LastName ?? ""),
                    x.Balance, x.Invoice.CreatedAt,
                    Description = x.Invoice.Items.OrderBy(i => i.Id).Select(i => i.Description).FirstOrDefault(), ItemCount = x.Invoice.Items.Count() }).ToListAsync(ct);
            return Results.Ok(new { items, total, page, pageSize, asOf = date });
        }).RequirePermission("invoices.view").WithTags("Finance");

        app.MapGet("/api/cash/day", async (DateOnly? date, AppDbContext db, CancellationToken ct) =>
        {
            var day = date ?? KabulClock.Today;
            var (start, end) = ReportEndpoints.UtcRange(day, day);
            var f = await ReportFigures.LoadAsync(start, end, db, ct);
            var sessions = await FinanceEndpoints.QuerySessions(db, db.CashSessions.AsNoTracking()
                .Where(s => s.OpenedAt < end && (s.ClosedAt == null || s.ClosedAt >= start)).OrderByDescending(s => s.Id)).ToListAsync(ct);
            var unassigned = await db.CashMovements.Where(m => m.CreatedAt >= start && m.CreatedAt < end && m.CashSessionId == null)
                .SumAsync(m => (decimal?)m.Amount, ct) ?? 0;
            return Results.Ok(new { date = day, receipts = f.Receipts, cashReceipts = f.ReceiptsCash,
                otherReceipts = f.Receipts - f.ReceiptsCash, refunds = f.RefundsPaid, expenses = f.Expenses,
                netReceived = f.NetReceived, sessions, unassignedCashOut = -unassigned });
        }).RequirePermission("payments.view").WithTags("Finance");

        app.MapGet("/api/refunds", async (AppDbContext db, DateOnly? from, DateOnly? to, string? q, int page = 1, int pageSize = 25, CancellationToken ct = default) =>
        {
            page = Math.Max(1, page); pageSize = Math.Clamp(pageSize, 1, 100);
            var query = db.Refunds.AsNoTracking();
            if (from is not null || to is not null)
            {
                var (first, last) = ReportEndpoints.Range(from ?? to, to ?? from);
                var (start, end) = ReportEndpoints.UtcRange(first, last);
                query = query.Where(r => r.CreatedAt >= start && r.CreatedAt < end);
            }
            if (!string.IsNullOrWhiteSpace(q))
            {
                q = OldNumbers.Plain(q.Trim());
                query = query.Where(r => r.RefundNumber.Contains(q) || r.Payment!.ReceiptNumber.Contains(q) || r.Payment.Invoice!.InvoiceNumber.Contains(q)
                    || (r.Payment.Invoice.Patient != null && (r.Payment.Invoice.Patient.FirstName.Contains(q) || (r.Payment.Invoice.Patient.LastName ?? "").Contains(q))));
            }
            var total = await query.CountAsync(ct);
            var items = await query.OrderByDescending(r => r.Id).Skip((page - 1) * pageSize).Take(pageSize)
                .Select(r => new { r.Id, r.RefundNumber, r.PaymentId, r.Payment!.ReceiptNumber, r.Payment.InvoiceId, r.Amount, r.Reason, r.CreatedAt,
                    PatientName = r.Payment.Invoice!.Patient == null ? null : r.Payment.Invoice.Patient.FirstName + " " + (r.Payment.Invoice.Patient.LastName ?? ""),
                    ByUser = db.Users.Where(u => u.Id == r.ApprovedByUserId).Select(u => u.FullName).FirstOrDefault(), Method = r.Payment.Method.ToString(),
                    CashSessionId = db.CashMovements.Where(m => m.Kind == "Refund" && m.SourceId == r.Id).Select(m => m.CashSessionId).FirstOrDefault() }).ToListAsync(ct);
            return Results.Ok(new { items, total, page, pageSize });
        }).RequirePermission("payments.view").WithTags("Finance");

        app.MapPost("/api/payments/{id:long}/correct", async (long id, CorrectPaymentRequest request, AppDbContext db, InvoiceService invoices,
            VisitFeeService fees, Idempotency idempotency, NumberService numbers, LedgerService ledger, CurrentUser user, AuditService audit, Outbox outbox, CancellationToken ct) =>
            await idempotency.RunAsync(request, async () =>
            {
                Money.EnsureCents(request.Amount);
                var original = await ReverseReceiptAsync(id, request.Version, request.Reason, db, invoices, fees, numbers, ledger, user, audit, outbox, ct);
                var replacement = await invoices.ReceiveAsync(new PaymentRequest(original.InvoiceId, request.Amount, original.Method, original.Reference), false, ct);
                original.ReplacementPaymentId = replacement.Id;
                var invoice = db.Invoices.Local.Single(i => i.Id == original.InvoiceId);
                var fee = await fees.LockFeeOfInvoiceAsync(invoice.Id, ct, forPayment: true);
                await fees.IssueNumberIfSettledAsync(fee, invoice, ct);
                audit.Add("PaymentCorrected", nameof(Payment), id.ToString(), $"replacement:{replacement.Id}; {request.Reason.Trim()}");
                await db.SaveChangesAsync(ct);
                return (StatusCodes.Status201Created, await FinanceEndpoints.QueryPayments(db, db.Payments.Where(p => p.Id == replacement.Id)).SingleAsync(ct));
            }, ct)).RequirePermission("payments.refund").WithTags("Finance");

        app.MapPost("/api/payments/{id:long}/cancel", async (long id, CancelPaymentRequest request, AppDbContext db, InvoiceService invoices,
            VisitFeeService fees, Idempotency idempotency, NumberService numbers, LedgerService ledger, CurrentUser user, AuditService audit, Outbox outbox, CancellationToken ct) =>
            await idempotency.RunAsync(request, async () =>
            {
                var payment = await ReverseReceiptAsync(id, request.Version, request.Reason, db, invoices, fees, numbers, ledger, user, audit, outbox, ct);
                audit.Add("PaymentCancelled", nameof(Payment), id.ToString(), request.Reason.Trim());
                await db.SaveChangesAsync(ct);
                return (StatusCodes.Status200OK, new { payment.Id });
            }, ct)).RequirePermission("payments.refund").WithTags("Finance");
    }

    private static async Task<Payment> ReverseReceiptAsync(long id, int version, string reason, AppDbContext db, InvoiceService invoices,
        VisitFeeService fees, NumberService numbers, LedgerService ledger, CurrentUser user, AuditService audit, Outbox outbox, CancellationToken ct)
    {
        var invoiceId = await db.Payments.Where(p => p.Id == id).Select(p => (long?)p.InvoiceId).SingleOrDefaultAsync(ct) ?? throw AppException.NotFound();
        await fees.LockFeeOfInvoiceAsync(invoiceId, ct);
        var invoice = await invoices.LockAsync(invoiceId, ct);
        var payment = await db.Payments.FromSqlInterpolated($"SELECT * FROM Payments WHERE Id = {id} FOR UPDATE").SingleAsync(ct);
        if (payment.Version != version) throw new DbUpdateConcurrencyException();
        if (invoice.Status == InvoiceStatus.Void || payment.Refunded > 0 || payment.ReplacementPaymentId != null)
            throw new AppException(409, "receipt_already_adjusted", "این رسید قبلاً برگشت یا اصلاح شده است؛ رسید و مانده تازه را بررسی کنید.");
        // Financial correction is a refund and a new receipt, both in today's transaction. Original drawer stays closed and unchanged.
        if (payment.Method == PaymentMethod.Cash) await CashBook.OpenSessionAsync(db, user.Id, true, ct);
        var refund = new Refund { RefundNumber = await numbers.NextNumberAsync("refund", ct), PaymentId = id, Amount = payment.Amount,
            Reason = reason.Trim(), ApprovedByUserId = user.Id };
        db.Refunds.Add(refund);
        payment.Refunded = payment.Amount;
        invoice.Refunded += payment.Amount;
        InvoiceService.UpdateStatus(invoice);
        await db.SaveChangesAsync(ct);
        ledger.Post("Refund", nameof(Refund), refund.Id, refund.Reason, (Accounts.Receivable, refund.Amount, 0),
            (payment.Method == PaymentMethod.Cash ? Accounts.Cash : Accounts.Bank, 0, refund.Amount));
        if (payment.Method == PaymentMethod.Cash) await CashBook.RecordAsync(db, user.Id, "Refund", nameof(Refund), refund.Id, -refund.Amount, ct, required: true);
        audit.Add("RefundApproved", nameof(Refund), refund.Id.ToString(), refund.Reason);
        outbox.Add("PaymentReceived", new { invoiceId }, Groups.Cashier, Groups.Finance, Groups.Management, Groups.Reception, Groups.Triage, Groups.Lab, Groups.Pharmacy, Groups.Optical);
        await db.SaveChangesAsync(ct);
        return payment;
    }
}
