using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Configuration;

namespace ThaiIdCardAgent.Service.Tests;

public sealed class CertificateValidationTests
{
    [Fact]
    public void ValidateCertificate_AcceptsUsableLocalhostServerCertificate()
    {
        using var certificate = CreateCertificate("localhost", DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(7), includeServerAuthentication: true);

        var errors = AgentDiagnostics.ValidateCertificate(certificate, "localhost");

        Assert.Empty(errors);
    }

    [Fact]
    public void ValidateCertificate_RejectsExpiredCertificate()
    {
        using var certificate = CreateCertificate("localhost", DateTimeOffset.UtcNow.AddDays(-7), DateTimeOffset.UtcNow.AddDays(-1), includeServerAuthentication: true);

        var errors = AgentDiagnostics.ValidateCertificate(certificate, "localhost");

        Assert.Contains(errors, error => error.Contains("expired", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ValidateCertificate_RejectsCertificateWithoutPrivateKey()
    {
        using var certificateWithPrivateKey = CreateCertificate("localhost", DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(7), includeServerAuthentication: true);
        using var publicOnlyCertificate = X509CertificateLoader.LoadCertificate(certificateWithPrivateKey.RawData);

        var errors = AgentDiagnostics.ValidateCertificate(publicOnlyCertificate, "localhost");

        Assert.Contains(errors, error => error.Contains("private key", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ValidateCertificate_RejectsCertificateWithoutServerAuthenticationEku()
    {
        using var certificate = CreateCertificate("localhost", DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(7), includeServerAuthentication: false);

        var errors = AgentDiagnostics.ValidateCertificate(certificate, "localhost");

        Assert.Contains(errors, error => error.Contains("Server Authentication", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidateCertificate_RejectsHostMismatch()
    {
        using var certificate = CreateCertificate("agent.local", DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(7), includeServerAuthentication: true);

        var errors = AgentDiagnostics.ValidateCertificate(certificate, "localhost");

        Assert.Contains(errors, error => error.Contains("SAN", StringComparison.Ordinal));
    }

    [Fact]
    public void FindConfiguredCertificate_ReturnsNullForUnknownThumbprint()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Agent:Https:Certificate:Thumbprint"] = "0000000000000000000000000000000000000000"
            })
            .Build();

        var certificate = AgentDiagnostics.FindConfiguredCertificate(configuration);

        Assert.Null(certificate);
    }

    private static X509Certificate2 CreateCertificate(
        string dnsName,
        DateTimeOffset notBefore,
        DateTimeOffset notAfter,
        bool includeServerAuthentication)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            $"CN={dnsName}",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(dnsName);
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, critical: true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection
            {
                includeServerAuthentication
                    ? new Oid("1.3.6.1.5.5.7.3.1", "Server Authentication")
                    : new Oid("1.3.6.1.5.5.7.3.2", "Client Authentication")
            },
            critical: false));

        using var certificate = request.CreateSelfSigned(notBefore, notAfter);
        return X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pfx), password: null, X509KeyStorageFlags.EphemeralKeySet);
    }
}