using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
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
    public async Task PublicKeyPath_AllowsProductionJwtValidation()
    {
        var publicKeyPath = Path.Combine(Path.GetTempPath(), $"thai-id-agent-public-{Guid.NewGuid():N}.pem");
        await File.WriteAllTextAsync(publicKeyPath, SigningKey.Rsa!.ExportSubjectPublicKeyInfoPem());
        try
        {
            using var factory = CreateFactory("Production", publicKeyPath: publicKeyPath);
            using var client = CreateProductionClient(factory);

            var response = await client.GetAsync("/api/v1/info");
            var body = await response.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains("workstation-1", body, StringComparison.Ordinal);
            Assert.Contains("runtimeVersion", body, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(publicKeyPath);
        }
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

    [Fact]
    public async Task Events_CardRemoved_StreamsSseEvent()
    {
        var platform = new InMemorySmartCardPlatform();
        platform.SetReader("Reader A", SmartCardPresenceStatus.CardPresent, [0x01, 0x02]);
        using var factory = CreateFactory("Development", platform);
        using var client = CreateDevelopmentClient(factory);
        client.Timeout = Timeout.InfiniteTimeSpan;

        using var response = await client.GetAsync("/api/v1/events", HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var stream = await response.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream);
        _ = await ReadSseEventAsync(reader, "CardInserted", TimeSpan.FromSeconds(5));

        platform.SetReader("Reader A", SmartCardPresenceStatus.NoCard);

        var readerEvent = await ReadSseEventAsync(reader, "CardRemoved", TimeSpan.FromSeconds(5));
        Assert.Equal("Reader A", readerEvent.ReaderName);
        Assert.Equal("CardRemoved", readerEvent.EventType);
        Assert.Null(readerEvent.Atr);
        Assert.True(readerEvent.OccurredAtUtc > DateTimeOffset.UtcNow.AddMinutes(-1));
    }

    [Fact]
    public async Task Events_CardInserted_StreamsSseEventWithSafeAtr()
    {
        var platform = new InMemorySmartCardPlatform();
        platform.SetReader("Reader A", SmartCardPresenceStatus.NoCard);
        using var factory = CreateFactory("Development", platform);
        using var client = CreateDevelopmentClient(factory);
        client.Timeout = Timeout.InfiniteTimeSpan;

        using var response = await client.GetAsync("/api/v1/events", HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var stream = await response.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream);
        _ = await ReadSseEventAsync(reader, "ReaderConnected", TimeSpan.FromSeconds(5));

        platform.SetReader("Reader A", SmartCardPresenceStatus.CardPresent, [0x3B, 0x79, 0x96]);

        var readerEvent = await ReadSseEventAsync(reader, "CardInserted", TimeSpan.FromSeconds(5));
        Assert.Equal("Reader A", readerEvent.ReaderName);
        Assert.Equal("CardInserted", readerEvent.EventType);
        Assert.Equal("3B-79-96", readerEvent.Atr);
        Assert.Matches("^([0-9A-F]{2})(-[0-9A-F]{2})*$", readerEvent.Atr);
        Assert.True(readerEvent.OccurredAtUtc > DateTimeOffset.UtcNow.AddMinutes(-1));
    }

    [Fact]
    public async Task Events_ClientCancellation_StopsAndDisposesSubscription()
    {
        var monitor = new TrackingMonitor();
        using var factory = CreateFactory("Development", configureServices: services =>
        {
            services.RemoveAll<ISmartCardMonitor>();
            services.AddSingleton<ISmartCardMonitor>(monitor);
        });
        using var client = CreateDevelopmentClient(factory);
        client.Timeout = Timeout.InfiniteTimeSpan;

        using var response = await client.GetAsync("/api/v1/events", HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await monitor.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        response.Dispose();

        await monitor.Stopped.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await monitor.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Events_WaitingForMissingEvent_TimesOutInTestClient()
    {
        var platform = new InMemorySmartCardPlatform();
        platform.SetReader("Reader A", SmartCardPresenceStatus.NoCard);
        using var factory = CreateFactory("Development", platform);
        using var client = CreateDevelopmentClient(factory);
        client.Timeout = Timeout.InfiniteTimeSpan;

        using var response = await client.GetAsync("/api/v1/events", HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var stream = await response.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream);

        await Assert.ThrowsAsync<TimeoutException>(() => ReadSseEventAsync(reader, "CardInserted", TimeSpan.FromMilliseconds(500)));
    }

    [Fact]
    public async Task Events_MultipleSubscribersReceiveCardRemoved()
    {
        var platform = new InMemorySmartCardPlatform();
        platform.SetReader("Reader A", SmartCardPresenceStatus.CardPresent, [0x01]);
        using var factory = CreateFactory("Development", platform);
        using var firstClient = CreateDevelopmentClient(factory);
        using var secondClient = CreateDevelopmentClient(factory);
        firstClient.Timeout = Timeout.InfiniteTimeSpan;
        secondClient.Timeout = Timeout.InfiniteTimeSpan;

        using var firstResponse = await firstClient.GetAsync("/api/v1/events", HttpCompletionOption.ResponseHeadersRead);
        using var secondResponse = await secondClient.GetAsync("/api/v1/events", HttpCompletionOption.ResponseHeadersRead);
        await using var firstStream = await firstResponse.Content.ReadAsStreamAsync();
        await using var secondStream = await secondResponse.Content.ReadAsStreamAsync();
        using var firstReader = new StreamReader(firstStream);
        using var secondReader = new StreamReader(secondStream);
        _ = await ReadSseEventAsync(firstReader, "CardInserted", TimeSpan.FromSeconds(5));
        _ = await ReadSseEventAsync(secondReader, "CardInserted", TimeSpan.FromSeconds(5));

        platform.SetReader("Reader A", SmartCardPresenceStatus.NoCard);

        var first = await ReadSseEventAsync(firstReader, "CardRemoved", TimeSpan.FromSeconds(5));
        var second = await ReadSseEventAsync(secondReader, "CardRemoved", TimeSpan.FromSeconds(5));
        Assert.Equal("CardRemoved", first.EventType);
        Assert.Equal("CardRemoved", second.EventType);
    }

    [Fact]
    public async Task Events_SubscriberDisconnectCleanup_DoesNotShareDisposedMonitor()
    {
        var firstMonitor = new TrackingMonitor();
        var secondMonitor = new TrackingMonitor();
        var monitors = new Queue<TrackingMonitor>([firstMonitor, secondMonitor]);
        using var factory = CreateFactory("Development", configureServices: services =>
        {
            services.RemoveAll<ISmartCardMonitor>();
            services.AddTransient<ISmartCardMonitor>(_ => monitors.Dequeue());
        });
        using var client = CreateDevelopmentClient(factory);
        client.Timeout = Timeout.InfiniteTimeSpan;

        using (var firstResponse = await client.GetAsync("/api/v1/events", HttpCompletionOption.ResponseHeadersRead))
        {
            Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
            await firstMonitor.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        await firstMonitor.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        using (var secondResponse = await client.GetAsync("/api/v1/events", HttpCompletionOption.ResponseHeadersRead))
        {
            Assert.Equal(HttpStatusCode.OK, secondResponse.StatusCode);
            await secondMonitor.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        await secondMonitor.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Events_RejectsInvalidExpiredAndReplayJwt()
    {
        using var factory = CreateFactory("Production");
        using var invalidClient = factory.CreateClient();
        invalidClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "invalid");
        var invalid = await invalidClient.GetAsync("/api/v1/events", HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.Unauthorized, invalid.StatusCode);

        using var expiredClient = factory.CreateClient();
        expiredClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", CreateToken(DateTime.UtcNow.AddMinutes(-2), DateTime.UtcNow.AddMinutes(-1)));
        var expired = await expiredClient.GetAsync("/api/v1/events", HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.Unauthorized, expired.StatusCode);

        var replayToken = CreateToken(DateTime.UtcNow.AddSeconds(-1), DateTime.UtcNow.AddSeconds(30));
        using var firstReplayClient = factory.CreateClient();
        firstReplayClient.Timeout = Timeout.InfiniteTimeSpan;
        firstReplayClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", replayToken);
        using var firstReplay = await firstReplayClient.GetAsync("/api/v1/events", HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.OK, firstReplay.StatusCode);

        using var secondReplayClient = factory.CreateClient();
        secondReplayClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", replayToken);
        var secondReplay = await secondReplayClient.GetAsync("/api/v1/events", HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.Unauthorized, secondReplay.StatusCode);
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
        Action<IServiceCollection>? configureServices = null,
        string? publicKeyPath = null)
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
                    ["Agent:Jwt:Audience"] = "thai-id-card-agent"
                };
                if (publicKeyPath is null)
                {
                    values["Agent:Jwt:PublicKeyPem"] = publicKeyPem;
                }
                else
                {
                    values["Agent:Jwt:PublicKeyPath"] = publicKeyPath;
                }
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

    private static async Task<SseReaderEvent> ReadSseEventAsync(StreamReader reader, string expectedEventType, TimeSpan timeout)
    {
        using var timeoutSource = new CancellationTokenSource(timeout);
        string? eventName = null;
        var dataLines = new List<string>();
        while (!timeoutSource.IsCancellationRequested)
        {
            var lineTask = reader.ReadLineAsync(timeoutSource.Token).AsTask();
            string? line;
            try
            {
                line = await lineTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (line is null)
            {
                break;
            }

            if (line.Length == 0)
            {
                if (dataLines.Count == 0)
                {
                    eventName = null;
                    continue;
                }

                var json = string.Join("\n", dataLines);
                var readerEvent = JsonSerializer.Deserialize<SseReaderEvent>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));
                Assert.NotNull(readerEvent);
                Assert.False(string.IsNullOrWhiteSpace(readerEvent.ReaderName));
                Assert.False(string.IsNullOrWhiteSpace(readerEvent.EventType));
                Assert.True(readerEvent.OccurredAtUtc > DateTimeOffset.MinValue);
                if (!string.IsNullOrWhiteSpace(readerEvent.Atr))
                {
                    Assert.Matches("^([0-9A-F]{2})(-[0-9A-F]{2})*$", readerEvent.Atr);
                }

                if (eventName is not null)
                {
                    Assert.Equal(eventName, readerEvent.EventType);
                }

                if (readerEvent.EventType == expectedEventType)
                {
                    return readerEvent;
                }

                eventName = null;
                dataLines.Clear();
                continue;
            }

            if (line.StartsWith("event:", StringComparison.Ordinal))
            {
                eventName = line[6..].Trim();
                continue;
            }

            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                dataLines.Add(line[5..].TrimStart());
            }
        }

        throw new TimeoutException($"Timed out waiting for SSE event {expectedEventType}.");
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

    private sealed record SseReaderEvent(string EventType, string ReaderName, bool? CardPresent, string? Atr, DateTimeOffset OccurredAtUtc);

    private sealed class TrackingMonitor : ISmartCardMonitor
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Stopped { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public event EventHandler<ReaderEvent>? ReaderEventReceived;

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            ReaderEventReceived?.Invoke(this, new ReaderEvent("Reader A", ReaderEventType.ReaderConnected, null, DateTimeOffset.UtcNow, null, "Empty", false));
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            Stopped.TrySetResult();
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Disposed.TrySetResult();
            return ValueTask.CompletedTask;
        }
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
