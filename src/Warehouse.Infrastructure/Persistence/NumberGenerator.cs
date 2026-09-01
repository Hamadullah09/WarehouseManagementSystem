using Microsoft.Extensions.Options;
using Warehouse.Application.Abstractions;
using Warehouse.Application.Options;
using Warehouse.Domain;

namespace Warehouse.Infrastructure.Persistence;

/// <summary>
/// Database-backed document, cycle and alarm numbering (§5).
/// </summary>
/// <remarks>
/// Numbers are allocated by the database, never by the client and never by
/// counting existing rows. The year is part of the sequence key so counters
/// restart cleanly on 1 January without any scheduled job.
/// </remarks>
public sealed class NumberGenerator(
    IWarehouseDbContext db,
    IClock clock,
    IOptionsMonitor<DocumentOptions> options) : INumberGenerator
{
    public Task<string> NextDocumentNumberAsync(
        DocumentType type,
        CancellationToken cancellationToken = default)
    {
        var cfg = options.CurrentValue;
        var prefix = type == DocumentType.Inward ? cfg.InwardPrefix : cfg.OutwardPrefix;

        return FormatAsync(prefix, cancellationToken);
    }

    public Task<string> NextCycleIdAsync(CancellationToken cancellationToken = default) =>
        FormatAsync(options.CurrentValue.CyclePrefix, cancellationToken);

    public Task<string> NextAlarmIdAsync(CancellationToken cancellationToken = default) =>
        FormatAsync(options.CurrentValue.AlarmPrefix, cancellationToken);

    private async Task<string> FormatAsync(string prefix, CancellationToken cancellationToken)
    {
        var year = clock.UtcNow.Year;
        var value = await db.NextSequenceValueAsync(prefix, year, cancellationToken).ConfigureAwait(false);
        var padding = options.CurrentValue.NumberPadding;

        return $"{prefix}-{year}-{value.ToString().PadLeft(padding, '0')}";
    }
}
