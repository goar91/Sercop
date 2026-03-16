using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace backend;

public sealed class AiAssistantService(
    HttpClient httpClient,
    IConfiguration configuration,
    CrmRepository repository)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<AssistantReplyDto> AskAsync(AssistantAskRequest request, CancellationToken cancellationToken)
    {
        var question = request.Question?.Trim();
        if (string.IsNullOrWhiteSpace(question))
        {
            throw new InvalidOperationException("La pregunta para el asistente es obligatoria.");
        }

        var module = NormalizeModule(request.Module, request.OpportunityId, request.WorkflowId);
        var context = await BuildContextAsync(module, request.OpportunityId, request.WorkflowId, cancellationToken);
        var sources = await LoadKnowledgeAsync(module, question, cancellationToken);
        var model = ChooseModel(module, question);
        var prompt = BuildPrompt(module, question, context.Summary, context.PayloadJson, sources);
        var generation = await GenerateWithFallbackAsync(model, prompt, cancellationToken);

        return new AssistantReplyDto(
            module,
            generation.Model,
            context.Summary,
            generation.Answer,
            sources.Select(source => new AssistantSourceDto(source.Label, source.Reference, source.Kind)).ToList());
    }

    private async Task<(string Summary, string PayloadJson)> BuildContextAsync(
        string module,
        long? opportunityId,
        string? workflowId,
        CancellationToken cancellationToken)
    {
        var dashboard = await repository.GetDashboardAsync(cancellationToken);

        object payload = module switch
        {
            "opportunities" => await BuildOpportunityContextAsync(dashboard, opportunityId, cancellationToken),
            "workflows" => await BuildWorkflowContextAsync(dashboard, workflowId, cancellationToken),
            "keywords" => await BuildKeywordContextAsync(dashboard, cancellationToken),
            "config" => await BuildConfigContextAsync(dashboard, cancellationToken),
            _ => await BuildDashboardContextAsync(dashboard, cancellationToken)
        };

        var summary = module switch
        {
            "opportunities" => opportunityId is null ? "Analisis del pipeline comercial." : $"Analisis de la oportunidad {opportunityId.Value}.",
            "workflows" => string.IsNullOrWhiteSpace(workflowId) ? "Analisis operativo de workflows." : $"Analisis del workflow {workflowId}.",
            "keywords" => "Analisis de reglas de palabras clave.",
            "config" => "Analisis de configuracion comercial y operativa.",
            _ => "Analisis del tablero general del CRM."
        };

        return (summary, JsonSerializer.Serialize(payload, JsonOptions));
    }

    private async Task<object> BuildDashboardContextAsync(DashboardSummaryDto dashboard, CancellationToken cancellationToken)
    {
        var workflows = await repository.GetWorkflowsAsync(cancellationToken);
        var keywords = await repository.GetKeywordRulesAsync(null, null, cancellationToken);

        return new
        {
            dashboard,
            workflows = workflows.Take(5).Select(workflow => new { workflow.Id, workflow.Name, workflow.Active, workflow.NodeCount }),
            keywordHighlights = keywords.Take(5).Select(rule => new { rule.Keyword, rule.RuleType, rule.Scope, rule.Active })
        };
    }

    private async Task<object> BuildOpportunityContextAsync(
        DashboardSummaryDto dashboard,
        long? opportunityId,
        CancellationToken cancellationToken)
    {
        var detail = opportunityId is null ? null : await repository.GetOpportunityAsync(opportunityId.Value, cancellationToken);
        var list = await repository.GetOpportunitiesAsync(null, null, null, null, false, cancellationToken);

        return new
        {
            dashboard,
            selectedOpportunity = detail is null ? null : new
            {
                detail.Id,
                detail.ProcessCode,
                detail.Titulo,
                detail.Entidad,
                detail.Tipo,
                detail.FechaLimite,
                detail.MontoRef,
                detail.Moneda,
                detail.IsInvitedMatch,
                detail.Priority,
                detail.Estado,
                detail.Recomendacion,
                detail.ZoneName,
                detail.AssignedUserName,
                detail.AiResumen,
                detail.AiEstrategiaAbastecimiento,
                documentCount = detail.Documents.Count
            },
            pipeline = list.Take(8).Select(item => new
            {
                item.Id,
                item.ProcessCode,
                item.Titulo,
                item.Entidad,
                item.MatchScore,
                item.AiScore,
                item.Estado,
                item.Priority,
                item.ZoneName,
                item.AssignedUserName
            })
        };
    }

    private async Task<object> BuildWorkflowContextAsync(
        DashboardSummaryDto dashboard,
        string? workflowId,
        CancellationToken cancellationToken)
    {
        var workflows = await repository.GetWorkflowsAsync(cancellationToken);
        var selected = string.IsNullOrWhiteSpace(workflowId)
            ? (workflows.Count > 0 ? await repository.GetWorkflowAsync(workflows[0].Id, cancellationToken) : null)
            : await repository.GetWorkflowAsync(workflowId, cancellationToken);

        return new
        {
            dashboard,
            selectedWorkflow = selected is null ? null : new
            {
                selected.Id,
                selected.Name,
                selected.Active,
                selected.Description,
                selected.NodeCount,
                nodes = selected.Nodes.Take(12).Select(node => new { node.Name, node.Type, node.Disabled })
            },
            workflows = workflows.Select(workflow => new { workflow.Id, workflow.Name, workflow.Active, workflow.NodeCount })
        };
    }

    private async Task<object> BuildKeywordContextAsync(DashboardSummaryDto dashboard, CancellationToken cancellationToken)
    {
        var keywords = await repository.GetKeywordRulesAsync(null, null, cancellationToken);
        return new
        {
            dashboard,
            keywordRules = keywords.Select(rule => new
            {
                rule.Id,
                rule.Keyword,
                rule.RuleType,
                rule.Scope,
                rule.Family,
                rule.Weight,
                rule.Active
            })
        };
    }

    private async Task<object> BuildConfigContextAsync(DashboardSummaryDto dashboard, CancellationToken cancellationToken)
    {
        var zones = await repository.GetZonesAsync(cancellationToken);
        var users = await repository.GetUsersAsync(cancellationToken);

        return new
        {
            dashboard,
            zones = zones.Select(zone => new { zone.Id, zone.Name, zone.Code, zone.Active }),
            users = users.Select(user => new { user.Id, user.FullName, user.Role, user.Active, user.ZoneName })
        };
    }

    private async Task<List<KnowledgeSource>> LoadKnowledgeAsync(string module, string question, CancellationToken cancellationToken)
    {
        var vector = await EmbedAsync(question, cancellationToken);
        var orderedCollections = module == "workflows" ? new[] { "code_kb", "sercop_docs" } : new[] { "sercop_docs", "code_kb" };
        var sources = new List<KnowledgeSource>();

        foreach (var collection in orderedCollections)
        {
            sources.AddRange(await QueryCollectionAsync(collection, vector, cancellationToken));
        }

        return sources
            .GroupBy(source => source.Reference, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Take(4)
            .ToList();
    }

    private async Task<float[]> EmbedAsync(string input, CancellationToken cancellationToken)
    {
        var model = configuration["OLLAMA_EMBED_MODEL"] ?? "nomic-embed-text";
        var payload = JsonSerializer.Serialize(new { model, input });
        using var response = await httpClient.PostAsync(
            $"{GetOllamaBaseUrl().TrimEnd('/')}/api/embed",
            new StringContent(payload, Encoding.UTF8, "application/json"),
            cancellationToken);

        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var embedding = document.RootElement.GetProperty("embeddings")[0].EnumerateArray().Select(value => value.GetSingle()).ToArray();
        return embedding;
    }

    private async Task<List<KnowledgeSource>> QueryCollectionAsync(string collection, float[] vector, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(new { query = vector, limit = 2, with_payload = true });
        using var response = await httpClient.PostAsync(
            $"{GetQdrantBaseUrl().TrimEnd('/')}/collections/{collection}/points/query",
            new ByteArrayContent(Encoding.UTF8.GetBytes(payload)) { Headers = { ContentType = new("application/json") } },
            cancellationToken);

        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        var items = new List<KnowledgeSource>();
        foreach (var point in document.RootElement.GetProperty("result").GetProperty("points").EnumerateArray())
        {
            if (!point.TryGetProperty("payload", out var payloadElement))
            {
                continue;
            }

            var reference = payloadElement.TryGetProperty("source_url", out var sourceUrl) ? sourceUrl.GetString() : null;
            var text = payloadElement.TryGetProperty("text", out var textElement) ? textElement.GetString() : null;
            if (string.IsNullOrWhiteSpace(reference) || string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            items.Add(new KnowledgeSource(Path.GetFileName(reference), reference, collection, Condense(text)));
        }

        return items;
    }

    private async Task<(string Model, string Answer)> GenerateWithFallbackAsync(string primaryModel, string prompt, CancellationToken cancellationToken)
    {
        var primaryAnswer = await GenerateAsync(primaryModel, prompt, cancellationToken);
        if (IsUsefulAnswer(primaryAnswer))
        {
            return (primaryModel, primaryAnswer);
        }

        var fallbackModel = primaryModel == (configuration["OLLAMA_CODE_MODEL"] ?? "qwen2.5-coder:0.5b")
            ? configuration["OLLAMA_GENERAL_MODEL"] ?? "qwen3:0.6b"
            : configuration["OLLAMA_CODE_MODEL"] ?? "qwen2.5-coder:0.5b";

        if (string.Equals(primaryModel, fallbackModel, StringComparison.OrdinalIgnoreCase))
        {
            return (primaryModel, primaryAnswer);
        }

        var fallbackAnswer = await GenerateAsync(fallbackModel, prompt, cancellationToken);
        return IsUsefulAnswer(fallbackAnswer)
            ? (fallbackModel, fallbackAnswer)
            : (fallbackModel, fallbackAnswer);
    }

    private async Task<string> GenerateAsync(string model, string prompt, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(new
        {
            model,
            prompt,
            stream = false,
            options = new
            {
                temperature = 0.2,
                num_predict = 260
            }
        });

        using var response = await httpClient.PostAsync(
            $"{GetOllamaBaseUrl().TrimEnd('/')}/api/generate",
            new StringContent(payload, Encoding.UTF8, "application/json"),
            cancellationToken);

        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var answer = document.RootElement.TryGetProperty("response", out var responseElement) ? responseElement.GetString() : null;
        return CleanAnswer(answer);
    }

    private static string BuildPrompt(string module, string question, string summary, string contextJson, IReadOnlyList<KnowledgeSource> sources)
    {
        var knowledgeBlock = sources.Count == 0
            ? "Sin fragmentos adicionales de la base de conocimiento."
            : string.Join(Environment.NewLine + Environment.NewLine, sources.Select((source, index) =>
                $"Fuente {index + 1}: {source.Reference}{Environment.NewLine}{source.Snippet}"));

        return $$"""
            Eres el copiloto de un CRM de licitaciones SERCOP.
            Responde solo en espanol.
            Usa el contexto del CRM y las fuentes recuperadas.
            Si falta informacion, dilo de forma directa.
            Prioriza respuestas practicas para operar el proyecto.
            Responde en maximo 120 palabras.
            Estructura la respuesta en:
            Resumen:
            Acciones:
            Riesgos u observaciones:
            Usa maximo 3 acciones y 2 observaciones.
            No uses etiquetas <think>, no expliques razonamiento interno y no repitas estas instrucciones.

            Modulo activo: {{module}}
            Contexto breve: {{summary}}
            Pregunta del usuario: {{question}}

            Contexto estructurado del CRM:
            {{contextJson}}

            Fragmentos recuperados:
            {{knowledgeBlock}}
            """;
    }

    private string ChooseModel(string module, string question)
    {
        var isTechnical =
            question.Contains("docker", StringComparison.OrdinalIgnoreCase)
            || question.Contains("compose", StringComparison.OrdinalIgnoreCase)
            || question.Contains("codigo", StringComparison.OrdinalIgnoreCase)
            || question.Contains("c#", StringComparison.OrdinalIgnoreCase)
            || question.Contains("script", StringComparison.OrdinalIgnoreCase)
            || question.Contains("api", StringComparison.OrdinalIgnoreCase)
            || question.Contains("endpoint", StringComparison.OrdinalIgnoreCase);

        return isTechnical
            ? configuration["OLLAMA_CODE_MODEL"] ?? "qwen2.5-coder:0.5b"
            : configuration["OLLAMA_GENERAL_MODEL"] ?? "qwen3:0.6b";
    }

    private static string NormalizeModule(string? module, long? opportunityId, string? workflowId)
        => module?.Trim().ToLowerInvariant() switch
        {
            "pipeline" or "opportunity" or "opportunities" => "opportunities",
            "workflow" or "workflows" => "workflows",
            "keyword" or "keywords" => "keywords",
            "config" or "configuration" or "users" or "zones" => "config",
            "dashboard" => "dashboard",
            _ when opportunityId is not null => "opportunities",
            _ when !string.IsNullOrWhiteSpace(workflowId) => "workflows",
            _ => "dashboard"
        };

    private string GetOllamaBaseUrl()
        => configuration["OLLAMA_API_BASE_URL"] ?? "http://localhost:11434";

    private string GetQdrantBaseUrl()
        => configuration["QDRANT_API_BASE_URL"] ?? "http://localhost:6333";

    private static string Condense(string value)
        => value.Replace("\r", " ").Replace("\n", " ").Trim();

    private static string CleanAnswer(string? answer)
    {
        if (string.IsNullOrWhiteSpace(answer))
        {
            return "No hubo respuesta util del modelo local.";
        }

        var cleaned = Regex.Replace(answer, "<think>[\\s\\S]*?</think>", string.Empty, RegexOptions.IgnoreCase);
        cleaned = Regex.Replace(cleaned, @"\n{3,}", "\n\n");
        return cleaned.Trim();
    }

    private static bool IsUsefulAnswer(string answer)
        => !string.IsNullOrWhiteSpace(answer) && answer.Length >= 12 && !string.Equals(answer, "No hubo respuesta util del modelo local.", StringComparison.Ordinal);

    private sealed record KnowledgeSource(string Label, string Reference, string Kind, string Snippet);
}
