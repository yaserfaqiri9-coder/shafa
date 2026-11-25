using AlShifa.Api.Features.Auth;
using AlShifa.Api.Features.Clinical;
using AlShifa.Api.Features.Finance;
using AlShifa.Api.Features.Visits;
using AlShifa.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace AlShifa.Api.Features.Calls;

public static class DepartmentQueueService
{
    public static string Key(long? visitId, long? tokenId) => visitId is { } v ? $"v:{v}" : $"t:{tokenId}";
    private static long LaneId(CallDesk desk, long userId) => desk == CallDesk.Doctor ? userId : 0;
    public static readonly string[] EventGroups = [Groups.Reception, Groups.Cashier, Groups.Triage, Groups.Doctor, Groups.Lab, Groups.Pharmacy, Groups.Optical, Groups.Surgery, Groups.Management, Groups.Audio];

    public static async Task<QueueDeskLane> LockLaneAsync(AppDbContext db, CallDesk desk, long userId, CancellationToken ct)
    {
        var day = KabulClock.Today;
        var lane = LaneId(desk, userId);
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO QueueDeskLanes (Day, Desk, LaneId, EntryKey) VALUES ({day.ToDateTime(TimeOnly.MinValue)}, {desk.ToString()}, {lane}, NULL) ON DUPLICATE KEY UPDATE LaneId = LaneId", ct);
        return await db.QueueDeskLanes.FromSqlInterpolated($"SELECT * FROM QueueDeskLanes WHERE Day = {day.ToDateTime(TimeOnly.MinValue)} AND Desk = {desk.ToString()} AND LaneId = {lane} FOR UPDATE").SingleAsync(ct);
    }

    private static IQueryable<Visit> ServingVisits(AppDbContext db, CallDesk desk) => desk switch
        {
            CallDesk.Doctor => db.Visits.Where(v => v.Status == VisitStatus.InConsultation),
            CallDesk.Vision => db.Visits.Where(v => v.Status == VisitStatus.InPreExam),
            CallDesk.Refraction or CallDesk.Imaging or CallDesk.Lab => db.Visits.Where(v => db.DiagnosticOrders.Any(o => o.VisitId == v.Id && o.Status == DiagnosticStatus.InProgress
                && (desk == CallDesk.Refraction ? o.Test!.Kind == DiagnosticKind.Refraction : desk == CallDesk.Imaging ? o.Test!.Kind == DiagnosticKind.Imaging : o.Test!.Kind == DiagnosticKind.Lab))),
            CallDesk.Surgery => db.Visits.Where(v => db.SurgeryCases.Any(s => s.VisitId == v.Id && s.Status == SurgeryStatus.InProgress)),
            _ => db.Visits.Where(v => false),
        };

    public static Task<bool> InServiceAsync(AppDbContext db, CallDesk desk, long? visitId, CancellationToken ct) =>
        ServingVisits(db, desk).AnyAsync(v => v.Id == visitId, ct);

    public sealed record Selection(CallQueueEntry? Current, CallQueueEntry? Next, IReadOnlySet<string> Absent)
    {
        public CallQueueEntry? Priority => Current ?? Next;
    }

    public static async Task<Selection> SelectAsync(AppDbContext db, CallDesk desk, long userId, CancellationToken ct, QueueDeskLane? lockedLane = null)
    {
        var entries = (await Eligible(db, desk, userId).ToListAsync(ct)).OrderBy(e => e.QueueShift == QueueShift.Afternoon).ThenBy(e => e.QueueNumber).ThenBy(e => e.VisitId).ThenBy(e => e.TokenId).ToList();
        var day = KabulClock.Today;
        var absent = (await db.QueueAttendances.AsNoTracking().Where(a => a.Day == day && a.Desk == desk && !a.Present).Select(a => a.EntryKey).ToListAsync(ct)).ToHashSet();
        var laneId = LaneId(desk, userId);
        var lane = lockedLane ?? await db.QueueDeskLanes.AsNoTracking().SingleOrDefaultAsync(l => l.Day == day && l.Desk == desk && l.LaneId == laneId, ct);
        var ids = entries.Where(e => e.VisitId != null).Select(e => e.VisitId!.Value).ToList();
        var serving = (await ServingVisits(db, desk).Where(v => ids.Contains(v.Id)).Select(v => v.Id).ToListAsync(ct)).ToHashSet();
        // A clinical procedure has priority even if an older client started it without calling first.
        var current = entries.FirstOrDefault(e => Key(e.VisitId, e.TokenId) == lane?.EntryKey);
        if (current?.VisitId is not { } currentId || !serving.Contains(currentId))
            current = entries.FirstOrDefault(e => e.VisitId is { } id && serving.Contains(id)) ?? current;
        if (current is not null && absent.Contains(Key(current.VisitId, current.TokenId)) && (current.VisitId is not { } id || !serving.Contains(id))) current = null;
        if (desk == CallDesk.Reception)
        {
            var attended = await db.PatientCalls.AsNoTracking().Where(c => c.Desk == desk && c.RequestedAt >= day.ToDateTime(TimeOnly.MinValue).AddHours(-4.5)
                && c.Outcome == CallOutcome.Attended).Select(c => c.VisitId != null ? "v:" + c.VisitId : "t:" + c.VisitTokenId).ToListAsync(ct);
            entries.RemoveAll(e => attended.Contains(Key(e.VisitId, e.TokenId)));
            if (current is not null && !entries.Contains(current)) current = null;
        }
        var next = entries.FirstOrDefault(e => e != current && !absent.Contains(Key(e.VisitId, e.TokenId)));
        return new(current, next, absent);
    }

    public static async Task EnsurePriorityAsync(AppDbContext db, CallDesk desk, long userId, CallQueueEntry entry, QueueDeskLane lane, bool recall, CancellationToken ct)
    {
        var selection = await SelectAsync(db, desk, userId, ct, lane);
        var target = Key(entry.VisitId, entry.TokenId);
        if (selection.Absent.Contains(target)) throw new AppException(StatusCodes.Status409Conflict, "patient_absent", "ابتدا حضور مریض در این بخش را ثبت کنید.");
        if (selection.Priority is { } priority && Key(priority.VisitId, priority.TokenId) != target)
            throw new AppException(StatusCodes.Status409Conflict, selection.Current is null ? "queue_priority" : "desk_busy",
                selection.Current is null ? "مریض جلوتر در صف حاضر و آماده است؛ ابتدا آن مریض را فراخوان کنید." : "مریض فعلی هنوز در این بخش در حال کار است؛ ابتدا کار او را تمام کنید.");
        if (selection.Priority is null && !recall) throw new AppException(StatusCodes.Status409Conflict, "not_in_queue", "مریض آماده‌ای در صف نیست.");
        if (selection.Priority is not null) lane.EntryKey = target;
    }

    /// <summary>
    /// Opening the chart puts a called patient "with the doctor" before anything is written. If that patient never came,
    /// nothing was recorded, so the visit goes back to ready and can be marked absent like any other.
    /// </summary>
    private static async Task ReleaseUnstartedAsync(AppDbContext db, long visitId, long userId, CancellationToken ct)
    {
        var visit = await db.Visits.SingleOrDefaultAsync(v => v.Id == visitId, ct);
        if (visit?.Status != VisitStatus.InConsultation || await db.Consultations.AnyAsync(c => c.VisitId == visitId, ct)) return;
        db.VisitStatusHistory.Add(new VisitStatusHistory { VisitId = visitId, FromStatus = visit.Status, ToStatus = VisitStatus.ReadyForDoctor, ChangedByUserId = userId });
        visit.Status = VisitStatus.ReadyForDoctor;
        await db.SaveChangesAsync(ct);
    }

    public static async Task SetPresenceAsync(AppDbContext db, CallDesk desk, long userId, long? visitId, long? tokenId, bool present, QueueDeskLane lane, CancellationToken ct)
    {
        if (!present && desk == CallDesk.Doctor && visitId is { } openedId) await ReleaseUnstartedAsync(db, openedId, userId, ct);
        if (!present && await InServiceAsync(db, desk, visitId, ct)) throw new AppException(StatusCodes.Status409Conflict, "patient_in_service", "مریض در حال معاینه را نمی‌توان غایب کرد یا از نوبت خارج ساخت.");
        var day = KabulClock.Today;
        var key = Key(visitId, tokenId);
        var attendance = await db.QueueAttendances.SingleOrDefaultAsync(a => a.Day == day && a.Desk == desk && a.EntryKey == key, ct);
        if (attendance is null) { attendance = new() { Day = day, Desk = desk, EntryKey = key }; db.QueueAttendances.Add(attendance); }
        attendance.Present = present;
        attendance.UpdatedAt = DateTime.UtcNow;
        if (!present && lane.EntryKey == key) lane.EntryKey = null;
        if (!present)
        {
            var pending = await db.PatientCalls.Where(c => c.Desk == desk && c.VisitId == visitId && c.VisitTokenId == tokenId && c.Status == CallStatus.Queued).ToListAsync(ct);
            foreach (var call in pending) { call.Status = CallStatus.Cancelled; call.FinishedAt = DateTime.UtcNow; }
        }
    }

    public static IOrderedQueryable<Visit> OrderVisits(AppDbContext db, IQueryable<Visit> visits, CallDesk desk) => visits
        .OrderByDescending(v => (desk == CallDesk.Doctor && v.Status == VisitStatus.InConsultation) || (desk == CallDesk.Vision && v.Status == VisitStatus.InPreExam)
            || db.QueueDeskLanes.Any(l => l.Day == v.VisitDate && l.Desk == desk && l.EntryKey == "v:" + v.Id))
        .ThenBy(v => db.QueueAttendances.Any(a => a.Day == v.VisitDate && a.Desk == desk && a.EntryKey == "v:" + v.Id && !a.Present))
        .ThenBy(v => v.QueueShift == QueueShift.Afternoon).ThenBy(v => v.QueueNumber).ThenBy(v => v.Id);

    public static IQueryable<CallQueueEntry> Eligible(AppDbContext db, CallDesk desk, long userId, long? visitId = null, long? tokenId = null, bool recall = false, bool includeSeen = false)
    {
        var today = KabulClock.Today;
        if (desk == CallDesk.Reception && tokenId is not null)
        {
            var tokens = db.VisitTokens.AsNoTracking().Where(t => t.TokenDate == today && t.VisitId == null && t.QueueNumber != null
                && t.Invoice!.Status != InvoiceStatus.Void && t.Invoice.Total - t.Invoice.Discount <= t.Invoice.Paid - t.Invoice.Refunded && t.Id == tokenId.Value);

            return tokens.OrderBy(t => t.QueueShift == QueueShift.Afternoon).ThenBy(t => t.QueueNumber).ThenBy(t => t.Id)
                .Select(t => new CallQueueEntry(null, t.Id, t.QueueNumber!.Value, t.QueueShift, t.QueueRound));
        }

        var visits = db.Visits.AsNoTracking().Where(v => v.VisitDate == today && v.QueueNumber != null && v.Status != VisitStatus.Cancelled && v.Status != VisitStatus.AwaitingPayment);
        if (visitId is { } oneVisit)
        {
            visits = visits.Where(v => v.Id == oneVisit);
        }

        var openOrders = db.DiagnosticOrders.Where(o => o.Status == DiagnosticStatus.Requested || o.Status == DiagnosticStatus.InProgress);
        var paidOrders = openOrders.Where(o => o.Invoice != null && o.Invoice.Status != InvoiceStatus.Void && o.Invoice.Total - o.Invoice.Discount <= o.Invoice.Paid - o.Invoice.Refunded);
        var awaited = DiagnosticGate.Awaited(db);
        var cashierVisits = db.Visits.AsNoTracking().Where(v => v.VisitDate == today && v.QueueNumber != null && v.Status != VisitStatus.Cancelled
            && db.Invoices.Any(i => i.VisitId == v.Id && i.Status != InvoiceStatus.Void && i.Total - i.Discount > i.Paid - i.Refunded));
        if (visitId is { } cashierVisitId)
        {
            cashierVisits = cashierVisits.Where(v => v.Id == cashierVisitId);
        }

        var queue = desk switch
        {
            CallDesk.Reception => visits,
            CallDesk.Cashier => cashierVisits,
            CallDesk.Vision => ClinicalEndpoints.VisionQueue(visits),
            CallDesk.Doctor => ClinicalEndpoints.DoctorQueue(db, visits, userId)
                .Where(v => (v.Status == VisitStatus.ReadyForDoctor || v.Status == VisitStatus.InConsultation) && !awaited.Any(o => o.VisitId == v.Id)),
            CallDesk.Refraction => visits.Where(v => paidOrders.Any(o => o.VisitId == v.Id && o.Test!.Kind == DiagnosticKind.Refraction)),
            CallDesk.Imaging => visits.Where(v => paidOrders.Any(o => o.VisitId == v.Id && o.Test!.Kind == DiagnosticKind.Imaging)),
            CallDesk.Lab => visits.Where(v => openOrders.Any(o => o.VisitId == v.Id && o.Test!.Kind == DiagnosticKind.Lab && o.InvoiceId == null)
                || paidOrders.Any(o => o.VisitId == v.Id && o.Test!.Kind == DiagnosticKind.Lab)),
            CallDesk.Pharmacy => visits.Where(v => db.MedicalPrescriptions.Any(p => p.VisitId == v.Id && (p.Status == PrescriptionStatus.Issued || p.Status == PrescriptionStatus.PartiallyDispensed)
                && p.PharmacyStatus != PharmacyRxStatus.BuyingOutside && p.PharmacyStatus != PharmacyRxStatus.NotNow && p.PharmacyStatus != PharmacyRxStatus.NeedsDoctorReview)),
            CallDesk.Optical => visits.Where(v => db.OpticalPrescriptions.Any(p => p.VisitId == v.Id && !p.IsCancelled
                && !db.OpticalOrders.Any(o => o.OpticalPrescriptionId == p.Id && o.Status == Inventory.OpticalOrderStatus.Delivered))),
            CallDesk.Surgery => visits.Where(v => db.SurgeryCases.Any(s => s.VisitId == v.Id && s.Status != SurgeryStatus.Cancelled && s.Status != SurgeryStatus.Closed && s.Status != SurgeryStatus.Completed && s.Status != SurgeryStatus.FollowUp)),
            _ => throw new AppException(StatusCodes.Status400BadRequest, "invalid_desk", "بخش انتخاب‌شده معتبر نیست."),
        };

        var current = queue.OrderBy(v => v.QueueShift == QueueShift.Afternoon).ThenBy(v => v.QueueNumber).ThenBy(v => v.Id)
            .Select(v => new
            {
                VisitId = (long?)v.Id,
                TokenId = (long?)null,
                QueueNumber = v.QueueNumber!.Value,
                v.QueueShift,
                v.QueueRound,
            });

        if (desk == CallDesk.Reception && tokenId is null && visitId is null)
        {
            var tokens = db.VisitTokens.AsNoTracking().Where(t => t.TokenDate == today && t.VisitId == null && t.QueueNumber != null
                && t.Invoice!.Status != InvoiceStatus.Void && t.Invoice.Total - t.Invoice.Discount <= t.Invoice.Paid - t.Invoice.Refunded)
                .Select(t => new
                {
                    VisitId = (long?)null,
                    TokenId = (long?)t.Id,
                    QueueNumber = t.QueueNumber!.Value,
                    t.QueueShift,
                    t.QueueRound,
                });
            current = tokens.Concat(current);
        }

        if (!includeSeen)
        {
            return current.Select(e => new CallQueueEntry(e.VisitId, e.TokenId, e.QueueNumber, e.QueueShift, e.QueueRound));
        }

        var allVisits = db.Visits.AsNoTracking().Where(v => v.VisitDate == today && v.QueueNumber != null && v.Status != VisitStatus.Cancelled);
        if (visitId is { } targetVisit)
        {
            allVisits = allVisits.Where(v => v.Id == targetVisit);
        }

        var doneVisits = desk switch
        {
            CallDesk.Reception => allVisits,
            CallDesk.Cashier => allVisits.Where(v => db.Invoices.Any(i => i.VisitId == v.Id && i.Status != InvoiceStatus.Void && i.Total - i.Discount > i.Paid - i.Refunded)),
            CallDesk.Vision => ClinicalEndpoints.VisionDone(db, allVisits),
            CallDesk.Doctor => ClinicalEndpoints.DoctorDone(db, allVisits, userId),
            CallDesk.Refraction => allVisits.Where(v => db.DiagnosticOrders.Any(o => o.VisitId == v.Id && o.Test!.Kind == DiagnosticKind.Refraction && o.Status.Equals(DiagnosticStatus.ResultReady))),
            CallDesk.Imaging => allVisits.Where(v => db.DiagnosticOrders.Any(o => o.VisitId == v.Id && o.Test!.Kind == DiagnosticKind.Imaging && o.Status.Equals(DiagnosticStatus.ResultReady))),
            CallDesk.Lab => allVisits.Where(v => db.DiagnosticOrders.Any(o => o.VisitId == v.Id && o.Test!.Kind == DiagnosticKind.Lab && o.Status.Equals(DiagnosticStatus.ResultReady))),
            CallDesk.Pharmacy => allVisits.Where(v => db.MedicalPrescriptions.Any(p => p.VisitId == v.Id && (p.Status == PrescriptionStatus.Dispensed || p.Status == PrescriptionStatus.PartiallyDispensed))),
            CallDesk.Optical => allVisits.Where(v => db.OpticalPrescriptions.Any(p => p.VisitId == v.Id && !p.IsCancelled
                && db.OpticalOrders.Any(o => o.OpticalPrescriptionId == p.Id && o.Status == Inventory.OpticalOrderStatus.Delivered))),
            CallDesk.Surgery => allVisits.Where(v => db.SurgeryCases.Any(s => s.VisitId == v.Id && s.Status != SurgeryStatus.Cancelled)),
            _ => allVisits.Where(_ => false),
        };

        var doneEntries = doneVisits.Select(v => new
        {
            VisitId = (long?)v.Id,
            TokenId = (long?)null,
            QueueNumber = v.QueueNumber!.Value,
            v.QueueShift,
            v.QueueRound,
        });

        var seenCalls = db.PatientCalls.AsNoTracking()
            .Where(c => c.Desk == desk && c.VisitId != null && c.Visit!.VisitDate == today && c.Visit.Status != VisitStatus.Cancelled
                && (visitId == null || c.VisitId == visitId))
            .GroupBy(c => new { c.VisitId, c.QueueNumber, c.QueueShift, c.QueueRound })
            .Select(g => new
            {
                VisitId = g.Key.VisitId,
                TokenId = (long?)null,
                QueueNumber = g.Key.QueueNumber,
                g.Key.QueueShift,
                g.Key.QueueRound,
            });

        return current.Concat(doneEntries).Concat(seenCalls).Distinct()
            .OrderBy(e => e.QueueShift == QueueShift.Afternoon).ThenBy(e => e.QueueNumber).ThenBy(e => e.VisitId).ThenBy(e => e.TokenId)
            .Select(e => new CallQueueEntry(e.VisitId, e.TokenId, e.QueueNumber, e.QueueShift, e.QueueRound));
    }

}
