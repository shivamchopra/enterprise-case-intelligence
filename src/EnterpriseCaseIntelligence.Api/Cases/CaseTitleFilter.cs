using System.Buffers;
using System.Text;

namespace EnterpriseCaseIntelligence.Api.Cases;

internal static class CaseTitleFilter
{
    public static string Normalize(string title)
    {
        ArgumentNullException.ThrowIfNull(title);
        var normalized = title.Trim();
        if (normalized.Length is < 1 or > 120) throw Invalid();
        var remaining = normalized.AsSpan();
        while (!remaining.IsEmpty)
        {
            if (Rune.DecodeFromUtf16(remaining, out _, out var consumed) != OperationStatus.Done) throw Invalid();
            remaining = remaining[consumed..];
        }
        return normalized;
    }

    private static ArgumentException Invalid() => new("Use 1 to 120 characters of valid text after trimming.", "title");
}
