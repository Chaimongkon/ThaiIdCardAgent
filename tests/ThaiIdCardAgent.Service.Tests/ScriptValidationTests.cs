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
    public void ProductionAcceptanceScript_RejectsMissingPublicKeyBeforeStateChanges()
    {
        using var temp = TempDirectory.Create();
        var privateKey = temp.WriteFile("private.pem", "private-key");
        var missingPublicKey = Path.Combine(temp.Path, "missing-public.pem");

        var run = RunAcceptanceWhatIf(missingPublicKey, privateKey);

        Assert.NotEqual(0, run.ExitCode);
        Assert.Contains("[Failed] JWT key preflight", run.Output);
        Assert.Contains("JWT public key file was not found.", run.Output);
        Assert.DoesNotContain("Production configuration", run.Output);
        Assert.DoesNotContain("LocalService private-key ACL", run.Output);
    }

    [Fact]
    public void ProductionAcceptanceScript_RejectsMissingPrivateKeyBeforeStateChanges()
    {
        using var temp = TempDirectory.Create();
        var publicKey = temp.WriteFile("public.pem", "public-key");
        var missingPrivateKey = Path.Combine(temp.Path, "missing-private.pem");

        var run = RunAcceptanceWhatIf(publicKey, missingPrivateKey);

        Assert.NotEqual(0, run.ExitCode);
        Assert.Contains("[Failed] JWT key preflight", run.Output);
        Assert.Contains("JWT private key file was not found.", run.Output);
        Assert.DoesNotContain("Production configuration", run.Output);
        Assert.DoesNotContain("LocalService private-key ACL", run.Output);
    }

    [Fact]
    public void ProductionAcceptanceScript_RejectsPlaceholderKeyPathBeforeStateChanges()
    {
        using var temp = TempDirectory.Create();
        var publicKey = temp.WriteFile("public.pem", "public-key");
        var placeholderPrivateKey = Path.Combine(temp.Path, "<PRIVATE-KEY-FILE>");

        var run = RunAcceptanceWhatIf(publicKey, placeholderPrivateKey);

        Assert.NotEqual(0, run.ExitCode);
        Assert.Contains("[Failed] JWT key preflight", run.Output);
        Assert.Contains("JWT private key path contains placeholder text.", run.Output);
        Assert.DoesNotContain("Production configuration", run.Output);
        Assert.DoesNotContain("LocalService private-key ACL", run.Output);
    }

    [Fact]
    public void ProductionAcceptanceScript_RejectsEmptyKeyFileBeforeStateChanges()
    {
        using var temp = TempDirectory.Create();
        var publicKey = temp.WriteFile("public.pem", "public-key");
        var privateKey = temp.WriteFile("private.pem", string.Empty);

        var run = RunAcceptanceWhatIf(publicKey, privateKey);

        Assert.NotEqual(0, run.ExitCode);
        Assert.Contains("[Failed] JWT key preflight", run.Output);
        Assert.Contains("JWT private key file is empty.", run.Output);
        Assert.DoesNotContain("Production configuration", run.Output);
        Assert.DoesNotContain("LocalService private-key ACL", run.Output);
    }

    [Fact]
    public void ProductionAcceptanceScript_RejectsSamePublicAndPrivateKeyPathBeforeStateChanges()
    {
        using var temp = TempDirectory.Create();
        var key = temp.WriteFile("shared.pem", "key");

        var run = RunAcceptanceWhatIf(key, key);

        Assert.NotEqual(0, run.ExitCode);
        Assert.Contains("[Failed] JWT key preflight", run.Output);
        Assert.Contains("JWT public and private key paths must be different.", run.Output);
        Assert.DoesNotContain("Production configuration", run.Output);
        Assert.DoesNotContain("LocalService private-key ACL", run.Output);
    }

    [Fact]
    public void ProductionAcceptanceScript_CapturesJwtToolNonzeroExitCode()
    {
        var script = ReadScript("scripts", "Test-ProductionAcceptance.ps1");

        Assert.Contains("$jwtToolOutput = & powershell.exe @jwtArgs 2>&1", script, StringComparison.Ordinal);
        Assert.Contains("$jwtExitCode = $LASTEXITCODE", script, StringComparison.Ordinal);
        Assert.Contains("JWT tool failed with exit code", script, StringComparison.Ordinal);
    }

    [Fact]
    public void ProductionAcceptanceScript_RequiresJwtTokenFileForSuccess()
    {
        var script = ReadScript("scripts", "Test-ProductionAcceptance.ps1");

        Assert.Contains("JWT tool did not create a token file.", script, StringComparison.Ordinal);
        Assert.Contains("JWT token file is empty.", script, StringComparison.Ordinal);
        Assert.Contains("JWT token was empty.", script, StringComparison.Ordinal);
        Assert.Contains("Remove-Item -LiteralPath $tokenPath -Force -ErrorAction SilentlyContinue", script, StringComparison.Ordinal);
    }

    [Fact]
    public void ProductionAcceptanceScript_DoesNotReportJwtPassedBeforeValidation()
    {
        var script = ReadScript("scripts", "Test-ProductionAcceptance.ps1");

        var issueProbeIndex = script.IndexOf("[void](New-TestToken -TokenName 'issue')", StringComparison.Ordinal);
        var passedIndex = script.IndexOf("Add-Result 'JWT issue' 'Passed'", StringComparison.Ordinal);
        var failedIndex = script.IndexOf("Add-Result 'JWT issue' 'Failed'", StringComparison.Ordinal);

        Assert.True(issueProbeIndex >= 0, "JWT issue must call New-TestToken first.");
        Assert.True(passedIndex > issueProbeIndex, "JWT issue must be marked Passed only after New-TestToken returns.");
        Assert.True(failedIndex > issueProbeIndex, "JWT issue must have a Failed path after New-TestToken throws.");
        Assert.DoesNotContain("New-TestJwt.ps1') `\r\n        -PrivateKeyPath", script, StringComparison.Ordinal);
    }

    [Fact]
    public void ProductionAcceptanceScript_WaitsForDelayedRemovalUsingPolling()
    {
        var script = ReadScript("scripts", "Test-ProductionAcceptance.ps1");

        Assert.Contains("function Wait-ForCardStatus", script, StringComparison.Ordinal);
        Assert.Contains("[int]$PollMilliseconds = 500", script, StringComparison.Ordinal);
        Assert.Contains("[int]$TimeoutSeconds = 15", script, StringComparison.Ordinal);

        var promptIndex = script.IndexOf("Read-Host 'Remove the card, then press Enter'", StringComparison.Ordinal);
        var waitIndex = script.IndexOf("Wait-ForCardStatus -ExpectedStatus 'NoCard' -ResultName 'CardRemoved transition'", StringComparison.Ordinal);

        Assert.True(promptIndex >= 0, "Removal prompt was not found.");
        Assert.True(waitIndex > promptIndex, "Removal must wait for NoCard after the operator prompt.");
    }

    [Fact]
    public void ProductionAcceptanceScript_WaitsForDelayedInsertionUsingPolling()
    {
        var script = ReadScript("scripts", "Test-ProductionAcceptance.ps1");
        var promptIndex = script.IndexOf("Read-Host 'Insert the card, then press Enter'", StringComparison.Ordinal);
        var waitIndex = script.IndexOf("Wait-ForCardStatus -ExpectedStatus 'CardPresent' -ResultName 'CardInserted transition'", StringComparison.Ordinal);

        Assert.True(promptIndex >= 0, "Insertion prompt was not found.");
        Assert.True(waitIndex > promptIndex, "Insertion must wait for CardPresent after the operator prompt.");
    }

    [Fact]
    public void ProductionAcceptanceScript_TimesOutWhenRemovalRemainsCardPresent()
    {
        var script = ReadScript("scripts", "Test-ProductionAcceptance.ps1");

        Assert.Contains("Timed out after ${TimeoutSeconds}s waiting for $ExpectedStatus. Latest status: $latestStatus.", script, StringComparison.Ordinal);
        Assert.Contains("Wait-ForCardStatus -ExpectedStatus 'NoCard' -ResultName 'CardRemoved transition'", script, StringComparison.Ordinal);
        Assert.Contains("Add-Result $ResultName 'Failed'", script, StringComparison.Ordinal);
        Assert.Contains("Complete-Acceptance 1", script, StringComparison.Ordinal);
    }

    [Fact]
    public void ProductionAcceptanceScript_TimesOutWhenInsertionRemainsNoCard()
    {
        var script = ReadScript("scripts", "Test-ProductionAcceptance.ps1");

        Assert.Contains("Timed out after ${TimeoutSeconds}s waiting for $ExpectedStatus. Latest status: $latestStatus.", script, StringComparison.Ordinal);
        Assert.Contains("Wait-ForCardStatus -ExpectedStatus 'CardPresent' -ResultName 'CardInserted transition'", script, StringComparison.Ordinal);
        Assert.Contains("Add-Result $ResultName 'Failed'", script, StringComparison.Ordinal);
        Assert.DoesNotContain("Expected CardPresent after insertion", script, StringComparison.Ordinal);
    }

    [Fact]
    public void ProductionAcceptanceScript_RequiresTwoConsecutiveCardStatusObservations()
    {
        var script = ReadScript("scripts", "Test-ProductionAcceptance.ps1");

        Assert.Contains("[int]$RequiredConsecutiveObservations = 2", script, StringComparison.Ordinal);
        Assert.Contains("$consecutiveObservations++", script, StringComparison.Ordinal);
        Assert.Contains("$consecutiveObservations = 0", script, StringComparison.Ordinal);
        Assert.Contains("$consecutiveObservations -ge $RequiredConsecutiveObservations", script, StringComparison.Ordinal);
    }

    [Fact]
    public void ProductionAcceptanceScript_UsesFreshJwtForEveryCardStatusPoll()
    {
        var script = ReadScript("scripts", "Test-ProductionAcceptance.ps1");

        Assert.Contains("$status = Invoke-AgentJson -Method Get -Path '/api/v1/card/status' -TokenName 'status'", script, StringComparison.Ordinal);
        Assert.Contains("$token = New-TestToken -TokenName $TokenName", script, StringComparison.Ordinal);
        Assert.DoesNotContain("cachedToken", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("$script:token", script, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProductionAcceptanceScript_TimeoutFailureDoesNotReportCardTransitionPassed()
    {
        var script = ReadScript("scripts", "Test-ProductionAcceptance.ps1");
        var failureIndex = script.IndexOf("Add-Result $ResultName 'Failed'", StringComparison.Ordinal);
        var exitIndex = script.IndexOf("Complete-Acceptance 1", failureIndex, StringComparison.Ordinal);
        var oldRemovalPassed = "Add-Result 'CardRemoved transition' 'Passed'";
        var oldInsertionPassed = "Add-Result 'CardInserted transition' 'Passed'";

        Assert.True(failureIndex >= 0, "Wait timeout failure result was not found.");
        Assert.True(exitIndex > failureIndex, "Wait timeout must return a non-zero exit after reporting Failed.");
        Assert.DoesNotContain(oldRemovalPassed, script, StringComparison.Ordinal);
        Assert.DoesNotContain(oldInsertionPassed, script, StringComparison.Ordinal);
    }

    [Fact]
    public void ProductionAcceptanceScript_SeparatesSseValidationFromStatusPolling()
    {
        var script = ReadScript("scripts", "Test-ProductionAcceptance.ps1");

        Assert.Contains("Add-Result 'SSE CardRemoved' 'Not Tested'", script, StringComparison.Ordinal);
        Assert.Contains("Add-Result 'SSE CardInserted' 'Not Tested'", script, StringComparison.Ordinal);
        Assert.Contains("Status polling is not SSE validation", script, StringComparison.Ordinal);
    }
    [Fact]
    public void SseEventsScript_UsesHttpsJwtAndDoesNotBypassCertificateValidation()
    {
        var script = ReadScript("scripts", "Test-SseEvents.ps1");

        Assert.StartsWith("#requires -Version 5.1", script, StringComparison.Ordinal);
        Assert.Contains("https://localhost:18443", script, StringComparison.Ordinal);
        Assert.Contains("/api/v1/events", script, StringComparison.Ordinal);
        Assert.Contains("New-TestJwt.ps1", script, StringComparison.Ordinal);
        Assert.Contains("-LifetimeSeconds", script, StringComparison.Ordinal);
        Assert.Contains("'60'", script, StringComparison.Ordinal);
        Assert.Contains("Authorization", script, StringComparison.Ordinal);
        Assert.DoesNotContain("SkipCertificate", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ServerCertificateValidationCallback", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Bearer $token\" |", script, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SseEventsScript_ValidatesExpectedEventsAndSafeAtr()
    {
        var script = ReadScript("scripts", "Test-SseEvents.ps1");

        Assert.Contains("Wait-SseEvent -Connection $connection -ExpectedEventType 'CardRemoved'", script, StringComparison.Ordinal);
        Assert.Contains("Wait-SseEvent -Connection $connection -ExpectedEventType 'CardInserted'", script, StringComparison.Ordinal);
        Assert.Contains("[int]$TimeoutSeconds = 30", script, StringComparison.Ordinal);
        Assert.Contains("readerName was missing", script, StringComparison.Ordinal);
        Assert.Contains("eventType was missing", script, StringComparison.Ordinal);
        Assert.Contains("occurredAtUtc was invalid", script, StringComparison.Ordinal);
        Assert.Contains("^([0-9A-F]{2})(-[0-9A-F]{2})*$", script, StringComparison.Ordinal);
        Assert.Contains("Repeated connect/disconnect", script, StringComparison.Ordinal);
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

        var powershell = GetWindowsPowerShellPath();
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

    private static ScriptRun RunAcceptanceWhatIf(string publicKeyPath, string privateKeyPath)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new ScriptRun(0, string.Empty, string.Empty);
        }

        var processStart = new System.Diagnostics.ProcessStartInfo
        {
            FileName = GetWindowsPowerShellPath(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        processStart.ArgumentList.Add("-NoProfile");
        processStart.ArgumentList.Add("-ExecutionPolicy");
        processStart.ArgumentList.Add("Bypass");
        processStart.ArgumentList.Add("-File");
        processStart.ArgumentList.Add(Path.Combine(RepositoryRoot, "scripts", "Test-ProductionAcceptance.ps1"));
        processStart.ArgumentList.Add("-CertificateThumbprint");
        processStart.ArgumentList.Add("0000000000000000000000000000000000000000");
        processStart.ArgumentList.Add("-CertificateHostName");
        processStart.ArgumentList.Add("localhost");
        processStart.ArgumentList.Add("-BaseUrl");
        processStart.ArgumentList.Add("https://localhost:18443");
        processStart.ArgumentList.Add("-JwtPublicKeyPath");
        processStart.ArgumentList.Add(publicKeyPath);
        processStart.ArgumentList.Add("-JwtPrivateKeyPath");
        processStart.ArgumentList.Add(privateKeyPath);
        processStart.ArgumentList.Add("-WhatIf");

        using var process = System.Diagnostics.Process.Start(processStart);
        Assert.NotNull(process);
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return new ScriptRun(process.ExitCode, output, error);
    }

    private static string GetWindowsPowerShellPath()
    {
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
    }

    private static IEnumerable<string> GetPowerShellScripts()
    {
        return Directory.GetFiles(Path.Combine(RepositoryRoot, "scripts"), "*.ps1").OrderBy(path => path, StringComparer.OrdinalIgnoreCase);
    }

    private static string ReadScript(params string[] parts)
    {
        return File.ReadAllText(Path.Combine([RepositoryRoot, .. parts]));
    }

    private sealed record ScriptRun(int ExitCode, string Output, string Error);

    private sealed class TempDirectory : IDisposable
    {
        private TempDirectory(string path)
        {
            Path = path;
        }

        public string Path { get; }

        public static TempDirectory Create()
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "thai-id-agent-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return new TempDirectory(path);
        }

        public string WriteFile(string fileName, string contents)
        {
            var path = System.IO.Path.Combine(Path, fileName);
            File.WriteAllText(path, contents);
            return path;
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}