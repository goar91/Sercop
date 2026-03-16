using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;

namespace backend;

public sealed class PersonalAssistantService(
    HttpClient httpClient,
    IConfiguration configuration,
    PersonalAssistantDocumentService documentService,
    CrmRepository repository,
    ILogger<PersonalAssistantService> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly Regex UrlRegex = new(@"https?://\S+", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex DuckDuckGoResultRegex = new(
        "<a[^>]*class=\"result__a\"[^>]*href=\"(?<href>[^\"]+)\"[^>]*>(?<title>[\\s\\S]*?)</a>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public async Task<PersonalAssistantReplyDto> AskAsync(PersonalAssistantAskRequest request, CancellationToken cancellationToken)
        => await AskCoreAsync(request, [], cancellationToken);

    public async Task<PersonalAssistantReplyDto> AnalyzeDocumentsAsync(
        string? question,
        long? sessionId,
        string? searchMode,
        IReadOnlyList<IFormFile> files,
        CancellationToken cancellationToken)
    {
        var uploadedDocuments = await documentService.ProcessUploadsAsync(files, cancellationToken);
        var normalizedQuestion = string.IsNullOrWhiteSpace(question)
            ? "Resume y analiza los documentos cargados sin asumir nada. Indica hallazgos, evidencia y pendientes."
            : question.Trim();

        return await AskCoreAsync(
            new PersonalAssistantAskRequest(
                normalizedQuestion,
                sessionId,
                searchMode,
                null,
                null,
                null,
                null),
            uploadedDocuments,
            cancellationToken);
    }

    private async Task<PersonalAssistantReplyDto> AskCoreAsync(
        PersonalAssistantAskRequest request,
        IReadOnlyList<PersonalAssistantUploadedDocument> uploadedDocuments,
        CancellationToken cancellationToken)
    {
        var question = request.Question?.Trim();
        if (string.IsNullOrWhiteSpace(question))
        {
            throw new InvalidOperationException("La pregunta es obligatoria.");
        }

        var searchMode = NormalizeSearchMode(request.SearchMode);
        var sessionId = request.SessionId ?? await repository.CreatePersonalAssistantSessionAsync(BuildSessionTitle(question), cancellationToken);
        await PersistUploadedDocumentsAsync(question, uploadedDocuments, cancellationToken);
        var uploadedSources = uploadedDocuments
            .Select(document => new AssistantSourceDto(document.FileName, document.DownloadUrl, "upload"))
            .ToList();

        await repository.AddPersonalAssistantMessageAsync(
            sessionId,
            "user",
            question,
            null,
            BuildContextJson(request, uploadedDocuments),
            uploadedSources,
            cancellationToken);

        var session = await repository.GetPersonalAssistantSessionAsync(sessionId, cancellationToken)
            ?? throw new InvalidOperationException("No se pudo cargar la sesion del asistente.");

        var recentMessages = session.Messages.TakeLast(10).ToList();
        var localMemory = await repository.SearchPersonalAssistantMemoryAsync(BuildMemoryQuery(question, request, uploadedDocuments), 6, cancellationToken);
        var shouldSearchWeb = ShouldSearchWeb(searchMode, question, request, localMemory, uploadedDocuments.Count);
        var webKnowledge = shouldSearchWeb
            ? await SearchAndFetchWebAsync(question, cancellationToken)
            : [];
        var assistantProfile = BuildAssistantProfile(question, request, uploadedDocuments);

        var learnedItems = 0;
        if (webKnowledge.Count > 0)
        {
            learnedItems = await PersistWebKnowledgeAsync(question, webKnowledge, cancellationToken);
        }

        var sources = BuildSources(question, request, localMemory, webKnowledge, uploadedDocuments);
        if (IsCapabilityQuestion(question))
        {
            var answer = BuildCapabilityAnswer(request, uploadedDocuments);
            const string model = "system_profile";

            await repository.AddPersonalAssistantMessageAsync(
                sessionId,
                "assistant",
                answer,
                model,
                "{}",
                sources,
                cancellationToken);

            await repository.UpsertPersonalAssistantMemoryAsync(
                new PersonalAssistantMemoryUpsert(
                    "answer_note",
                    BuildSessionTitle(question),
                    Truncate(answer, 6000),
                    "assistant",
                    null,
                    0.95m,
                    question,
                    sources),
                cancellationToken);

            return new PersonalAssistantReplyDto(
                sessionId,
                session.Title,
                searchMode,
                false,
                learnedItems,
                model,
                answer,
                sources,
                uploadedDocuments.Select(ToUploadedDocumentDto).ToList());
        }

        if (sources.Count == 0 && recentMessages.Count <= 1 && !HasCodeContext(request) && uploadedDocuments.Count == 0)
        {
            var answer = shouldSearchWeb
                ? "Respuesta\nNo encontre evidencia suficiente ni en tu memoria ni en la web para responder sin asumir.\n\nEvidencia\nNo hay fuentes verificables disponibles para esta consulta.\n\nPendientes\nReformula la pregunta con mas contexto o comparte una URL especifica."
                : "Respuesta\nNo hay conocimiento guardado suficiente para responder sin asumir.\n\nEvidencia\nLa memoria personal no devolvio resultados verificables.\n\nPendientes\nActiva el modo web o agrega mas contexto.";

            var model = ChooseModel(question, request, uploadedDocuments);
            await repository.AddPersonalAssistantMessageAsync(
                sessionId,
                "assistant",
                answer,
                model,
                "{}",
                [],
                cancellationToken);

            return new PersonalAssistantReplyDto(
                sessionId,
                session.Title,
                searchMode,
                shouldSearchWeb,
                learnedItems,
                model,
                answer,
                [],
                uploadedDocuments.Select(ToUploadedDocumentDto).ToList());
        }

        var prompt = BuildPrompt(question, request, recentMessages, localMemory, webKnowledge, uploadedDocuments, searchMode, assistantProfile);
        var selectedModel = ChooseModel(question, request, uploadedDocuments);
        var generation = await GenerateWithFallbackAsync(selectedModel, prompt, cancellationToken);

        await repository.AddPersonalAssistantMessageAsync(
            sessionId,
            "assistant",
            generation.Answer,
            generation.Model,
            "{}",
            sources,
            cancellationToken);

        await repository.UpsertPersonalAssistantMemoryAsync(
            new PersonalAssistantMemoryUpsert(
                "answer_note",
                BuildSessionTitle(question),
                Truncate(generation.Answer, 6000),
                "assistant",
                null,
                sources.Count == 0 ? 0.45m : shouldSearchWeb ? 0.86m : 0.72m,
                question,
                sources),
            cancellationToken);

        await repository.TouchPersonalAssistantMemoryAsync(localMemory.Select(item => item.Id), cancellationToken);

        return new PersonalAssistantReplyDto(
            sessionId,
            session.Title,
            searchMode,
            shouldSearchWeb,
            learnedItems,
            generation.Model,
            generation.Answer,
            sources,
            uploadedDocuments.Select(ToUploadedDocumentDto).ToList());
    }

    private async Task<int> PersistWebKnowledgeAsync(
        string question,
        IReadOnlyList<WebKnowledgeItem> webKnowledge,
        CancellationToken cancellationToken)
    {
        var stored = 0;
        foreach (var item in webKnowledge)
        {
            try
            {
                await repository.UpsertPersonalAssistantMemoryAsync(
                    new PersonalAssistantMemoryUpsert(
                        "web_learning",
                        item.Title,
                        item.Snippet,
                        "web",
                        item.Url,
                        0.84m,
                        question,
                        [new AssistantSourceDto(item.Title, item.Url, "web")]),
                    cancellationToken);
                stored++;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "No se pudo guardar el aprendizaje web para {Url}", item.Url);
            }
        }

        return stored;
    }

    private async Task<List<WebKnowledgeItem>> SearchAndFetchWebAsync(string question, CancellationToken cancellationToken)
    {
        var directUrl = ExtractDirectUrl(question);
        if (!string.IsNullOrWhiteSpace(directUrl))
        {
            var directPage = await FetchPageAsync(directUrl, cancellationToken);
            return directPage is null ? [] : [directPage];
        }

        var searchResults = await SearchWebAsync(question, cancellationToken);
        var pages = new List<WebKnowledgeItem>();

        foreach (var result in searchResults.Take(GetWebResultLimit()))
        {
            var page = await FetchPageAsync(result.Url, cancellationToken);
            if (page is null)
            {
                continue;
            }

            pages.Add(page with { Title = string.IsNullOrWhiteSpace(page.Title) ? result.Title : page.Title });
        }

        return pages;
    }

    private async Task<List<WebSearchResult>> SearchWebAsync(string query, CancellationToken cancellationToken)
    {
        var url = $"https://html.duckduckgo.com/html/?q={Uri.EscapeDataString(query)}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd(GetWebUserAgent());

        using var response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync(cancellationToken);

        var results = new List<WebSearchResult>();
        foreach (Match match in DuckDuckGoResultRegex.Matches(html))
        {
            var href = DecodeSearchUrl(match.Groups["href"].Value);
            if (string.IsNullOrWhiteSpace(href) || !Uri.TryCreate(href, UriKind.Absolute, out var uri))
            {
                continue;
            }

            if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var title = DecodeHtml(StripHtml(match.Groups["title"].Value));
            results.Add(new WebSearchResult(title, uri.ToString()));
        }

        return results
            .GroupBy(item => item.Url, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
    }

    private async Task<WebKnowledgeItem?> FetchPageAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd(GetWebUserAgent());

            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var mediaType = response.Content.Headers.ContentType?.MediaType ?? "text/html";
            if (!mediaType.Contains("html", StringComparison.OrdinalIgnoreCase)
                && !mediaType.Contains("text", StringComparison.OrdinalIgnoreCase)
                && !mediaType.Contains("json", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var payload = await response.Content.ReadAsStringAsync(cancellationToken);
            var title = ExtractHtmlTitle(payload);
            var snippet = mediaType.Contains("json", StringComparison.OrdinalIgnoreCase)
                ? Truncate(Condense(payload), 3600)
                : Truncate(ExtractReadableText(payload), 3600);

            if (string.IsNullOrWhiteSpace(snippet))
            {
                return null;
            }

            return new WebKnowledgeItem(
                string.IsNullOrWhiteSpace(title) ? url : title,
                url,
                snippet);
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "Fallo al leer fuente web {Url}", url);
            return null;
        }
    }

    private async Task<(string Model, string Answer)> GenerateWithFallbackAsync(
        string primaryModel,
        string prompt,
        CancellationToken cancellationToken)
    {
        var primaryAnswer = await GenerateAsync(primaryModel, prompt, cancellationToken);
        if (IsUsefulAnswer(primaryAnswer))
        {
            return (primaryModel, primaryAnswer);
        }

        var fallbackModel = string.Equals(primaryModel, GetCodeModel(), StringComparison.OrdinalIgnoreCase)
            ? GetGeneralModel()
            : GetCodeModel();

        if (string.Equals(primaryModel, fallbackModel, StringComparison.OrdinalIgnoreCase))
        {
            return (primaryModel, primaryAnswer);
        }

        var fallbackAnswer = await GenerateAsync(fallbackModel, prompt, cancellationToken);
        return (fallbackModel, fallbackAnswer);
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
                temperature = model == GetCodeModel() ? 0.1 : 0.15,
                num_predict = model == GetCodeModel() ? 700 : 520
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
            max_output_tokens = 900
        }), Encoding.UTF8, "application/json");

        using var response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        return CleanAnswer(TryExtractOpenAiOutput(document.RootElement));
    }

    private static string BuildPrompt(
        string question,
        PersonalAssistantAskRequest request,
        IReadOnlyList<PersonalAssistantMessageDto> recentMessages,
        IReadOnlyList<PersonalAssistantMemoryDto> localMemory,
        IReadOnlyList<WebKnowledgeItem> webKnowledge,
        IReadOnlyList<PersonalAssistantUploadedDocument> uploadedDocuments,
        string searchMode,
        string assistantProfile)
    {
        var conversationBlock = recentMessages.Count == 0
            ? "Sin historial reciente."
            : string.Join(
                Environment.NewLine + Environment.NewLine,
                recentMessages.Select(message => $"{message.Role}: {Truncate(message.Content, 1000)}"));

        var memoryBlock = localMemory.Count == 0
            ? "Sin hallazgos en la memoria personal."
            : string.Join(
                Environment.NewLine + Environment.NewLine,
                localMemory.Select((item, index) =>
                    $"Memoria {index + 1}: {item.Title}{Environment.NewLine}Fuente: {item.SourceUrl ?? item.SourceKind}{Environment.NewLine}{Truncate(item.Content, 1400)}"));

        var webBlock = webKnowledge.Count == 0
            ? "No hubo hallazgos web nuevos para esta consulta."
            : string.Join(
                Environment.NewLine + Environment.NewLine,
                webKnowledge.Select((item, index) =>
                    $"Fuente web {index + 1}: {item.Url}{Environment.NewLine}{Truncate(item.Snippet, 1600)}"));

        var documentBlock = uploadedDocuments.Count == 0
            ? "No se cargaron documentos en esta consulta."
            : string.Join(
                Environment.NewLine + Environment.NewLine,
                uploadedDocuments.Select((document, index) =>
                    $"Documento {index + 1}: {document.FileName}{Environment.NewLine}Ruta: {document.DownloadUrl}{Environment.NewLine}{Truncate(document.ExtractedText, 5200)}"));

        var codeBlock = HasCodeContext(request)
            ? $$"""
                Contexto tecnico:
                Archivo: {{request.FilePath ?? "no informado"}}
                Lenguaje: {{request.Language ?? "no informado"}}
                Seleccion:
                {{Truncate(request.Selection, 1800)}}

                Contexto ampliado:
                {{Truncate(request.CodeContext, 5000)}}
                """
            : string.Empty;

        return $$"""
            Eres un asistente personal privado.
            Responde solo en espanol.
            Tu identidad y tus capacidades deben describirse segun este entorno real de ejecucion, no segun el proveedor o entrenamiento base del modelo.
            No te presentes como un producto de Alibaba Cloud, OpenAI, Qwen ni otro proveedor salvo que el usuario pregunte especificamente por el stack tecnico.
            Trabaja exclusivamente con el perfil del sistema, el historial, la memoria guardada, los documentos cargados y las fuentes web verificadas provistas abajo.
            No completes huecos con conocimiento general no citado.
            Si algo no esta sustentado, di literalmente "No tengo evidencia suficiente".
            No uses etiquetas <think> ni expliques razonamiento interno.
            Usa siempre los encabezados Respuesta, Evidencia y Pendientes.
            Si la consulta es tecnica, responde con pasos concretos y codigo pequeno si esta sustentado por el contexto.
            Si la consulta es tecnica y hay contexto de codigo suficiente, puedes proponer hipotesis de ingenieria y pasos de depuracion basados en patrones tecnicos habituales.
            Toda hipotesis debe etiquetarse explicitamente como Hipotesis y nunca presentarse como hecho confirmado.
            Si hay documentos cargados, priorizalos por encima de la memoria y la web.
            Si el usuario pregunta por tus alcances, capacidades o que puedes hacer, responde con una lista amplia pero precisa basada en el perfil del sistema.

            Modo de busqueda: {{searchMode}}
            Pregunta: {{question}}

            Perfil del sistema:
            {{assistantProfile}}

            {{codeBlock}}

            Historial reciente:
            {{conversationBlock}}

            Memoria personal:
            {{memoryBlock}}

            Documentos cargados:
            {{documentBlock}}

            Hallazgos web:
            {{webBlock}}
            """;
    }

    private static string BuildAssistantProfile(
        string question,
        PersonalAssistantAskRequest request,
        IReadOnlyList<PersonalAssistantUploadedDocument> uploadedDocuments)
    {
        var capabilityFocus = IsCapabilityQuestion(question)
            ? "Esta consulta pide explicitar alcances. Debes enumerar capacidades reales, casos de uso y limites sin minimizar el sistema."
            : "Usa este perfil como marco operativo cuando necesites describir tus capacidades o tus limites.";

        var uploadedFormats = uploadedDocuments.Count == 0
            ? "txt, md, json, csv, html, xml, docx, pdf con texto embebido y archivos de codigo comunes."
            : string.Join(", ", uploadedDocuments.Select(document => document.FileName).Take(6));

        var codeContext = HasCodeContext(request)
            ? "Hay contexto tecnico activo de archivo, lenguaje o seleccion de codigo."
            : "No hay contexto tecnico adjunto en esta consulta.";

        return $$"""
            {{capabilityFocus}}
            Identidad:
            - Asistente personal privado desacoplado del CRM y orientado a trabajo real, no a demostraciones superficiales.
            - Aprende guardando memoria verificable en PostgreSQL; esto no es fine-tuning automatico del modelo base.
            - Debe evitar suposiciones y salir a la web cuando falte evidencia o la consulta sea temporal, cambiante o dudosa.

            Capacidades reales:
            - Investigacion web verificable con lectura de paginas, extraccion de texto y reutilizacion posterior de hallazgos guardados.
            - Analisis de documentos cargados, incluidos multiples archivos, con resumen, comparacion, extraccion de hallazgos, riesgos, requisitos, pendientes y planes de accion.
            - Programacion y trabajo tecnico: explicar codigo, revisar bugs, proponer refactors, escribir funciones, scripts, consultas SQL, prompts, documentacion tecnica y planes de implementacion.
            - Analisis estructurado de informacion para convertir contenido en Markdown, JSON, tablas, checklist, briefings ejecutivos, matrices de decision o pasos operativos.
            - Soporte para arquitectura de software, APIs, Docker, bases de datos, automatizaciones, VS Code y flujo de trabajo de ingenieria.
            - Asistencia para investigacion, redaccion, traduccion, comparativas, toma de decisiones y organizacion de conocimiento personal.
            - Capacidad de citar fuentes usadas: memoria, web, documentos cargados y contexto de codigo.
            - Puede preparar materiales conceptuales para reportes, guiones, storyboard o contenido tecnico, aunque esta interfaz no renderiza imagenes ni video por si sola.

            Alcance documental actual:
            - Formatos manejados: {{uploadedFormats}}
            - {{codeContext}}

            Limites reales:
            - No debe afirmar cosas no sustentadas por memoria, documentos, contexto de codigo o fuentes web verificadas.
            - No ejecuta acciones fisicas ni controla cuentas externas por si mismo.
            - No hace OCR avanzado de PDFs escaneados en esta interfaz.
            - No genera imagenes ni video final desde este chat; puede disenar el contenido y la estructura.
            """;
    }

    private static string BuildCapabilityAnswer(
        PersonalAssistantAskRequest request,
        IReadOnlyList<PersonalAssistantUploadedDocument> uploadedDocuments)
    {
        var extras = new List<string>();
        if (HasCodeContext(request))
        {
            extras.Add("- En esta consulta ademas tengo contexto tecnico activo, asi que puedo bajar al nivel de archivo, fragmento de codigo, bug, refactor o arquitectura.");
        }

        if (uploadedDocuments.Count > 0)
        {
            extras.Add($"- En esta consulta tambien puedo trabajar directamente sobre {uploadedDocuments.Count} documento(s) cargado(s): resumir, comparar, extraer hallazgos, revisar riesgos, generar checklist y convertirlos en planes de accion.");
        }

        var extraBlock = extras.Count == 0
            ? "- Si me das mas contexto, URLs, documentos o codigo, puedo bajar de una respuesta general a una ejecucion mucho mas concreta."
            : string.Join(Environment.NewLine, extras);

        return $$"""
            Respuesta
            Mis alcances reales en este entorno son bastante mas amplios que un simple chat generalista:

            - Investigacion web verificable: puedo salir a la web cuando falta evidencia, leer fuentes, resumirlas y devolver respuestas con respaldo.
            - Memoria operativa: puedo guardar hallazgos, respuestas y fuentes para reutilizarlos despues sin empezar de cero cada vez.
            - Analisis documental: puedo trabajar con varios archivos, extraer puntos clave, comparar versiones, detectar riesgos, requisitos, pendientes y contradicciones.
            - Programacion: puedo explicar codigo, revisar bugs, proponer refactors, escribir funciones, scripts, consultas SQL, prompts, documentacion tecnica y planes de implementacion.
            - Ingenieria de software: puedo ayudarte con arquitectura, APIs, Docker, bases de datos, automatizaciones, VS Code, despliegue y flujo de trabajo tecnico.
            - Analisis estructurado: puedo convertir informacion en Markdown, JSON, tablas, checklist, matrices de decision, briefings ejecutivos o pasos operativos.
            - Soporte de decisiones: puedo comparar opciones, priorizar tareas, evaluar riesgos, proponer rutas de accion y ordenar trabajo complejo.
            - Redaccion y sintesis: puedo escribir informes, resenas tecnicas, resumentes ejecutivos, instrucciones, guias, correos y contenido estructurado.
            - Aprendizaje basado en evidencia: puedo incorporar a mi memoria lo que encuentre en documentos o en la web, siempre que quede trazabilidad de la fuente.
            - Trabajo tecnico-personal: puedo servir como copiloto para investigacion, estudio, operaciones, documentacion y programacion.
            - Preparacion de contenido: puedo disenar guiones, estructuras, storyboard textual, reportes y piezas conceptuales para otros modulos del proyecto.

            {{extraBlock}}

            Evidencia
            Esta descripcion se basa en el perfil real del asistente en este proyecto: memoria persistente, busqueda web verificable, analisis de documentos y soporte tecnico de codigo.

            Pendientes
            Si quieres, puedo devolverte esta misma descripcion en una version todavia mas potente orientada a:
            1. uso personal general
            2. programacion e ingenieria
            3. analisis documental y reportes
            4. perfil comercial o de presentacion
            """;
    }

    private static bool ShouldSearchWeb(
        string searchMode,
        string question,
        PersonalAssistantAskRequest request,
        IReadOnlyList<PersonalAssistantMemoryDto> localMemory,
        int uploadedDocumentCount)
    {
        if (string.Equals(searchMode, "web", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(searchMode, "memory", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (uploadedDocumentCount > 0)
        {
            return IsTimeSensitiveQuestion(question);
        }

        if (localMemory.Count == 0)
        {
            return !HasCodeContext(request) || IsTimeSensitiveQuestion(question);
        }

        return IsTimeSensitiveQuestion(question);
    }

    private static bool IsTimeSensitiveQuestion(string question)
    {
        var markers = new[]
        {
            "hoy",
            "actual",
            "actualizado",
            "reciente",
            "ultima",
            "ultimas",
            "latest",
            "version",
            "release",
            "precio",
            "documentacion",
            "documentation",
            "api",
            "breaking",
            "noticia",
            "news",
            "web",
            "internet"
        };

        return markers.Any(marker => question.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    private static string BuildMemoryQuery(
        string question,
        PersonalAssistantAskRequest request,
        IReadOnlyList<PersonalAssistantUploadedDocument> uploadedDocuments)
    {
        var stopwords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "que",
            "cual",
            "cuales",
            "como",
            "para",
            "por",
            "con",
            "sin",
            "desde",
            "sobre",
            "del",
            "de",
            "la",
            "las",
            "los",
            "el",
            "una",
            "uno",
            "unos",
            "unas",
            "recuerdame",
            "necesito",
            "quiero",
            "documentacion",
            "oficial",
            "reciente",
            "mas"
        };

        var parts = new List<string> { question };
        if (!string.IsNullOrWhiteSpace(request.FilePath))
        {
            parts.Add(request.FilePath);
        }

        if (!string.IsNullOrWhiteSpace(request.Language))
        {
            parts.Add(request.Language);
        }

        foreach (var document in uploadedDocuments)
        {
            parts.Add(document.FileName);
        }

        var flattened = string.Join(' ', parts);
        var keywords = Regex.Matches(flattened.ToLowerInvariant(), @"[\p{L}\p{N}\-\.#]+")
            .Select(match => match.Value.Trim())
            .Where(token => token.Length >= 4 || token.Any(char.IsDigit))
            .Where(token => !stopwords.Contains(token))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(10)
            .ToList();

        return keywords.Count > 0 ? string.Join(' ', keywords) : flattened;
    }

    private string ChooseModel(
        string question,
        PersonalAssistantAskRequest request,
        IReadOnlyList<PersonalAssistantUploadedDocument> uploadedDocuments)
    {
        var technicalMarkers = new[]
        {
            "programacion",
            "programación",
            "programar",
            "codigo",
            "código",
            "code",
            "bug",
            "debug",
            "depurar",
            "refactor",
            "refactorizar",
            "script",
            "sql",
            "c#",
            "dotnet",
            ".net",
            "asp.net",
            "typescript",
            "javascript",
            "node",
            "python",
            "java",
            "go",
            "rust",
            "angular",
            "react",
            "docker",
            "compose",
            "api",
            "endpoint",
            "backend",
            "frontend",
            "postgres",
            "postgresql",
            "n8n",
            "vscode",
            "visual studio code"
        };

        var isTechnical = HasCodeContext(request)
            || uploadedDocuments.Any(document => document.IsTechnical)
            || technicalMarkers.Any(marker => question.Contains(marker, StringComparison.OrdinalIgnoreCase));

        return isTechnical ? GetCodeModel() : GetGeneralModel();
    }

    private static IReadOnlyList<AssistantSourceDto> BuildSources(
        string question,
        PersonalAssistantAskRequest request,
        IReadOnlyList<PersonalAssistantMemoryDto> localMemory,
        IReadOnlyList<WebKnowledgeItem> webKnowledge,
        IReadOnlyList<PersonalAssistantUploadedDocument> uploadedDocuments)
    {
        var sources = new List<AssistantSourceDto>();
        if (IsCapabilityQuestion(question))
        {
            sources.Add(new AssistantSourceDto("Perfil del asistente", "system:assistant-profile", "system_profile"));
        }

        sources.AddRange(uploadedDocuments.Select(document => new AssistantSourceDto(document.FileName, document.DownloadUrl, "upload")));
        sources.AddRange(webKnowledge.Select(item => new AssistantSourceDto(item.Title, item.Url, "web")));
        foreach (var item in localMemory)
        {
            if (item.Sources.Count > 0)
            {
                sources.AddRange(item.Sources);
                continue;
            }

            sources.Add(new AssistantSourceDto(item.Title, item.SourceUrl ?? $"memory:{item.Id}", item.SourceKind));
        }

        if (HasCodeContext(request))
        {
            var label = !string.IsNullOrWhiteSpace(request.FilePath)
                ? Path.GetFileName(request.FilePath)
                : "editor selection";
            var reference = request.FilePath ?? "editor-selection";
            sources.Add(new AssistantSourceDto(label, reference, "code_context"));
        }

        return sources
            .GroupBy(source => source.Reference, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Take(8)
            .ToList();
    }

    private static bool IsCapabilityQuestion(string question)
    {
        var markers = new[]
        {
            "alcances",
            "alcance",
            "capacidades",
            "capacidad",
            "habilidades",
            "que puedes hacer",
            "qué puedes hacer",
            "que haces",
            "qué haces",
            "funciones",
            "para que sirves",
            "para qué sirves",
            "quien eres",
            "quién eres"
        };

        return markers.Any(marker => question.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeSearchMode(string? searchMode)
        => searchMode?.Trim().ToLowerInvariant() switch
        {
            "web" or "web_first" or "web-only" => "web",
            "memory" or "local" or "memory_only" => "memory",
            _ => "auto"
        };

    private string GetOllamaBaseUrl()
        => configuration["OLLAMA_API_BASE_URL"] ?? "http://localhost:11434";

    private string GetOpenAiBaseUrl()
        => configuration["OPENAI_BASE_URL"] ?? "https://api.openai.com/v1";

    private string GetGeneralModel()
        => UseOpenAi()
            ? configuration["OPENAI_GENERAL_MODEL"] ?? "gpt-5.2"
            : configuration["OLLAMA_GENERAL_MODEL"] ?? "qwen2.5:14b";

    private string GetCodeModel()
        => UseOpenAi()
            ? configuration["OPENAI_CODE_MODEL"] ?? "gpt-5.2-codex"
            : configuration["OLLAMA_CODE_MODEL"] ?? "qwen2.5-coder:14b";

    private bool UseOpenAi()
        => string.Equals(configuration["AI_PROVIDER"], "openai", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(configuration["OPENAI_API_KEY"]);

    private string GetWebUserAgent()
        => configuration["PERSONAL_AI_WEB_USER_AGENT"] ?? "SERCOP-Personal-AI/1.0";

    private int GetWebResultLimit()
        => int.TryParse(configuration["PERSONAL_AI_WEB_RESULT_LIMIT"], out var limit) && limit > 0
            ? Math.Min(limit, 5)
            : 3;

    private static bool HasCodeContext(PersonalAssistantAskRequest request)
        => !string.IsNullOrWhiteSpace(request.FilePath)
            || !string.IsNullOrWhiteSpace(request.Language)
            || !string.IsNullOrWhiteSpace(request.Selection)
            || !string.IsNullOrWhiteSpace(request.CodeContext);

    private static string BuildContextJson(
        PersonalAssistantAskRequest request,
        IReadOnlyList<PersonalAssistantUploadedDocument> uploadedDocuments)
        => JsonSerializer.Serialize(new
        {
            request.FilePath,
            request.Language,
            request.Selection,
            request.CodeContext,
            UploadedDocuments = uploadedDocuments.Select(document => new
            {
                document.FileName,
                document.ContentType,
                document.SizeBytes,
                document.CharacterCount,
                document.DownloadUrl
            })
        }, JsonOptions);

    private async Task PersistUploadedDocumentsAsync(
        string question,
        IReadOnlyList<PersonalAssistantUploadedDocument> uploadedDocuments,
        CancellationToken cancellationToken)
    {
        foreach (var document in uploadedDocuments)
        {
            await repository.UpsertPersonalAssistantMemoryAsync(
                new PersonalAssistantMemoryUpsert(
                    "uploaded_document",
                    document.FileName,
                    Truncate(document.ExtractedText, 8000),
                    "upload",
                    document.DownloadUrl,
                    0.97m,
                    question,
                    [new AssistantSourceDto(document.FileName, document.DownloadUrl, "upload")]),
                cancellationToken);
        }
    }

    private static PersonalAssistantUploadedDocumentDto ToUploadedDocumentDto(PersonalAssistantUploadedDocument document)
        => new(
            document.FileName,
            document.ContentType,
            document.SizeBytes,
            document.CharacterCount,
            document.DownloadUrl);

    private static string BuildSessionTitle(string question)
    {
        var cleaned = Condense(question);
        return cleaned.Length <= 72 ? cleaned : $"{cleaned[..72]}...";
    }

    private static string? ExtractDirectUrl(string question)
    {
        var match = UrlRegex.Match(question);
        return match.Success ? match.Value.Trim().TrimEnd('.', ',', ';', ')', ']') : null;
    }

    private static string DecodeSearchUrl(string rawHref)
    {
        var href = WebUtility.HtmlDecode(rawHref).Trim();
        if (href.StartsWith("//", StringComparison.Ordinal))
        {
            href = $"https:{href}";
        }

        if (!Uri.TryCreate(href, UriKind.Absolute, out var uri))
        {
            return href;
        }

        if (!uri.Host.Contains("duckduckgo.com", StringComparison.OrdinalIgnoreCase))
        {
            return uri.ToString();
        }

        var query = QueryHelpers.ParseQuery(uri.Query);
        if (query.TryGetValue("uddg", out var uddgValue))
        {
            return WebUtility.UrlDecode(uddgValue.ToString());
        }

        return uri.ToString();
    }

    private static string ExtractHtmlTitle(string html)
    {
        var match = Regex.Match(html, "<title[^>]*>(?<title>[\\s\\S]*?)</title>", RegexOptions.IgnoreCase);
        return match.Success ? DecodeHtml(StripHtml(match.Groups["title"].Value)) : string.Empty;
    }

    private static string ExtractReadableText(string html)
    {
        var cleaned = Regex.Replace(html, "<script[\\s\\S]*?</script>", " ", RegexOptions.IgnoreCase);
        cleaned = Regex.Replace(cleaned, "<style[\\s\\S]*?</style>", " ", RegexOptions.IgnoreCase);
        cleaned = Regex.Replace(cleaned, "<noscript[\\s\\S]*?</noscript>", " ", RegexOptions.IgnoreCase);
        cleaned = DecodeHtml(StripHtml(cleaned));
        return Condense(cleaned);
    }

    private static string StripHtml(string value)
        => Regex.Replace(value, "<[^>]+>", " ");

    private static string DecodeHtml(string value)
        => WebUtility.HtmlDecode(value);

    private static string Condense(string value)
        => Regex.Replace(value.Replace('\r', ' ').Replace('\n', ' '), "\\s{2,}", " ").Trim();

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
            return "Respuesta\nNo hubo una salida util del modelo.\n\nEvidencia\nNo tengo evidencia suficiente.\n\nPendientes\nIntenta reformular la consulta.";
        }

        var cleaned = Regex.Replace(answer, "<think>[\\s\\S]*?</think>", string.Empty, RegexOptions.IgnoreCase);
        cleaned = Regex.Replace(cleaned, "^(respuesta|output)\\s*:\\s*", string.Empty, RegexOptions.IgnoreCase);
        cleaned = Regex.Replace(cleaned, @"\n{3,}", "\n\n");
        return cleaned.Trim();
    }

    private static bool IsUsefulAnswer(string answer)
        => !string.IsNullOrWhiteSpace(answer)
            && answer.Length >= 60
            && !answer.Contains("No hubo una salida util", StringComparison.OrdinalIgnoreCase);

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

    private sealed record WebSearchResult(string Title, string Url);
    private sealed record WebKnowledgeItem(string Title, string Url, string Snippet);
}
