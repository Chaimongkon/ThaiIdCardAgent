using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using ThaiIdCardAgent.Core;
using ThaiIdCardAgent.Pcsc;
using ThaiIdCardAgent.ThaiCard;
using ThaiIdCardAgent.ThaiCard.Testing;

namespace ThaiIdCardAgent.Service.Tests;

/// <summary>
/// Phase 13A <c>POST /api/v1/card/read</c>.
/// </summary>
/// <remarks>
/// The citizen ID used throughout is a synthetic, checksum-valid value. No real citizen ID appears
/// in this repository.
/// </remarks>
public sealed class CardReadEndpointTests
{
    private const string DevelopmentKey = "test-development-key";
    private const string SyntheticCitizenId = "1101700207366";
    private static readonly RsaSecurityKey SigningKey = CreateSigningKey();

    // ---- Provider wiring ------------------------------------------------------------

    [Fact]
    public void Host_RegistersTheNotConfiguredProvider_NeverTheMock()
    {
        // Registering the mock in the host would make the endpoint return fabricated identity data.
        using var factory = CreateFactory("Development");

        var provider = factory.Services.GetRequiredService<IThaiCardDataProvider>();

        Assert.IsType<NotConfiguredThaiCardDataProvider>(provider);
        Assert.False(provider.IsConfigured);
        Assert.IsNotType<MockThaiCardDataProvider>(provider);
    }

    [Fact]
    public async Task NoProviderConfigured_Returns501WithoutAnyIdentityField()
    {
        using var factory = CreateFactory("Development");
        using var client = CreateDevelopmentClient(factory);

        var response = await client.PostAsync("/api/v1/card/read", JsonContent.Create(new { readerName = "Reader A" }));
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.NotImplemented, response.StatusCode);
        Assert.Contains(AgentErrorCodes.ThaiCardProtocolNotConfigured, body, StringComparison.Ordinal);
        Assert.DoesNotContain("citizenId", body, StringComparison.OrdinalIgnoreCase);
    }

    // ---- Authorization --------------------------------------------------------------

    [Fact]
    public async Task WithoutCardReadPermission_Returns403()
    {
        using var factory = CreateFactory("Production");
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", CreateToken(scope: "card.status"));

        var response = await client.PostAsync("/api/v1/card/read", JsonContent.Create(new { readerName = "Reader A" }));
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains(AgentErrorCodes.Forbidden, body, StringComparison.Ordinal);
        Assert.DoesNotContain("citizenId", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WithNoScopeClaimAtAll_Returns403()
    {
        using var factory = CreateFactory("Production");
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", CreateToken(scope: null));

        var response = await client.PostAsync("/api/v1/card/read", JsonContent.Create(new { readerName = "Reader A" }));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task WithCardReadPermission_PassesAuthorization()
    {
        using var factory = CreateFactory("Production", configureServices: UseProvider(MockThaiCardDataProvider.Returning(SyntheticCitizenId)));
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", CreateToken(scope: "card.read"));

        var response = await client.PostAsync("/api/v1/card/read", JsonContent.Create(new { readerName = "Reader A" }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task PermissionsClaim_IsAcceptedAlongsideScope()
    {
        using var factory = CreateFactory("Production", configureServices: UseProvider(MockThaiCardDataProvider.Returning(SyntheticCitizenId)));
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", CreateToken(scope: null, permissions: "card.read"));

        var response = await client.PostAsync("/api/v1/card/read", JsonContent.Create(new { readerName = "Reader A" }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ---- Token broker contract (closes the gap found in AUDIT-2026-08-07) -------------
    //
    // The Next.js broker mints tokens whose permissions live in a space-delimited `scope` claim
    // (examples/nextjs-client/lib/local-agent-jwt.ts). Before that change it emitted no permission
    // claim at all, so the browser could never reach this endpoint. These three tests pin the
    // Agent's half of that contract; tests/token-broker.test.ts pins the broker's half.

    /// <summary>A: the broker's default "status" token carries no scope claim at all.</summary>
    [Fact]
    public async Task BrokerStatusToken_HasNoScopeClaim_AndIsRejectedByCardReadPolicy()
    {
        using var factory = CreateFactory("Production", configureServices: UseProvider(MockThaiCardDataProvider.Returning(SyntheticCitizenId)));
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", CreateBrokerToken(cardRead: false));

        // A status token still works for the endpoints it is meant for.
        var readers = await client.GetAsync("/api/v1/readers");

        using var client2 = factory.CreateClient();
        client2.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", CreateBrokerToken(cardRead: false));
        var read = await client2.PostAsync("/api/v1/card/read", JsonContent.Create(new { readerName = "Reader A" }));

        Assert.Equal(HttpStatusCode.OK, readers.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, read.StatusCode);
        Assert.Contains(AgentErrorCodes.Forbidden, await read.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    /// <summary>B: the broker's "card-read" token satisfies CardReadPolicy and reaches the provider.</summary>
    [Fact]
    public async Task BrokerCardReadToken_PassesCardReadPolicy_AndReachesProvider()
    {
        using var factory = CreateFactory("Production", configureServices: UseProvider(MockThaiCardDataProvider.Returning(SyntheticCitizenId)));
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", CreateBrokerToken(cardRead: true));

        var response = await client.PostAsync("/api/v1/card/read", JsonContent.Create(new { readerName = "Reader A" }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(SyntheticCitizenId, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    /// <summary>C: with the provider the host actually registers, the same token yields 501.</summary>
    [Fact]
    public async Task BrokerCardReadToken_AgainstProductionProvider_Returns501NotConfigured()
    {
        // No provider override: this is exactly what Program.cs wires up.
        using var factory = CreateFactory("Production");
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", CreateBrokerToken(cardRead: true));

        var response = await client.PostAsync("/api/v1/card/read", JsonContent.Create(new { readerName = "Reader A" }));
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.NotImplemented, response.StatusCode);
        Assert.Contains(AgentErrorCodes.ThaiCardProtocolNotConfigured, body, StringComparison.Ordinal);
        Assert.DoesNotContain("citizenId", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WithoutAuthentication_Returns401()
    {
        using var factory = CreateFactory("Production");
        using var client = factory.CreateClient();

        var response = await client.PostAsync("/api/v1/card/read", JsonContent.Create(new { readerName = "Reader A" }));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ReplayedToken_IsRejected()
    {
        using var factory = CreateFactory("Production", configureServices: UseProvider(MockThaiCardDataProvider.Returning(SyntheticCitizenId)));
        using var client = factory.CreateClient();
        var token = CreateToken(scope: "card.read");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var first = await client.PostAsync("/api/v1/card/read", JsonContent.Create(new { readerName = "Reader A" }));
        var second = await client.PostAsync("/api/v1/card/read", JsonContent.Create(new { readerName = "Reader A" }));

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, second.StatusCode);
    }

    // ---- Successful read ------------------------------------------------------------

    [Fact]
    public async Task SuccessfulRead_ReturnsCitizenIdAndNoOtherPersonalField()
    {
        using var factory = CreateFactory("Development", configureServices: UseProvider(MockThaiCardDataProvider.Returning(SyntheticCitizenId, "official-mock")));
        using var client = CreateDevelopmentClient(factory);

        var response = await client.PostAsync("/api/v1/card/read", JsonContent.Create(new { readerName = "Reader A" }));
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(SyntheticCitizenId, body, StringComparison.Ordinal);
        Assert.Contains("verificationId", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("official-mock", body, StringComparison.Ordinal);

        // Phase 13A reads the citizen ID only. No other cardholder attribute may appear.
        foreach (var forbidden in new[] { "photo", "address", "religion", "birthDate", "thaiFirstName", "englishFirstName", "lastName" })
        {
            Assert.DoesNotContain(forbidden, body, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task SuccessfulRead_SetsNoStoreCacheHeaders()
    {
        using var factory = CreateFactory("Development", configureServices: UseProvider(MockThaiCardDataProvider.Returning(SyntheticCitizenId)));
        using var client = CreateDevelopmentClient(factory);

        var response = await client.PostAsync("/api/v1/card/read", JsonContent.Create(new { readerName = "Reader A" }));

        Assert.True(response.Headers.CacheControl?.NoStore, response.Headers.CacheControl?.ToString() ?? "(no Cache-Control)");
    }

    // ---- Failure paths --------------------------------------------------------------

    [Fact]
    public async Task NoCardPresent_Returns422()
    {
        var platform = new InMemorySmartCardPlatform();
        platform.SetReader("Reader A", SmartCardPresenceStatus.NoCard);
        using var factory = CreateFactory("Development", platform, UseProvider(MockThaiCardDataProvider.Returning(SyntheticCitizenId)));
        using var client = CreateDevelopmentClient(factory);

        var response = await client.PostAsync("/api/v1/card/read", JsonContent.Create(new { readerName = "Reader A" }));
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains(AgentErrorCodes.CardNotPresent, body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReaderNotFound_Returns404()
    {
        using var factory = CreateFactory("Development", configureServices: UseProvider(MockThaiCardDataProvider.Returning(SyntheticCitizenId)));
        using var client = CreateDevelopmentClient(factory);

        var response = await client.PostAsync("/api/v1/card/read", JsonContent.Create(new { readerName = "Reader Z" }));
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains(AgentErrorCodes.ReaderNotFound, body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvalidCardData_Returns422WithoutEchoingTheValue()
    {
        const string malformed = "110170020736X";
        using var factory = CreateFactory("Development", configureServices: UseProvider(MockThaiCardDataProvider.Returning(malformed)));
        using var client = CreateDevelopmentClient(factory);

        var response = await client.PostAsync("/api/v1/card/read", JsonContent.Create(new { readerName = "Reader A" }));
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains(AgentErrorCodes.CardDataInvalid, body, StringComparison.Ordinal);
        Assert.DoesNotContain(malformed, body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CardRemovedDuringRead_Returns409()
    {
        using var factory = CreateFactory("Development", configureServices: UseProvider(
            MockThaiCardDataProvider.Throwing(new CardRemovedDuringReadException("Reader A"))));
        using var client = CreateDevelopmentClient(factory);

        var response = await client.PostAsync("/api/v1/card/read", JsonContent.Create(new { readerName = "Reader A" }));
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains(AgentErrorCodes.CardRemovedDuringRead, body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CardCommunicationError_Returns502()
    {
        using var factory = CreateFactory("Development", configureServices: UseProvider(
            MockThaiCardDataProvider.Throwing(new CardCommunicationException("Reader A"))));
        using var client = CreateDevelopmentClient(factory);

        var response = await client.PostAsync("/api/v1/card/read", JsonContent.Create(new { readerName = "Reader A" }));
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Contains(AgentErrorCodes.CardCommunicationError, body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProviderUnavailable_Returns503()
    {
        using var factory = CreateFactory("Development", configureServices: UseProvider(MockThaiCardDataProvider.Unavailable()));
        using var client = CreateDevelopmentClient(factory);

        var response = await client.PostAsync("/api/v1/card/read", JsonContent.Create(new { readerName = "Reader A" }));
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains(AgentErrorCodes.ProviderUnavailable, body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadTimeout_Returns504()
    {
        using var factory = CreateFactory("Development", configureServices: services =>
        {
            UseProvider(MockThaiCardDataProvider.Hanging())(services);
            services.RemoveAll<ThaiCardReadSettings>();
            services.AddSingleton(new ThaiCardReadSettings { Timeout = TimeSpan.FromMilliseconds(200) });
        });
        using var client = CreateDevelopmentClient(factory);

        var response = await client.PostAsync("/api/v1/card/read", JsonContent.Create(new { readerName = "Reader A" }));
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
        Assert.Contains(AgentErrorCodes.CardReadTimeout, body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProductionError_IsSanitizedWithNoStackTrace()
    {
        using var factory = CreateFactory("Production", configureServices: UseProvider(
            MockThaiCardDataProvider.Throwing(new CardCommunicationException("Reader A", new InvalidOperationException("internal driver detail")))));
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", CreateToken(scope: "card.read"));

        var response = await client.PostAsync("/api/v1/card/read", JsonContent.Create(new { readerName = "Reader A" }));
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.DoesNotContain(" at ", body, StringComparison.Ordinal);
        Assert.DoesNotContain("internal driver detail", body, StringComparison.Ordinal);
        Assert.DoesNotContain("ThaiIdCardAgent.ThaiCard", body, StringComparison.Ordinal);
    }

    // ---- Double-read protection ------------------------------------------------------

    [Fact]
    public async Task ConcurrentReads_SecondIsRejectedAsBusy_AndOnlyOneCardReadOccurs()
    {
        var gate = new TaskCompletionSource();
        var started = new TaskCompletionSource();
        var provider = MockThaiCardDataProvider.Custom(async (context, _) =>
        {
            started.TrySetResult();
            await gate.Task.ConfigureAwait(false);
            return new ThaiIdCardIdentityResult(context.RequestId, context.ReaderName, SyntheticCitizenId, DateTimeOffset.UtcNow, "mock");
        });

        using var factory = CreateFactory("Development", configureServices: UseProvider(provider));
        using var client = CreateDevelopmentClient(factory);

        var first = client.PostAsync("/api/v1/card/read", JsonContent.Create(new { readerName = "Reader A" }));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = await client.PostAsync("/api/v1/card/read", JsonContent.Create(new { readerName = "Reader A" }));
        gate.SetResult();
        var firstResponse = await first;

        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Contains(AgentErrorCodes.AgentBusy, await second.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        // The physical card must have been read exactly once.
        Assert.Equal(1, provider.ReadAttempts);
    }

    // ---- Logging hygiene -------------------------------------------------------------

    [Fact]
    public async Task SuccessfulRead_NeverWritesTheCitizenIdToLogs()
    {
        var logSink = new CapturingLoggerProvider();
        using var factory = CreateFactory("Development", configureServices: services =>
        {
            UseProvider(MockThaiCardDataProvider.Returning(SyntheticCitizenId))(services);
            services.AddSingleton<ILoggerProvider>(logSink);
        });
        using var client = CreateDevelopmentClient(factory);

        var response = await client.PostAsync("/api/v1/card/read", JsonContent.Create(new { readerName = "Reader A" }));
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(SyntheticCitizenId, body, StringComparison.Ordinal);

        var logs = logSink.Snapshot();
        Assert.DoesNotContain(SyntheticCitizenId, logs, StringComparison.Ordinal);
        // The unmasked middle digits must not appear anywhere in the log either.
        Assert.DoesNotContain(SyntheticCitizenId.Substring(5, 5), logs, StringComparison.Ordinal);
        // The audit record itself should be present, with the masked form.
        Assert.Contains("IdentityVerification", logs, StringComparison.Ordinal);
        Assert.Contains("CardReadSucceeded", logs, StringComparison.Ordinal);
        Assert.Contains("xxxxx", logs, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FailedRead_WritesAuditWithSanitizedErrorCodeAndNoCardData()
    {
        const string malformed = "110170020736X";
        var logSink = new CapturingLoggerProvider();
        using var factory = CreateFactory("Development", configureServices: services =>
        {
            UseProvider(MockThaiCardDataProvider.Returning(malformed))(services);
            services.AddSingleton<ILoggerProvider>(logSink);
        });
        using var client = CreateDevelopmentClient(factory);

        await client.PostAsync("/api/v1/card/read", JsonContent.Create(new { readerName = "Reader A" }));

        var logs = logSink.Snapshot();
        Assert.Contains("CardReadFailed", logs, StringComparison.Ordinal);
        Assert.Contains(AgentErrorCodes.CardDataInvalid, logs, StringComparison.Ordinal);
        Assert.DoesNotContain(malformed, logs, StringComparison.Ordinal);
    }

    // ---- Helpers ---------------------------------------------------------------------

    private static Action<IServiceCollection> UseProvider(IThaiCardDataProvider provider) => services =>
    {
        services.RemoveAll<IThaiCardDataProvider>();
        services.AddSingleton(provider);
    };

    private static HttpClient CreateDevelopmentClient(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Agent-Development-Key", DevelopmentKey);
        return client;
    }

    private static WebApplicationFactory<Program> CreateFactory(
        string environment,
        InMemorySmartCardPlatform? platform = null,
        Action<IServiceCollection>? configureServices = null)
    {
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(environment);
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Security:DevelopmentApiKey"] = DevelopmentKey,
                ["Agent:Jwt:Issuer"] = "thai-id-card-agent-client",
                ["Agent:Jwt:Audience"] = "thai-id-card-agent",
                ["Agent:Jwt:PublicKeyPem"] = SigningKey.Rsa!.ExportSubjectPublicKeyInfoPem(),
                ["Agent:AllowedOrigins:0"] = "http://localhost:3000",
                ["Security:CitizenIdCorrelationKey"] = "test-correlation-key"
            }));
            builder.ConfigureTestServices(services =>
            {
                var fakePlatform = platform ?? CreateDefaultPlatform();
                services.RemoveAll<IPcscPlatform>();
                services.AddSingleton<IPcscPlatform>(fakePlatform);
                configureServices?.Invoke(services);
            });
        });
    }

    private static InMemorySmartCardPlatform CreateDefaultPlatform()
    {
        var platform = new InMemorySmartCardPlatform();
        platform.SetReader("Reader A", SmartCardPresenceStatus.CardPresent, [0x01, 0x02, 0x00]);
        return platform;
    }

    private static string CreateToken(string? scope, string? permissions = null)
    {
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
            new(JwtRegisteredClaimNames.Sub, "operator-1"),
            new("workstation_id", "workstation-1")
        };
        if (!string.IsNullOrWhiteSpace(scope))
        {
            claims.Add(new Claim("scope", scope));
        }

        if (!string.IsNullOrWhiteSpace(permissions))
        {
            claims.Add(new Claim("permissions", permissions));
        }

        var credentials = new SigningCredentials(SigningKey, SecurityAlgorithms.RsaSha256);
        var token = new JwtSecurityToken(
            "thai-id-card-agent-client",
            "thai-id-card-agent",
            claims,
            DateTime.UtcNow.AddSeconds(-1),
            DateTime.UtcNow.AddSeconds(30),
            credentials);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    /// <summary>
    /// Mints a token in exactly the shape the Next.js broker emits: the same claim set, and
    /// permissions in a space-delimited <c>scope</c> claim that is omitted entirely when no
    /// permission was requested.
    /// </summary>
    private static string CreateBrokerToken(bool cardRead)
        => CreateToken(scope: cardRead ? "card.read" : null);

    private static RsaSecurityKey CreateSigningKey() => new(RSA.Create(2048)) { KeyId = "test-key" };

    /// <summary>Captures every log message so tests can assert what did and did not reach the log.</summary>
    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> _messages = new();

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(_messages);

        public string Snapshot() => string.Join('\n', _messages);

        public void Dispose()
        {
        }

        private sealed class CapturingLogger : ILogger
        {
            private readonly ConcurrentQueue<string> _messages;

            public CapturingLogger(ConcurrentQueue<string> messages) => _messages = messages;

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                var builder = new StringBuilder(formatter(state, exception));
                if (exception is not null)
                {
                    builder.Append('\n').Append(exception);
                }

                _messages.Enqueue(builder.ToString());
            }
        }
    }
}
