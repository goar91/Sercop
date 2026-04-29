using System.Text.Json;
using System.Text.RegularExpressions;

namespace backend;

internal static class ChemistryOpportunityPolicy
{
    private static readonly Lazy<CompiledChemistryPolicyDefinition> CompiledDefinition = new(LoadCompiledDefinition);
    private static readonly HashSet<string> SuppressibleGenericExcludeKeywords = new(StringComparer.Ordinal)
    {
        "adquisicion de equipos",
        "equipos de laboratorio",
        "instrumentos de medicion"
    };

    public static bool IsChemicalKeywordFamily(string? family)
        => !CompiledDefinition.Value.NonChemicalKeywordFamilies.Contains(TextNormalization.NormalizeForComparison(family));

    public static ChemistryPolicyEvaluation Evaluate(string title, string? entity, string? processType, KeywordRuleSnapshot keywordRules)
    {
        var scored = EvaluateScored(title, entity, processType, null, keywordRules);
        return new ChemistryPolicyEvaluation(scored.IsVisible, scored.Reasons);
    }

    public static ChemistryScoredEvaluation EvaluateScored(
        string title,
        string? entity,
        string? processType,
        string? rawPayloadText,
        KeywordRuleSnapshot keywordRules)
    {
        var normalizedPrimaryHaystack = TextNormalization.NormalizeForComparison($"{title} {entity ?? string.Empty} {processType ?? string.Empty}");
        var normalizedHaystack = TextNormalization.NormalizeForComparison($"{normalizedPrimaryHaystack} {rawPayloadText ?? string.Empty}");
        var reasons = new List<string>();
        var definition = CompiledDefinition.Value;

        var includeHits = keywordRules.IncludeKeywords
            .Where(keyword => normalizedHaystack.Contains(keyword, StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(keyword => keyword, StringComparer.Ordinal)
            .ToArray();
        var chemicalIncludeHits = includeHits
            .Where(keyword => keywordRules.FamilyByKeyword.TryGetValue(keyword, out var family)
                ? IsChemicalKeywordFamily(family)
                : IsChemicalKeywordFamily(null))
            .ToArray();
        var allExcludeHits = keywordRules.ExcludeKeywords
            .Where(keyword =>
            {
                var excludeHaystack = keywordRules.FamilyByKeyword.TryGetValue(keyword, out var family)
                    && TextNormalization.NormalizeForComparison(family) == "medico"
                        ? normalizedPrimaryHaystack
                        : normalizedHaystack;
                return excludeHaystack.Contains(keyword, StringComparison.Ordinal);
            })
            .Distinct(StringComparer.Ordinal)
            .OrderBy(keyword => keyword, StringComparer.Ordinal)
            .ToArray();
        var excludeHits = allExcludeHits
            .Where(keyword => !ShouldSuppressGenericExclude(keyword, keywordRules, chemicalIncludeHits.Length))
            .ToArray();
        var suppressedExcludeHits = allExcludeHits
            .Except(excludeHits, StringComparer.Ordinal)
            .ToArray();

        if (excludeHits.Length > 0)
        {
            reasons.Add($"Coincide con palabra excluida: {excludeHits[0]}.");
        }

        if (suppressedExcludeHits.Length > 0)
        {
            reasons.Add($"Ignora exclusion generica por contexto quimico: {suppressedExcludeHits[0]}.");
        }

        if (chemicalIncludeHits.Length == 0)
        {
            if (includeHits.Length > 0)
            {
                reasons.Add($"Coincide con palabra include no quimica: {includeHits[0]}.");
            }

            reasons.Add("No coincide con ninguna palabra clave include quimica activa.");
        }

        var isStrictExcluded = MatchesAny(normalizedPrimaryHaystack, definition.StrictExcludeSignals);
        var isMedicalExcluded = MatchesAny(normalizedPrimaryHaystack, definition.MedicalExcludeSignals);
        var isPharmaExcluded = MatchesAny(normalizedPrimaryHaystack, definition.PharmaSignals);
        if (isStrictExcluded)
        {
            reasons.Add("Excluido automaticamente por politica: señales no quimicas.");
        }

        if (isMedicalExcluded)
        {
            reasons.Add("Excluido automaticamente por politica: señales medicas.");
        }

        if (isPharmaExcluded)
        {
            reasons.Add("Excluido automaticamente por politica: señales farmaceuticas.");
        }

        var autoExcluded = isStrictExcluded || isMedicalExcluded || isPharmaExcluded;
        var includeWeight = chemicalIncludeHits.Sum(keywordRules.GetIncludeWeight);
        var excludeWeight = excludeHits.Sum(keywordRules.GetExcludeWeight);
        var baseScore = Math.Clamp((int)Math.Round((includeWeight - excludeWeight) * 25m), 0, 100);
        var isVisible = chemicalIncludeHits.Length > 0 && excludeHits.Length == 0 && !autoExcluded;
        var matchScore = isVisible ? Math.Max(60, baseScore) : Math.Min(baseScore, 40);
        var recommendation = isVisible ? "revisar" : "descartar";

        return new ChemistryScoredEvaluation(
            isVisible,
            reasons,
            chemicalIncludeHits,
            excludeHits,
            matchScore,
            recommendation);
    }

    private static bool ShouldSuppressGenericExclude(string keyword, KeywordRuleSnapshot keywordRules, int chemicalIncludeCount)
    {
        if (chemicalIncludeCount <= 0)
        {
            return false;
        }

        if (!SuppressibleGenericExcludeKeywords.Contains(keyword))
        {
            return false;
        }

        return keywordRules.FamilyByKeyword.TryGetValue(keyword, out var family)
            && TextNormalization.NormalizeForComparison(family) == "ruido";
    }

    public static bool ShouldDisplay(string title, string? entity, string? processType, KeywordRuleSnapshot keywordRules)
        => Evaluate(title, entity, processType, keywordRules).IsVisible;

    public static IReadOnlyList<string> ResolveWinningAreas(IEnumerable<string> keywords, IReadOnlyDictionary<string, string> familyByKeyword)
        => keywords
            .Select(TextNormalization.NormalizeForComparison)
            .Where(keyword => familyByKeyword.TryGetValue(keyword, out _))
            .Select(keyword => familyByKeyword[keyword])
            .Where(family => !string.IsNullOrWhiteSpace(family))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static CompiledChemistryPolicyDefinition LoadCompiledDefinition()
    {
        var path = ResolvePolicyPath();
        using var stream = File.OpenRead(path);
        var definition = JsonSerializer.Deserialize<ChemistryPolicyDefinition>(stream, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? throw new InvalidOperationException($"No se pudo cargar la politica de quimica desde {path}.");

        return new CompiledChemistryPolicyDefinition(
            Compile(definition.SupplySignals),
            Compile(definition.LaboratoryEquipmentSignals),
            Compile(definition.ChemistryContextSignals),
            Compile(definition.StrictExcludeSignals),
            Compile(definition.MedicalExcludeSignals),
            Compile(definition.PharmaSignals),
            definition.TypeScoreRules
                .Select(rule => new TypeScoreRule(new Regex(rule.Pattern, RegexOptions.CultureInvariant | RegexOptions.Compiled), rule.Score))
                .ToArray(),
            definition.DefaultTypeScore,
            definition.EmptyTypeScore,
            definition.MaxTaxonomyScore,
            definition.KeywordWeightMultiplier,
            definition.HeuristicIncludeBonus,
            definition.NonChemicalKeywordFamilies
                .Select(TextNormalization.NormalizeForComparison)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToHashSet(StringComparer.Ordinal));
    }

    private static string ResolvePolicyPath()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "config", "chemistry-policy.json"),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "config", "chemistry-policy.json"),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "config", "chemistry-policy.json"),
            Path.Combine(AppContext.BaseDirectory, "..", "config", "chemistry-policy.json"),
        }
        .Select(Path.GetFullPath);

        return candidates.FirstOrDefault(File.Exists)
            ?? throw new FileNotFoundException("No se encontro config/chemistry-policy.json.");
    }

    private static Regex[] Compile(IEnumerable<string> patterns)
        => patterns
            .Where(pattern => !string.IsNullOrWhiteSpace(pattern))
            .Select(pattern => new Regex(pattern, RegexOptions.CultureInvariant | RegexOptions.Compiled))
            .ToArray();

    private static bool MatchesAny(string value, IEnumerable<Regex> patterns)
        => patterns.Any(pattern => pattern.IsMatch(value));

    private static int ResolveTypeScore(string normalizedProcessType, CompiledChemistryPolicyDefinition definition)
    {
        if (string.IsNullOrWhiteSpace(normalizedProcessType))
        {
            return definition.EmptyTypeScore;
        }

        foreach (var rule in definition.TypeScoreRules)
        {
            if (rule.Pattern.IsMatch(normalizedProcessType))
            {
                return rule.Score;
            }
        }

        return definition.DefaultTypeScore;
    }

    private static int ClampScore(int score, int maxTaxonomyScore)
        => Math.Clamp(score, 0, maxTaxonomyScore);
}

internal sealed record ChemistryPolicyEvaluation(
    bool IsVisible,
    IReadOnlyList<string> Reasons);

internal sealed record ChemistryScoredEvaluation(
    bool IsVisible,
    IReadOnlyList<string> Reasons,
    IReadOnlyList<string> IncludeHits,
    IReadOnlyList<string> ExcludeHits,
    decimal MatchScore,
    string Recommendation);

internal sealed record KeywordRuleSnapshot(
    IReadOnlyList<string> IncludeKeywords,
    IReadOnlyList<string> ExcludeKeywords,
    IReadOnlyDictionary<string, string> FamilyByKeyword,
    IReadOnlyDictionary<string, decimal> IncludeWeights,
    IReadOnlyDictionary<string, decimal> ExcludeWeights)
{
    public decimal GetIncludeWeight(string keyword)
        => IncludeWeights.TryGetValue(keyword, out var weight) ? weight : 1m;

    public decimal GetExcludeWeight(string keyword)
        => ExcludeWeights.TryGetValue(keyword, out var weight) ? weight : 1m;
}

internal sealed record ChemistryPolicyDefinition(
    IReadOnlyList<string> SupplySignals,
    IReadOnlyList<string> LaboratoryEquipmentSignals,
    IReadOnlyList<string> ChemistryContextSignals,
    IReadOnlyList<string> StrictExcludeSignals,
    IReadOnlyList<string> MedicalExcludeSignals,
    IReadOnlyList<string> PharmaSignals,
    IReadOnlyList<TypeScoreRuleDefinition> TypeScoreRules,
    int DefaultTypeScore,
    int EmptyTypeScore,
    int MaxTaxonomyScore,
    int KeywordWeightMultiplier,
    int HeuristicIncludeBonus,
    IReadOnlyList<string> NonChemicalKeywordFamilies);

internal sealed record TypeScoreRuleDefinition(
    string Pattern,
    int Score);

internal sealed record CompiledChemistryPolicyDefinition(
    IReadOnlyList<Regex> SupplySignals,
    IReadOnlyList<Regex> LaboratoryEquipmentSignals,
    IReadOnlyList<Regex> ChemistryContextSignals,
    IReadOnlyList<Regex> StrictExcludeSignals,
    IReadOnlyList<Regex> MedicalExcludeSignals,
    IReadOnlyList<Regex> PharmaSignals,
    IReadOnlyList<TypeScoreRule> TypeScoreRules,
    int DefaultTypeScore,
    int EmptyTypeScore,
    int MaxTaxonomyScore,
    int KeywordWeightMultiplier,
    int HeuristicIncludeBonus,
    IReadOnlySet<string> NonChemicalKeywordFamilies);

internal sealed record TypeScoreRule(
    Regex Pattern,
    int Score);
