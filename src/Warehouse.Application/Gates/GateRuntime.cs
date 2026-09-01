using System.Collections.Concurrent;
using System.Threading.Channels;
using Warehouse.Domain;

namespace Warehouse.Application.Gates;

/// <summary>Running tally for one EPC within one cycle.</summary>
public sealed class EpcAccumulator
{
    private int _readCount;
    private long _peakRssiBits = BitConverter.DoubleToInt64Bits(double.NegativeInfinity);

    public required string Epc { get; init; }

    public int ReadCount => Volatile.Read(ref _readCount);

    public double? PeakRssi
    {
        get
        {
            var value = BitConverter.Int64BitsToDouble(Interlocked.Read(ref _peakRssiBits));
            return double.IsNegativeInfinity(value) ? null : value;
        }
    }

    public int? Antenna { get; private set; }

    public DateTimeOffset FirstSeenAt { get; init; }

    public DateTimeOffset LastSeenAt { get; private set; }

    /// <summary>Set once the EPC has been classified against the catalogue.</summary>
    public bool? IsKnown { get; set; }

    public void Observe(double? rssi, int? antenna, DateTimeOffset at)
    {
        Interlocked.Increment(ref _readCount);
        LastSeenAt = at;

        if (antenna is not null)
        {
            Antenna = antenna;
        }

        if (rssi is not { } value)
        {
            return;
        }

        // Keep the strongest reading: it is the most useful signal when
        // diagnosing an antenna that is reaching too far or not far enough.
        long current, updated;

        do
        {
            current = Interlocked.Read(ref _peakRssiBits);

            if (value <= BitConverter.Int64BitsToDouble(current))
            {
                return;
            }

            updated = BitConverter.DoubleToInt64Bits(value);
        }
        while (Interlocked.CompareExchange(ref _peakRssiBits, updated, current) != current);
    }
}

/// <summary>State of one in-flight gate cycle, held in memory until validation.</summary>
public sealed class ActiveCycle
{
    public required long Id { get; init; }

    public required string CycleId { get; init; }

    public required DateTimeOffset StartedAt { get; init; }

    public int? DocumentId { get; init; }

    public string? DocumentNumber { get; init; }

    public DocumentType? DocumentType { get; init; }

    /// <summary>
    /// EPCs the document still expects when the cycle opened. Held in memory
    /// so the read path never queries the database (§36).
    /// </summary>
    public required IReadOnlySet<string> ExpectedEpcs { get; init; }

    /// <summary>
    /// Distinct EPCs seen this cycle. The dictionary is the deduplication:
    /// six reads of one tag produce one entry, not six movements (§12).
    /// </summary>
    public ConcurrentDictionary<string, EpcAccumulator> Epcs { get; } = new(Epc.Comparer);

    /// <summary>Newly-sighted EPCs awaiting classification and broadcast.</summary>
    public Channel<string> NewEpcs { get; } = Channel.CreateUnbounded<string>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    private int _rawReadCount;

    /// <summary>Total reads received, before deduplication.</summary>
    public int RawReadCount => Volatile.Read(ref _rawReadCount);

    private int _readerHealthy = 1;

    /// <summary>False once the reader has reported any fault during this cycle.</summary>
    public bool ReaderHealthy => Volatile.Read(ref _readerHealthy) == 1;

    public void MarkReaderUnhealthy() => Volatile.Write(ref _readerHealthy, 0);

    public CancellationTokenSource TimeoutSource { get; } = new();

    /// <summary>
    /// Records one read. Returns the accumulator and whether this was the
    /// first sighting of the EPC in this cycle.
    /// </summary>
    public (EpcAccumulator Accumulator, bool IsNew) Observe(
        string epc,
        double? rssi,
        int? antenna,
        DateTimeOffset at)
    {
        Interlocked.Increment(ref _rawReadCount);

        var isNew = false;

        var accumulator = Epcs.GetOrAdd(epc, key =>
        {
            isNew = true;
            return new EpcAccumulator { Epc = key, FirstSeenAt = at };
        });

        accumulator.Observe(rssi, antenna, at);

        return (accumulator, isNew);
    }
}

/// <summary>A tag read observed before its cycle record existed.</summary>
public readonly record struct StagedRead(string Epc, double? Rssi, int? Antenna, DateTimeOffset At);

/// <summary>Everything the service knows about one gate between requests.</summary>
public sealed class GateRuntime
{
    public required int GateId { get; init; }

    public required string GateCode { get; init; }

    public string? ReaderId { get; set; }

    public int? ReaderDbId { get; set; }

    public GateState State { get; set; } = GateState.Idle;

    public ActiveCycle? Cycle { get; set; }

    public string? StatusMessage { get; set; }

    public AlarmType? ActiveAlarm { get; set; }

    public bool ReaderOnline { get; set; }

    public string? LastEpc { get; set; }

    /// <summary>When the last accepted gate-signal edge arrived. Drives duplicate suppression.</summary>
    public DateTimeOffset? LastAcceptedEdgeAt { get; set; }

    /// <summary>Serialises transitions so two edges cannot open two cycles.</summary>
    public SemaphoreSlim Lock { get; } = new(1, 1);

    private int _opening;

    /// <summary>
    /// True between the input edge being observed and the cycle record
    /// existing. Reads arriving in that window are staged rather than dropped.
    /// </summary>
    public bool IsOpeningCycle
    {
        get => Volatile.Read(ref _opening) == 1;
        set => Volatile.Write(ref _opening, value ? 1 : 0);
    }

    /// <summary>
    /// Tag reads observed before the cycle record existed.
    /// </summary>
    /// <remarks>
    /// Opening a cycle costs a database round trip. On a fast conveyor the
    /// first carton can reach the antenna inside that window, and those reads
    /// belong to the cycle just as much as the ones that follow it. Dropping
    /// them would manufacture a missing-EPC alarm on a perfectly good load.
    /// </remarks>
    public ConcurrentQueue<StagedRead> StagedReads { get; } = new();

    /// <summary>Background task draining <see cref="ActiveCycle.NewEpcs"/>.</summary>
    public Task? ConsumerTask { get; set; }
}

/// <summary>Read model of a gate for the API and the display (§45).</summary>
public sealed record GateSnapshot
{
    public required string GateCode { get; init; }

    public required string GateName { get; init; }

    public required GateState State { get; init; }

    public bool ReaderOnline { get; init; }

    public string? ReaderId { get; init; }

    public string? CycleId { get; init; }

    public DateTimeOffset? CycleStartedAt { get; init; }

    public string? DocumentNumber { get; init; }

    public DocumentType? MovementType { get; init; }

    public string? UserDisplayName { get; init; }

    public int ExpectedArticles { get; init; }

    public int DetectedArticles { get; init; }

    public int BalanceArticles { get; init; }

    public int ExpectedQuantity { get; init; }

    public int DetectedQuantity { get; init; }

    public int BalanceQuantity { get; init; }

    /// <summary>Distinct EPCs seen in the live cycle, if one is running.</summary>
    public int CycleDetectedCount { get; init; }

    public IReadOnlyList<string> BalanceEpcs { get; init; } = [];

    public string? LastEpc { get; init; }

    public string? StatusMessage { get; init; }

    public AlarmType? ActiveAlarm { get; init; }

    public DateTimeOffset Timestamp { get; init; }
}
