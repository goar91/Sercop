using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace backend;

public sealed class StudioService(
    HttpClient httpClient,
    IConfiguration configuration,
    CrmRepository repository,
    VideoRenderService videoRenderService)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<StudioGenerateResultDto> GenerateAsync(StudioGenerateRequest request, CancellationToken cancellationToken)
    {
        if (!request.IncludeReport && !request.IncludeVideo)
        {
            throw new InvalidOperationException("Debes pedir al menos reporte o video.");
        }

        var assetScope = NormalizeScope(request.AssetScope, request.OpportunityId, request.WorkflowId);
        var context = await BuildContextAsync(assetScope, request, cancellationToken);
        var model = GetGeneralModel();

        string? reportMarkdown = null;
        if (request.IncludeReport)
        {
            reportMarkdown = await GenerateReportAsync(model, context, request, cancellationToken);
        }

        VideoBlueprint? blueprint = null;
        string? storyboardMarkdown = null;
        string? captionsSrt = null;
        long? renderedVideoAssetId = null;
        var savedAssets = new List<StudioAssetDto>();

        if (request.IncludeVideo)
        {
            blueprint = await GenerateVideoBlueprintAsync(model, context, request, reportMarkdown, cancellationToken);
            storyboardMarkdown = BuildStoryboardMarkdown(blueprint);
            captionsSrt = BuildCaptionsSrt(blueprint);

            savedAssets.Add(await repository.CreateStudioAssetAsync(
                "video_blueprint",
                assetScope,
                request.OpportunityId,
                request.WorkflowId,
                blueprint.Headline,
                "application/json",
                request.Audience,
                request.Tone,
                model,
                JsonSerializer.Serialize(blueprint, JsonOptions),
                blueprint,
                cancellationToken));

            savedAssets.Add(await repository.CreateStudioAssetAsync(
                "storyboard_markdown",
                assetScope,
                request.OpportunityId,
                request.WorkflowId,
                $"{blueprint.Headline} storyboard",
                "text/markdown",
                request.Audience,
                request.Tone,
                model,
                storyboardMarkdown,
                new { blueprint.Headline, blueprint.TargetDurationSeconds, sceneCount = blueprint.Scenes.Count },
                cancellationToken));

            savedAssets.Add(await repository.CreateStudioAssetAsync(
                "captions_srt",
                assetScope,
                request.OpportunityId,
                request.WorkflowId,
                $"{blueprint.Headline} captions",
                "application/x-subrip",
                request.Audience,
                request.Tone,
                model,
                captionsSrt,
                new { blueprint.Headline, blueprint.TargetDurationSeconds },
                cancellationToken));

            if (request.RenderVideo)
            {
                var render = await videoRenderService.RenderAsync(blueprint.Headline, blueprint.VoiceoverScript, blueprint.Scenes, captionsSrt, cancellationToken);
                var videoAsset = await repository.CreateStudioAssetAsync(
                    "rendered_video",
                    assetScope,
                    request.OpportunityId,
                    request.WorkflowId,
                    blueprint.Headline,
                    "video/mp4",
                    request.Audience,
                    request.Tone,
                    "ffmpeg/flite",
                    render.RelativePath,
                    new
                    {
                        relativePath = render.RelativePath,
                        absolutePath = render.AbsolutePath,
                        durationSeconds = render.DurationSeconds
                    },
                    cancellationToken);

                renderedVideoAssetId = videoAsset.Id;
                savedAssets.Add(videoAsset);
            }
        }

        if (request.IncludeReport)
        {
            savedAssets.Insert(0, await repository.CreateStudioAssetAsync(
                "report_markdown",
                assetScope,
                request.OpportunityId,
                request.WorkflowId,
                context.Title,
                "text/markdown",
                request.Audience,
                request.Tone,
                model,
                reportMarkdown,
                new { context.Title, context.Summary, request.Goal },
                cancellationToken));
        }

        return new StudioGenerateResultDto(
            assetScope,
            model,
            context.Summary,
            reportMarkdown,
            blueprint?.Headline,
            blueprint?.Hook,
            blueprint?.VoiceoverScript,
            storyboardMarkdown,
            captionsSrt,
            renderedVideoAssetId,
            blueprint?.Scenes ?? [],
            savedAssets);
    }

    private async Task<StudioContext> BuildContextAsync(
        string assetScope,
        StudioGenerateRequest request,
        CancellationToken cancellationToken)
    {
        var dashboard = await repository.GetDashboardAsync(cancellationToken);
        var workflows = await repository.GetWorkflowsAsync(cancellationToken);

        if (assetScope == "opportunity" && request.OpportunityId is not null)
        {
            var opportunity = await repository.GetOpportunityAsync(request.OpportunityId.Value, cancellationToken)
                ?? throw new InvalidOperationException("La oportunidad solicitada no existe.");

            var payload = new
            {
                dashboard,
                opportunity = new
                {
                    opportunity.Id,
                    opportunity.ProcessCode,
                    opportunity.Titulo,
                    opportunity.Entidad,
                    opportunity.Tipo,
                    opportunity.FechaPublicacion,
                    opportunity.FechaLimite,
                    opportunity.MontoRef,
                    opportunity.Moneda,
                    opportunity.IsInvitedMatch,
                    opportunity.Estado,
                    opportunity.Priority,
                    opportunity.AssignedUserName,
                    opportunity.ZoneName,
                    opportunity.AiResumen,
                    opportunity.AiEstrategiaAbastecimiento,
                    opportunity.AiRiesgosJson,
                    opportunity.AiChecklistJson
                }
            };

            return new StudioContext(
                $"Studio de oportunidad {opportunity.ProcessCode}: {opportunity.Titulo}",
                $"Oportunidad {opportunity.ProcessCode} para {opportunity.Entidad ?? "entidad no informada"}.",
                JsonSerializer.Serialize(payload, JsonOptions),
                dashboard,
                opportunity,
                null,
                workflows);
        }

        if (assetScope == "workflow" && !string.IsNullOrWhiteSpace(request.WorkflowId))
        {
            var workflow = await repository.GetWorkflowAsync(request.WorkflowId, cancellationToken)
                ?? throw new InvalidOperationException("El workflow solicitado no existe.");

            var payload = new
            {
                dashboard,
                workflow = new
                {
                    workflow.Id,
                    workflow.Name,
                    workflow.Active,
                    workflow.Description,
                    workflow.NodeCount,
                    nodes = workflow.Nodes.Select(node => new { node.Name, node.Type, node.Disabled })
                },
                workflows = workflows.Take(8).Select(item => new { item.Id, item.Name, item.Active, item.NodeCount })
            };

            return new StudioContext(
                $"Studio de workflow {workflow.Name}",
                $"Workflow {workflow.Name} con {workflow.NodeCount} nodos.",
                JsonSerializer.Serialize(payload, JsonOptions),
                dashboard,
                null,
                workflow,
                workflows);
        }

        var dashboardPayload = new
        {
            dashboard,
            workflows = workflows.Take(8).Select(item => new { item.Id, item.Name, item.Active, item.NodeCount })
        };

        return new StudioContext(
            "Studio ejecutivo del tablero",
            "Reporte ejecutivo y video del tablero general del proyecto.",
            JsonSerializer.Serialize(dashboardPayload, JsonOptions),
            dashboard,
            null,
            null,
            workflows);
    }

    private async Task<string> GenerateReportAsync(
        string model,
        StudioContext context,
        StudioGenerateRequest request,
        CancellationToken cancellationToken)
    {
        var prompt = $$"""
            Eres el director de estrategia comercial y comunicacion del proyecto SERCOP CRM.
            Trabaja solo con el contexto dado y no inventes cifras ni riesgos.
            Devuelve markdown en espanol.
            Estructura:
            # Titulo
            ## Resumen ejecutivo
            ## Hallazgos clave
            ## Riesgos y vacios
            ## Acciones prioritarias
            ## Datos de referencia
            Mantente entre 250 y 450 palabras.

            Audiencia: {{request.Audience ?? "gerencia"}}
            Tono: {{request.Tone ?? "ejecutivo"}}
            Objetivo: {{request.Goal ?? "resumir el estado actual y siguientes pasos"}}
            Contexto breve: {{context.Summary}}

            Contexto estructurado:
            {{context.PayloadJson}}
            """;

        var answer = CleanAnswer(await GenerateTextAsync(model, prompt, cancellationToken));
        return IsUsefulText(answer) ? answer : BuildFallbackReport(context, request);
    }

    private async Task<VideoBlueprint> GenerateVideoBlueprintAsync(
        string model,
        StudioContext context,
        StudioGenerateRequest request,
        string? reportMarkdown,
        CancellationToken cancellationToken)
    {
        var prompt = $$"""
            Eres productor de contenido para una pieza vertical de video sobre el proyecto SERCOP CRM.
            Responde solo con JSON valido y sin markdown.
            Usa esta estructura exacta:
            {
              "headline": "string",
              "hook": "string",
              "cta": "string",
              "targetDurationSeconds": 60,
              "voiceoverScript": "string",
              "scenes": [
                {
                  "order": 1,
                  "title": "string",
                  "overlayText": "string",
                  "visualBrief": "string",
                  "voiceover": "string"
                }
              ]
            }
            Reglas:
            - maximo 5 escenas
            - usa solo el contexto y no inventes datos
            - el video debe servir para gerencia, ventas o demo interna
            - cada escena debe tener overlay corto y voz concreta

            Audiencia: {{request.Audience ?? "gerencia"}}
            Tono: {{request.Tone ?? "comercial"}}
            Objetivo: {{request.Goal ?? "presentar el valor del proyecto"}}
            Contexto breve: {{context.Summary}}
            Reporte base:
            {{reportMarkdown ?? "Sin reporte previo"}}

            Contexto estructurado:
            {{context.PayloadJson}}
            """;

        var answer = await GenerateTextAsync(model, prompt, cancellationToken);
        var parsed = TryParseBlueprint(answer);
        return parsed ?? BuildFallbackBlueprint(context, reportMarkdown);
    }

    private async Task<string> GenerateTextAsync(string model, string prompt, CancellationToken cancellationToken)
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
                temperature = 0.25,
                num_predict = 1100
            }
        });

        using var response = await httpClient.PostAsync(
            $"{GetOllamaBaseUrl().TrimEnd('/')}/api/generate",
            new StringContent(payload, Encoding.UTF8, "application/json"),
            cancellationToken);

        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        return document.RootElement.TryGetProperty("response", out var output) ? output.GetString() ?? string.Empty : string.Empty;
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
            max_output_tokens = 1800
        }), Encoding.UTF8, "application/json");

        using var response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        return TryExtractOpenAiOutput(document.RootElement) ?? string.Empty;
    }

    private static VideoBlueprint? TryParseBlueprint(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var cleaned = CleanAnswer(raw);
        var match = Regex.Match(cleaned, "\\{[\\s\\S]*\\}", RegexOptions.Multiline);
        if (!match.Success)
        {
            return null;
        }

        try
        {
            var blueprint = JsonSerializer.Deserialize<VideoBlueprint>(match.Value, JsonOptions);
            if (blueprint is null || string.IsNullOrWhiteSpace(blueprint.Headline) || blueprint.Scenes.Count == 0)
            {
                return null;
            }

            return blueprint with
            {
                TargetDurationSeconds = blueprint.TargetDurationSeconds <= 0 ? 60 : blueprint.TargetDurationSeconds,
                Scenes = blueprint.Scenes
                    .Where(scene => !string.IsNullOrWhiteSpace(scene.Title))
                    .Select((scene, index) => scene with { Order = index + 1 })
                    .Take(5)
                    .ToList()
            };
        }
        catch
        {
            return null;
        }
    }

    private static VideoBlueprint BuildFallbackBlueprint(StudioContext context, string? reportMarkdown)
    {
        var scenes = new List<StudioSceneDto>();

        if (context.Opportunity is not null)
        {
            scenes.Add(new StudioSceneDto(1, "Proceso detectado", context.Opportunity.ProcessCode, "Tarjeta con codigo del proceso y entidad.", $"Se detecto la oportunidad {context.Opportunity.ProcessCode} para {context.Opportunity.Entidad ?? "una entidad no informada"}"));
            scenes.Add(new StudioSceneDto(2, "Datos clave", context.Opportunity.FechaLimite?.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture) ?? "Sin fecha limite", "Resumen de fecha, monto y prioridad.", $"La fecha limite es {context.Opportunity.FechaLimite?.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture) ?? "no visible"} y la prioridad actual es {context.Opportunity.Priority}."));
            scenes.Add(new StudioSceneDto(3, "Diagnostico IA", context.Opportunity.Recomendacion ?? "Sin recomendacion", "Panel con resumen de IA y checklist.", context.Opportunity.AiResumen ?? "Todavia no existe un analisis IA persistido para esta oportunidad."));
            scenes.Add(new StudioSceneDto(4, "Operacion comercial", context.Opportunity.AssignedUserName ?? "Sin vendedor asignado", "Tarjeta de zona, vendedor y estado.", $"El estado CRM es {context.Opportunity.Estado ?? "nuevo"} y el responsable actual es {context.Opportunity.AssignedUserName ?? "pendiente de asignacion"}."));
            scenes.Add(new StudioSceneDto(5, "Siguiente paso", "Definir decision comercial", "Cierre con accion inmediata.", "El siguiente paso es validar documentacion, decision de participar y cierre comercial."));
        }
        else if (context.Workflow is not null)
        {
            scenes.Add(new StudioSceneDto(1, "Automatizacion activa", context.Workflow.Name, "Workflow protagonista con nodos destacados.", $"Este video resume el workflow {context.Workflow.Name}."));
            scenes.Add(new StudioSceneDto(2, "Cobertura", $"{context.Workflow.NodeCount} nodos", "Panel operativo del flujo.", $"El flujo contiene {context.Workflow.NodeCount} nodos y su estado actual es {(context.Workflow.Active ? "activo" : "inactivo")}."));
            scenes.Add(new StudioSceneDto(3, "Objetivo", "Monitoreo y analisis", "Relato del impacto del flujo en el CRM.", context.Workflow.Description ?? "El workflow apoya el monitoreo y operacion de procesos SERCOP."));
            scenes.Add(new StudioSceneDto(4, "Integraciones", "n8n + CRM + IA", "Mapa simple de integraciones.", "La automatizacion se conecta con n8n, el CRM, PostgreSQL y los servicios de IA."));
            scenes.Add(new StudioSceneDto(5, "Siguiente paso", "Optimizar y medir", "Cierre con accion operativa.", "El siguiente paso es medir tiempos, errores y agregar trazabilidad de ejecucion."));
        }
        else
        {
            scenes.Add(new StudioSceneDto(1, "Radar comercial", $"{context.Dashboard.TotalOpportunities} procesos", "Portada con KPIs del tablero.", $"El tablero consolida {context.Dashboard.TotalOpportunities} procesos y {context.Dashboard.WorkflowCount} workflows."));
            scenes.Add(new StudioSceneDto(2, "Invitaciones y asignacion", $"{context.Dashboard.InvitedOpportunities} invitados", "Tarjetas con oportunidades invitadas y asignadas.", $"Hay {context.Dashboard.InvitedOpportunities} procesos invitados y {context.Dashboard.AssignedOpportunities} asignados."));
            scenes.Add(new StudioSceneDto(3, "Operacion", $"{context.Dashboard.ActiveZones} zonas activas", "Panel de carga operativa por zona.", $"La operacion actual tiene {context.Dashboard.ActiveZones} zonas activas y {context.Dashboard.ActiveUsers} usuarios activos."));
            scenes.Add(new StudioSceneDto(4, "Automatizacion", $"{context.Dashboard.WorkflowCount} workflows", "Vista de automatizaciones y copiloto IA.", "El sistema integra automatizacion en n8n, base vectorial y modelos locales o remotos."));
            scenes.Add(new StudioSceneDto(5, "Mensaje final", "Escalar la operacion", "Cierre con CTA gerencial.", "La prioridad es poblar datos reales, cerrar trazabilidad y profesionalizar reportes y video."));
        }

        var voiceover = string.Join(" ", scenes.Select(scene => scene.Voiceover));
        return new VideoBlueprint(
            $"{context.Title} video",
            "Resumen ejecutivo del proyecto en formato visual.",
            "Solicita la siguiente iteracion del Studio para profundizar el caso.",
            60,
            voiceover,
            scenes);
    }

    private static string BuildFallbackReport(StudioContext context, StudioGenerateRequest request)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"# {context.Title}");
        builder.AppendLine();
        builder.AppendLine("## Resumen ejecutivo");
        builder.AppendLine(context.Summary);
        builder.AppendLine();
        builder.AppendLine("## Hallazgos clave");
        builder.AppendLine($"- Total de procesos: {context.Dashboard.TotalOpportunities}");
        builder.AppendLine($"- Procesos invitados: {context.Dashboard.InvitedOpportunities}");
        builder.AppendLine($"- Workflows activos/importados: {context.Dashboard.WorkflowCount}");

        if (context.Opportunity is not null)
        {
            builder.AppendLine($"- Oportunidad focal: {context.Opportunity.ProcessCode} / {context.Opportunity.Titulo}");
            builder.AppendLine($"- Estado: {context.Opportunity.Estado ?? "nuevo"} / Prioridad: {context.Opportunity.Priority}");
        }

        if (context.Workflow is not null)
        {
            builder.AppendLine($"- Workflow focal: {context.Workflow.Name} ({context.Workflow.NodeCount} nodos)");
        }

        builder.AppendLine();
        builder.AppendLine("## Riesgos y vacios");
        builder.AppendLine("- Todavia faltan mas datos reales y trazabilidad historica para enriquecer el reporte.");
        builder.AppendLine("- Si el contexto documental es limitado, el reporte debe tomarse como un primer brief operativo.");
        builder.AppendLine();
        builder.AppendLine("## Acciones prioritarias");
        builder.AppendLine($"- Ajustar el material para la audiencia objetivo: {request.Audience ?? "gerencia"}.");
        builder.AppendLine($"- Profundizar el objetivo principal: {request.Goal ?? "resumir el estado y proximo paso"}.");
        builder.AppendLine("- Generar o renderizar el video desde el Studio para uso interno o comercial.");
        builder.AppendLine();
        builder.AppendLine("## Datos de referencia");
        builder.AppendLine($"- Zonas activas: {context.Dashboard.ActiveZones}");
        builder.AppendLine($"- Usuarios activos: {context.Dashboard.ActiveUsers}");
        builder.AppendLine($"- Fecha de generacion: {DateTimeOffset.Now:dd/MM/yyyy HH:mm}");
        return builder.ToString().Trim();
    }

    private static string BuildStoryboardMarkdown(VideoBlueprint blueprint)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"# {blueprint.Headline}");
        builder.AppendLine();
        builder.AppendLine($"- Hook: {blueprint.Hook}");
        builder.AppendLine($"- CTA: {blueprint.Cta}");
        builder.AppendLine($"- Duracion objetivo: {blueprint.TargetDurationSeconds} segundos");
        builder.AppendLine();

        foreach (var scene in blueprint.Scenes)
        {
            builder.AppendLine($"## Escena {scene.Order}: {scene.Title}");
            builder.AppendLine($"- Overlay: {scene.OverlayText}");
            builder.AppendLine($"- Visual: {scene.VisualBrief}");
            builder.AppendLine($"- Voz: {scene.Voiceover}");
            builder.AppendLine();
        }

        return builder.ToString().Trim();
    }

    private static string BuildCaptionsSrt(VideoBlueprint blueprint)
    {
        var totalSeconds = Math.Max(18, blueprint.TargetDurationSeconds);
        var sceneDuration = blueprint.Scenes.Count == 0 ? totalSeconds : totalSeconds / blueprint.Scenes.Count;
        var builder = new StringBuilder();

        for (var index = 0; index < blueprint.Scenes.Count; index++)
        {
            var scene = blueprint.Scenes[index];
            var start = TimeSpan.FromSeconds(index * sceneDuration);
            var end = TimeSpan.FromSeconds(Math.Min(totalSeconds, (index + 1) * sceneDuration));
            builder.AppendLine((index + 1).ToString(CultureInfo.InvariantCulture));
            builder.AppendLine($"{FormatSrtTime(start)} --> {FormatSrtTime(end)}");
            builder.AppendLine(scene.Voiceover);
            builder.AppendLine();
        }

        return builder.ToString().Trim();
    }

    private static string FormatSrtTime(TimeSpan value)
        => $"{value:hh\\:mm\\:ss\\,fff}";

    private string GetOllamaBaseUrl()
        => configuration["OLLAMA_API_BASE_URL"] ?? "http://localhost:11434";

    private string GetOpenAiBaseUrl()
        => configuration["OPENAI_BASE_URL"] ?? "https://api.openai.com/v1";

    private string GetGeneralModel()
        => UseOpenAi()
            ? configuration["OPENAI_GENERAL_MODEL"] ?? "gpt-5.2"
            : configuration["OLLAMA_GENERAL_MODEL"] ?? "qwen2.5:7b";

    private bool UseOpenAi()
        => string.Equals(configuration["AI_PROVIDER"], "openai", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(configuration["OPENAI_API_KEY"]);

    private static string NormalizeScope(string? assetScope, long? opportunityId, string? workflowId)
        => assetScope?.Trim().ToLowerInvariant() switch
        {
            "opportunity" when opportunityId is not null => "opportunity",
            "workflow" when !string.IsNullOrWhiteSpace(workflowId) => "workflow",
            "dashboard" => "dashboard",
            _ when opportunityId is not null => "opportunity",
            _ when !string.IsNullOrWhiteSpace(workflowId) => "workflow",
            _ => "dashboard"
        };

    private static bool IsUsefulText(string? value)
        => !string.IsNullOrWhiteSpace(value) && value.Length >= 80;

    private static string CleanAnswer(string? answer)
    {
        if (string.IsNullOrWhiteSpace(answer))
        {
            return string.Empty;
        }

        var cleaned = Regex.Replace(answer, "<think>[\\s\\S]*?</think>", string.Empty, RegexOptions.IgnoreCase);
        cleaned = cleaned.Replace("```json", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("```", string.Empty, StringComparison.OrdinalIgnoreCase);
        return cleaned.Trim();
    }

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

    private sealed record StudioContext(
        string Title,
        string Summary,
        string PayloadJson,
        DashboardSummaryDto Dashboard,
        OpportunityDetailDto? Opportunity,
        WorkflowDetailDto? Workflow,
        IReadOnlyList<WorkflowSummaryDto> Workflows);

    private sealed record VideoBlueprint(
        string Headline,
        string Hook,
        string Cta,
        int TargetDurationSeconds,
        string VoiceoverScript,
        IReadOnlyList<StudioSceneDto> Scenes);
}
