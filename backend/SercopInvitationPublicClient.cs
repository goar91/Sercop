using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace backend;

public sealed record PublicInvitationVerificationResult(
    bool IsInvited,
    string? MatchedSupplierName,
    string? PublicProcessCode,
    string? EvidenceUrl,
    string? Notes
);

public sealed class SercopInvitationPublicClient(HttpClient httpClient)
{
    private const string ProcessSearchPageUrl = "https://www.compraspublicas.gob.ec/ProcesoContratacion/compras/PC/buscarProceso.cpe";
    private const string InvitationReportBaseUrl = "https://www.compraspublicas.gob.ec/ProcesoContratacion/compras/IV/ReporteInvitaciones.cpe";
    private const string PublicServiceUrl = "https://www.compraspublicas.gob.ec/ProcesoContratacion/compras/servicio/interfazWeb.php";

    private static readonly Regex HiddenInputRegex = new(
        "<input[^>]+name=\"(?<name>[^\"]+)\"[^>]+value=\"(?<value>[^\"]*)\"",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly HashSet<string> SearchStopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "adquisicion", "adquisición", "para", "del", "las", "los", "con", "por", "una", "unos",
        "unas", "hospital", "general", "reactivos", "insumos", "servicio", "tecnologico", "tecnológico",
        "apoyo", "clinico", "clínico", "laboratorio", "meses", "ano", "año", "area", "área", "hdm"
    };

    public async Task<PublicInvitationVerificationResult> VerifyInvitationAsync(
        string? processCode,
        string title,
        string? entity,
        string? processType,
        string invitedCompanySearch,
        string? invitedCompanyRuc,
        CancellationToken cancellationToken)
    {
        var publicProcess = await FindPublicProcessAsync(processCode, title, entity, processType, cancellationToken);
        if (publicProcess is null)
        {
            return new PublicInvitationVerificationResult(false, null, null, null, null);
        }

        var context = await LoadInvitationContextAsync(publicProcess.InternalToken, cancellationToken);
        if (context is null)
        {
            return new PublicInvitationVerificationResult(false, null, publicProcess.Code, null, null);
        }

        var matches = await SearchInvitationMatchesAsync(context, invitedCompanySearch, invitedCompanyRuc, cancellationToken);
        if (matches.Count == 0)
        {
            return new PublicInvitationVerificationResult(false, null, publicProcess.Code, context.ReportUrl, null);
        }

        var supplier = matches[0];
        var notes = $"Proveedor confirmado en reporte SERCOP: {supplier.SupplierName}. Estado {supplier.Status}.";
        return new PublicInvitationVerificationResult(
            true,
            supplier.SupplierName,
            publicProcess.Code,
            context.ReportUrl,
            notes);
    }

    private async Task<PublicProcessCandidate?> FindPublicProcessAsync(
        string? processCode,
        string title,
        string? entity,
        string? processType,
        CancellationToken cancellationToken)
    {
        var codeCandidates = BuildProcessCodeCandidates(processCode);
        foreach (var codeCandidate in codeCandidates)
        {
            var byCode = await SearchPublicProcessesAsync(codeCandidate, null, processType, cancellationToken);
            var exactMatch = byCode.FirstOrDefault(candidate =>
                codeCandidates.Any(expected => string.Equals(NormalizeSearchText(expected), NormalizeSearchText(candidate.Code), StringComparison.Ordinal)));

            if (exactMatch is not null)
            {
                return exactMatch;
            }
        }

        var keywordSearch = BuildKeywordSearch(title, entity);
        if (string.IsNullOrWhiteSpace(keywordSearch))
        {
            return null;
        }

        var keywordCandidates = await SearchPublicProcessesAsync(null, keywordSearch, processType, cancellationToken);
        return SelectBestPublicCandidate(keywordCandidates, codeCandidates, title, entity);
    }

    private async Task<List<PublicProcessCandidate>> SearchPublicProcessesAsync(
        string? processCode,
        string? keywords,
        string? processType,
        CancellationToken cancellationToken)
    {
        await InitializePublicSearchSessionAsync(cancellationToken);

        var form = new Dictionary<string, string>
        {
            ["__class"] = "SolicitudCompra",
            ["__action"] = "buscarProcesoxEntidad",
            ["txtPalabrasClaves"] = keywords ?? string.Empty,
            ["txtEntidadContratante"] = string.Empty,
            ["cmbEntidad"] = string.Empty,
            ["txtCodigoTipoCompra"] = MapPublicProcessType(processType) ?? string.Empty,
            ["txtCodigoProceso"] = processCode ?? string.Empty,
            ["f_inicio"] = DateTime.UtcNow.AddYears(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["f_fin"] = DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["image"] = string.Empty,
            ["captccc2"] = "2",
            ["paginaActual"] = "0",
            ["estado"] = string.Empty,
            ["trx"] = string.Empty,
        };

        using var response = await PostFormAsync(PublicServiceUrl, form, cancellationToken);
        var payload = GetXJsonPayload(response);
        if (string.IsNullOrWhiteSpace(payload))
        {
            return [];
        }

        using var document = JsonDocument.Parse(payload);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var candidates = new List<PublicProcessCandidate>();
        foreach (var row in document.RootElement.EnumerateArray())
        {
            var code = GetString(row, "c");
            var processTitle = GetString(row, "d");
            var processEntity = GetString(row, "r");
            var internalToken = GetString(row, "i");

            if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(processTitle) || string.IsNullOrWhiteSpace(internalToken))
            {
                continue;
            }

            candidates.Add(new PublicProcessCandidate(code.Trim(), processTitle.Trim(), processEntity?.Trim(), internalToken.Trim()));
        }

        return candidates;
    }

    private async Task InitializePublicSearchSessionAsync(CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync(ProcessSearchPageUrl, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private async Task<InvitationContext?> LoadInvitationContextAsync(string solicitationToken, CancellationToken cancellationToken)
    {
        var reportUrl = $"{InvitationReportBaseUrl}?solicitud={Uri.EscapeDataString(solicitationToken)}";
        using var response = await httpClient.GetAsync(reportUrl, cancellationToken);
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync(cancellationToken);

        var hiddenInputs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in HiddenInputRegex.Matches(html))
        {
            hiddenInputs[match.Groups["name"].Value] = System.Net.WebUtility.HtmlDecode(match.Groups["value"].Value);
        }

        if (!hiddenInputs.TryGetValue("idSoliCompra", out var rawId) || !int.TryParse(rawId, out var idSoliCompra))
        {
            return null;
        }

        hiddenInputs.TryGetValue("tipoProceso", out var tipoProceso);
        return new InvitationContext(idSoliCompra, tipoProceso ?? string.Empty, reportUrl);
    }

    private async Task<List<InvitationSupplierMatch>> SearchInvitationMatchesAsync(
        InvitationContext context,
        string invitedCompanySearch,
        string? invitedCompanyRuc,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(invitedCompanyRuc))
        {
            var byRuc = await SearchInvitationMatchesByActionAsync(
                "listarInvitadoxRucReporte",
                new Dictionary<string, string>
                {
                    ["idSoliCompra"] = context.IdSoliCompra.ToString(CultureInfo.InvariantCulture),
                    ["ruc"] = invitedCompanyRuc.Trim(),
                },
                cancellationToken);

            if (byRuc.Count > 0)
            {
                return byRuc;
            }
        }

        if (string.IsNullOrWhiteSpace(invitedCompanySearch))
        {
            return [];
        }

        return await SearchInvitationMatchesByActionAsync(
            "listarInvitadoxRazonSocialReporte",
            new Dictionary<string, string>
            {
                ["idSoliCompra"] = context.IdSoliCompra.ToString(CultureInfo.InvariantCulture),
                ["razonSocial"] = invitedCompanySearch.Trim(),
            },
            cancellationToken);
    }

    private async Task<List<InvitationSupplierMatch>> SearchInvitationMatchesByActionAsync(
        string action,
        Dictionary<string, string> form,
        CancellationToken cancellationToken)
    {
        form["__class"] = "TcomInvitacion";
        form["__action"] = action;

        using var response = await PostFormAsync(PublicServiceUrl, form, cancellationToken);
        var payload = GetXJsonPayload(response);
        if (string.IsNullOrWhiteSpace(payload))
        {
            return [];
        }

        using var document = JsonDocument.Parse(payload);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var matches = new List<InvitationSupplierMatch>();
        foreach (var row in document.RootElement.EnumerateArray())
        {
            if (!row.TryGetProperty("i", out var invitation) || invitation.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var supplierName = GetString(invitation, "r");
            if (string.IsNullOrWhiteSpace(supplierName))
            {
                continue;
            }

            matches.Add(new InvitationSupplierMatch(
                supplierName.Trim(),
                GetString(invitation, "c"),
                GetString(invitation, "f"),
                GetString(invitation, "h"),
                GetString(invitation, "l")));
        }

        return matches;
    }

    private static PublicProcessCandidate? SelectBestPublicCandidate(
        IReadOnlyList<PublicProcessCandidate> candidates,
        IReadOnlyCollection<string> codeCandidates,
        string title,
        string? entity)
    {
        if (candidates.Count == 0)
        {
            return null;
        }

        var normalizedCodes = codeCandidates.Select(NormalizeSearchText).Where(value => value.Length > 0).ToHashSet(StringComparer.Ordinal);
        PublicProcessCandidate? best = null;
        var bestScore = double.MinValue;

        foreach (var candidate in candidates)
        {
            var score = 0d;
            var normalizedCandidateCode = NormalizeSearchText(candidate.Code);

            if (normalizedCodes.Contains(normalizedCandidateCode))
            {
                score += 500;
            }
            else if (normalizedCodes.Any(code => normalizedCandidateCode.Contains(code, StringComparison.Ordinal)))
            {
                score += 200;
            }

            score += TokenOverlapScore(title, candidate.Title) * 100;
            score += TokenOverlapScore(entity, candidate.Entity) * 40;

            if (score > bestScore)
            {
                bestScore = score;
                best = candidate;
            }
        }

        return bestScore > 0 ? best : null;
    }

    private static List<string> BuildProcessCodeCandidates(string? processCode)
    {
        if (string.IsNullOrWhiteSpace(processCode))
        {
            return [];
        }

        var candidates = new List<string>();
        AddProcessCodeCandidate(candidates, processCode);
        AddProcessCodeCandidate(candidates, NormalizePublicProcessCode(processCode));
        AddProcessCodeCandidate(candidates, ExtractProcessCodeFromOcid(processCode));
        AddProcessCodeCandidate(candidates, NormalizePublicProcessCode(ExtractProcessCodeFromOcid(processCode)));
        return candidates;
    }

    private static void AddProcessCodeCandidate(List<string> candidates, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        var trimmed = value.Trim();
        if (!candidates.Any(existing => string.Equals(existing, trimmed, StringComparison.OrdinalIgnoreCase)))
        {
            candidates.Add(trimmed);
        }
    }

    private static string BuildKeywordSearch(string title, string? entity)
    {
        var titleTokens = Tokenize(title).Where(token => token.Length >= 4 && !SearchStopWords.Contains(token)).Distinct(StringComparer.OrdinalIgnoreCase).Take(6);
        var entityTokens = Tokenize(entity).Where(token => token.Length >= 5 && !SearchStopWords.Contains(token)).Distinct(StringComparer.OrdinalIgnoreCase).Take(2);
        return string.Join(' ', titleTokens.Concat(entityTokens));
    }

    private static string? MapPublicProcessType(string? processType)
        => NormalizeSearchText(processType).Contains("SUBASTA INVERSA", StringComparison.Ordinal) ? "386" : string.Empty;

    private static IEnumerable<string> Tokenize(string? value)
        => NormalizeSearchText(value).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static double TokenOverlapScore(string? left, string? right)
    {
        var leftTokens = Tokenize(left).ToHashSet(StringComparer.Ordinal);
        var rightTokens = Tokenize(right).ToHashSet(StringComparer.Ordinal);
        if (leftTokens.Count == 0 || rightTokens.Count == 0)
        {
            return 0;
        }

        var overlap = leftTokens.Intersect(rightTokens, StringComparer.Ordinal).Count();
        return overlap == 0 ? 0 : (double)overlap / Math.Min(leftTokens.Count, rightTokens.Count);
    }

    private static string NormalizeSearchText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(normalized.Length);
        foreach (var ch in normalized)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (category == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            builder.Append(char.IsLetterOrDigit(ch) ? char.ToUpperInvariant(ch) : ' ');
        }

        return Regex.Replace(builder.ToString(), @"\s+", " ").Trim();
    }

    private static string? NormalizePublicProcessCode(string? rawCode)
    {
        if (string.IsNullOrWhiteSpace(rawCode))
        {
            return null;
        }

        var normalized = ExtractProcessCodeFromOcid(rawCode) ?? rawCode.Trim();
        return Regex.IsMatch(normalized, @"-\d{3,}$", RegexOptions.CultureInvariant)
            ? Regex.Replace(normalized, @"-\d{3,}$", string.Empty, RegexOptions.CultureInvariant).Trim()
            : normalized.Trim();
    }

    private static string? ExtractProcessCodeFromOcid(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : Regex.Replace(value, "^ocds-[^-]+-", string.Empty, RegexOptions.IgnoreCase);

    private async Task<HttpResponseMessage> PostFormAsync(string url, IReadOnlyDictionary<string, string> form, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new FormUrlEncodedContent(form)
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));

        var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        return response;
    }

    private static string? GetXJsonPayload(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("X-JSON", out var values))
        {
            return null;
        }

        var raw = values.FirstOrDefault()?.Trim();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        return raw.StartsWith('(') && raw.EndsWith(')') ? raw[1..^1] : raw;
    }

    private static string? GetString(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var value) && value.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined
            ? value.ToString()
            : null;

    private sealed record PublicProcessCandidate(string Code, string Title, string? Entity, string InternalToken);

    private sealed record InvitationContext(int IdSoliCompra, string TipoProceso, string ReportUrl);

    private sealed record InvitationSupplierMatch(string SupplierName, string? Status, string? InvitedAt, string? RupStatus, string? Location);
}
