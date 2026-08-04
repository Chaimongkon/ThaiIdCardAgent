using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using ThaiIdCardAgent.Core;
using ThaiIdCardAgent.Pcsc;

public sealed record CertificateTrustDiagnostics(string? RootThumbprint, bool CurrentUserRootTrusted, bool LocalMachineRootTrusted);

public static class AgentDiagnostics
{
    public static async Task<int> RunAsync(IConfiguration configuration, IHostEnvironment environment, CancellationToken cancellationToken)
    {
        var checks = new List<DiagnosticCheck>();
        checks.Add(Check("Environment", environment.EnvironmentName, DiagnosticStatus.Pass));
        checks.Add(Check("HTTP 18442", environment.IsDevelopment() ? "enabled for Development only" : "disabled for Production", DiagnosticStatus.Pass));
        checks.Add(Check("HTTPS 18443", "loopback only", DiagnosticStatus.Pass));
        checks.Add(Check("Client certificate required", "false", DiagnosticStatus.Pass));

        var allowedOrigins = configuration.GetSection("Agent:AllowedOrigins").Get<string[]>() ?? [];
        var originsAreExact = allowedOrigins.All(origin => !string.IsNullOrWhiteSpace(origin) && !origin.Contains('*', StringComparison.Ordinal));
        checks.Add(Check("AllowedOrigins", allowedOrigins.Length == 0 ? "not configured" : $"configured ({allowedOrigins.Length})", allowedOrigins.Length > 0 && originsAreExact ? DiagnosticStatus.Pass : DiagnosticStatus.Fail));

        var developmentKeyConfigured = HasValue(configuration["Security:DevelopmentApiKey"])
            || HasValue(configuration["Agent:DevelopmentKey"])
            || HasValue(Environment.GetEnvironmentVariable("Security__DevelopmentApiKey"))
            || HasValue(Environment.GetEnvironmentVariable("THAI_ID_AGENT_DEV_KEY"));
        checks.Add(Check("Development API key", developmentKeyConfigured ? "configured" : "not configured", environment.IsDevelopment() || !developmentKeyConfigured ? DiagnosticStatus.Pass : DiagnosticStatus.Fail));

        var publicKeyPath = configuration["Agent:Jwt:PublicKeyPath"]
            ?? configuration["Security:Jwt:PublicKeyPath"]
            ?? Environment.GetEnvironmentVariable("Agent__Jwt__PublicKeyPath")
            ?? Environment.GetEnvironmentVariable("Security__Jwt__PublicKeyPath");
        var publicKeyConfigured = HasValue(configuration["Agent:Jwt:PublicKeyPem"])
            || HasValue(configuration["Security:Jwt:PublicKeyPem"])
            || HasValue(Environment.GetEnvironmentVariable("Security__Jwt__PublicKeyPem"))
            || (HasValue(publicKeyPath) && File.Exists(publicKeyPath));
        var symmetricKeyConfigured = HasValue(configuration["Agent:Jwt:SymmetricSigningKey"])
            || HasValue(configuration["Security:Jwt:SymmetricSigningKey"])
            || HasValue(Environment.GetEnvironmentVariable("THAI_ID_AGENT_JWT_SIGNING_KEY"));
        checks.Add(Check("JWT public verification key", publicKeyConfigured ? "configured" : "not configured", publicKeyConfigured ? DiagnosticStatus.Pass : environment.IsDevelopment() ? DiagnosticStatus.Warn : DiagnosticStatus.Fail));
        checks.Add(Check("JWT symmetric/private material", symmetricKeyConfigured ? "configured in agent" : "not configured", symmetricKeyConfigured && !environment.IsDevelopment() ? DiagnosticStatus.Fail : DiagnosticStatus.Pass));

        checks.Add(Check("Port 18443", IsPortAvailable(18443) ? "available" : "already in use", IsPortAvailable(18443) ? DiagnosticStatus.Pass : DiagnosticStatus.Fail));
        checks.Add(await CheckSmartCardServiceAsync(cancellationToken).ConfigureAwait(false));
        checks.Add(await CheckReadersAsync(cancellationToken).ConfigureAwait(false));

        if (!environment.IsDevelopment())
        {
            checks.AddRange(CheckCertificate(configuration));
        }

        foreach (var check in checks)
        {
            Console.WriteLine($"[{check.Status}] {check.Name}: {check.Message}");
        }

        return checks.Any(check => check.Status == DiagnosticStatus.Fail) ? 1 : 0;
    }

    public static IReadOnlyList<string> ValidateCertificate(X509Certificate2 certificate, string expectedHost)
    {
        var errors = new List<string>();
        var now = DateTimeOffset.UtcNow;
        if (now < certificate.NotBefore || now > certificate.NotAfter)
        {
            errors.Add("certificate is expired or not yet valid");
        }

        if (!CertificatePrivateKeyUsable(certificate))
        {
            errors.Add("certificate private key is missing or not usable");
        }

        if (!CertificateHasServerAuthentication(certificate))
        {
            errors.Add("certificate does not have Server Authentication EKU");
        }

        if (!CertificateMatchesHost(certificate, expectedHost))
        {
            errors.Add("certificate SAN does not match configured HTTPS host");
        }

        return errors;
    }

    public static CertificateTrustDiagnostics GetCertificateTrustDiagnostics(
        X509Certificate2 certificate,
        Func<StoreLocation, StoreName, string, bool>? storeContainsThumbprint = null)
    {
        var rootThumbprint = GetChainRootThumbprint(certificate);
        if (rootThumbprint is null)
        {
            return new CertificateTrustDiagnostics(null, CurrentUserRootTrusted: false, LocalMachineRootTrusted: false);
        }

        var contains = storeContainsThumbprint ?? CertificateStoreContainsThumbprint;
        return new CertificateTrustDiagnostics(
            rootThumbprint,
            contains(StoreLocation.CurrentUser, StoreName.Root, rootThumbprint),
            contains(StoreLocation.LocalMachine, StoreName.Root, rootThumbprint));
    }
    public static X509Certificate2? FindConfiguredCertificate(IConfiguration configuration)
    {
        var options = new HttpsCertificateOptions();
        configuration.GetSection("Agent:Https:Certificate").Bind(options);
        configuration.GetSection("Security:Https:Certificate").Bind(options);

        var storeName = Enum.TryParse<StoreName>(options.StoreName, ignoreCase: true, out var parsedStoreName) ? parsedStoreName : StoreName.My;
        var storeLocation = Enum.TryParse<StoreLocation>(options.StoreLocation, ignoreCase: true, out var parsedStoreLocation) ? parsedStoreLocation : StoreLocation.LocalMachine;

        using var store = new X509Store(storeName, storeLocation);
        store.Open(OpenFlags.ReadOnly);

        if (HasValue(options.Thumbprint))
        {
            var normalizedThumbprint = NormalizeThumbprint(options.Thumbprint!);
            return store.Certificates
                .Find(X509FindType.FindByThumbprint, normalizedThumbprint, validOnly: false)
                .OfType<X509Certificate2>()
                .OrderByDescending(certificate => certificate.NotAfter)
                .FirstOrDefault();
        }

        if (HasValue(options.SubjectName))
        {
            return store.Certificates
                .Find(X509FindType.FindBySubjectName, options.SubjectName!, validOnly: false)
                .OfType<X509Certificate2>()
                .Where(certificate => certificate.HasPrivateKey)
                .OrderByDescending(certificate => certificate.NotAfter)
                .FirstOrDefault();
        }

        return null;
    }

    private static IEnumerable<DiagnosticCheck> CheckCertificate(IConfiguration configuration)
    {
        var certificate = FindConfiguredCertificate(configuration);
        if (certificate is null)
        {
            yield return Check("HTTPS certificate", "not found in configured LocalMachine store", DiagnosticStatus.Fail);
            yield break;
        }

        yield return Check("HTTPS certificate", $"found thumbprint {certificate.Thumbprint}", DiagnosticStatus.Pass);
        yield return Check("HTTPS certificate private key", CertificatePrivateKeyUsable(certificate) ? "usable by current process" : "missing or not usable", CertificatePrivateKeyUsable(certificate) ? DiagnosticStatus.Pass : DiagnosticStatus.Fail);
        yield return Check("HTTPS certificate SAN", CertificateMatchesHost(certificate, "localhost") ? "matches localhost" : "does not match localhost", CertificateMatchesHost(certificate, "localhost") ? DiagnosticStatus.Pass : DiagnosticStatus.Fail);

        using var chain = new X509Chain();
        var trusted = chain.Build(certificate);
        yield return Check("HTTPS certificate chain", trusted ? "trusted for current user" : "not trusted for current user", trusted ? DiagnosticStatus.Pass : DiagnosticStatus.Fail);

        var trust = GetCertificateTrustDiagnostics(certificate);
        if (trust.RootThumbprint is null)
        {
            yield return Check("HTTPS certificate CurrentUser Root", "chain root could not be resolved", DiagnosticStatus.Fail);
            yield return Check("HTTPS certificate LocalMachine Root", "chain root could not be resolved", DiagnosticStatus.Fail);
        }
        else
        {
            yield return Check("HTTPS certificate CurrentUser Root", trust.CurrentUserRootTrusted ? "trusted root present" : "trusted root missing", trust.CurrentUserRootTrusted ? DiagnosticStatus.Pass : DiagnosticStatus.Fail);
            yield return Check("HTTPS certificate LocalMachine Root", trust.LocalMachineRootTrusted ? "trusted root present" : "trusted root missing", trust.LocalMachineRootTrusted ? DiagnosticStatus.Pass : DiagnosticStatus.Fail);
        }
    }

    private static async Task<DiagnosticCheck> CheckSmartCardServiceAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            return Check("SCardSvr", "not checked on non-Windows OS", DiagnosticStatus.Warn);
        }

        var startInfo = new ProcessStartInfo("sc.exe", "query SCardSvr")
        {
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            return Check("SCardSvr", "unable to start sc.exe", DiagnosticStatus.Fail);
        }

        var output = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        var error = await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var combined = string.Concat(output, error);
        return process.ExitCode == 0 && combined.Contains("RUNNING", StringComparison.OrdinalIgnoreCase)
            ? Check("SCardSvr", "running", DiagnosticStatus.Pass)
            : Check("SCardSvr", "not running or not available", DiagnosticStatus.Fail);
    }

    private static async Task<DiagnosticCheck> CheckReadersAsync(CancellationToken cancellationToken)
    {
        try
        {
            var platform = new WinSCardPlatform();
            var readers = await platform.ListReadersAsync(cancellationToken).ConfigureAwait(false);
            return Check("PC/SC readers", readers.Count == 0 ? "none detected" : $"detected ({readers.Count})", readers.Count > 0 ? DiagnosticStatus.Pass : DiagnosticStatus.Fail);
        }
        catch (Exception exception) when (exception is InvalidOperationException or SmartCardServiceUnavailableException)
        {
            return Check("PC/SC readers", exception.Message, DiagnosticStatus.Fail);
        }
    }

    private static bool IsPortAvailable(int port)
    {
        try
        {
            using var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }


    private static string? GetChainRootThumbprint(X509Certificate2 certificate)
    {
        using var chain = new X509Chain();
        _ = chain.Build(certificate);
        return chain.ChainElements.Count == 0
            ? null
            : chain.ChainElements[^1].Certificate.Thumbprint;
    }

    private static bool CertificateStoreContainsThumbprint(StoreLocation location, StoreName name, string thumbprint)
    {
        using var store = new X509Store(name, location);
        store.Open(OpenFlags.ReadOnly);
        var normalizedThumbprint = NormalizeThumbprint(thumbprint);
        return store.Certificates
            .Find(X509FindType.FindByThumbprint, normalizedThumbprint, validOnly: false)
            .Count > 0;
    }
    private static bool CertificatePrivateKeyUsable(X509Certificate2 certificate)
    {
        try
        {
            using var key = certificate.GetRSAPrivateKey();
            if (key is null)
            {
                return false;
            }

            var probe = RandomNumberGenerator.GetBytes(32);
            _ = key.SignData(probe, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            return true;
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return false;
        }
    }
    private static bool CertificateHasServerAuthentication(X509Certificate2 certificate)
    {
        var eku = certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>().FirstOrDefault();
        return eku is null || eku.EnhancedKeyUsages.OfType<System.Security.Cryptography.Oid>().Any(oid => string.Equals(oid.Value, "1.3.6.1.5.5.7.3.1", StringComparison.Ordinal));
    }

    private static bool CertificateMatchesHost(X509Certificate2 certificate, string expectedHost)
    {
        var san = certificate.Extensions
            .OfType<X509Extension>()
            .FirstOrDefault(extension => string.Equals(extension.Oid?.Value, "2.5.29.17", StringComparison.Ordinal))
            ?.Format(false) ?? string.Empty;

        if (System.Net.IPAddress.TryParse(expectedHost, out _))
        {
            return san.Contains($"IP Address={expectedHost}", StringComparison.OrdinalIgnoreCase)
                || san.Contains($"IPAddress={expectedHost}", StringComparison.OrdinalIgnoreCase);
        }

        return san.Contains($"DNS Name={expectedHost}", StringComparison.OrdinalIgnoreCase)
            || certificate.GetNameInfo(X509NameType.DnsName, forIssuer: false).Equals(expectedHost, StringComparison.OrdinalIgnoreCase);
    }
    private static string NormalizeThumbprint(string thumbprint) => thumbprint.Replace(" ", string.Empty, StringComparison.Ordinal).ToUpperInvariant();

    private static bool HasValue(string? value) => !string.IsNullOrWhiteSpace(value);

    private static DiagnosticCheck Check(string name, string message, DiagnosticStatus status) => new(name, message, status);

    private sealed record DiagnosticCheck(string Name, string Message, DiagnosticStatus Status);

    private enum DiagnosticStatus
    {
        Pass,
        Warn,
        Fail
    }
}
