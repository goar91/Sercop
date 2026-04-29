using System.Text.Json;

namespace backend;

public sealed partial class CrmRepository
{
    private ClassificationSnapshot BuildClassificationSnapshot(
        string source,
        string? processCode,
        string? tipo,
        string title,
        string? entity,
        string? rawPayloadText,
        string? existingCaptureScope,
        KeywordRuleSnapshot keywordRules)
    {
        var category = OpportunityProcessCategory.Resolve(source, tipo, processCode);
        var chemistryEvaluation = ChemistryOpportunityPolicy.EvaluateScored(title, entity, tipo, rawPayloadText, keywordRules);
        var captureScope = ResolveCaptureScope(existingCaptureScope, category);
        var modalityReason = BuildModalityReason(source, processCode, tipo, category);
        var chemistryReasons = chemistryEvaluation.Reasons.ToArray();
        var allReasons = modalityReason is null
            ? chemistryReasons
            : new[] { modalityReason }.Concat(chemistryReasons).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

        var payload = JsonSerializer.Serialize(new
        {
            processCategory = category,
            captureScope = captureScope,
            isChemistryCandidate = chemistryEvaluation.IsVisible,
            reasons = allReasons,
            chemistry = new
            {
                matchScore = chemistryEvaluation.MatchScore,
                recommendation = chemistryEvaluation.Recommendation,
                includeHits = chemistryEvaluation.IncludeHits,
                excludeHits = chemistryEvaluation.ExcludeHits,
            }
        });

        return new ClassificationSnapshot(
            category,
            captureScope,
            chemistryEvaluation.IsVisible,
            allReasons,
            payload,
            chemistryEvaluation.IncludeHits.ToArray(),
            chemistryEvaluation.MatchScore,
            chemistryEvaluation.Recommendation);
    }

    private static string ResolveStoredProcessCategory(string? storedCategory, string source, string? tipo, string? processCode)
    {
        var normalizedStored = OpportunityProcessCategory.NormalizeFilter(storedCategory);
        return normalizedStored == OpportunityProcessCategory.All
            ? OpportunityProcessCategory.Resolve(source, tipo, processCode)
            : normalizedStored;
    }

    private static string ResolveCaptureScope(string? existingCaptureScope, string category)
    {
        var normalized = NormalizeNullableText(existingCaptureScope);
        if (!string.IsNullOrWhiteSpace(normalized))
        {
            return normalized!;
        }

        return category switch
        {
            OpportunityProcessCategory.Infimas => "infimas",
            OpportunityProcessCategory.Nco => "nco",
            OpportunityProcessCategory.Sie => "sie",
            OpportunityProcessCategory.Re => "re",
            _ => "all_public",
        };
    }

    private static string? BuildModalityReason(string source, string? processCode, string? tipo, string category)
    {
        var normalizedSource = NormalizeNullableText(source)?.ToLowerInvariant() ?? string.Empty;
        var normalizedCode = NormalizeNullableText(processCode) ?? string.Empty;
        var normalizedType = NormalizeNullableText(tipo) ?? string.Empty;

        return category switch
        {
            OpportunityProcessCategory.Infimas when normalizedCode.StartsWith("NIC-", StringComparison.OrdinalIgnoreCase)
                => $"Clasificado como ínfimas por el código {normalizedCode}.",
            OpportunityProcessCategory.Nco when normalizedCode.StartsWith("NC-", StringComparison.OrdinalIgnoreCase)
                => $"Clasificado como necesidades de contratación por el código {normalizedCode}.",
            OpportunityProcessCategory.Nco when normalizedSource == "nco"
                => $"Clasificado como necesidades de contratación por tipo {normalizedType}.",
            OpportunityProcessCategory.Sie when normalizedCode.StartsWith("SIE-", StringComparison.OrdinalIgnoreCase)
                => $"Clasificado como subasta inversa por el código {normalizedCode}.",
            OpportunityProcessCategory.Sie
                => $"Clasificado como subasta inversa por tipo {normalizedType}.",
            OpportunityProcessCategory.Re when normalizedCode.StartsWith("RE-", StringComparison.OrdinalIgnoreCase)
                => $"Clasificado como régimen especial por el código {normalizedCode}.",
            OpportunityProcessCategory.Re
                => $"Clasificado como régimen especial por tipo {normalizedType}.",
            _ => "Clasificado como otros procesos públicos por modalidad distinta a NIC, NC, SIE o RE."
        };
    }

    private static IReadOnlyList<string> ParseClassificationReasons(string? classificationPayloadText)
    {
        if (string.IsNullOrWhiteSpace(classificationPayloadText))
        {
            return Array.Empty<string>();
        }

        try
        {
            using var document = JsonDocument.Parse(classificationPayloadText);
            if (!document.RootElement.TryGetProperty("reasons", out var reasonsElement)
                || reasonsElement.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<string>();
            }

            return reasonsElement
                .EnumerateArray()
                .Select(element => element.GetString())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Cast<string>()
                .ToArray();
        }
        catch (JsonException)
        {
            return Array.Empty<string>();
        }
    }

    private sealed record ClassificationSnapshot(
        string ProcessCategory,
        string CaptureScope,
        bool IsChemistryCandidate,
        IReadOnlyList<string> Reasons,
        string PayloadJson,
        IReadOnlyList<string> KeywordsHit,
        decimal MatchScore,
        string Recommendation);
}
