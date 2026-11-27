using AlShifa.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace AlShifa.Api.Features.Finance;

public static class CashBook
{
    // The shared lock has the same lifetime as the surrounding business transaction. Closing waits for all movements.
    public static async Task<long?> OpenSessionAsync(AppDbContext db, long userId, bool required, CancellationToken ct)
    {
        var rows = await db.CashSessions.FromSqlInterpolated($"SELECT * FROM CashSessions WHERE OpenedByUserId = {userId} AND ClosedAt IS NULL ORDER BY Id LIMIT 1 FOR SHARE")
            .AsNoTracking().ToListAsync(ct);
        if (rows.Count == 0 && required)
            throw new AppException(409, "no_open_session", "ابتدا صندوق خود را باز کنید؛ اصلاح پول در صندوق امروز ثبت می‌شود.");
        return rows.FirstOrDefault()?.Id;
    }

    public static async Task RecordAsync(AppDbContext db, long userId, string kind, string sourceType, long sourceId, decimal amount, CancellationToken ct, bool required = false)
    {
        var sessionId = await OpenSessionAsync(db, userId, required, ct);
        db.CashMovements.Add(new CashMovement { CashSessionId = sessionId, Kind = kind, SourceType = sourceType,
            SourceId = sourceId, Amount = amount, CreatedByUserId = userId });
    }
}
