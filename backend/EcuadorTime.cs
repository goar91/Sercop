using System.Globalization;
using System.Text.RegularExpressions;

namespace backend;

internal static partial class EcuadorTime
{
    private static readonly TimeZoneInfo TimeZone = ResolveTimeZone();

    public static DateTimeOffset Now()
        => TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZone);

    public static DateTimeOffset? ParseTimestamp(string? value)
    {
        var raw = value?.Trim();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        if (HasExplicitOffset(raw))
        {
            return DateTimeOffset.TryParse(
                raw,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.RoundtripKind,
                out var absoluteTimestamp)
                ? TimeZoneInfo.ConvertTime(absoluteTimestamp, TimeZone)
                : null;
        }

        if (TryParseLocalTimestamp(raw, out var localTimestamp))
        {
            return new DateTimeOffset(localTimestamp, TimeZone.GetUtcOffset(localTimestamp));
        }

        if (DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var fallback))
        {
            var unspecified = DateTime.SpecifyKind(fallback, DateTimeKind.Unspecified);
            return new DateTimeOffset(unspecified, TimeZone.GetUtcOffset(unspecified));
        }

        return null;
    }

    private static bool TryParseLocalTimestamp(string raw, out DateTime parsed)
    {
        var formats = new[]
        {
            "yyyy-MM-dd",
            "yyyy-MM-dd HH:mm",
            "yyyy-MM-dd HH:mm:ss",
            "yyyy-MM-ddTHH:mm",
            "yyyy-MM-ddTHH:mm:ss",
            "yyyy-MM-ddTHH:mm:ss.fff",
            "dd/MM/yyyy",
            "dd/MM/yyyy HH:mm",
            "dd/MM/yyyy HH:mm:ss",
            "dd-MM-yyyy",
            "dd-MM-yyyy HH:mm",
            "dd-MM-yyyy HH:mm:ss",
        };

        if (DateTime.TryParseExact(raw, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed))
        {
            parsed = DateTime.SpecifyKind(parsed, DateTimeKind.Unspecified);
            return true;
        }

        return false;
    }

    private static bool HasExplicitOffset(string value)
        => OffsetSuffixPattern().IsMatch(value);

    private static TimeZoneInfo ResolveTimeZone()
    {
        foreach (var candidate in new[] { "America/Guayaquil", "SA Pacific Standard Time" })
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(candidate);
            }
            catch (TimeZoneNotFoundException)
            {
            }
            catch (InvalidTimeZoneException)
            {
            }
        }

        return TimeZoneInfo.CreateCustomTimeZone(
            "America/Guayaquil",
            TimeSpan.FromHours(-5),
            "Ecuador",
            "Ecuador");
    }

    [GeneratedRegex(@"(?:[zZ]|[+-]\d{2}(?::?\d{2})?)$")]
    private static partial Regex OffsetSuffixPattern();
}
