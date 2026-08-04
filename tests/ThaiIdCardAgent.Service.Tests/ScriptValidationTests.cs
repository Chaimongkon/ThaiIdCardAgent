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

    [Fact]
    public void PowerShellScripts_DeclareWindowsPowerShell51Compatibility()
    {
        foreach (var scriptPath in GetPowerShellScripts())
        {
            var script = File.ReadAllText(scriptPath);

            Assert.StartsWith("#requires -Version 5.1", script, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void PowerShellScripts_DoNotUseInlineIfExpressionsUnsupportedByWindowsPowerShell51()
    {
        foreach (var scriptPath in GetPowerShellScripts())
        {
            var script = File.ReadAllText(scriptPath);
            var match = System.Text.RegularExpressions.Regex.Match(script, @"\(\s*if\s*\(", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            Assert.False(match.Success, $"{Path.GetFileName(scriptPath)} contains inline '(if (...))' at character {match.Index}. Assign the conditional result to a variable before passing it to a command.");
        }
    }

    [Fact]
    public void PowerShellScripts_ParseWithWindowsPowerShell51()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
        Assert.True(File.Exists(powershell), $"Windows PowerShell executable was not found: {powershell}");

        foreach (var scriptPath in GetPowerShellScripts())
        {
            var escapedScriptPath = scriptPath.Replace("'", "''");
            var command = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"$errors = $null; [System.Management.Automation.Language.Parser]::ParseFile('{escapedScriptPath}', [ref]$null, [ref]$errors) | Out-Null; if ($errors.Count) {{ $errors | ForEach-Object {{ $_.Message }}; exit 1 }}");
            var encodedCommand = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(command));
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = powershell,
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -EncodedCommand {encodedCommand}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });

            Assert.NotNull(process);
            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();

            Assert.True(process.ExitCode == 0, $"{Path.GetFileName(scriptPath)} failed Windows PowerShell 5.1 parser check. Output: {output} Error: {error}");
        }
    }
    private static IEnumerable<string> GetPowerShellScripts()
    {
        return Directory.GetFiles(Path.Combine(RepositoryRoot, "scripts"), "*.ps1").OrderBy(path => path, StringComparer.OrdinalIgnoreCase);
    }
    private static string ReadScript(params string[] parts)
    {
        return File.ReadAllText(Path.Combine([RepositoryRoot, .. parts]));
    }
}