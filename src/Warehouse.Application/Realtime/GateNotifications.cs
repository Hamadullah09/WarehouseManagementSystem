using Warehouse.Domain;

namespace Warehouse.Application.Realtime;

/// <summary>Live gate snapshot pushed to displays and the dashboard (§19, §20).</summary>
public sealed record GateStatusUpdate
{
    public required string GateCode { get; init; }

    public required GateState State { get; init; }

    public string? DocumentNumber { get; init; }

    public DocumentType? MovementType { get; init; }

    public string? UserDisplayName { get; init; }

    public string? CycleId { get; init; }

    public int ExpectedArticles { get; init; }

    public int DetectedArticles { get; init; }

    public int BalanceArticles { get; init; }

    public int ExpectedQuantity { get; init; }

    public int DetectedQuantity { get; init; }

    public int BalanceQuantity { get; init; }

    /// <summary>EPCs still outstanding on the document. Feeds the balance list.</summary>
    public IReadOnlyList<string> BalanceEpcs { get; init; } = [];

    /// <summary>Most recent EPC seen, for the current-EPC readout.</summary>
    public string? LastEpc { get; init; }

    public bool ReaderOnline { get; init; }

    public string? StatusMessage { get; init; }

    public AlarmType? ActiveAlarm { get; init; }

    public DateTimeOffset Timestamp { get; init; }
}

/// <summary>Incremental EPC notification during a live cycle.</summary>
public sealed record EpcDetectedUpdate
{
    public required string GateCode { get; init; }

    public required string CycleId { get; init; }

    public required string Epc { get; init; }

    public bool IsKnown { get; init; }

    public bool IsExpected { get; init; }

    public int DetectedCount { get; init; }

    public int ExpectedCount { get; init; }

    public double? Rssi { get; init; }

    public int? Antenna { get; init; }

    public DateTimeOffset Timestamp { get; init; }
}

public sealed record CycleCompletedUpdate
{
    public required string GateCode { get; init; }

    public required string CycleId { get; init; }

    public required bool Passed { get; init; }

    public string? DocumentNumber { get; init; }

    public DocumentStatus? DocumentStatus { get; init; }

    public int ExpectedCount { get; init; }

    public int DetectedCount { get; init; }

    public IReadOnlyList<string> Missing { get; init; } = [];

    public IReadOnlyList<string> Unknown { get; init; } = [];

    public IReadOnlyList<string> Unexpected { get; init; } = [];

    public required string Summary { get; init; }

    public DateTimeOffset Timestamp { get; init; }
}

public sealed record AlarmRaisedUpdate
{
    public required string AlarmId { get; init; }

    public required AlarmType AlarmType { get; init; }

    public string? GateCode { get; init; }

    public string? DocumentNumber { get; init; }

    public string? CycleId { get; init; }

    public required string Message { get; init; }

    public string? Epc { get; init; }

    public IReadOnlyList<string> Epcs { get; init; } = [];

    public DateTimeOffset Timestamp { get; init; }
}

public sealed record ReaderStatusUpdate
{
    public required string ReaderId { get; init; }

    public required string GateCode { get; init; }

    public required bool Online { get; init; }

    public bool Inventorying { get; init; }

    public string? Message { get; init; }

    public IReadOnlyList<string> GpioState { get; init; } = [];

    public DateTimeOffset Timestamp { get; init; }
}

public sealed record GpioStateUpdate
{
    public required string GateCode { get; init; }

    public required string ReaderId { get; init; }

    public required string Pin { get; init; }

    public required bool High { get; init; }

    public bool IsInput { get; init; }

    public DateTimeOffset Timestamp { get; init; }
}

/// <summary>
/// Transport-neutral push channel (§22).
/// </summary>
/// <remarks>
/// Implemented over SignalR in the API. Kept as an interface so the gate cycle
/// service has no dependency on ASP.NET, and so tests can assert what would
/// have been broadcast. Implementations must never throw: a display that has
/// dropped off must not be able to fail a warehouse transaction.
/// </remarks>
public interface IGateNotifier
{
    Task GateStatusChangedAsync(GateStatusUpdate update, CancellationToken cancellationToken = default);

    Task EpcDetectedAsync(EpcDetectedUpdate update, CancellationToken cancellationToken = default);

    Task CycleCompletedAsync(CycleCompletedUpdate update, CancellationToken cancellationToken = default);

    Task AlarmRaisedAsync(AlarmRaisedUpdate update, CancellationToken cancellationToken = default);

    Task ReaderStatusChangedAsync(ReaderStatusUpdate update, CancellationToken cancellationToken = default);

    Task GpioChangedAsync(GpioStateUpdate update, CancellationToken cancellationToken = default);
}
