using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using ThaiIdCardAgent.Core;
using ThaiIdCardAgent.Pcsc;
using ThaiIdCardAgent.ThaiCard;
using Microsoft.Extensions.Hosting.WindowsServices;

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
    .Validate(options => options.AllowedOrigins.All(origin => !origin.Contains('*', StringComparison.Ordinal)), "CORS origins must be exact and cannot contain wildcard characters.")
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

builder.WebHost.ConfigureKestrel(options =>
{
    var enableDevelopmentHttps = builder.Configuration.GetValue("Agent:EnableHttpsInDevelopment", false);
    if (!builder.Environment.IsDevelopment() || enableDevelopmentHttps)
    {
        options.ListenLocalhost(18443, listenOptions => listenOptions.UseHttps());
    }

    if (builder.Environment.IsDevelopment())
    {
        options.ListenLocalhost(18442);
    }
});

var app = builder.Build();

app.UseExceptionHandler(errorApp =>
{
    errorApp.Run(async context =>
    {
        var exception = context.Features.Get<IExceptionHandlerFeature>()?.Error ?? new InvalidOperationException("Unhandled agent error.");
        var includeDetail = app.Environment.IsDevelopment();
        var error = AgentErrorMapper.FromException(exception, includeDetail);
        context.Response.StatusCode = ToStatusCode(error.Code);
        await context.Response.WriteAsJsonAsync(new AgentErrorResponse(context.TraceIdentifier, error.Code, error.Message, error.TechnicalDetail), context.RequestAborted).ConfigureAwait(false);
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
    checkedAtUtc = DateTimeOffset.UtcNow
})).AllowAnonymous();
api.MapGet("/info", () => Results.Ok(new
{
    service = "Thai ID Card Local Agent",
    version = typeof(Program).Assembly.GetName().Version?.ToString() ?? "0.0.0",
    thaiCardProtocol = "not_configured"
})).RequireAuthorization();
api.MapGet("/readers", async (ISmartCardReaderService service, CancellationToken cancellationToken) =>
    Results.Ok(OperationResult<IReadOnlyList<SmartCardReaderInfo>>.Ok(await service.GetReadersAsync(cancellationToken).ConfigureAwait(false)))).RequireAuthorization();
api.MapGet("/card/status", async ([FromQuery] string? readerName, ISmartCardReaderService service, CancellationToken cancellationToken) =>
{
    var resolvedReader = await ResolveReaderAsync(service, readerName, cancellationToken).ConfigureAwait(false);
    var status = await service.GetStatusAsync(resolvedReader, cancellationToken).ConfigureAwait(false);
    return Results.Ok(OperationResult<SmartCardStatus>.Ok(status));
}).RequireAuthorization();
api.MapPost("/card/atr", async ([FromBody] ReaderRequest request, ISmartCardReaderService service, CancellationToken cancellationToken) =>
{
    var resolvedReader = await ResolveReaderAsync(service, request.ReaderName, cancellationToken).ConfigureAwait(false);
    var atr = await service.GetAtrAsync(resolvedReader, cancellationToken).ConfigureAwait(false);
    return Results.Ok(OperationResult<CardAtrResponse>.Ok(new CardAtrResponse(resolvedReader, AtrFormatter.ToHex(atr), DateTimeOffset.UtcNow)));
}).RequireAuthorization();
api.MapPost("/card/read", (HttpContext context) => Results.Json(
    new AgentErrorResponse(context.TraceIdentifier, AgentErrorCodes.ThaiCardProtocolNotConfigured, "Thai ID card protocol provider is not configured."),
    statusCode: StatusCodes.Status501NotImplemented)).RequireAuthorization();
api.MapGet("/events", async (HttpContext context, ISmartCardMonitor monitor) =>
{
    context.Response.Headers.ContentType = "text/event-stream";
    await using (monitor.ConfigureAwait(false))
    {
        void OnReaderEvent(object? _, ReaderEvent readerEvent)
        {
            var line = $"event: {readerEvent.EventType}\ndata: {System.Text.Json.JsonSerializer.Serialize(readerEvent)}\n\n";
            context.Response.WriteAsync(line, context.RequestAborted).GetAwaiter().GetResult();
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

static async Task<string> ResolveReaderAsync(ISmartCardReaderService service, string? readerName, CancellationToken cancellationToken)
{
    if (!string.IsNullOrWhiteSpace(readerName))
    {
        return readerName;
    }

    var readers = await service.GetReadersAsync(cancellationToken).ConfigureAwait(false);
    return readers.FirstOrDefault()?.Name ?? throw new ReaderNotFoundException("<default>");
}

static int ToStatusCode(string code) => code switch
{
    AgentErrorCodes.ReaderNotFound => StatusCodes.Status404NotFound,
    AgentErrorCodes.CardNotPresent => StatusCodes.Status409Conflict,
    AgentErrorCodes.CardRemoved => StatusCodes.Status409Conflict,
    AgentErrorCodes.AgentBusy => StatusCodes.Status423Locked,
    AgentErrorCodes.SmartCardServiceUnavailable => StatusCodes.Status503ServiceUnavailable,
    AgentErrorCodes.ThaiCardProtocolNotConfigured => StatusCodes.Status501NotImplemented,
    AgentErrorCodes.Timeout => StatusCodes.Status408RequestTimeout,
    AgentErrorCodes.Unauthorized => StatusCodes.Status401Unauthorized,
    _ => StatusCodes.Status500InternalServerError
};

public sealed record ReaderRequest(string? ReaderName);

public sealed record CardAtrResponse(string ReaderName, string Atr, DateTimeOffset ReadAtUtc);

public sealed class AgentSecurityOptions
{
    public string[] AllowedOrigins { get; set; } = [];

    public JwtOptions Jwt { get; set; } = new();
}

public sealed class JwtOptions
{
    public string Issuer { get; set; } = "thai-id-card-agent-client";

    public string Audience { get; set; } = "thai-id-card-agent";

    public string? SymmetricSigningKey { get; set; }
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

    private AuthenticateResult AuthenticateDevelopment()
    {
        var expected = _configuration["Agent:DevelopmentKey"] ?? Environment.GetEnvironmentVariable("THAI_ID_AGENT_DEV_KEY");
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
        if (string.IsNullOrWhiteSpace(options.Jwt.SymmetricSigningKey))
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
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(options.Jwt.SymmetricSigningKey)),
                RequireExpirationTime = true,
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
        catch (Exception exception) when (exception is SecurityTokenException or ArgumentException)
        {
            return AuthenticateResult.Fail("JWT validation failed.");
        }
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
        return CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }
}

public partial class Program;