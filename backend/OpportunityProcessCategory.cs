using System.Globalization;
using System.Text;

namespace backend;

internal static class OpportunityProcessCategory
{
    public const string All = "all";
    public const string Infimas = "infimas";
    public const string Nco = "nco";
    public const string Sie = "sie";
    public const string Re = "re";
    public const string OtherPublic = "other_public";
    public const string LegacyNcoInfimas = "nco_infimas";
    public const string LegacyRegimenEspecial = "regimen_especial";
    public const string LegacyProcesosContratacion = "procesos_contratacion";

    public static string Resolve(string? source, string? tipo, string? processCode = null)
    {
        var normalizedSource = Normalize(source);
        var normalizedType = Normalize(tipo);
        var normalizedCode = Normalize(processCode);

        if (normalizedCode.StartsWith("nic ", StringComparison.Ordinal) || normalizedCode.StartsWith("nic-", StringComparison.Ordinal))
        {
            return Infimas;
        }

        if (normalizedCode.StartsWith("nc ", StringComparison.Ordinal) || normalizedCode.StartsWith("nc-", StringComparison.Ordinal))
        {
            return Nco;
        }

        if (normalizedCode.StartsWith("sie ", StringComparison.Ordinal) || normalizedCode.StartsWith("sie-", StringComparison.Ordinal))
        {
            return Sie;
        }

        if (normalizedCode.StartsWith("re ", StringComparison.Ordinal) || normalizedCode.StartsWith("re-", StringComparison.Ordinal))
        {
            return Re;
        }

        if (normalizedSource == "nco")
        {
            if (normalizedType.Contains("infima", StringComparison.Ordinal))
            {
                return Infimas;
            }

            if (normalizedType.Contains("recepcion de proformas", StringComparison.Ordinal)
                || normalizedType.Contains("necesidades de contratacion", StringComparison.Ordinal))
            {
                return Nco;
            }
        }

        if (normalizedType.Contains("subasta inversa", StringComparison.Ordinal))
        {
            return Sie;
        }

        if (normalizedType.Contains("regimen especial", StringComparison.Ordinal))
        {
            return Re;
        }

        return OtherPublic;
    }

    public static string NormalizeFilter(string? value)
    {
        var normalized = NormalizeFilterValue(value);
        return normalized switch
        {
            Infimas => Infimas,
            Nco => Nco,
            Sie => Sie,
            Re => Re,
            OtherPublic => OtherPublic,
            LegacyNcoInfimas => LegacyNcoInfimas,
            LegacyRegimenEspecial => LegacyRegimenEspecial,
            LegacyProcesosContratacion => LegacyProcesosContratacion,
            _ => All,
        };
    }

    public static bool IsValidFilter(string? value)
        => NormalizeFilter(value) == All
            ? string.IsNullOrWhiteSpace(value) || NormalizeFilterValue(value) == All
            : true;

    public static bool MatchesFilter(string category, string filter)
    {
        var normalizedCategory = NormalizeFilter(category);
        var normalizedFilter = NormalizeFilter(filter);

        if (normalizedFilter == All)
        {
            return true;
        }

        if (normalizedFilter == LegacyNcoInfimas)
        {
            return normalizedCategory is Infimas or Nco;
        }

        if (normalizedFilter == LegacyRegimenEspecial)
        {
            return normalizedCategory == Re;
        }

        if (normalizedFilter == LegacyProcesosContratacion)
        {
            return normalizedCategory is Sie or Re or OtherPublic;
        }

        return string.Equals(normalizedCategory, normalizedFilter, StringComparison.Ordinal);
    }

    private static string NormalizeFilterValue(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Trim().ToLowerInvariant();

    private static string Normalize(string? value)
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

        return builder
            .ToString()
            .Normalize(NormalizationForm.FormC)
            .Replace('_', ' ')
            .Replace('-', ' ')
            .Replace("  ", " ", StringComparison.Ordinal)
            .Trim();
    }
}
