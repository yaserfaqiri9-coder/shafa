using System.ComponentModel.DataAnnotations;
using AlShifa.Api.Features.Calls;

namespace AlShifa.Api.Features.Visits;

/// <summary>
/// A physical card can be assigned once per Kabul calendar day and shift, across every department: the cards restart at 1
/// in the afternoon. The key stays (Day, Number) so no schema change is needed; an afternoon card is kept as Number + 100.
/// </summary>
public sealed class QueueCard
{
    public const int Cards = 100;
    public DateOnly Day { get; set; }
    public int Number { get; set; }

    public static int Slot(QueueShift shift, int number) => shift == QueueShift.Afternoon ? number + Cards : number;
}

/// <summary>Attendance is local to a department; it never changes clinical or financial state.</summary>
public sealed class QueueAttendance
{
    public DateOnly Day { get; set; }
    public CallDesk Desk { get; set; }
    [MaxLength(40)] public string EntryKey { get; set; } = "";
    public bool Present { get; set; } = true;
    public DateTime UpdatedAt { get; set; }
}

/// <summary>Serializes selection and holds the current patient until that department finishes or marks absence.</summary>
public sealed class QueueDeskLane
{
    public DateOnly Day { get; set; }
    public CallDesk Desk { get; set; }
    public long LaneId { get; set; }
    [MaxLength(40)] public string? EntryKey { get; set; }
}

public sealed record AssignQueueNumberRequest([property: Required, Range(1, 100)] int? QueueNumber);
