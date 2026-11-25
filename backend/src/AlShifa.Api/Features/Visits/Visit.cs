using System.ComponentModel.DataAnnotations;
using AlShifa.Api.Features.Patients;
using AlShifa.Api.Features.Users;

namespace AlShifa.Api.Features.Visits;

public enum VisitStatus
{
    Registered,
    Waiting,
    InPreExam,
    ReadyForDoctor,
    InConsultation,
    AwaitingDiagnostic,
    FollowUpRequired,
    Completed,
    Cancelled,

    /// <summary>Registered with a physical card; clinical queues remain blocked until the fee is settled.</summary>
    AwaitingPayment,
}

public enum QueueShift
{
    Morning,
    Afternoon,
}

public static class QueueShifts
{
    public static QueueShift Current
    {
        get
        {
            var kabul = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("Asia/Kabul"));
            return kabul.TimeOfDay >= new TimeSpan(15, 0, 0) ? QueueShift.Afternoon : QueueShift.Morning;
        }
    }

    public static string Period(DateOnly date, QueueShift shift) => $"{date:yyyyMMdd}-{shift}";

    public static string Label(QueueShift shift, int round = 1)
    {
        var label = shift switch
        {
            QueueShift.Morning => "صبح",
            QueueShift.Afternoon => "بعدازچاشت",
            _ => throw new ArgumentOutOfRangeException(nameof(shift)),
        };
        return round > 1 ? $"{label} - دور {round}" : label;
    }
}

public class Visit
{
    public long Id { get; set; }

    [MaxLength(20)]
    public string VisitNumber { get; set; } = "";

    public long PatientId { get; set; }

    public Patient? Patient { get; set; }

    public int DepartmentId { get; set; }

    public Department? Department { get; set; }

    public long? DoctorStaffId { get; set; }

    public Staff? Doctor { get; set; }

    public VisitStatus Status { get; set; } = VisitStatus.Registered;

    [MaxLength(300)]
    public string? Reason { get; set; }

    /// <summary>Calendar day in Asia/Kabul. Physical card uniqueness spans the entire day.</summary>
    public DateOnly VisitDate { get; set; }

    public QueueShift QueueShift { get; set; } = global::AlShifa.Api.Features.Visits.QueueShift.Morning;

    public int QueueRound { get; set; } = 1;

    /// <summary>Manually entered physical card number, fixed across every department of this visit.</summary>
    public int? QueueNumber { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public long CreatedByUserId { get; set; }

    public int Version { get; set; }
}

/// <summary>
/// The visit fee and its fixed physical card. Payment activates the visit without generating another number.
/// Tokens from before the patient-first flow may still have no visit and are completed from the reception page.
/// </summary>
public class VisitToken
{
    public long Id { get; set; }

    public int? QueueNumber { get; set; }

    public DateOnly TokenDate { get; set; }

    public QueueShift QueueShift { get; set; } = global::AlShifa.Api.Features.Visits.QueueShift.Morning;

    public int QueueRound { get; set; } = 1;

    public int DepartmentId { get; set; }

    public Department? Department { get; set; }

    public long? DoctorStaffId { get; set; }

    public Staff? Doctor { get; set; }

    public long InvoiceId { get; set; }

    public Finance.Invoice? Invoice { get; set; }

    public long? VisitId { get; set; }

    public Visit? Visit { get; set; }

    public long CreatedByUserId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public int Version { get; set; }
}

public class VisitStatusHistory
{
    public long Id { get; set; }

    public long VisitId { get; set; }

    public Visit? Visit { get; set; }

    public VisitStatus? FromStatus { get; set; }

    public VisitStatus ToStatus { get; set; }

    public long ChangedByUserId { get; set; }

    public DateTime ChangedAt { get; set; } = DateTime.UtcNow;
}

public enum AppointmentStatus
{
    Scheduled,
    CheckedIn,
    Cancelled,
    NoShow,
}

public class Appointment
{
    public long Id { get; set; }

    public long PatientId { get; set; }

    public Patient? Patient { get; set; }

    public long DoctorStaffId { get; set; }

    public Staff? Doctor { get; set; }

    public DateTime ScheduledAt { get; set; }

    public AppointmentStatus Status { get; set; } = AppointmentStatus.Scheduled;

    [MaxLength(300)]
    public string? Note { get; set; }

    public long? VisitId { get; set; }

    public Visit? Visit { get; set; }

    public long CreatedByUserId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public int Version { get; set; }
}
