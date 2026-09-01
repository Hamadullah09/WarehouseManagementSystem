using System.Collections.Concurrent;

namespace Warehouse.Rfid.Abstractions;

/// <summary>
/// Holds the live reader instances for the process.
/// </summary>
/// <remarks>
/// Registered as a singleton. Readers are constructed once at startup by the
/// RFID hosted service and resolved from here by gate code or reader id, so no
/// business service ever owns a reader's lifetime or knows which driver backs
/// it (§24).
/// </remarks>
public sealed class RfidReaderRegistry : IRfidReaderRegistry
{
    private readonly ConcurrentDictionary<string, IRfidReader> _byReaderId =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly ConcurrentDictionary<string, string> _readerIdByGate =
        new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<IRfidReader> All => _byReaderId.Values.ToList();

    public void Register(string gateId, IRfidReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        _byReaderId[reader.ReaderId] = reader;

        if (!string.IsNullOrWhiteSpace(gateId))
        {
            _readerIdByGate[gateId] = reader.ReaderId;
        }
    }

    public bool TryGet(string readerId, out IRfidReader reader)
    {
        if (!string.IsNullOrEmpty(readerId))
        {
            return _byReaderId.TryGetValue(readerId, out reader!);
        }

        reader = null!;

        return false;
    }

    public IRfidReader? ForGate(string gateId) =>
        _readerIdByGate.TryGetValue(gateId, out var readerId) && _byReaderId.TryGetValue(readerId, out var reader)
            ? reader
            : null;

    public IEnumerable<(string GateId, IRfidReader Reader)> Pairs() =>
        _readerIdByGate
            .Where(kv => _byReaderId.ContainsKey(kv.Value))
            .Select(kv => (kv.Key, _byReaderId[kv.Value]));
}
