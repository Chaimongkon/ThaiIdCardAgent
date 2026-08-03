using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Tokens;
using ThaiIdCardAgent.Core;
using ThaiIdCardAgent.Pcsc;

namespace ThaiIdCardAgent.Service.Tests;

public sealed class ServiceIntegrationTests
{
    private const string DevelopmentKey = "test-development-key";
    private const string SigningKey = "0123456789ABCDEF0123456789ABCDEF";

    [Fact]
    public async Task Health_DoesNotRequireAuthentication()
    {
        using var factory = CreateFactory("Development");
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/v1/health");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("Reader A", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Readers_RequiresAuthentication()
    {
        using var factory = CreateFactory("Development");
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/v1/readers");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Readers_AllowsDevelopmentKeyInDevelopment()
    {
        using var factory = CreateFactory("Development");
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Agent-Development-Key", DevelopmentKey);

        var response = await client.GetAsync("/api/v1/readers");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Reader A", body, StringComparison.OrdinalIgnoreCase);
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
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", CreateToken(DateTime.UtcNow, DateTime.UtcNow.AddSeconds(30), audience: "wrong"));

        var response = await client.GetAsync("/api/v1/info");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task MissingWorkstationId_IsRejectedInProduction()
    {
        using var factory = CreateFactory("Production");
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", CreateToken(DateTime.UtcNow, DateTime.UtcNow.AddSeconds(30), includeWorkstationId: false));

        var response = await client.GetAsync("/api/v1/info");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ReplayToken_IsRejectedInProduction()
    {
        using var factory = CreateFactory("Production");
        using var client = factory.CreateClient();
        var token = CreateToken(DateTime.UtcNow, DateTime.UtcNow.AddSeconds(30));
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
    public async Task ReadCard_ReturnsNotConfiguredErrorShape()
    {
        using var factory = CreateFactory("Development");
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Agent-Development-Key", DevelopmentKey);

        var response = await client.PostAsync("/api/v1/card/read", JsonContent.Create(new { readerName = "Reader A" }));
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.NotImplemented, response.StatusCode);
        Assert.Contains(AgentErrorCodes.ThaiCardProtocolNotConfigured, body, StringComparison.Ordinal);
        Assert.Contains("requestId", body, StringComparison.OrdinalIgnoreCase);
    }

    private static WebApplicationFactory<Program> CreateFactory(string environment)
    {
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(environment);
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Agent:DevelopmentKey"] = DevelopmentKey,
                    ["Agent:AllowedOrigins:0"] = "http://localhost:3000",
                    ["Agent:Jwt:Issuer"] = "thai-id-card-agent-client",
                    ["Agent:Jwt:Audience"] = "thai-id-card-agent",
                    ["Agent:Jwt:SymmetricSigningKey"] = SigningKey
                });
            });
            builder.ConfigureTestServices(services =>
            {
                var fake = new InMemorySmartCardPlatform();
                fake.SetReader("Reader A", SmartCardPresenceStatus.CardPresent, [0x3B, 0x67, 0x00]);
                services.RemoveAll<IPcscPlatform>();
                services.AddSingleton<IPcscPlatform>(fake);
            });
        });
    }

    private static string CreateToken(DateTime notBefore, DateTime expires, string audience = "thai-id-card-agent", bool includeWorkstationId = true)
    {
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
            new(JwtRegisteredClaimNames.Sub, "operator-1")
        };
        if (includeWorkstationId)
        {
            claims.Add(new Claim("workstation_id", "workstation-1"));
        }

        var credentials = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(SigningKey)), SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken("thai-id-card-agent-client", audience, claims, notBefore, expires, credentials);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}