using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Server.Kestrel.Https;

namespace ThaiIdCardAgent.Service.Tests;

public sealed class TlsConfigurationTests
{
    [Fact]
    public void ProductionTls_DoesNotRequireClientCertificate()
    {
        using var certificate = CreateCertificate("localhost");
        var options = new HttpsConnectionAdapterOptions();

        AgentTlsSettings.ConfigureProductionHttps(options, certificate);

        Assert.False(AgentTlsSettings.ClientCertificateRequired);
        Assert.Same(certificate, options.ServerCertificate);
        Assert.Equal(ClientCertificateMode.NoCertificate, options.ClientCertificateMode);
    }

    [Fact]
    public void DevelopmentTls_DoesNotRequireClientCertificate()
    {
        var options = new HttpsConnectionAdapterOptions();

        AgentTlsSettings.ConfigureDevelopmentHttps(options);

        Assert.Equal(ClientCertificateMode.NoCertificate, options.ClientCertificateMode);
    }

    [Fact]
    public void CertificateTrustDiagnostics_ReportsUntrustedRoots()
    {
        using var certificate = CreateCertificate("localhost");

        var diagnostics = AgentDiagnostics.GetCertificateTrustDiagnostics(certificate, (_, _, _) => false);

        Assert.NotNull(diagnostics.RootThumbprint);
        Assert.False(diagnostics.CurrentUserRootTrusted);
        Assert.False(diagnostics.LocalMachineRootTrusted);
    }

    [Fact]
    public void CertificateTrustDiagnostics_ReportsTrustedRoots()
    {
        using var certificate = CreateCertificate("localhost");

        var diagnostics = AgentDiagnostics.GetCertificateTrustDiagnostics(certificate, (_, _, _) => true);

        Assert.NotNull(diagnostics.RootThumbprint);
        Assert.True(diagnostics.CurrentUserRootTrusted);
        Assert.True(diagnostics.LocalMachineRootTrusted);
    }

    private static X509Certificate2 CreateCertificate(string dnsName)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest($"CN={dnsName}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(dnsName);
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new Oid("1.3.6.1.5.5.7.3.1", "Server Authentication") },
            critical: false));
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(7));
        return X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pfx), password: null, X509KeyStorageFlags.EphemeralKeySet);
    }
}