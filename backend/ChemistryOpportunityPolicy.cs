using System.Text.RegularExpressions;

namespace backend;

internal static class ChemistryOpportunityPolicy
{
    private static readonly Regex[] SupplySignals =
    {
        CreateRegex(@"reactiv"),
        CreateRegex(@"reagent"),
        CreateRegex(@"insumos?"),
        CreateRegex(@"material(?:es)? de laboratorio"),
        CreateRegex(@"material(?:es)? de referencia"),
        CreateRegex(@"est[aá]ndares?"),
        CreateRegex(@"qu[ií]mic(?:o|a|os|as)"),
        CreateRegex(@"insumos? qu[ií]mic"),
        CreateRegex(@"solvente"),
        CreateRegex(@"etanol"),
        CreateRegex(@"isopropanol"),
        CreateRegex(@"hidroxido"),
        CreateRegex(@"hipoclorito"),
        CreateRegex(@"acetileno"),
        CreateRegex(@"\bgases?\b"),
        CreateRegex(@"kits?"),
        CreateRegex(@"tests? fotom[eé]tricos?"),
        CreateRegex(@"medios? de cultivo"),
        CreateRegex(@"buffer"),
        CreateRegex(@"calibradores?"),
        CreateRegex(@"controles?"),
        CreateRegex(@"colorantes?"),
    };

    private static readonly Regex[] ChemistryContextSignals =
    {
        CreateRegex(@"laborator"),
        CreateRegex(@"microbiolog"),
        CreateRegex(@"fitopatolog"),
        CreateRegex(@"bromatolog"),
        CreateRegex(@"absorc[ií]on at[oó]mica"),
        CreateRegex(@"docencia e investigaci[oó]n"),
        CreateRegex(@"control de calidad"),
        CreateRegex(@"calidad del agua"),
        CreateRegex(@"aguas? residuales?"),
        CreateRegex(@"aguas? potables?"),
        CreateRegex(@"agua cruda"),
        CreateRegex(@"tejidos? vegetales?"),
        CreateRegex(@"suelos?"),
        CreateRegex(@"contaminantes?"),
    };

    private static readonly Regex ChemistryTermSignal = CreateRegex(@"qu[ií]mic");

    private static readonly Regex[] StrictExcludeSignals =
    {
        CreateRegex(@"transporte"),
        CreateRegex(@"estiba"),
        CreateRegex(@"alimentaci[oó]n"),
        CreateRegex(@"uniforme"),
        CreateRegex(@"combustible"),
        CreateRegex(@"extintor"),
        CreateRegex(@"base naval"),
        CreateRegex(@"base de datos"),
        CreateRegex(@"servidor"),
        CreateRegex(@"impresi[oó]n"),
        CreateRegex(@"fotocopi"),
        CreateRegex(@"outsourcing"),
        CreateRegex(@"agropecuar"),
        CreateRegex(@"agr[ií]col"),
        CreateRegex(@"fertiliz"),
        CreateRegex(@"malezas?"),
        CreateRegex(@"plagas?"),
        CreateRegex(@"[aá]reas? verdes"),
        CreateRegex(@"mobiliario"),
        CreateRegex(@"equipo inform[aá]tico"),
        CreateRegex(@"repuestos?"),
        CreateRegex(@"seguridad"),
        CreateRegex(@"veh[ií]culo"),
        CreateRegex(@"construcci[oó]n"),
        CreateRegex(@"servicio(?:s)? de"),
        CreateRegex(@"mantenimiento"),
        CreateRegex(@"calibraci[oó]n"),
        CreateRegex(@"desaduaniz"),
        CreateRegex(@"instrumentos? de medici[oó]n"),
        CreateRegex(@"aire acondicionado"),
        CreateRegex(@"vitrina"),
        CreateRegex(@"incubadora"),
        CreateRegex(@"centrifug"),
        CreateRegex(@"balanzas?"),
        CreateRegex(@"electrodom[eé]stic"),
        CreateRegex(@"accesorios? electr[oó]nic"),
        CreateRegex(@"invernadero"),
        CreateRegex(@"malla antipajaro"),
        CreateRegex(@"\bsaran\b"),
        CreateRegex(@"marmitas?"),
        CreateRegex(@"agitaci[oó]n"),
        CreateRegex(@"herramient"),
        CreateRegex(@"ferreter"),
        CreateRegex(@"campanas? de extracci[oó]n"),
        CreateRegex(@"adquisi(?:ci[oó]n)? de equipos?"),
        CreateRegex(@"adquirir equipos?"),
        CreateRegex(@"equipos?\s+para"),
        CreateRegex(@"\bmuflas?\b"),
        CreateRegex(@"\bestufas?\b"),
        CreateRegex(@"celdas? electroqu[ií]mic"),
        CreateRegex(@"analizador"),
        CreateRegex(@"insumos generales"),
        CreateRegex(@"equipos? de laboratorio"),
    };

    private static readonly Regex[] MedicalExcludeSignals =
    {
        CreateRegex(@"hospital(?:es)?"),
        CreateRegex(@"salud"),
        CreateRegex(@"cl[ií]nic(?:o|a|os|as)"),
        CreateRegex(@"centro m[eé]dic"),
        CreateRegex(@"m[eé]dic(?:o|a|os|as)"),
        CreateRegex(@"dispositivos? m[eé]dic"),
        CreateRegex(@"diagn[oó]stic"),
        CreateRegex(@"ex[aá]menes? de laboratorio"),
        CreateRegex(@"pacientes?"),
        CreateRegex(@"serolog"),
        CreateRegex(@"hormonas?"),
        CreateRegex(@"sangu[ií]n"),
        CreateRegex(@"hematol[oó]g"),
        CreateRegex(@"bioqu[ií]mic"),
        CreateRegex(@"anatom[ií]a patol[oó]gica"),
        CreateRegex(@"laboratorio de patolog[ií]a"),
        CreateRegex(@"veterinari"),
        CreateRegex(@"hemoaglutin"),
        CreateRegex(@"\b(vih|hiv)\b"),
        CreateRegex(@"bcr\s*\/?\s*abl"),
        CreateRegex(@"\bjak2\b"),
        CreateRegex(@"\bpcr\b"),
        CreateRegex(@"farmacotecnia"),
        CreateRegex(@"hospital del d[ií]a"),
        CreateRegex(@"hospitalari"),
        CreateRegex(@"apoyo tecnol[oó]gico"),
        CreateRegex(@"convenio de uso"),
        CreateRegex(@"pruebas? r[aá]pidas?"),
    };

    private static readonly Regex[] PharmaSignals =
    {
        CreateRegex(@"solido oral"),
        CreateRegex(@"s[oó]lido oral"),
        CreateRegex(@"parenteral"),
        CreateRegex(@"ampollas?"),
        CreateRegex(@"c[aá]psulas?"),
        CreateRegex(@"tabletas?"),
        CreateRegex(@"comprimidos?"),
        CreateRegex(@"jarabe"),
        CreateRegex(@"mg\/ml"),
    };

    private static readonly HashSet<string> NonChemicalKeywordFamilies = new(StringComparer.OrdinalIgnoreCase)
    {
        "contratacion",
        "nco",
        "medico",
        "ruido",
    };

    public static bool IsChemicalKeywordFamily(string? family)
        => !NonChemicalKeywordFamilies.Contains((family ?? string.Empty).Trim());

    public static bool ShouldDisplay(string title, string? entity, string? processType, KeywordRuleSnapshot keywordRules)
    {
        var haystack = $"{title} {entity ?? string.Empty} {processType ?? string.Empty}";
        var normalizedHaystack = haystack.ToLowerInvariant();

        if (keywordRules.ExcludeKeywords.Any(keyword => normalizedHaystack.Contains(keyword, StringComparison.Ordinal)))
        {
            return false;
        }

        var hasKeywordInclude = keywordRules.IncludeKeywords.Any(keyword => normalizedHaystack.Contains(keyword, StringComparison.Ordinal));

        if (!MatchesAny(haystack, SupplySignals) && !hasKeywordInclude)
        {
            return false;
        }

        if (!MatchesAny(haystack, ChemistryContextSignals) && !ChemistryTermSignal.IsMatch(haystack) && !hasKeywordInclude)
        {
            return false;
        }

        if (MatchesAny(haystack, MedicalExcludeSignals))
        {
            return false;
        }

        if (MatchesAny(haystack, StrictExcludeSignals))
        {
            return false;
        }

        return !MatchesAny(haystack, PharmaSignals);
    }

    public static IReadOnlyList<string> ResolveWinningAreas(IEnumerable<string> keywords, IReadOnlyDictionary<string, string> familyByKeyword)
        => keywords
            .Select(keyword => familyByKeyword.TryGetValue(keyword.Trim().ToLowerInvariant(), out var family) ? family : null)
            .Where(family => !string.IsNullOrWhiteSpace(family))
            .Select(family => family!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static Regex CreateRegex(string pattern)
        => new(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static bool MatchesAny(string value, IEnumerable<Regex> patterns)
        => patterns.Any(pattern => pattern.IsMatch(value));
}

internal sealed record KeywordRuleSnapshot(
    IReadOnlyList<string> IncludeKeywords,
    IReadOnlyList<string> ExcludeKeywords,
    IReadOnlyDictionary<string, string> FamilyByKeyword);
