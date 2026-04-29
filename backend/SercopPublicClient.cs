using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace backend;

public sealed record ImportedOpportunityCandidate(
    string Source,
    string ExternalId,
    string OcidOrNic,
    string ProcessCode,
    string Titulo,
    string? Entidad,
    string? Tipo,
    DateTimeOffset? FechaPublicacion,
    DateTimeOffset? FechaLimite,
    decimal? MontoRef,
    string Moneda,
    string Url,
    string RawPayloadJson
);

public sealed class SercopPublicClient(HttpClient httpClient)
{
    private const string OcdsSearchBaseUrl = "https://datosabiertos.compraspublicas.gob.ec/PLATAFORMA/api/search_ocds";
    private const string OcdsRecordBaseUrl = "https://datosabiertos.compraspublicas.gob.ec/PLATAFORMA/api/record";
    private const string NcoListBaseUrl = "https://www.compraspublicas.gob.ec/ProcesoContratacion/compras/NCO/NCORetornaRegistros.cpe?lot=1&draw=1";
    private const string NcoListPostUrl = "https://www.compraspublicas.gob.ec/ProcesoContratacion/compras/NCO/NCORetornaRegistros.cpe?lot=1";
    private const string PortalSearchPageUrl = "https://www.compraspublicas.gob.ec/ProcesoContratacion/compras/PC/buscarProceso.cpe";
    private const string PortalServiceUrl = "https://www.compraspublicas.gob.ec/ProcesoContratacion/compras/servicio/interfazWeb.php";
    private const string PortalInfoUrlBase = "https://www.compraspublicas.gob.ec/ProcesoContratacion/compras/PC/informacionProcesoContratacion2.cpe?id=";
    private const int NcoPageSize = 500;
    private const int NcoMaxRows = 10000;

    public async Task<ImportedOpportunityCandidate?> ResolveByCodeAsync(string code, int fallbackYear, CancellationToken cancellationToken)
    {
        var trimmedCode = code.Trim();
        if (string.IsNullOrWhiteSpace(trimmedCode))
        {
            return null;
        }

        if (trimmedCode.StartsWith("NIC-", StringComparison.OrdinalIgnoreCase))
        {
            return await ResolveNcoAsync(trimmedCode, cancellationToken);
        }

        return await ResolveOcdsAsync(trimmedCode, ExtractYear(trimmedCode) ?? fallbackYear, cancellationToken)
            ?? await ResolvePortalPcAsync(trimmedCode, cancellationToken)
            ?? await ResolveNcoAsync(trimmedCode, cancellationToken);
    }

    private async Task<ImportedOpportunityCandidate?> ResolveOcdsAsync(string code, int year, CancellationToken cancellationToken)
    {
        var searchUrl = $"{OcdsSearchBaseUrl}?year={year}&search={Uri.EscapeDataString(code)}&page=1";
        await using var searchStream = await httpClient.GetStreamAsync(searchUrl, cancellationToken);
        using var searchDocument = await JsonDocument.ParseAsync(searchStream, cancellationToken: cancellationToken);

        if (!searchDocument.RootElement.TryGetProperty("data", out var dataElement) || dataElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var row in dataElement.EnumerateArray())
        {
            var ocid = GetString(row, "ocid");
            var title = GetString(row, "title");
            if (!MatchesCode(code, ocid, title))
            {
                continue;
            }

            var resolvedOcid = ocid ?? code;
            var recordUrl = $"{OcdsRecordBaseUrl}?ocid={Uri.EscapeDataString(resolvedOcid)}";
            await using var recordStream = await httpClient.GetStreamAsync(recordUrl, cancellationToken);
            using var recordDocument = await JsonDocument.ParseAsync(recordStream, cancellationToken: cancellationToken);

            var release = GetPrimaryRelease(recordDocument.RootElement);
            if (release is null)
            {
                continue;
            }

            var entity = GetNestedString(release.Value, "buyer", "name")
                ?? GetNestedString(release.Value, "tender", "procuringEntity", "name");
            var processType = GetNestedString(release.Value, "tender", "procurementMethodDetails");
            var publishedAt = ParseDateTimeOffset(GetString(release.Value, "date"));
            var deadlineAt = ParseDateTimeOffset(GetNestedString(release.Value, "tender", "tenderPeriod", "endDate"))
                ?? ParseDateTimeOffset(GetNestedString(release.Value, "tender", "enquiryPeriod", "endDate"));
            var amount = ParseDecimal(GetNestedString(release.Value, "tender", "value", "amount"))
                ?? ParseDecimal(GetNestedString(release.Value, "awards", "0", "value", "amount"));
            var currency = GetNestedString(release.Value, "tender", "value", "currency")
                ?? GetNestedString(release.Value, "awards", "0", "value", "currency")
                ?? "USD";
            var processCode = NormalizeProcessCode(GetNestedString(release.Value, "tender", "id") ?? ExtractProcessCodeFromOcid(ocid) ?? code);
            if (string.IsNullOrWhiteSpace(processCode))
            {
                processCode = code;
            }
            var description = GetNestedString(release.Value, "tender", "description")
                ?? GetNestedString(release.Value, "planning", "rationale")
                ?? title
                ?? processCode;

            return new ImportedOpportunityCandidate(
                "ocds",
                processCode,
                resolvedOcid,
                processCode,
                description,
                entity,
                processType,
                publishedAt,
                deadlineAt,
                amount,
                currency,
                recordUrl,
                JsonSerializer.Serialize(new
                {
                    source = "ocds_import",
                    search = row,
                    record = recordDocument.RootElement
                }));
        }

        return null;
    }

    private async Task<ImportedOpportunityCandidate?> ResolvePortalPcAsync(string code, CancellationToken cancellationToken)
    {
        var trimmedCode = code.Trim();
        if (string.IsNullOrWhiteSpace(trimmedCode))
        {
            return null;
        }

        var normalizedCode = NormalizeProcessCode(trimmedCode);
        if (string.IsNullOrWhiteSpace(normalizedCode))
        {
            return null;
        }

        var today = EcuadorTime.Now().Date;
        var form = new Dictionary<string, string>
        {
            ["__class"] = "SolicitudCompra",
            ["__action"] = "buscarProcesoxEntidad",
            ["txtPalabrasClaves"] = string.Empty,
            ["txtEntidadContratante"] = string.Empty,
            ["cmbEntidad"] = string.Empty,
            ["txtCodigoTipoCompra"] = trimmedCode.StartsWith("SIE-", StringComparison.OrdinalIgnoreCase) ? "386" : string.Empty,
            ["txtCodigoProceso"] = trimmedCode,
            ["f_inicio"] = today.AddYears(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["f_fin"] = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["image"] = string.Empty,
            ["captccc2"] = "2",
            ["paginaActual"] = "0",
            ["estado"] = string.Empty,
            ["trx"] = string.Empty,
        };

        string? payload;
        using (var response = await PostPortalFormAsync(form, cancellationToken))
        {
            payload = GetXJsonPayload(response);
            if (string.IsNullOrWhiteSpace(payload))
            {
                payload = await response.Content.ReadAsStringAsync(cancellationToken);
            }
        }

        payload = payload?.Trim();
        if (string.IsNullOrWhiteSpace(payload))
        {
            await httpClient.GetAsync(PortalSearchPageUrl, cancellationToken);
            using var retryResponse = await PostPortalFormAsync(form, cancellationToken);
            payload = GetXJsonPayload(retryResponse);
            if (string.IsNullOrWhiteSpace(payload))
            {
                payload = await retryResponse.Content.ReadAsStringAsync(cancellationToken);
            }

            payload = payload?.Trim();
            if (string.IsNullOrWhiteSpace(payload))
            {
                return null;
            }
        }

        if (payload.StartsWith('(') && payload.EndsWith(')'))
        {
            payload = payload[1..^1].Trim();
        }

        using var document = JsonDocument.Parse(payload);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var row in document.RootElement.EnumerateArray())
        {
            var rowCode = NormalizeProcessCode(GetString(row, "c"));
            if (!string.Equals(rowCode, normalizedCode, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var portalToken = GetString(row, "i");
            var title = GetString(row, "d") ?? rowCode ?? trimmedCode;
            var entity = GetString(row, "r");
            var date = GetString(row, "f");
            var amount = ParseDecimal(GetString(row, "p"));

            var infoUrl = string.IsNullOrWhiteSpace(portalToken)
                ? string.Empty
                : PortalInfoUrlBase + Uri.EscapeDataString(portalToken.Trim());

            return new ImportedOpportunityCandidate(
                "ocds",
                rowCode ?? normalizedCode,
                rowCode ?? normalizedCode,
                rowCode ?? normalizedCode,
                title,
                entity,
                ResolvePortalProcessType(rowCode ?? normalizedCode),
                ParseDateTimeOffset(date),
                null,
                amount,
                "USD",
                infoUrl,
                JsonSerializer.Serialize(new
                {
                    source = "portal_pc_import",
                    searched_code = trimmedCode,
                    portal_row = row,
                }));
        }

        return null;
    }

    private async Task<HttpResponseMessage> PostPortalFormAsync(
        IReadOnlyDictionary<string, string> form,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, PortalServiceUrl)
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

    private static string ResolvePortalProcessType(string code)
        => NormalizeProcessCode(code).StartsWith("SIE-", StringComparison.OrdinalIgnoreCase)
            ? "Subasta Inversa Electrónica"
            : string.IsNullOrWhiteSpace(code) ? string.Empty : code.Split('-', 2, StringSplitOptions.TrimEntries)[0];

    private static string NormalizeProcessCode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return value.Trim();
    }

    private async Task<ImportedOpportunityCandidate?> ResolveNcoAsync(string code, CancellationToken cancellationToken)
    {
        if (code.StartsWith("NC-", StringComparison.OrdinalIgnoreCase))
        {
            var contractingCandidate = await ResolveNcoContractingAsync(code, cancellationToken);
            if (contractingCandidate is not null)
            {
                return contractingCandidate;
            }
        }

        for (var start = 0; start < NcoMaxRows; start += NcoPageSize)
        {
            var pageUrl = $"{NcoListBaseUrl}&draw=1&start={start}&length={NcoPageSize}";
            await using var responseStream = await httpClient.GetStreamAsync(pageUrl, cancellationToken);
            using var document = await JsonDocument.ParseAsync(responseStream, cancellationToken: cancellationToken);

            if (!document.RootElement.TryGetProperty("data", out var dataElement) || dataElement.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            if (dataElement.GetArrayLength() == 0)
            {
                break;
            }

            foreach (var row in dataElement.EnumerateArray())
            {
                var candidate = BuildNcoCandidateFromRow(row, code);
                if (candidate is not null)
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private async Task<ImportedOpportunityCandidate?> ResolveNcoContractingAsync(string code, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsync(
            NcoListPostUrl,
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["sEcho"] = "1",
                ["iDisplayStart"] = "0",
                ["iDisplayLength"] = "2000",
                ["sSearch_0"] = "54089",
                ["sSearch_5"] = "384",
                ["iSortCol_0"] = "1",
                ["sSortDir_0"] = "desc",
            }),
            cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(responseStream, cancellationToken: cancellationToken);
        if (!document.RootElement.TryGetProperty("data", out var dataElement) || dataElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var row in dataElement.EnumerateArray())
        {
            var candidate = BuildNcoCandidateFromRow(row, code);
            if (candidate is not null)
            {
                return candidate;
            }
        }

        return null;
    }

    private static ImportedOpportunityCandidate? BuildNcoCandidateFromRow(JsonElement row, string code)
    {
        var processCode = GetString(row, "codigo_contratacion");
        if (!string.Equals(processCode, code, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var detailUrl = ParseNcoDetailUrl(GetString(row, "url"));
        return new ImportedOpportunityCandidate(
            "nco",
            processCode ?? code,
            processCode ?? code,
            processCode ?? code,
            GetString(row, "objeto_contratacion") ?? processCode ?? code,
            GetString(row, "razon_social"),
            GetString(row, "tipo_necesidad"),
            ParseDateTimeOffset(GetString(row, "fecha_publicacion")),
            ParseDateTimeOffset(GetString(row, "fecha_limite_propuesta")),
            null,
            "USD",
            detailUrl,
            JsonSerializer.Serialize(new
            {
                source = "nco_import",
                list = row
            }));
    }

    private static JsonElement? GetPrimaryRelease(JsonElement root)
    {
        if (root.TryGetProperty("releases", out var releases) && releases.ValueKind == JsonValueKind.Array && releases.GetArrayLength() > 0)
        {
            return releases[0];
        }

        if (root.TryGetProperty("release", out var release) && release.ValueKind == JsonValueKind.Object)
        {
            return release;
        }

        return null;
    }

    private static bool MatchesCode(string expectedCode, string? ocid, string? title)
    {
        if (string.IsNullOrWhiteSpace(expectedCode))
        {
            return false;
        }

        if (string.Equals(expectedCode, ocid, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(expectedCode, ExtractProcessCodeFromOcid(ocid), StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(ocid) && ocid.EndsWith(expectedCode, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return string.Equals(expectedCode, title, StringComparison.OrdinalIgnoreCase);
    }

    private static string ParseNcoDetailUrl(string? anchorHtml)
    {
        var match = System.Text.RegularExpressions.Regex.Match(anchorHtml ?? string.Empty, "href=([^\\s>]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            return string.Empty;
        }

        var relativeUrl = match.Groups[1].Value.Trim('\'', '"');
        if (relativeUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            return relativeUrl;
        }

        return "https://www.compraspublicas.gob.ec/ProcesoContratacion/compras/NCO/" +
            relativeUrl
                .Replace("../NCO/", string.Empty, StringComparison.OrdinalIgnoreCase)
                .Replace("../", string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    private static int? ExtractYear(string code)
    {
        var match = System.Text.RegularExpressions.Regex.Match(code, "(20\\d{2})");
        return match.Success && int.TryParse(match.Groups[1].Value, out var year) ? year : null;
    }

    private static string? ExtractProcessCodeFromOcid(string? ocid)
        => string.IsNullOrWhiteSpace(ocid)
            ? null
            : Regex.Replace(ocid, "^ocds-[^-]+-", string.Empty, RegexOptions.IgnoreCase).Trim();

    private static string? GetString(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var value) && value.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined
            ? value.ToString()
            : null;

    private static string? GetNestedString(JsonElement element, params string[] path)
    {
        var current = element;
        foreach (var segment in path)
        {
            if (current.ValueKind == JsonValueKind.Array && int.TryParse(segment, out var index))
            {
                if (current.GetArrayLength() <= index)
                {
                    return null;
                }

                current = current[index];
                continue;
            }

            if (!current.TryGetProperty(segment, out var next))
            {
                return null;
            }

            current = next;
        }

        return current.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ? null : current.ToString();
    }

    private static DateTimeOffset? ParseDateTimeOffset(string? value)
        => EcuadorTime.ParseTimestamp(value);

    private static decimal? ParseDecimal(string? value)
        => decimal.TryParse(value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
}
