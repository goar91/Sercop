using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;

namespace backend;

internal sealed record SercopPortalSessionSnapshot(
    string PortalSessionStatus,
    DateTimeOffset? LastPortalLoginAt
);

internal sealed record SercopPortalValidationResult(
    bool IsSuccess,
    string? FailureReason,
    SercopPortalSessionSnapshot SessionSnapshot
);

internal sealed class SercopAuthenticatedClient : IDisposable
{
    private const string ValidationStatusValidated = "validated";
    private const string ValidationStatusFailed = "failed";
    private const string PortalSessionAuthenticated = "authenticated";
    private const string PortalSessionError = "error";
    private const string PortalSessionNotAuthenticated = "not_authenticated";
    private const string DefaultPortalBaseUrl = "https://www.compraspublicas.gob.ec/ProcesoContratacion/compras/";
    private const string DefaultPortalUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/123.0.0.0 Safari/537.36";
    private static readonly Regex HtmlTagRegex = new("<[^>]+>", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly ISercopCredentialStore credentialStore;
    private readonly ILogger<SercopAuthenticatedClient> logger;
    private readonly SemaphoreSlim loginGate = new(1, 1);
    private readonly string portalBaseUrl;
    private readonly string portalUserAgent;
    private HttpMessageHandler? customHandler;
    private CookieContainer? cookieContainer;
    private HttpClient? httpClient;
    private SercopPortalSessionSnapshot sessionSnapshot = new(PortalSessionNotAuthenticated, null);

    public SercopAuthenticatedClient(ISercopCredentialStore credentialStore, ILogger<SercopAuthenticatedClient> logger, IConfiguration configuration)
        : this(credentialStore, logger, configuration, null)
    {
    }

    internal SercopAuthenticatedClient(ISercopCredentialStore credentialStore, ILogger<SercopAuthenticatedClient>? logger, IConfiguration configuration, HttpMessageHandler? handler)
    {
        this.credentialStore = credentialStore;
        this.logger = logger ?? NullLogger<SercopAuthenticatedClient>.Instance;
        portalBaseUrl = NormalizePortalBaseUrl(configuration["SERCOP_PORTAL_BASE_URL"] ?? DefaultPortalBaseUrl);
        portalUserAgent = (configuration["SERCOP_PORTAL_USER_AGENT"] ?? DefaultPortalUserAgent).Trim();
        if (string.IsNullOrWhiteSpace(portalUserAgent))
        {
            portalUserAgent = DefaultPortalUserAgent;
        }
        customHandler = handler;
        ResetTransport();
    }

    public SercopPortalSessionSnapshot GetSessionSnapshot()
        => sessionSnapshot;

    public async Task<SercopPortalValidationResult> ValidateStoredCredentialAsync(bool forceReauthenticate, CancellationToken cancellationToken)
    {
        var credential = await credentialStore.GetPortalCredentialAsync(cancellationToken);
        if (credential is null)
        {
            sessionSnapshot = new SercopPortalSessionSnapshot(PortalSessionNotAuthenticated, null);
            return new SercopPortalValidationResult(false, "No hay credenciales SERCOP configuradas para el backend.", sessionSnapshot);
        }

        await loginGate.WaitAsync(cancellationToken);
        try
        {
            if (!forceReauthenticate && string.Equals(sessionSnapshot.PortalSessionStatus, PortalSessionAuthenticated, StringComparison.Ordinal))
            {
                return new SercopPortalValidationResult(true, null, sessionSnapshot);
            }

            ResetTransport();

            try
            {
                var hashedPassword = ComputeMd5Hex(credential.Password);
                await InitializeLoginSessionAsync(cancellationToken);
                var verificationToken = await ExecutePreLoginAsync(credential, hashedPassword, cancellationToken);
                await CompleteLoginAsync(credential, hashedPassword, verificationToken, cancellationToken);

                var validatedAt = DateTimeOffset.UtcNow;
                sessionSnapshot = new SercopPortalSessionSnapshot(PortalSessionAuthenticated, validatedAt);
                await credentialStore.UpdateValidationAsync(ValidationStatusValidated, validatedAt, null, cancellationToken);
                logger.LogInformation("Sesion SERCOP autenticada para la cuenta compartida {MaskedUser}.", MaskForLogs(credential.UserName));
                return new SercopPortalValidationResult(true, null, sessionSnapshot);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                var validatedAt = DateTimeOffset.UtcNow;
                var failureReason = ExtractFailureReason(exception);
                sessionSnapshot = new SercopPortalSessionSnapshot(PortalSessionError, null);
                await credentialStore.UpdateValidationAsync(ValidationStatusFailed, validatedAt, failureReason, cancellationToken);
                logger.LogWarning(exception, "Fallo la autenticacion SERCOP para la cuenta compartida {MaskedUser}.", MaskForLogs(credential.UserName));
                return new SercopPortalValidationResult(false, failureReason, sessionSnapshot);
            }
        }
        finally
        {
            loginGate.Release();
        }
    }

    public void ClearSession()
    {
        loginGate.Wait();
        try
        {
            sessionSnapshot = new SercopPortalSessionSnapshot(PortalSessionNotAuthenticated, null);
            ResetTransport();
        }
        finally
        {
            loginGate.Release();
        }
    }

    public async Task<T?> ExecuteAuthenticatedAsync<T>(Func<HttpClient, Task<T>> work, CancellationToken cancellationToken)
    {
        var validation = await ValidateStoredCredentialAsync(forceReauthenticate: false, cancellationToken);
        if (!validation.IsSuccess)
        {
            return default;
        }

        await loginGate.WaitAsync(cancellationToken);
        try
        {
            if (!string.Equals(sessionSnapshot.PortalSessionStatus, PortalSessionAuthenticated, StringComparison.Ordinal)
                || httpClient is null)
            {
                return default;
            }

            return await work(httpClient);
        }
        finally
        {
            loginGate.Release();
        }
    }

    public void Dispose()
    {
        httpClient?.Dispose();
        if (customHandler is IDisposable disposableHandler)
        {
            disposableHandler.Dispose();
        }

        loginGate.Dispose();
    }

    private async Task InitializeLoginSessionAsync(CancellationToken cancellationToken)
    {
        using var response = await httpClient!.GetAsync(portalBaseUrl, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private async Task<string> ExecutePreLoginAsync(SercopConfiguredCredential credential, string hashedPassword, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, BuildAbsoluteUri("servicio/interfazWeb.php"))
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__class"] = "Usuario",
                ["__action"] = "hacerLoginAjaxV1",
                ["txtRUCRecordatorio"] = credential.Ruc,
                ["txtLogin"] = credential.UserName,
                ["txtPassword"] = hashedPassword,
            })
        };
        request.Headers.Referrer = new Uri(portalBaseUrl);
        request.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
        request.Headers.TryAddWithoutValidation("X-Prototype-Version", "1.6.0");
        request.Headers.TryAddWithoutValidation("Accept", "text/javascript, text/html, application/xml, text/xml, */*");

        using var response = await httpClient!.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var ajaxPayload = await ReadAjaxPayloadAsync(response, cancellationToken);
        if (!string.Equals(ajaxPayload.Code, "RUU", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(ajaxPayload.Code, "RDU", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(ajaxPayload.Message)
                ? $"SERCOP rechazo el prelogin con codigo {ajaxPayload.Code ?? "desconocido"}."
                : ajaxPayload.Message);
        }

        if (string.IsNullOrWhiteSpace(ajaxPayload.VerificationToken))
        {
            var keys = ajaxPayload.Keys.Count > 0 ? string.Join(", ", ajaxPayload.Keys) : "ninguno";
            var message = string.IsNullOrWhiteSpace(ajaxPayload.Message) ? string.Empty : $" Mensaje={ajaxPayload.Message}.";
            throw new InvalidOperationException($"SERCOP no devolvio el token de verificacion del login (campo 'b'). Codigo={ajaxPayload.Code ?? "desconocido"}. Campos={keys}.{message}");
        }

        return ajaxPayload.VerificationToken;
    }

    private async Task CompleteLoginAsync(SercopConfiguredCredential credential, string hashedPassword, string verificationToken, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, BuildAbsoluteUri("exe/login_exe_2.php"))
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["txtRUCRecordatorio"] = credential.Ruc,
                ["txtLogin"] = credential.UserName,
                ["txtPassword"] = hashedPassword,
                ["txtVerifica"] = verificationToken,
            })
        };
        request.Headers.Referrer = new Uri(portalBaseUrl);

        using var response = await httpClient!.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var content = await response.Content.ReadAsStringAsync(cancellationToken);

        if (LooksLikeLoginPage(response, content))
        {
            throw new InvalidOperationException("SERCOP no confirmo la sesion autenticada en el portal.");
        }
    }

    private void ResetTransport()
    {
        httpClient?.Dispose();
        httpClient = null;

        if (customHandler is not null)
        {
            httpClient = new HttpClient(customHandler, disposeHandler: false)
            {
                Timeout = TimeSpan.FromSeconds(45),
                BaseAddress = new Uri(portalBaseUrl),
            };
            httpClient.DefaultRequestHeaders.UserAgent.Clear();
            httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(portalUserAgent);
            return;
        }

        cookieContainer = new CookieContainer();
        var handler = new HttpClientHandler
        {
            CookieContainer = cookieContainer,
            UseCookies = true,
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
        };

        httpClient = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(45),
            BaseAddress = new Uri(portalBaseUrl),
        };
        httpClient.DefaultRequestHeaders.UserAgent.Clear();
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(portalUserAgent);
    }

    private Uri BuildAbsoluteUri(string relativePath)
        => new(new Uri(portalBaseUrl), relativePath);

    private static string NormalizePortalBaseUrl(string value)
        => value.EndsWith("/", StringComparison.Ordinal) ? value : $"{value}/";

    private static string ComputeMd5Hex(string value)
    {
        using var md5 = MD5.Create();
        var bytes = md5.ComputeHash(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static bool LooksLikeLoginPage(HttpResponseMessage response, string content)
    {
        if (content.Contains("id=\"frmIngreso\"", StringComparison.OrdinalIgnoreCase)
            || content.Contains("Ingreso al Sistema - Compras Públicas", StringComparison.OrdinalIgnoreCase)
            || content.Contains("txtLogin", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var finalUri = response.RequestMessage?.RequestUri?.AbsoluteUri ?? string.Empty;
        return string.Equals(finalUri.TrimEnd('/'), DefaultPortalBaseUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<AjaxLoginPayload> ReadAjaxPayloadAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var rawPayload = TryReadHeader(response, "X-JSON");
        if (string.IsNullOrWhiteSpace(rawPayload))
        {
            rawPayload = await response.Content.ReadAsStringAsync(cancellationToken);
        }

        rawPayload = rawPayload.Trim();
        if (rawPayload.StartsWith('(') && rawPayload.EndsWith(')'))
        {
            rawPayload = rawPayload[1..^1];
        }

        using var document = JsonDocument.Parse(rawPayload);
        var root = document.RootElement;
        var keys = root.ValueKind == JsonValueKind.Object
            ? root.EnumerateObject().Select(property => property.Name).ToArray()
            : [];
        var code = root.TryGetProperty("a", out var codeElement) ? codeElement.GetString() : null;
        var verificationToken = root.TryGetProperty("b", out var tokenElement) ? tokenElement.GetString() : null;
        if (string.IsNullOrWhiteSpace(verificationToken)
            && (string.Equals(code, "RUU", StringComparison.OrdinalIgnoreCase)
                || string.Equals(code, "RDU", StringComparison.OrdinalIgnoreCase))
            && root.TryGetProperty("estado", out var stateElement))
        {
            verificationToken = stateElement.ValueKind switch
            {
                JsonValueKind.String => stateElement.GetString(),
                JsonValueKind.Number => stateElement.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => stateElement.GetRawText()
            };
        }
        var message = root.TryGetProperty("mensaje", out var messageElement) ? ExtractPlainText(messageElement.GetString()) : null;
        return new AjaxLoginPayload(code, verificationToken, message, keys);
    }

    private static string? TryReadHeader(HttpResponseMessage response, string headerName)
    {
        if (!response.Headers.TryGetValues(headerName, out var values))
        {
            return null;
        }

        return values.FirstOrDefault();
    }

    private static string ExtractFailureReason(Exception exception)
        => string.IsNullOrWhiteSpace(exception.Message)
            ? "No se pudo autenticar la cuenta SERCOP compartida."
            : exception.Message.Trim();

    private static string? ExtractPlainText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var withoutTags = HtmlTagRegex.Replace(value, " ");
        var decoded = WebUtility.HtmlDecode(withoutTags);
        var normalized = Regex.Replace(decoded, "\\s+", " ").Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private static string MaskForLogs(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "***";
        }

        return value.Length <= 4 ? "***" : $"{value[..2]}***{value[^2..]}";
    }

    private sealed record AjaxLoginPayload(
        string? Code,
        string? VerificationToken,
        string? Message,
        IReadOnlyList<string> Keys
    );
}
