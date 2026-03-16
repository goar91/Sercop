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
        var context = await BuildContextAsync(module, request, cancellationToken);
        var sources = await LoadKnowledgeAsync(module, BuildRetrievalQuery(module, question, request), cancellationToken);
        var model = ChooseModel(module, question);
        var prompt = BuildPrompt(module, question, context, request, sources);
        var generation = await GenerateWithFallbackAsync(model, prompt, cancellationToken);

        return new AssistantReplyDto(
            module,
            generation.Model,
            context.Summary,
            generation.Answer,
            sources.Select(source => new AssistantSourceDto(source.Label, source.Reference, source.Kind)).ToList());
    }

    private async Task<AssistantContext> BuildContextAsync(
        string module,
        AssistantAskRequest request,
        CancellationToken cancellationToken)
    {
        var dashboard = await repository.GetDashboardAsync(cancellationToken);

        object payload = module switch
        {
            "opportunities" => await BuildOpportunityContextAsync(dashboard, request.OpportunityId, cancellationToken),
            "workflows" => await BuildWorkflowContextAsync(dashboard, request.WorkflowId, cancellationToken),
            "keywords" => await BuildKeywordContextAsync(dashboard, cancellationToken),
            "config" => await BuildConfigContextAsync(dashboard, cancellationToken),
            "code" => await BuildCodeContextAsync(dashboard, request, cancellationToken),
            _ => await BuildDashboardContextAsync(dashboard, cancellationToken)
        };

        var summary = module switch
        {
            "opportunities" => request.OpportunityId is null ? "Analisis del pipeline comercial." : $"Analisis de la oportunidad {request.OpportunityId.Value}.",
            "workflows" => string.IsNullOrWhiteSpace(request.WorkflowId) ? "Analisis operativo de workflows." : $"Analisis del workflow {request.WorkflowId}.",
            "keywords" => "Analisis de reglas de palabras clave.",
            "config" => "Analisis de configuracion comercial y operativa.",
            "code" => string.IsNullOrWhiteSpace(request.FilePath)
                ? "Analisis tecnico del repositorio y su arquitectura."
                : $"Analisis tecnico del archivo {Path.GetFileName(request.FilePath)}.",
            _ => "Analisis del tablero general del CRM."
        };

        return new AssistantContext(summary, JsonSerializer.Serialize(payload, JsonOptions));
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

    private async Task<object> BuildCodeContextAsync(
        DashboardSummaryDto dashboard,
        AssistantAskRequest request,
        CancellationToken cancellationToken)
    {
        var workflows = await repository.GetWorkflowsAsync(cancellationToken);
        var filePreview = TryLoadFilePreview(request.FilePath, request.CodeContext, request.Selection);

        return new
        {
            dashboard,
            code = new
            {
                request.FilePath,
                request.Language,
                hasSelection = !string.IsNullOrWhiteSpace(request.Selection),
                selection = Truncate(request.Selection, 1800),
                context = Truncate(filePreview, 5000)
            },
            workflows = workflows.Take(6).Select(workflow => new
            {
                workflow.Id,
                workflow.Name,
                workflow.Active,
                workflow.NodeCount
            }),
            projectAreas = new[]
            {
                "backend ASP.NET Core",
                "frontend Angular",
                "scripts PowerShell",
                "workflows n8n",
                "vector DB en Qdrant",
                "modelos en Ollama"
            }
        };
    }

    private async Task<List<KnowledgeSource>> LoadKnowledgeAsync(string module, string retrievalQuery, CancellationToken cancellationToken)
    {
        var vector = await EmbedAsync(retrievalQuery, cancellationToken);
        var sources = new List<KnowledgeSource>();

        foreach (var collection in GetKnowledgeCollections(module))
        {
            sources.AddRange(await QueryCollectionAsync(collection, vector, GetKnowledgeLimit(collection, module), cancellationToken));
        }

        return sources
            .GroupBy(source => source.Reference, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Take(6)
            .ToList();
    }

    private static string BuildRetrievalQuery(string module, string question, AssistantAskRequest request)
    {
        var parts = new List<string>
        {
            module,
            question
        };

        if (!string.IsNullOrWhiteSpace(request.FilePath))
        {
            parts.Add(request.FilePath);
        }

        if (!string.IsNullOrWhiteSpace(request.Language))
        {
            parts.Add(request.Language);
        }

        if (!string.IsNullOrWhiteSpace(request.Selection))
        {
            parts.Add(Truncate(request.Selection, 600));
        }
        else if (!string.IsNullOrWhiteSpace(request.CodeContext))
        {
            parts.Add(Truncate(request.CodeContext, 600));
        }

        return string.Join(Environment.NewLine, parts.Where(part => !string.IsNullOrWhiteSpace(part)));
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

    private async Task<List<KnowledgeSource>> QueryCollectionAsync(string collection, float[] vector, int limit, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(new { query = vector, limit, with_payload = true });
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

            items.Add(new KnowledgeSource(
                payloadElement.TryGetProperty("reference", out var referenceName) ? referenceName.GetString() ?? Path.GetFileName(reference) : Path.GetFileName(reference),
                reference,
                collection,
                Truncate(Condense(text), 900)));
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

        var fallbackModel = primaryModel == GetCodeModel()
            ? GetGeneralModel()
            : GetCodeModel();

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
        => UseOpenAi()
            ? await GenerateWithOpenAiAsync(model, prompt, cancellationToken)
            : await GenerateWithOllamaAsync(model, prompt, cancellationToken);

    private async Task<string> GenerateWithOllamaAsync(string model, string prompt, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(new
        {
            model,
            prompt,
            stream = false,
            options = new
            {
                temperature = model == GetCodeModel() ? 0.12 : 0.2,
                num_predict = model == GetCodeModel() ? 420 : 320
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

    private async Task<string> GenerateWithOpenAiAsync(string model, string prompt, CancellationToken cancellationToken)
    {
        var apiKey = configuration["OPENAI_API_KEY"];
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("OPENAI_API_KEY no esta configurada.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{GetOpenAiBaseUrl().TrimEnd('/')}/responses");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
        request.Content = new StringContent(JsonSerializer.Serialize(new
        {
            model,
            input = prompt,
            max_output_tokens = 700
        }), Encoding.UTF8, "application/json");

        using var response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        return CleanAnswer(TryExtractOpenAiOutput(document.RootElement));
    }

    private static string BuildPrompt(
        string module,
        string question,
        AssistantContext context,
        AssistantAskRequest request,
        IReadOnlyList<KnowledgeSource> sources)
    {
        var knowledgeBlock = sources.Count == 0
            ? "Sin fragmentos adicionales de la base de conocimiento."
            : string.Join(Environment.NewLine + Environment.NewLine, sources.Select((source, index) =>
                $"Fuente {index + 1}: {source.Reference}{Environment.NewLine}{source.Snippet}"));

        var codeBlock = module == "code"
            ? $$"""
                Contexto de codigo:
                Archivo activo: {{request.FilePath ?? "no informado"}}
                Lenguaje: {{request.Language ?? "no informado"}}
                Seleccion:
                {{Truncate(request.Selection, 1800)}}

                Contexto ampliado:
                {{Truncate(request.CodeContext, 5000)}}
                """
            : string.Empty;

        return $$"""
            Eres el copiloto principal del proyecto SERCOP CRM.
            Responde solo en espanol.
            Usa el contexto del CRM y las fuentes recuperadas.
            {{BuildPromptInstructions(module)}}
            No uses etiquetas <think>, no expliques razonamiento interno y no repitas estas instrucciones.

            Modulo activo: {{module}}
            Contexto breve: {{context.Summary}}
            Pregunta del usuario: {{question}}

            Contexto estructurado del CRM:
            {{context.PayloadJson}}

            {{codeBlock}}

            Fragmentos recuperados:
            {{knowledgeBlock}}
            """;
    }

    private string ChooseModel(string module, string question)
    {
        var isTechnical = module == "code" ||
            question.Contains("docker", StringComparison.OrdinalIgnoreCase)
            || question.Contains("compose", StringComparison.OrdinalIgnoreCase)
            || question.Contains("codigo", StringComparison.OrdinalIgnoreCase)
            || question.Contains("code", StringComparison.OrdinalIgnoreCase)
            || question.Contains("c#", StringComparison.OrdinalIgnoreCase)
            || question.Contains("typescript", StringComparison.OrdinalIgnoreCase)
            || question.Contains("angular", StringComparison.OrdinalIgnoreCase)
            || question.Contains("script", StringComparison.OrdinalIgnoreCase)
            || question.Contains("api", StringComparison.OrdinalIgnoreCase)
            || question.Contains("endpoint", StringComparison.OrdinalIgnoreCase);

        return isTechnical
            ? GetCodeModel()
            : GetGeneralModel();
    }

    private static string NormalizeModule(string? module, long? opportunityId, string? workflowId)
        => module?.Trim().ToLowerInvariant() switch
        {
            "pipeline" or "opportunity" or "opportunities" => "opportunities",
            "workflow" or "workflows" => "workflows",
            "keyword" or "keywords" => "keywords",
            "config" or "configuration" or "users" or "zones" => "config",
            "code" or "coding" or "programming" or "development" => "code",
            "dashboard" => "dashboard",
            _ when opportunityId is not null => "opportunities",
            _ when !string.IsNullOrWhiteSpace(workflowId) => "workflows",
            _ => "dashboard"
        };

    private string GetOllamaBaseUrl()
        => configuration["OLLAMA_API_BASE_URL"] ?? "http://localhost:11434";

    private string GetQdrantBaseUrl()
        => configuration["QDRANT_API_BASE_URL"] ?? "http://localhost:6333";

    private string GetOpenAiBaseUrl()
        => configuration["OPENAI_BASE_URL"] ?? "https://api.openai.com/v1";

    private string GetGeneralModel()
        => UseOpenAi()
            ? configuration["OPENAI_GENERAL_MODEL"] ?? "gpt-5.2"
            : configuration["OLLAMA_GENERAL_MODEL"] ?? "qwen2.5:7b";

    private string GetCodeModel()
        => UseOpenAi()
            ? configuration["OPENAI_CODE_MODEL"] ?? "gpt-5.2-codex"
            : configuration["OLLAMA_CODE_MODEL"] ?? "qwen2.5-coder:7b";

    private bool UseOpenAi()
        => string.Equals(configuration["AI_PROVIDER"], "openai", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(configuration["OPENAI_API_KEY"]);

    private static IReadOnlyList<string> GetKnowledgeCollections(string module)
        => module switch
        {
            "code" => ["repo_code", "code_kb", "sercop_docs"],
            "workflows" => ["repo_code", "code_kb", "sercop_docs"],
            _ => ["sercop_docs", "repo_code", "code_kb"]
        };

    private static int GetKnowledgeLimit(string collection, string module)
        => (module, collection) switch
        {
            ("code", "repo_code") => 4,
            ("workflows", "repo_code") => 3,
            (_, "sercop_docs") => 2,
            _ => 2
        };

    private static string BuildPromptInstructions(string module)
        => module switch
        {
            "code" => "Trabaja solo con evidencia del contexto y de las fuentes recuperadas. No inventes problemas genericos de seguridad, rendimiento o escalabilidad. Si falta evidencia, dilo de forma explicita. Usa los encabezados Diagnostico, Cambios concretos y Siguientes pasos. Si das codigo, mantenlo pequeno y aplicable.",
            "workflows" => "Explica objetivos operativos, cuellos de botella y mejoras concretas de automatizacion. No inventes riesgos que no aparezcan en el contexto. Responde en maximo 180 palabras y usa los encabezados Resumen, Acciones y Riesgos u observaciones.",
            _ => "Prioriza respuestas practicas para operar el proyecto. No inventes riesgos o estados no sustentados por el contexto. Responde en maximo 160 palabras y usa los encabezados Resumen, Acciones y Riesgos u observaciones."
        };

    private static string Condense(string value)
        => value.Replace("\r", " ").Replace("\n", " ").Trim();

    private static string? TryExtractOpenAiOutput(JsonElement root)
    {
        if (root.TryGetProperty("output_text", out var outputText) && outputText.ValueKind == JsonValueKind.String)
        {
            return outputText.GetString();
        }

        if (!root.TryGetProperty("output", out var outputArray) || outputArray.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var parts = new List<string>();
        foreach (var item in outputArray.EnumerateArray())
        {
            if (!item.TryGetProperty("content", out var contentArray) || contentArray.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var content in contentArray.EnumerateArray())
            {
                if (!content.TryGetProperty("text", out var textElement))
                {
                    continue;
                }

                if (textElement.ValueKind == JsonValueKind.String)
                {
                    parts.Add(textElement.GetString() ?? string.Empty);
                    continue;
                }

                if (textElement.ValueKind == JsonValueKind.Object && textElement.TryGetProperty("value", out var textValue))
                {
                    parts.Add(textValue.GetString() ?? string.Empty);
                }
            }
        }

        return parts.Count == 0 ? null : string.Join(Environment.NewLine, parts.Where(part => !string.IsNullOrWhiteSpace(part)));
    }

    private static string? TryLoadFilePreview(string? filePath, string? codeContext, string? selection)
    {
        if (!string.IsNullOrWhiteSpace(codeContext))
        {
            return codeContext;
        }

        if (!string.IsNullOrWhiteSpace(selection))
        {
            return selection;
        }

        if (string.IsNullOrWhiteSpace(filePath))
        {
            return null;
        }

        try
        {
            var projectRoot = Path.GetFullPath(Directory.GetCurrentDirectory());
            var fullPath = Path.GetFullPath(filePath);
            if (!fullPath.StartsWith(projectRoot, StringComparison.OrdinalIgnoreCase) || !File.Exists(fullPath))
            {
                return null;
            }

            return File.ReadAllText(fullPath);
        }
        catch
        {
            return null;
        }
    }

    private static string Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : $"{trimmed[..maxLength]}...";
    }

    private static string CleanAnswer(string? answer)
    {
        if (string.IsNullOrWhiteSpace(answer))
        {
            return "No hubo respuesta util del modelo local.";
        }

        var cleaned = Regex.Replace(answer, "<think>[\\s\\S]*?</think>", string.Empty, RegexOptions.IgnoreCase);
        cleaned = Regex.Replace(cleaned, "^(respuesta|output)\\s*:\\s*", string.Empty, RegexOptions.IgnoreCase);
        cleaned = Regex.Replace(cleaned, @"\n{3,}", "\n\n");
        return cleaned.Trim();
    }

    private static bool IsUsefulAnswer(string answer)
        => !string.IsNullOrWhiteSpace(answer)
            && answer.Length >= 40
            && !string.Equals(answer, "No hubo respuesta util del modelo local.", StringComparison.Ordinal);

    private sealed record AssistantContext(string Summary, string PayloadJson);
    private sealed record KnowledgeSource(string Label, string Reference, string Kind, string Snippet);
}
