using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace backend;

internal static partial class TextNormalization
{
    public static string NormalizeForComparison(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = value.Trim().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(normalized.Length);
        foreach (var character in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(char.ToLowerInvariant(character));
            }
        }

        return CollapseWhitespace(builder.ToString());
    }

    public static string NormalizeKeywordDisplay(string value)
        => CollapseWhitespace(value).ToLowerInvariant();

    public static string[] TokenizeSearch(string? value)
        => NormalizeForComparison(value)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    public static string EscapeLikeToken(string value)
        => value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);

    private static string CollapseWhitespace(string value)
        => MultiWhitespaceRegex().Replace(value.Trim(), " ");

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex MultiWhitespaceRegex();
}
