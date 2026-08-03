using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using ThaiIdCardAgent.Core;
using ThaiIdCardAgent.Pcsc;
using ThaiIdCardAgent.ThaiCard;

var builder = WebApplication.CreateBuilder(args);
var isWindowsService = WindowsServiceHelpers.IsWindowsService();
if (isWindowsService)
{
    builder.Host.UseWindowsService(options => options.ServiceName = "ThaiIdCardAgent");
}
else
{
    builder.Logging.ClearProviders();
    builder.Logging.AddConsole();
    builder.Logging.AddDebug();
}

var dataProtectionPath = isWindowsService
    ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ThaiIdCardAgent", "Keys")
    : Path.Combine(builder.Environment.ContentRootPath, "artifacts", "data-protection-keys");
Directory.CreateDirectory(dataProtectionPath);
builder.Services.AddDataProtection()
    .SetApplicationName("ThaiIdCardAgent")
    .PersistKeysToFileSystem(new DirectoryInfo(dataProtectionPath));

builder.Services.AddMemoryCache();
builder.Services.AddSingleton<IPiiRedactor, PiiRedactor>();
builder.Services.AddSingleton<IThaiIdCardReader, NotConfiguredThaiIdCardReader>();
builder.Services.AddPcscSmartCardServices();
builder.Services.AddSingleton<IConfigureOptions<AgentSecurityOptions>, AgentSecurityOptionsSetup>();
builder.Services.AddOptions<AgentSecurityOptions>()
    .Validate(options => options.AllowedOrigins.All(origin => !string.IsNullOrWhiteSpace(origin) && !origin.Contains('*', StringComparison.Ordinal)), "CORS origins must be exact and cannot contain wildcard characters.")
    .Validate(options => options.Jwt.Audience == "thai-id-card-agent", "JWT audience must be thai-id-card-agent.")
    .ValidateOnStart();
builder.Services.AddCors(options =>
{
    options.AddPolicy("AgentCors", policy =>
    {
        var origins = builder.Configuration.GetSection("Agent:AllowedOrigins").Get<string[]>() ?? Array.Empty<string>();
        policy.WithOrigins(origins).AllowAnyHeader().AllowAnyMethod();
    });
});
builder.Services.AddAuthentication(AgentAuthenticationHandler.SchemeName)
    .AddScheme<AuthenticationSchemeOptions, AgentAuthenticationHandler>(AgentAuthenticationHandler.SchemeName, _ => { });
builder.Services.AddAuthorization();
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

builder.WebHost.ConfigureKestrel(options =>
{
    var enableDevelopmentHttps = builder.Configuration.GetValue("Agent:EnableHttpsInDevelopment", false);
    if (builder.Environment.IsDevelopment() && enableDevelopmentHttps)
    {
        options.ListenLocalhost(18443, listenOptions => listenOptions.UseHttps());
    }
    else if (!builder.Environment.IsDevelopment())
    {
        var certificate = AgentDiagnostics.FindConfiguredCertificate(builder.Configuration);
        if (certificate is null)
        {
            throw new InvalidOperationException("Production HTTPS certificate is not configured or cannot be found.");
        }

        options.ListenLocalhost(18443, listenOptions => listenOptions.UseHttps(certificate));
    }

    if (builder.Environment.IsDevelopment())
    {
        options.ListenLocalhost(18442);
    }
});

if (args.Any(argument => string.Equals(argument, "--diagnostics", StringComparison.OrdinalIgnoreCase)))
{
    return await AgentDiagnostics.RunAsync(builder.Configuration, builder.Environment, CancellationToken.None).ConfigureAwait(false);
}

var app = builder.Build();

app.UseExceptionHandler(errorApp =>
{
    errorApp.Run(async context =>
    {
        var exception = context.Features.Get<IExceptionHandlerFeature>()?.Error ?? new InvalidOperationException("Unhandled agent error.");
        var includeDetail = app.Environment.IsDevelopment();
        var error = AgentErrorMapper.FromException(exception, includeDetail);
        context.Response.StatusCode = ToStatusCode(error.Code);
        await WriteErrorAsync(context, error).ConfigureAwait(false);
    });
});

app.UseCors("AgentCors");
app.UseAuthentication();
app.UseAuthorization();

var api = app.MapGroup("/api/v1");
api.MapGet("/health", () => Results.Ok(new
{
    status = "healthy",
    service = "ThaiIdCardAgent",
    version = typeof(Program).Assembly.GetName().Version?.ToString() ?? "0.0.0",
    utcTime = DateTimeOffset.UtcNow
})).AllowAnonymous();
api.MapGet("/info", (HttpContext context) => Results.Ok(OperationResult<object>.Ok(new
{
    service = "Thai ID Card Local Agent",
    version = typeof(Program).Assembly.GetName().Version?.ToString() ?? "0.0.0",
    thaiCardProtocol = "not_configured"
}, context.TraceIdentifier))).RequireAuthorization();
api.MapGet("/readers", async (HttpContext context, ISmartCardReaderService service, CancellationToken cancellationToken) =>
    Results.Ok(OperationResult<IReadOnlyList<SmartCardReaderInfo>>.Ok(await service.GetReadersAsync(cancellationToken).ConfigureAwait(false), context.TraceIdentifier))).RequireAuthorization();
api.MapGet("/card/status", async (HttpContext context, [FromQuery] string? readerName, ISmartCardReaderService service, CancellationToken cancellationToken) =>
{
    var resolvedReader = await ResolveReaderAsync(service, readerName, cancellationToken).ConfigureAwait(false);
    var status = await service.GetStatusAsync(resolvedReader, cancellationToken).ConfigureAwait(false);
    return Results.Ok(OperationResult<SmartCardStatus>.Ok(status, context.TraceIdentifier));
}).RequireAuthorization();
api.MapPost("/card/atr", async (HttpContext context, [FromBody] ReaderRequest? request, ISmartCardReaderService service, CancellationToken cancellationToken) =>
{
    var resolvedReader = await ResolveReaderAsync(service, request?.ReaderName, cancellationToken).ConfigureAwait(false);
    var atr = await service.GetAtrAsync(resolvedReader, cancellationToken).ConfigureAwait(false);
    return Results.Ok(OperationResult<CardAtrResponse>.Ok(new CardAtrResponse(resolvedReader, AtrFormatter.ToHex(atr), DateTimeOffset.UtcNow), context.TraceIdentifier));
}).RequireAuthorization();
api.MapPost("/card/read", async (HttpContext context, [FromBody] ThaiCardReadRequest? request, IThaiIdCardReader thaiIdCardReader, ISmartCardReaderService service, CancellationToken cancellationToken) =>
{
    var resolvedReader = await ResolveReaderAsync(service, request?.ReaderName, cancellationToken).ConfigureAwait(false);
    var data = await thaiIdCardReader.ReadAsync(resolvedReader, request?.Options ?? new ThaiIdCardReadOptions(), cancellationToken).ConfigureAwait(false);
    return Results.Ok(OperationResult<ThaiIdCardData>.Ok(data, context.TraceIdentifier));
}).RequireAuthorization();
api.MapGet("/events", async (HttpContext context, ISmartCardMonitor monitor) =>
{
    context.Response.Headers.ContentType = "text/event-stream";
    await using (monitor.ConfigureAwait(false))
    {
        async void OnReaderEvent(object? _, ReaderEvent readerEvent)
        {
            try
            {
                var safeEvent = new ReaderEventDto(
                    readerEvent.EventType.ToString(),
                    readerEvent.ReaderName,
                    readerEvent.IsCardPresent,
                    readerEvent.EventType is ReaderEventType.CardRemoved ? null : readerEvent.Atr,
                    readerEvent.OccurredAtUtc);
                var data = JsonSerializer.Serialize(safeEvent);
                await context.Response.WriteAsync($"event: {safeEvent.EventType}\n", context.RequestAborted).ConfigureAwait(false);
                await context.Response.WriteAsync($"data: {data}\n\n", context.RequestAborted).ConfigureAwait(false);
                await context.Response.Body.FlushAsync(context.RequestAborted).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
            {
            }
        }

        monitor.ReaderEventReceived += OnReaderEvent;
        await monitor.StartAsync(context.RequestAborted).ConfigureAwait(false);
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, context.RequestAborted).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
        }
        finally
        {
            monitor.ReaderEventReceived -= OnReaderEvent;
            await monitor.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }
}).RequireAuthorization();

app.Run();
return 0;

static async Task<string> ResolveReaderAsync(ISmartCardReaderService service, string? readerName, CancellationToken cancellationToken)
{
    if (!string.IsNullOrWhiteSpace(readerName))
    {
        return readerName;
    }

    var readers = await service.GetReadersAsync(cancellationToken).ConfigureAwait(false);
    return readers.Count switch
    {
        0 => throw new ReaderNotFoundException("<default>"),
        1 => readers[0].Name,
        _ => throw new ReaderSelectionRequiredException()
    };
}

static Task WriteErrorAsync(HttpContext context, AgentError error)
{
    return context.Response.WriteAsJsonAsync(AgentErrorResponse.FromError(context.TraceIdentifier, error), context.RequestAborted);
}

static int ToStatusCode(string code) => code switch
{
    AgentErrorCodes.InvalidRequest => StatusCodes.Status400BadRequest,
    AgentErrorCodes.Unauthorized => StatusCodes.Status401Unauthorized,
    AgentErrorCodes.Forbidden => StatusCodes.Status403Forbidden,
    AgentErrorCodes.ReaderNotFound => StatusCodes.Status404NotFound,
    AgentErrorCodes.AgentBusy => StatusCodes.Status409Conflict,
    AgentErrorCodes.CardRemoved => StatusCodes.Status409Conflict,
    AgentErrorCodes.CardNotPresent => StatusCodes.Status422UnprocessableEntity,
    AgentErrorCodes.ReaderSelectionRequired => StatusCodes.Status422UnprocessableEntity,
    AgentErrorCodes.SmartCardServiceUnavailable => StatusCodes.Status503ServiceUnavailable,
    AgentErrorCodes.ReaderUnavailable => StatusCodes.Status503ServiceUnavailable,
    AgentErrorCodes.Timeout => StatusCodes.Status504GatewayTimeout,
    AgentErrorCodes.ThaiCardProtocolNotConfigured => StatusCodes.Status501NotImplemented,
    _ => StatusCodes.Status500InternalServerError
};

public sealed record ReaderRequest(string? ReaderName, string? RequestId = null);

public sealed record ThaiCardReadRequest(string? ReaderName, ThaiIdCardReadOptions? Options, string? RequestId = null);

public sealed record CardAtrResponse(string ReaderName, string Atr, DateTimeOffset ReadAtUtc);

public sealed record ReaderEventDto(string EventType, string ReaderName, bool? CardPresent, string? Atr, DateTimeOffset OccurredAtUtc);

public sealed class AgentSecurityOptions
{
    public string[] AllowedOrigins { get; set; } = [];

    public JwtOptions Jwt { get; set; } = new();
}

public sealed class JwtOptions
{
    public string Issuer { get; set; } = "thai-id-card-agent-client";

    public string Audience { get; set; } = "thai-id-card-agent";

    public string? PublicKeyPem { get; set; }

    public string? SymmetricSigningKey { get; set; }
}

public sealed class HttpsCertificateOptions
{
    public string StoreName { get; set; } = "My";

    public string StoreLocation { get; set; } = "LocalMachine";

    public string? Thumbprint { get; set; }

    public string? SubjectName { get; set; } = "localhost";
}

public sealed class AgentSecurityOptionsSetup : IConfigureOptions<AgentSecurityOptions>
{
    private readonly IConfiguration _configuration;

    public AgentSecurityOptionsSetup(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public void Configure(AgentSecurityOptions options)
    {
        _configuration.GetSection("Agent").Bind(options);
        _configuration.GetSection("Security:Jwt").Bind(options.Jwt);
        options.Jwt.PublicKeyPem ??= Environment.GetEnvironmentVariable("Security__Jwt__PublicKeyPem");
        options.Jwt.SymmetricSigningKey ??= Environment.GetEnvironmentVariable("THAI_ID_AGENT_JWT_SIGNING_KEY");
    }
}

public sealed class AgentAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "Agent";
    private const string DevelopmentHeader = "X-Agent-Development-Key";
    private readonly IConfiguration _configuration;
    private readonly IWebHostEnvironment _environment;
    private readonly IMemoryCache _cache;
    private readonly IOptionsMonitor<AgentSecurityOptions> _securityOptions;

    public AgentAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IConfiguration configuration,
        IWebHostEnvironment environment,
        IMemoryCache cache,
        IOptionsMonitor<AgentSecurityOptions> securityOptions)
        : base(options, logger, encoder)
    {
        _configuration = configuration;
        _environment = environment;
        _cache = cache;
        _securityOptions = securityOptions;
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (_environment.IsDevelopment())
        {
            return Task.FromResult(AuthenticateDevelopment());
        }

        return Task.FromResult(AuthenticateJwt());
    }

    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        await Response.WriteAsJsonAsync(AgentErrorResponse.FromError(Context.TraceIdentifier, new AgentError(AgentErrorCodes.Unauthorized, "Authentication is required.")), Context.RequestAborted).ConfigureAwait(false);
    }

    protected override async Task HandleForbiddenAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status403Forbidden;
        await Response.WriteAsJsonAsync(AgentErrorResponse.FromError(Context.TraceIdentifier, new AgentError(AgentErrorCodes.Forbidden, "Access is forbidden.")), Context.RequestAborted).ConfigureAwait(false);
    }

    private AuthenticateResult AuthenticateDevelopment()
    {
        var expected = _configuration["Security:DevelopmentApiKey"]
            ?? _configuration["Agent:DevelopmentKey"]
            ?? Environment.GetEnvironmentVariable("Security__DevelopmentApiKey")
            ?? Environment.GetEnvironmentVariable("THAI_ID_AGENT_DEV_KEY");
        if (string.IsNullOrWhiteSpace(expected))
        {
            return AuthenticateResult.Fail("Development key is not configured.");
        }

        if (!Request.Headers.TryGetValue(DevelopmentHeader, out var provided) || !ConstantTimeEquals(expected, provided.ToString()))
        {
            return AuthenticateResult.Fail("Development key is invalid.");
        }

        return AuthenticateResult.Success(CreateTicket("development", "development-workstation", "development"));
    }

    private AuthenticateResult AuthenticateJwt()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return AuthenticateResult.Fail("Bearer token is required.");
        }

        var token = header["Bearer ".Length..].Trim();
        var options = _securityOptions.CurrentValue;
        var signingKey = CreateValidationKey(options.Jwt);
        if (signingKey is null)
        {
            return AuthenticateResult.Fail("JWT validation key is not configured.");
        }

        try
        {
            var handler = new JwtSecurityTokenHandler();
            var principal = handler.ValidateToken(token, new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = options.Jwt.Issuer,
                ValidateAudience = true,
                ValidAudience = options.Jwt.Audience,
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = signingKey,
                RequireExpirationTime = true,
                RequireSignedTokens = true,
                ValidateLifetime = true,
                ClockSkew = TimeSpan.Zero
            }, out var validatedToken);

            if (validatedToken is not JwtSecurityToken jwt || jwt.ValidTo <= DateTime.UtcNow || jwt.ValidTo - jwt.ValidFrom > TimeSpan.FromSeconds(60))
            {
                return AuthenticateResult.Fail("JWT lifetime is invalid.");
            }

            var jti = principal.FindFirstValue(JwtRegisteredClaimNames.Jti);
            var subject = principal.FindFirstValue(JwtRegisteredClaimNames.Sub) ?? principal.FindFirstValue(ClaimTypes.NameIdentifier);
            var workstationId = principal.FindFirstValue("workstation_id");
            if (string.IsNullOrWhiteSpace(jti) || string.IsNullOrWhiteSpace(subject) || string.IsNullOrWhiteSpace(workstationId))
            {
                return AuthenticateResult.Fail("JWT required claims are missing.");
            }

            if (_cache.TryGetValue($"jti:{jti}", out _))
            {
                return AuthenticateResult.Fail("JWT replay was detected.");
            }

            _cache.Set($"jti:{jti}", true, new DateTimeOffset(jwt.ValidTo, TimeSpan.Zero));
            return AuthenticateResult.Success(CreateTicket(subject, workstationId, "jwt"));
        }
        catch (Exception exception) when (exception is SecurityTokenException or ArgumentException or CryptographicException)
        {
            return AuthenticateResult.Fail("JWT validation failed.");
        }
    }

    private static SecurityKey? CreateValidationKey(JwtOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.PublicKeyPem))
        {
            var rsa = RSA.Create();
            rsa.ImportFromPem(options.PublicKeyPem);
            return new RsaSecurityKey(rsa);
        }

        return string.IsNullOrWhiteSpace(options.SymmetricSigningKey)
            ? null
            : new SymmetricSecurityKey(Encoding.UTF8.GetBytes(options.SymmetricSigningKey));
    }

    private AuthenticationTicket CreateTicket(string subject, string workstationId, string authenticationType)
    {
        var identity = new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, subject),
            new Claim("workstation_id", workstationId)
        ], authenticationType);
        return new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName);
    }

    private static bool ConstantTimeEquals(string left, string right)
    {
        var leftBytes = Encoding.UTF8.GetBytes(left);
        var rightBytes = Encoding.UTF8.GetBytes(right);
        return leftBytes.Length == rightBytes.Length && CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }
}

public partial class Program;
