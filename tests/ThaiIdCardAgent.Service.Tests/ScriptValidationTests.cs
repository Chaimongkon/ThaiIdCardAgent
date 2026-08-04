namespace ThaiIdCardAgent.Service.Tests;

public sealed class ScriptValidationTests
{
    private static readonly string RepositoryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    [Fact]
    public void NewAgentCertificate_RequiresExplicitLocalMachineTrustFlag()
    {
        var script = ReadScript("scripts", "New-AgentCertificate.ps1");

        Assert.Contains("TrustForLocalMachine", script, StringComparison.Ordinal);
        Assert.Contains("Import-Certificate", script, StringComparison.Ordinal);
        Assert.Contains("Write-Warning", script, StringComparison.Ordinal);
        Assert.Contains("Export-Certificate", script, StringComparison.Ordinal);
        Assert.DoesNotContain("Export-PfxCertificate", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PFX", script, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InstallService_ChecksCertificateTrustAndPrivateKeyAclBeforeInstall()
    {
        var script = ReadScript("scripts", "Install-Service.ps1");

        Assert.Contains("Test-LocalMachineTrust", script, StringComparison.Ordinal);
        Assert.Contains("Cert:\\LocalMachine\\My", script, StringComparison.Ordinal);
        Assert.Contains("HasPrivateKey", script, StringComparison.Ordinal);
        Assert.Contains("Test-CertificateSan", script, StringComparison.Ordinal);
        Assert.Contains("Test-CertificateServerAuthenticationEku", script, StringComparison.Ordinal);
        Assert.Contains("Test-PrivateKeyAcl", script, StringComparison.Ordinal);
        Assert.Contains("certutil -addstore Root", script, StringComparison.Ordinal);
    }

    [Fact]
    public void ProductionAcceptanceScript_SupportsWhatIfAndExpectedParameters()
    {
        var script = ReadScript("scripts", "Test-ProductionAcceptance.ps1");

        Assert.Contains("SupportsShouldProcess = $true", script, StringComparison.Ordinal);
        Assert.Contains("ConfigureMachineEnvironment", script, StringComparison.Ordinal);
        Assert.Contains("SkipInteractiveCardTransitions", script, StringComparison.Ordinal);
        Assert.Contains("New-TestJwt.ps1", script, StringComparison.Ordinal);
        Assert.Contains("/api/v1/readers", script, StringComparison.Ordinal);
        Assert.Contains("/api/v1/card/status", script, StringComparison.Ordinal);
        Assert.Contains("/api/v1/card/atr", script, StringComparison.Ordinal);
        Assert.Contains("Uninstall-Service.ps1", script, StringComparison.Ordinal);
        Assert.DoesNotContain("RemoveData", script, StringComparison.Ordinal);
    }

    private static string ReadScript(params string[] parts)
    {
        return File.ReadAllText(Path.Combine([RepositoryRoot, .. parts]));
    }
}