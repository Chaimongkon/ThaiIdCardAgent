using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using ThaiIdCardAgent.Core;
using ThaiIdCardAgent.Pcsc;

namespace ThaiIdCardAgent.Service.Tests;

public sealed class ServiceIntegrationTests
{
    private const string DevelopmentKey = "test-development-key";
    private static readonly RsaSecurityKey SigningKey = CreateSigningKey();

    [Fact]
    public async Task Health_DoesNotRequireAuthentication()
    {
        using var factory = CreateFactory("Development");
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/v1/health");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("healthy", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("version", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("utcTime", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Reader A", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("01-02-00", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Readers_WithoutAuthentication_Returns401ErrorShape()
    {
        using var factory = CreateFactory("Development");
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/v1/readers");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains(AgentErrorCodes.Unauthorized, body, StringComparison.Ordinal);
        Assert.Contains("requestId", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("success", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Readers_AllowsDevelopmentKeyInDevelopment()
    {
        using var factory = CreateFactory("Development");
        using var client = CreateDevelopmentClient(factory);

        var response = await client.GetAsync("/api/v1/readers");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Reader A", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("requestId", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Readers_RejectsWrongDevelopmentKey()
    {
        using var factory = CreateFactory("Development");
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Agent-Development-Key", "wrong");

        var response = await client.GetAsync("/api/v1/readers");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task DevelopmentKey_IsRejectedInProduction()
    {
        using var factory = CreateFactory("Production");
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Agent-Development-Key", DevelopmentKey);

        var response = await client.GetAsync("/api/v1/readers");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task InvalidToken_IsRejectedInProduction()
    {
        using var factory = CreateFactory("Production");
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "invalid");

        var response = await client.GetAsync("/api/v1/info");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ExpiredToken_IsRejectedInProduction()
    {
        using var factory = CreateFactory("Production");
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", CreateToken(DateTime.UtcNow.AddMinutes(-2), DateTime.UtcNow.AddMinutes(-1)));

        var response = await client.GetAsync("/api/v1/info");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task WrongAudience_IsRejectedInProduction()
    {
        using var factory = CreateFactory("Production");
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", CreateToken(DateTime.UtcNow.AddSeconds(-1), DateTime.UtcNow.AddSeconds(30), audience: "wrong"));

        var response = await client.GetAsync("/api/v1/info");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task MissingJti_IsRejectedInProduction()
    {
        using var factory = CreateFactory("Production");
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", CreateToken(DateTime.UtcNow.AddSeconds(-1), DateTime.UtcNow.AddSeconds(30), includeJti: false));

        var response = await client.GetAsync("/api/v1/info");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task MissingSubject_IsRejectedInProduction()
    {
        using var factory = CreateFactory("Production");
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", CreateToken(DateTime.UtcNow.AddSeconds(-1), DateTime.UtcNow.AddSeconds(30), includeSubject: false));

        var response = await client.GetAsync("/api/v1/info");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task MissingWorkstationId_IsRejectedInProduction()
    {
        using var factory = CreateFactory("Production");
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", CreateToken(DateTime.UtcNow.AddSeconds(-1), DateTime.UtcNow.AddSeconds(30), includeWorkstationId: false));

        var response = await client.GetAsync("/api/v1/info");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ReplayToken_IsRejectedInProduction()
    {
        using var factory = CreateFactory("Production");
        using var client = factory.CreateClient();
        var token = CreateToken(DateTime.UtcNow.AddSeconds(-1), DateTime.UtcNow.AddSeconds(30));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var first = await client.GetAsync("/api/v1/info");
        var second = await client.GetAsync("/api/v1/info");

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, second.StatusCode);
    }

    [Fact]
    public async Task AllowedCorsOrigin_IsReturned()
    {
        using var factory = CreateFactory("Development");
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Options, "/api/v1/readers");
        request.Headers.Add("Origin", "http://localhost:3000");
        request.Headers.Add("Access-Control-Request-Method", "GET");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.True(response.Headers.TryGetValues("Access-Control-Allow-Origin", out var values));
        Assert.Equal("http://localhost:3000", Assert.Single(values));
    }

    [Fact]
    public async Task RejectedCorsOrigin_IsNotReturned()
    {
        using var factory = CreateFactory("Development");
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Options, "/api/v1/readers");
        request.Headers.Add("Origin", "http://evil.example");
        request.Headers.Add("Access-Control-Request-Method", "GET");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public void WildcardCorsOrigin_IsRejectedByOptionsValidation()
    {
        using var factory = CreateFactory("Development", allowedOrigins: ["*"]);

        Assert.Throws<OptionsValidationException>(() => factory.Services.GetRequiredService<IOptions<AgentSecurityOptions>>().Value);
    }

    [Fact]
    public async Task CardStatus_OneReader_SelectsAutomatically()
    {
        using var factory = CreateFactory("Development");
        using var client = CreateDevelopmentClient(factory);

        var response = await client.GetAsync("/api/v1/card/status");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("CardPresent", body, StringComparison.Ordinal);
        Assert.Contains("01-02-00", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CardStatus_MultipleReaders_RequiresSelection()
    {
        var platform = new InMemorySmartCardPlatform();
        platform.SetReader("Reader A", SmartCardPresenceStatus.CardPresent, [0x01]);
        platform.SetReader("Reader B", SmartCardPresenceStatus.NoCard);
        using var factory = CreateFactory("Development", platform);
        using var client = CreateDevelopmentClient(factory);

        var response = await client.GetAsync("/api/v1/card/status");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains(AgentErrorCodes.ReaderSelectionRequired, body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CardStatus_ReaderNotFound_Returns404()
    {
        using var factory = CreateFactory("Development");
        using var client = CreateDevelopmentClient(factory);

        var response = await client.GetAsync("/api/v1/card/status?readerName=Missing");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains(AgentErrorCodes.ReaderNotFound, body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CardStatus_NoCard_ReturnsNoCardWithoutCrash()
    {
        var platform = new InMemorySmartCardPlatform();
        platform.SetReader("Reader A", SmartCardPresenceStatus.NoCard);
        using var factory = CreateFactory("Development", platform);
        using var client = CreateDevelopmentClient(factory);

        var response = await client.GetAsync("/api/v1/card/status");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("NoCard", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CardAtr_NoCard_Returns422()
    {
        var platform = new InMemorySmartCardPlatform();
        platform.SetReader("Reader A", SmartCardPresenceStatus.NoCard);
        using var factory = CreateFactory("Development", platform);
        using var client = CreateDevelopmentClient(factory);

        var response = await client.PostAsync("/api/v1/card/atr", JsonContent.Create(new { readerName = (string?)null }));
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains(AgentErrorCodes.CardNotPresent, body, StringComparison.Ordinal);
        Assert.Contains("requestId", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CardAtr_WithCard_ReturnsAtr()
    {
        using var factory = CreateFactory("Development");
        using var client = CreateDevelopmentClient(factory);

        var response = await client.PostAsync("/api/v1/card/atr", JsonContent.Create(new { readerName = (string?)null }));
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("01-02-00", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CardAtr_ConcurrentSameReader_ReturnsAgentBusy()
    {
        var busyService = new BlockingReaderService();
        using var factory = CreateFactory("Development", configureServices: services =>
        {
            services.RemoveAll<ISmartCardReaderService>();
            services.AddSingleton<ISmartCardReaderService>(busyService);
        });
        using var client = CreateDevelopmentClient(factory);

        var first = client.PostAsync("/api/v1/card/atr", JsonContent.Create(new { readerName = (string?)null }));
        await busyService.FirstReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = await client.PostAsync("/api/v1/card/atr", JsonContent.Create(new { readerName = (string?)null }));
        busyService.ReleaseFirstRead.SetResult();
        await first;
        var body = await second.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Contains(AgentErrorCodes.AgentBusy, body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadCard_ReturnsNotConfiguredErrorShape()
    {
        using var factory = CreateFactory("Development");
        using var client = CreateDevelopmentClient(factory);

        var response = await client.PostAsync("/api/v1/card/read", JsonContent.Create(new { readerName = "Reader A", options = new { readCitizenId = true } }));
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.NotImplemented, response.StatusCode);
        Assert.Contains(AgentErrorCodes.ThaiCardProtocolNotConfigured, body, StringComparison.Ordinal);
        Assert.Contains("requestId", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("success", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("citizenId", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ProductionError_DoesNotExposeStackTrace()
    {
        using var factory = CreateFactory("Production");
        using var client = CreateProductionClient(factory);

        var response = await client.GetAsync("/api/v1/card/status?readerName=Missing");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.DoesNotContain(" at ", body, StringComparison.Ordinal);
        Assert.DoesNotContain("ThaiIdCardAgent.Service", body, StringComparison.Ordinal);
    }

    private static HttpClient CreateDevelopmentClient(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Agent-Development-Key", DevelopmentKey);
        return client;
    }

    private static HttpClient CreateProductionClient(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", CreateToken(DateTime.UtcNow.AddSeconds(-1), DateTime.UtcNow.AddSeconds(30)));
        return client;
    }

    private static WebApplicationFactory<Program> CreateFactory(
        string environment,
        InMemorySmartCardPlatform? platform = null,
        string[]? allowedOrigins = null,
        Action<IServiceCollection>? configureServices = null)
    {
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(environment);
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                var publicKeyPem = SigningKey.Rsa!.ExportSubjectPublicKeyInfoPem();
                var values = new Dictionary<string, string?>
                {
                    ["Security:DevelopmentApiKey"] = DevelopmentKey,
                    ["Agent:Jwt:Issuer"] = "thai-id-card-agent-client",
                    ["Agent:Jwt:Audience"] = "thai-id-card-agent",
                    ["Agent:Jwt:PublicKeyPem"] = publicKeyPem
                };
                var origins = allowedOrigins ?? ["http://localhost:3000"];
                for (var index = 0; index < origins.Length; index++)
                {
                    values[$"Agent:AllowedOrigins:{index}"] = origins[index];
                }

                configuration.AddInMemoryCollection(values);
            });
            builder.ConfigureTestServices(services =>
            {
                if (platform is not null)
                {
                    services.RemoveAll<IPcscPlatform>();
                    services.AddSingleton<IPcscPlatform>(platform);
                }
                else if (configureServices is null)
                {
                    var fake = new InMemorySmartCardPlatform();
                    fake.SetReader("Reader A", SmartCardPresenceStatus.CardPresent, [0x01, 0x02, 0x00]);
                    services.RemoveAll<IPcscPlatform>();
                    services.AddSingleton<IPcscPlatform>(fake);
                }

                configureServices?.Invoke(services);
            });
        });
    }

    private static string CreateToken(
        DateTime notBefore,
        DateTime expires,
        string audience = "thai-id-card-agent",
        bool includeJti = true,
        bool includeSubject = true,
        bool includeWorkstationId = true)
    {
        var claims = new List<Claim>();
        if (includeJti)
        {
            claims.Add(new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")));
        }

        if (includeSubject)
        {
            claims.Add(new Claim(JwtRegisteredClaimNames.Sub, "operator-1"));
        }

        if (includeWorkstationId)
        {
            claims.Add(new Claim("workstation_id", "workstation-1"));
        }

        var credentials = new SigningCredentials(SigningKey, SecurityAlgorithms.RsaSha256);
        var token = new JwtSecurityToken("thai-id-card-agent-client", audience, claims, notBefore, expires, credentials);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static RsaSecurityKey CreateSigningKey()
    {
        var rsa = RSA.Create(2048);
        return new RsaSecurityKey(rsa) { KeyId = "test-key" };
    }

    private sealed class BlockingReaderService : ISmartCardReaderService
    {
        private readonly SemaphoreSlim _gate = new(1, 1);

        public TaskCompletionSource FirstReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseFirstRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<IReadOnlyList<SmartCardReaderInfo>> GetReadersAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<SmartCardReaderInfo>>([
                new SmartCardReaderInfo("Reader A", true, true, "01", DateTimeOffset.UtcNow)
            ]);
        }

        public Task<SmartCardStatus> GetStatusAsync(string readerName, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new SmartCardStatus(readerName, SmartCardPresenceStatus.CardPresent, "01", DateTimeOffset.UtcNow));
        }

        public async Task<byte[]> GetAtrAsync(string readerName, CancellationToken cancellationToken = default)
        {
            if (!await _gate.WaitAsync(TimeSpan.Zero, cancellationToken).ConfigureAwait(false))
            {
                throw new SmartCardBusyException(readerName);
            }

            try
            {
                FirstReadStarted.TrySetResult();
                await ReleaseFirstRead.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                return [0x01];
            }
            finally
            {
                _gate.Release();
            }
        }
    }
}
