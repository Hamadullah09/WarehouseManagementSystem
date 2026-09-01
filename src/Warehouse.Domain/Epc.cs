namespace Warehouse.Domain;

/// <summary>
/// Canonical form for EPC strings.
/// </summary>
/// <remarks>
/// Readers, CSV imports and hand-entered documents disagree about casing and
/// stray whitespace. Every EPC is normalised at every boundary so a casing
/// difference can never masquerade as an unknown tag and raise a false alarm.
/// </remarks>
public static class Epc
{
    /// <summary>Longest EPC the schema stores. 128 hex chars covers EPC-496 with room to spare.</summary>
    public const int MaxLength = 128;

    /// <summary>Uppercases and strips whitespace. Returns empty for null/blank input.</summary>
    public static string Normalize(string? epc)
    {
        if (string.IsNullOrWhiteSpace(epc))
        {
            return string.Empty;
        }

        Span<char> buffer = epc.Length <= 256 ? stackalloc char[epc.Length] : new char[epc.Length];
        var length = 0;

        foreach (var c in epc)
        {
            if (char.IsWhiteSpace(c))
            {
                continue;
            }

            buffer[length++] = char.ToUpperInvariant(c);
        }

        return length == 0 ? string.Empty : new string(buffer[..length]);
    }

    /// <summary>
    /// True when the value is a plausible EPC: non-empty, even-length hex,
    /// within the storage limit. Used by the import pipeline to reject junk
    /// rows before they reach the database.
    /// </summary>
    public static bool IsValid(string? epc)
    {
        if (string.IsNullOrEmpty(epc) || epc.Length > MaxLength || epc.Length % 2 != 0)
        {
            return false;
        }

        foreach (var c in epc)
        {
            var isHex = c is >= '0' and <= '9' or >= 'A' and <= 'F' or >= 'a' and <= 'f';
            if (!isHex)
            {
                return false;
            }
        }

        return true;
    }

    public static readonly StringComparer Comparer = StringComparer.Ordinal;
}
