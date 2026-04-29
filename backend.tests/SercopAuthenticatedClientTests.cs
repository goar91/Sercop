using System.Net;
using System.Net.Http;
using System.Text;
using backend;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace backend.tests;

public sealed class SercopAuthenticatedClientTests
{
    private const string PortalBaseUrl = "https://portal.test/ProcesoContratacion/compras/";

    [Fact]
    public async Task ValidateStoredCredentialAsync_authenticates_and_reuses_session()
    {
        var store = new FakeCredentialStore(new SercopConfiguredCredential("1790012345001", "usuario.portal", "ClaveSegura1"));
        var handler = new SequenceHandler(
            CreateResponse(HttpStatusCode.OK, "<html><form id=\"frmIngreso\"></form></html>", PortalBaseUrl),
            CreateAjaxResponse("""({"a":"RUU","b":"token-validacion"})"""),
            CreateResponse(HttpStatusCode.OK, "<html><body>Bienvenido al portal</body></html>", $"{PortalBaseUrl}EP/home.cpe"));
        using var client = CreateClient(store, handler);

        var first = await client.ValidateStoredCredentialAsync(forceReauthenticate: true, CancellationToken.None);
        var second = await client.ValidateStoredCredentialAsync(forceReauthenticate: false, CancellationToken.None);

        Assert.True(first.IsSuccess);
        Assert.True(second.IsSuccess);
        Assert.Equal("validated", store.ValidationStatus);
        Assert.Null(store.ValidationError);
        Assert.Equal("authenticated", second.SessionSnapshot.PortalSessionStatus);
        Assert.NotNull(second.SessionSnapshot.LastPortalLoginAt);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task ValidateStoredCredentialAsync_records_validation_error_when_prelogin_fails()
    {
        var store = new FakeCredentialStore(new SercopConfiguredCredential("1790012345001", "usuario.portal", "ClaveSegura1"));
        using var client = CreateClient(store, new SequenceHandler(
            CreateResponse(HttpStatusCode.OK, "<html><form id=\"frmIngreso\"></form></html>", PortalBaseUrl),
            CreateAjaxResponse("""({"a":"SDR","mensaje":"<div><strong>Error de ingreso</strong><span>Credenciales invalidas</span></div>"})""")));

        var result = await client.ValidateStoredCredentialAsync(forceReauthenticate: true, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("failed", store.ValidationStatus);
        Assert.Equal("error", result.SessionSnapshot.PortalSessionStatus);
        Assert.Contains("Credenciales invalidas", store.ValidationError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ValidateStoredCredentialAsync_returns_not_authenticated_when_credentials_are_missing()
    {
        var store = new FakeCredentialStore(null);
        using var client = CreateClient(store, new SequenceHandler());

        var result = await client.ValidateStoredCredentialAsync(forceReauthenticate: true, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("not_authenticated", result.SessionSnapshot.PortalSessionStatus);
        Assert.Null(store.ValidationStatus);
    }

    private static SercopAuthenticatedClient CreateClient(FakeCredentialStore store, SequenceHandler handler)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SERCOP_PORTAL_BASE_URL"] = PortalBaseUrl,
            })
            .Build();

        return new SercopAuthenticatedClient(store, NullLogger<SercopAuthenticatedClient>.Instance, configuration, handler);
    }

    private static HttpResponseMessage CreateResponse(HttpStatusCode statusCode, string content, string requestUri)
        => new(statusCode)
        {
            Content = new StringContent(content, Encoding.UTF8, "text/html"),
            RequestMessage = new HttpRequestMessage(HttpMethod.Get, requestUri),
        };

    private static HttpResponseMessage CreateAjaxResponse(string payload)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(string.Empty, Encoding.UTF8, "application/json"),
            RequestMessage = new HttpRequestMessage(HttpMethod.Post, $"{PortalBaseUrl}servicio/interfazWeb.php"),
        };
        response.Headers.TryAddWithoutValidation("X-JSON", payload);
        return response;
    }

    private sealed class FakeCredentialStore : ISercopCredentialStore
    {
        private SercopConfiguredCredential? credential;

        public FakeCredentialStore(SercopConfiguredCredential? credential)
        {
            this.credential = credential;
        }

        public string? ValidationStatus { get; private set; }
        public string? ValidationError { get; private set; }

        public Task ClearAsync(CancellationToken cancellationToken)
        {
            credential = null;
            ValidationStatus = null;
            ValidationError = null;
            return Task.CompletedTask;
        }

        public Task<SercopConfiguredCredential?> GetPortalCredentialAsync(CancellationToken cancellationToken)
            => Task.FromResult(credential);

        public Task<SercopCredentialStatusDto> GetStatusAsync(CancellationToken cancellationToken)
            => Task.FromResult(new SercopCredentialStatusDto(
                credential is not null,
                credential is null ? null : "17***01",
                credential is null ? null : "us***al",
                "admin",
                DateTimeOffset.UtcNow,
                ValidationStatus,
                null,
                ValidationError,
                "not_authenticated",
                null));

        public Task SaveAsync(string ruc, string userName, string password, long? configuredByUserId, string? configuredByLoginName, CancellationToken cancellationToken)
        {
            credential = new SercopConfiguredCredential(ruc, userName, password);
            return Task.CompletedTask;
        }

        public Task UpdateValidationAsync(string validationStatus, DateTimeOffset validatedAt, string? validationError, CancellationToken cancellationToken)
        {
            ValidationStatus = validationStatus;
            ValidationError = validationError;
            return Task.CompletedTask;
        }
    }

    private sealed class SequenceHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> responses = new(responses);

        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (responses.Count == 0)
            {
                throw new InvalidOperationException("No hay mas respuestas configuradas para la prueba.");
            }

            var response = responses.Dequeue();
            response.RequestMessage ??= request;
            return Task.FromResult(response);
        }
    }
}
