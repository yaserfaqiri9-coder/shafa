using AlShifa.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;
using MySql.Data.MySqlClient;

namespace AlShifa.Api.Features.Visits;

/// <summary>Only the operator supplies physical card numbers. A database key guards concurrent admission.</summary>
public sealed class QueueNumberService(AppDbContext db)
{
    public static int Validate(int? number) => number is >= 1 and <= 100 ? number.Value
        : throw new AppException(StatusCodes.Status400BadRequest, "queue_number_required", "شماره نوبت روی کارت فیزیکی را از ۱ تا ۱۰۰ وارد کنید.");

    /// <summary>The caller stores the same shift on the visit and token, so a reservation at 14:59:59 cannot land in the other shift.</summary>
    public async Task<int> ReserveAsync(DateOnly day, QueueShift shift, int? number, CancellationToken ct)
    {
        var value = Validate(number);
        if (db.Database.CurrentTransaction is null) throw new InvalidOperationException("Card reservation requires a transaction.");
        if (await db.Visits.AnyAsync(v => v.VisitDate == day && v.QueueShift == shift && v.QueueNumber == value, ct)
            || await db.VisitTokens.AnyAsync(t => t.TokenDate == day && t.QueueShift == shift && t.QueueNumber == value, ct)) throw Duplicate();
        db.QueueCards.Add(new QueueCard { Day = day, Number = QueueCard.Slot(shift, value) });
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException e) when (e.InnerException is MySqlException { Number: 1062 }) { throw Duplicate(); }
        return value;
    }

    private static AppException Duplicate() => new(StatusCodes.Status409Conflict, "queue_number_taken", "این شماره کارت در همین نوبت (صبح/چاشت) امروز قبلاً ثبت شده است؛ شماره کارت دیگری وارد کنید.");
}
