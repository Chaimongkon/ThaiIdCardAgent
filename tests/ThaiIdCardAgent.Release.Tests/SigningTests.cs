namespace ThaiIdCardAgent.Release.Tests;

public sealed class SigningTests : IDisposable
{
    private readonly PowerShellHarness _ps = new();

    public void Dispose() => _ps.Dispose();

    [Fact]
    public void CodeSigningCertificate_WithCodeSigningEku_IsAccepted()
    {
        var res = _ps.Run(Fixtures.NewCodeSigningCert() + @"
try {
  $ok = Test-CodeSigningCertificate -Certificate $cert
  Write-Host ('ACCEPTED=' + $ok)
} finally {" + Fixtures.RemoveCert + "}");
        Assert.True(res.Succeeded, res.All);
        Assert.Contains("ACCEPTED=True", res.StdOut);
    }

    [Fact]
    public void CodeSigningCertificate_ServerAuthOnly_IsRejected()
    {
        // A localhost/HTTPS-style certificate (Server Authentication EKU) must not be usable.
        var res = _ps.Run(@"
$cert = New-SelfSignedCertificate -Type SSLServerAuthentication -Subject 'CN=localhost' -CertStoreLocation Cert:\CurrentUser\My
try {
  Test-CodeSigningCertificate -Certificate $cert
  Write-Host 'NO-THROW'
} catch {
  Write-Host ('THREW=' + $_.Exception.Message)
} finally {" + Fixtures.RemoveCert + "}");
        Assert.True(res.Succeeded, res.All);
        Assert.Contains("THREW=", res.StdOut);
        Assert.Contains("Code Signing EKU", res.StdOut);
        Assert.DoesNotContain("NO-THROW", res.StdOut);
    }

    [Fact]
    public void CodeSigningCertificate_Expired_IsRejected()
    {
        var res = _ps.Run(@"
$cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject 'CN=Expired Signer' -CertStoreLocation Cert:\CurrentUser\My -NotBefore (Get-Date).AddDays(-10) -NotAfter (Get-Date).AddDays(-1)
try {
  Test-CodeSigningCertificate -Certificate $cert
  Write-Host 'NO-THROW'
} catch {
  Write-Host ('THREW=' + $_.Exception.Message)
} finally {" + Fixtures.RemoveCert + "}");
        Assert.True(res.Succeeded, res.All);
        Assert.Contains("expired", res.All, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("NO-THROW", res.StdOut);
    }

    [Fact]
    public void SignRelease_UnsignedMode_KeepsUnsignedPilot()
    {
        var work = _ps.NewTempDir();
        var res = _ps.Run(Fixtures.BuildUnsignedPackage(work, "1.0.0") + @"
& (Join-Path $ScriptsDir 'Sign-Release.ps1') -PackagePath $pkg -Unsigned
$m = Get-Content -LiteralPath (Join-Path $pkg 'release-manifest.json') -Raw | ConvertFrom-Json
Write-Host ('signingStatus=' + $m.signingStatus)
");
        Assert.True(res.Succeeded, res.All);
        Assert.Contains("signingStatus=UnsignedPilot", res.StdOut);
        Assert.Contains("UNSIGNED PILOT", res.All, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TestReleaseSignature_SingleTargetFile_DoesNotErrorUnderStrictMode()
    {
        // A self-contained single-file publish yields exactly one signable target (the exe).
        // Regression: $targets must be treated as an array so .Count works under StrictMode.
        var work = _ps.NewTempDir();
        var res = _ps.Run($@"
$work = {Fixtures.Lit(work)}
$pub = Join-Path $work 'publish'
New-Item -ItemType Directory -Force -Path $pub | Out-Null
Copy-Item $UnsignedPe (Join-Path $pub 'ThaiIdCardAgent.Service.exe')
& (Join-Path $ScriptsDir 'New-ReleasePackage.ps1') -Version '1.0.0' -PublishPath $pub -OutputRoot (Join-Path $work 'release') -SkipPublish *> $null
$pkg = Join-Path $work 'release\ThaiIdCardAgent-1.0.0-win-x64'
& (Join-Path $ScriptsDir 'Test-ReleaseSignature.ps1') -PackagePath $pkg
Write-Host ('exit=' + $LASTEXITCODE)
");
        Assert.True(res.Succeeded, res.All);
        Assert.Contains("Allowlist targets: 1", res.StdOut);
        Assert.DoesNotContain("cannot be found", res.All);
    }

    [Fact]
    public void TestReleaseSignature_RequireSigned_RejectsUnsignedPackage()
    {
        var work = _ps.NewTempDir();
        var res = _ps.Run(Fixtures.BuildUnsignedPackage(work, "1.0.0") + @"
& (Join-Path $ScriptsDir 'Test-ReleaseSignature.ps1') -PackagePath $pkg -RequireSigned
");
        Assert.False(res.Succeeded, res.All);
        Assert.Contains("REJECTED", res.All, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SignAndVerify_WithRealCert_Passes()
    {
        var work = _ps.NewTempDir();
        var res = _ps.Run(Fixtures.BuildUnsignedPackage(work, "1.0.0") + Fixtures.NewCodeSigningCert() + @"
try {
  & (Join-Path $ScriptsDir 'Sign-Release.ps1') -PackagePath $pkg -CertificateThumbprint $cert.Thumbprint -StoreLocation CurrentUser
  & (Join-Path $ScriptsDir 'Test-ReleaseSignature.ps1') -PackagePath $pkg -RequireSigned
  $m = Get-Content -LiteralPath (Join-Path $pkg 'release-manifest.json') -Raw | ConvertFrom-Json
  Write-Host ('signingStatus=' + $m.signingStatus)
  Write-Host ('thumb=' + $m.signing.certificateThumbprint)
} finally {" + Fixtures.RemoveCert + "}");
        Assert.True(res.Succeeded, res.All);
        Assert.Contains("signingStatus=Signed", res.StdOut);
        Assert.Contains("RequireSigned: PASSED", res.StdOut);
    }

    [Fact]
    public void SignedThenTampered_SignatureVerification_IsRejected()
    {
        var work = _ps.NewTempDir();
        var res = _ps.Run(Fixtures.BuildUnsignedPackage(work, "1.0.0") + Fixtures.NewCodeSigningCert() + @"
try {
  & (Join-Path $ScriptsDir 'Sign-Release.ps1') -PackagePath $pkg -CertificateThumbprint $cert.Thumbprint -StoreLocation CurrentUser | Out-Null
  # Tamper the signed executable, then refresh checksums so integrity passes and only the
  # Authenticode signature is left to catch the change. Flip a run of bytes near the start of
  # the first section so the change is guaranteed to fall inside the Authenticode-hashed region.
  $exe = Join-Path $pkg 'app\ThaiIdCardAgent.Service.exe'
  $bytes = [System.IO.File]::ReadAllBytes($exe)
  $start = [Math]::Min(512, [int]($bytes.Length * 0.25))
  for ($j = 0; $j -lt 64; $j++) { $bytes[$start + $j] = [byte]($bytes[$start + $j] -bxor 0xFF) }
  [System.IO.File]::WriteAllBytes($exe, $bytes)
  New-ReleaseChecksumManifest -PackageRoot $pkg | Out-Null
  & (Join-Path $ScriptsDir 'Test-ReleaseSignature.ps1') -PackagePath $pkg -RequireSigned
} finally {" + Fixtures.RemoveCert + "}");
        Assert.False(res.Succeeded, res.All);
        Assert.Contains("REJECTED", res.All, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Sign_WithUnreachableTimestampServer_DoesNotReportPassed()
    {
        var work = _ps.NewTempDir();
        var res = _ps.Run(Fixtures.BuildUnsignedPackage(work, "1.0.0") + Fixtures.NewCodeSigningCert() + @"
try {
  & (Join-Path $ScriptsDir 'Sign-Release.ps1') -PackagePath $pkg -CertificateThumbprint $cert.Thumbprint -StoreLocation CurrentUser -TimestampServer 'http://127.0.0.1:9/tsa'
  Write-Host 'REPORTED-PASSED'
} catch {
  Write-Host ('THREW=' + $_.Exception.Message)
} finally {
  $m = Get-Content -LiteralPath (Join-Path $pkg 'release-manifest.json') -Raw | ConvertFrom-Json
  Write-Host ('signingStatus=' + $m.signingStatus)
" + Fixtures.RemoveCert + "}");
        // Either the script threw (Stop) or reported a failure; in no case should it flip to Signed.
        Assert.Contains("signingStatus=UnsignedPilot", res.StdOut);
        Assert.DoesNotContain("REPORTED-PASSED", res.StdOut);
    }

    [Fact]
    public void Sign_WithPfxPassword_DoesNotLogThePassword()
    {
        const string password = "P@ssw0rd-DO-NOT-LOG-8842";
        var work = _ps.NewTempDir();
        var body = Fixtures.BuildUnsignedPackage(work, "1.0.0") + Fixtures.NewCodeSigningCert()
            + "$pfx = Join-Path $work 'signer.pfx'\n"
            + "$pw = ConvertTo-SecureString '" + password + "' -AsPlainText -Force\n"
            + "try {\n"
            + "  Export-PfxCertificate -Cert $cert -FilePath $pfx -Password $pw | Out-Null\n"
            + "  & (Join-Path $ScriptsDir 'Sign-Release.ps1') -PackagePath $pkg -PfxPath $pfx -PfxPassword $pw\n"
            + "  $m = Get-Content -LiteralPath (Join-Path $pkg 'release-manifest.json') -Raw | ConvertFrom-Json\n"
            + "  Write-Host ('signingStatus=' + $m.signingStatus)\n"
            + "} finally {" + Fixtures.RemoveCert + "}\n";
        var res = _ps.Run(body);
        Assert.True(res.Succeeded, res.All);
        Assert.Contains("signingStatus=Signed", res.StdOut);
        Assert.DoesNotContain(password, res.All);
    }
}
