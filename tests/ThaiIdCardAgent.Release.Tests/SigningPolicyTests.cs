namespace ThaiIdCardAgent.Release.Tests;

/// <summary>
/// Production signing readiness: the signing allowlist, certificate-identity gates, signing
/// configuration hygiene, the mandatory release stage order, and the signing evidence recorded in
/// release-manifest.json.
/// </summary>
public sealed class SigningPolicyTests : IDisposable
{
    private readonly PowerShellHarness _ps = new();

    public void Dispose() => _ps.Dispose();

    // ---- Certificate gates ----------------------------------------------------------

    [Fact]
    public void Certificate_WithoutPrivateKey_IsRejected()
    {
        var work = _ps.NewTempDir();
        var res = _ps.Run($@"
$work = {Fixtures.Lit(work)}
" + Fixtures.NewCodeSigningCert() + @"
try {
  $cer = Join-Path $work 'public-only.cer'
  Export-Certificate -Cert $cert -FilePath $cer | Out-Null
  $publicOnly = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new($cer)
  Write-Host ('hasPrivateKey=' + $publicOnly.HasPrivateKey)
  try {
    Test-CodeSigningCertificate -Certificate $publicOnly
    Write-Host 'NO-THROW'
  } catch { Write-Host ('THREW=' + $_.Exception.Message) }
} finally {" + Fixtures.RemoveCert + "}");
        Assert.True(res.Succeeded, res.All);
        Assert.Contains("hasPrivateKey=False", res.StdOut);
        Assert.Contains("private key", res.StdOut, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("NO-THROW", res.StdOut);
    }

    [Fact]
    public void Certificate_WithNoEkuAtAll_IsRejected()
    {
        // A certificate with no EKU extension is not usable for code signing either: the check
        // must require the Code Signing EKU to be present, not merely require Server Auth absent.
        var res = _ps.Run(@"
$cert = New-SelfSignedCertificate -Type Custom -Subject 'CN=No Eku Signer' -CertStoreLocation Cert:\CurrentUser\My -KeyUsage DigitalSignature
try {
  Test-CodeSigningCertificate -Certificate $cert
  Write-Host 'NO-THROW'
} catch { Write-Host ('THREW=' + $_.Exception.Message) }
finally {" + Fixtures.RemoveCert + "}");
        Assert.True(res.Succeeded, res.All);
        Assert.Contains("Code Signing EKU", res.StdOut);
        Assert.DoesNotContain("NO-THROW", res.StdOut);
    }

    [Fact]
    public void Certificate_NotYetValid_IsRejected()
    {
        var res = _ps.Run(@"
$cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject 'CN=Future Signer' -CertStoreLocation Cert:\CurrentUser\My -NotBefore (Get-Date).AddDays(5) -NotAfter (Get-Date).AddDays(30)
try {
  Test-CodeSigningCertificate -Certificate $cert
  Write-Host 'NO-THROW'
} catch { Write-Host ('THREW=' + $_.Exception.Message) }
finally {" + Fixtures.RemoveCert + "}");
        Assert.True(res.Succeeded, res.All);
        Assert.Contains("not yet valid", res.All, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("NO-THROW", res.StdOut);
    }

    [Fact]
    public void Certificate_WrongSignerThumbprint_IsRejected()
    {
        var res = _ps.Run(Fixtures.NewCodeSigningCert() + @"
try {
  Test-CodeSigningCertificate -Certificate $cert -ExpectedThumbprint '0000000000000000000000000000000000000000'
  Write-Host 'NO-THROW'
} catch { Write-Host ('THREW=' + $_.Exception.Message) }
finally {" + Fixtures.RemoveCert + "}");
        Assert.True(res.Succeeded, res.All);
        Assert.Contains("Signer mismatch", res.StdOut);
        Assert.DoesNotContain("NO-THROW", res.StdOut);
    }

    [Fact]
    public void Certificate_TooCloseToExpiry_IsRejected()
    {
        // Renewal guard: a certificate that is still valid but expires within the required window
        // must not be used to sign a release.
        var res = _ps.Run(Fixtures.NewCodeSigningCert(validDays: 2) + @"
try {
  Test-CodeSigningCertificate -Certificate $cert -MinimumRemainingDays 30
  Write-Host 'NO-THROW'
} catch { Write-Host ('THREW=' + $_.Exception.Message) }
finally {" + Fixtures.RemoveCert + "}");
        Assert.True(res.Succeeded, res.All);
        Assert.Contains("below the required minimum", res.StdOut);
        Assert.DoesNotContain("NO-THROW", res.StdOut);
    }

    // ---- Signing allowlist ----------------------------------------------------------

    [Fact]
    public void Allowlist_UnexpectedExecutableInPayload_IsRejectedAtVerification()
    {
        var work = _ps.NewTempDir();
        var res = _ps.Run(Fixtures.BuildUnsignedPackage(work, "1.0.0") + @"
# Drop an executable the allowlist does not account for, then refresh checksums so ONLY the
# allowlist can catch it (integrity alone would already be satisfied).
Copy-Item $UnsignedPe (Join-Path $pkg 'app\rogue-payload.dll')
New-ReleaseChecksumManifest -PackageRoot $pkg | Out-Null
$plan = Resolve-ReleaseSigningPlan -PackageRoot $pkg
Write-Host ('planOk=' + $plan.Ok)
Write-Host ('unexpected=' + (@($plan.UnexpectedExecutables | ForEach-Object { $_.RelativePath }) -join ','))
& (Join-Path $ScriptsDir 'Test-ReleaseSignature.ps1') -PackagePath $pkg
");
        Assert.False(res.Succeeded, res.All);
        Assert.Contains("planOk=False", res.StdOut);
        Assert.Contains("app/rogue-payload.dll", res.StdOut);
        Assert.Contains("not in the signing allowlist", res.All);
        Assert.Contains("REJECTED", res.All, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Allowlist_UnexpectedExecutableInPublish_RefusesToPackage()
    {
        var work = _ps.NewTempDir();
        var res = _ps.Run($@"
$work = {Fixtures.Lit(work)}
$pub = Join-Path $work 'publish'
New-Item -ItemType Directory -Force -Path $pub | Out-Null
Copy-Item $UnsignedPe (Join-Path $pub 'ThaiIdCardAgent.Service.exe')
Copy-Item $UnsignedPe (Join-Path $pub 'ThirdParty.Unexpected.exe')
& (Join-Path $ScriptsDir 'New-ReleasePackage.ps1') -Version '1.0.0' -PublishPath $pub -OutputRoot (Join-Path $work 'release') -SkipPublish
");
        Assert.False(res.Succeeded, res.All);
        Assert.Contains("Unexpected executable content", res.All);
        Assert.Contains("ThirdParty.Unexpected.exe", res.All);
    }

    [Fact]
    public void Allowlist_MissingRequiredFile_IsRejected()
    {
        var work = _ps.NewTempDir();
        var res = _ps.Run(Fixtures.BuildUnsignedPackage(work, "1.0.0") + @"
Remove-Item -LiteralPath (Join-Path $pkg 'app\ThaiIdCardAgent.Service.exe') -Force
New-ReleaseChecksumManifest -PackageRoot $pkg | Out-Null
$plan = Resolve-ReleaseSigningPlan -PackageRoot $pkg
Write-Host ('missing=' + ($plan.MissingRequired -join ','))
& (Join-Path $ScriptsDir 'Test-ReleaseSignature.ps1') -PackagePath $pkg
");
        Assert.False(res.Succeeded, res.All);
        Assert.Contains("app/ThaiIdCardAgent.Service.exe", res.StdOut);
        Assert.Contains("missing required signed file", res.All, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Allowlist_IsLoadedFromDisk_AndFailsClosedWhenMissing()
    {
        var work = _ps.NewTempDir();
        var res = _ps.Run($@"
$missing = Join-Path {Fixtures.Lit(work)} 'no-such-allowlist.json'
try {{
  Get-ReleaseSigningPolicy -PolicyPath $missing
  Write-Host 'NO-THROW'
}} catch {{ Write-Host ('THREW=' + $_.Exception.Message) }}
$policy = Get-ReleaseSigningPolicy
Write-Host ('required=' + ($policy.RequiredSigned -join ','))
");
        Assert.True(res.Succeeded, res.All);
        Assert.Contains("THREW=", res.StdOut);
        Assert.DoesNotContain("NO-THROW", res.StdOut);
        Assert.Contains("required=app/ThaiIdCardAgent.Service.exe", res.StdOut);
    }

    // ---- Unsigned / wrong signer gates ----------------------------------------------

    [Fact]
    public void UnsignedRequiredBinary_InAnOtherwiseSignedPackage_IsRejected()
    {
        var work = _ps.NewTempDir();
        var res = _ps.Run(Fixtures.BuildUnsignedPackage(work, "1.0.0") + Fixtures.NewCodeSigningCert() + @"
try {
  & (Join-Path $ScriptsDir 'Sign-Release.ps1') -PackagePath $pkg -CertificateThumbprint $cert.Thumbprint -StoreLocation CurrentUser *> $null
  # Swap the signed required binary back to an unsigned copy and refresh checksums, so the package
  # still declares Signed and still passes integrity but the required file carries no signature.
  Copy-Item $UnsignedPe (Join-Path $pkg 'app\ThaiIdCardAgent.Service.exe') -Force
  New-ReleaseChecksumManifest -PackageRoot $pkg | Out-Null
  & (Join-Path $ScriptsDir 'Test-ReleaseSignature.ps1') -PackagePath $pkg -RequireSigned
} finally {" + Fixtures.RemoveCert + "}");
        Assert.False(res.Succeeded, res.All);
        Assert.Contains("NotSigned: app/ThaiIdCardAgent.Service.exe", res.StdOut);
        Assert.Contains("REJECTED", res.All, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WrongSigner_IsRejectedByVerification()
    {
        var work = _ps.NewTempDir();
        var res = _ps.Run(Fixtures.BuildUnsignedPackage(work, "1.0.0") + Fixtures.NewCodeSigningCert() + @"
try {
  & (Join-Path $ScriptsDir 'Sign-Release.ps1') -PackagePath $pkg -CertificateThumbprint $cert.Thumbprint -StoreLocation CurrentUser *> $null
  # Correct signer passes.
  & (Join-Path $ScriptsDir 'Test-ReleaseSignature.ps1') -PackagePath $pkg -RequireSigned -ExpectedSignerThumbprint $cert.Thumbprint *> $null
  Write-Host ('correctSigner=' + ($LASTEXITCODE -eq 0 -or $null -eq $LASTEXITCODE))
  # A different expected signer must be rejected.
  & (Join-Path $ScriptsDir 'Test-ReleaseSignature.ps1') -PackagePath $pkg -RequireSigned -ExpectedSignerThumbprint '1111111111111111111111111111111111111111'
} finally {" + Fixtures.RemoveCert + "}");
        Assert.False(res.Succeeded, res.All);
        Assert.Contains("correctSigner=True", res.StdOut);
        Assert.Contains("Signer mismatch", res.All);
    }

    [Fact]
    public void CatalogSignedFileWithoutEmbeddedSignature_IsNotAcceptedAsSigned()
    {
        // Windows resolves a catalog-signed file as Valid and reports the catalog's signer even
        // though that signer never signed this file. A release binary must carry its own embedded
        // signature, so verification must not be satisfied by a catalog signature.
        var res = _ps.Run(@"
$sample = 'C:\Windows\System32\where.exe'
if (-not (Test-Path -LiteralPath $sample)) { Write-Host 'SKIPPED=no-sample'; exit 0 }
$os = Get-AuthenticodeSignature -LiteralPath $sample
$detail = Get-AuthenticodeSignatureDetail -LiteralPath $sample
Write-Host ('osStatus=' + $os.Status)
Write-Host ('embedded=' + $detail.HasSignature)
$r = Test-ReleaseSignatureFile -LiteralPath $sample -RelativePath 'app/sample.exe'
Write-Host ('ok=' + $r.Ok)
Write-Host ('signed=' + $r.Signed)
Write-Host ('reasons=' + ($r.Reasons -join '|'))
");
        Assert.True(res.Succeeded, res.All);
        if (res.StdOut.Contains("SKIPPED=no-sample", StringComparison.Ordinal))
        {
            return;
        }
        Assert.Contains("osStatus=Valid", res.StdOut);
        Assert.Contains("embedded=False", res.StdOut);
        Assert.Contains("ok=False", res.StdOut);
        Assert.Contains("signed=False", res.StdOut);
        Assert.Contains("no embedded Authenticode signature", res.StdOut);
    }

    // ---- Timestamp requirements -----------------------------------------------------

    [Fact]
    public void RequireRfc3161Timestamp_WithoutTimestampServer_RefusesToSign()
    {
        var work = _ps.NewTempDir();
        var res = _ps.Run(Fixtures.BuildUnsignedPackage(work, "1.0.0") + Fixtures.NewCodeSigningCert() + @"
try {
  & (Join-Path $ScriptsDir 'Sign-Release.ps1') -PackagePath $pkg -CertificateThumbprint $cert.Thumbprint -StoreLocation CurrentUser -RequireRfc3161Timestamp
  Write-Host 'NO-THROW'
} catch { Write-Host ('THREW=' + $_.Exception.Message) }
finally {
  $m = Get-Content -LiteralPath (Join-Path $pkg 'release-manifest.json') -Raw | ConvertFrom-Json
  Write-Host ('signingStatus=' + $m.signingStatus)
" + Fixtures.RemoveCert + "}");
        Assert.Contains("signingStatus=UnsignedPilot", res.StdOut);
        Assert.DoesNotContain("NO-THROW", res.StdOut);
    }

    [Fact]
    public void PowerShellBackend_CannotSatisfyRfc3161Requirement()
    {
        // Set-AuthenticodeSignature applies the legacy Authenticode timestamp, never RFC 3161.
        // The script must say so instead of producing a release that silently lacks RFC 3161.
        var work = _ps.NewTempDir();
        var res = _ps.Run(Fixtures.BuildUnsignedPackage(work, "1.0.0") + Fixtures.NewCodeSigningCert() + @"
try {
  & (Join-Path $ScriptsDir 'Sign-Release.ps1') -PackagePath $pkg -CertificateThumbprint $cert.Thumbprint `
      -StoreLocation CurrentUser -Backend PowerShell -TimestampServer 'http://127.0.0.1:9/tsa' -RequireRfc3161Timestamp
  Write-Host 'NO-THROW'
} catch { Write-Host ('THREW=' + $_.Exception.Message) }
finally {
  $m = Get-Content -LiteralPath (Join-Path $pkg 'release-manifest.json') -Raw | ConvertFrom-Json
  Write-Host ('signingStatus=' + $m.signingStatus)
" + Fixtures.RemoveCert + "}");
        Assert.Contains("legacy Authenticode timestamp", res.All);
        Assert.Contains("signingStatus=UnsignedPilot", res.StdOut);
        Assert.DoesNotContain("NO-THROW", res.StdOut);
    }

    [Fact]
    public void SignedPackageWithoutTimestamp_IsRejectedWhenTimestampIsRequired()
    {
        var work = _ps.NewTempDir();
        var res = _ps.Run(Fixtures.BuildUnsignedPackage(work, "1.0.0") + Fixtures.NewCodeSigningCert() + @"
try {
  & (Join-Path $ScriptsDir 'Sign-Release.ps1') -PackagePath $pkg -CertificateThumbprint $cert.Thumbprint -StoreLocation CurrentUser *> $null
  & (Join-Path $ScriptsDir 'Test-ReleaseSignature.ps1') -PackagePath $pkg -RequireSigned -RequireTimestamp
} finally {" + Fixtures.RemoveCert + "}");
        Assert.False(res.Succeeded, res.All);
        Assert.Contains("no timestamp", res.All, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("REJECTED", res.All, StringComparison.OrdinalIgnoreCase);
    }

    // ---- Signing configuration hygiene ----------------------------------------------

    [Fact]
    public void SigningConfig_WithUnresolvedPlaceholder_IsRejected()
    {
        // The shipped template must not be usable as-is: procurement has to fill in the confirmed
        // certificate thumbprint and RFC 3161 timestamp URL first.
        var res = _ps.Run(@"
$template = Join-Path $ScriptsDir 'signing-config.template.json'
Write-Host ('templateExists=' + (Test-Path -LiteralPath $template))
try {
  New-ReleaseSigningOption -ConfigPath $template
  Write-Host 'NO-THROW'
} catch { Write-Host ('THREW=' + $_.Exception.Message) }
");
        Assert.True(res.Succeeded, res.All);
        Assert.Contains("templateExists=True", res.StdOut);
        Assert.Contains("unresolved placeholder", res.StdOut);
        Assert.DoesNotContain("NO-THROW", res.StdOut);
    }

    [Fact]
    public void SigningConfig_WithSecretBearingKey_IsRejected()
    {
        var work = _ps.NewTempDir();
        var res = _ps.Run($@"
$work = {Fixtures.Lit(work)}
$cfg = Join-Path $work 'signing.json'
$json = '{{ ""certificateThumbprint"": ""AABBCCDDEEFF00112233445566778899AABBCCDD"", ""timestampServerUrl"": ""http://tsa.example.invalid"", ""tokenPassword"": ""hunter2"" }}'
Set-Content -LiteralPath $cfg -Value $json -NoNewline
try {{
  New-ReleaseSigningOption -ConfigPath $cfg
  Write-Host 'NO-THROW'
}} catch {{ Write-Host ('THREW=' + $_.Exception.Message) }}
");
        Assert.True(res.Succeeded, res.All);
        Assert.Contains("forbidden secret-bearing key", res.StdOut);
        Assert.Contains("tokenPassword", res.StdOut);
        Assert.DoesNotContain("NO-THROW", res.StdOut);
        // The rejection message must not echo the value itself.
        Assert.DoesNotContain("hunter2", res.All);
    }

    [Fact]
    public void SigningConfig_MissingCertificateThumbprint_IsRejected()
    {
        var work = _ps.NewTempDir();
        var res = _ps.Run($@"
$work = {Fixtures.Lit(work)}
$cfg = Join-Path $work 'signing.json'
Set-Content -LiteralPath $cfg -Value '{{ ""timestampServerUrl"": ""http://tsa.example.invalid"" }}' -NoNewline
try {{
  New-ReleaseSigningOption -ConfigPath $cfg
  Write-Host 'NO-THROW'
}} catch {{ Write-Host ('THREW=' + $_.Exception.Message) }}
");
        Assert.True(res.Succeeded, res.All);
        Assert.Contains("certificateThumbprint", res.StdOut);
        Assert.DoesNotContain("NO-THROW", res.StdOut);
    }

    [Fact]
    public void SignToolArguments_CarryingACredential_AreRejected()
    {
        var res = _ps.Run(@"
foreach ($bad in @('/p', '/pin', '--password', 'pin=1234')) {
  try {
    Test-SignToolArgumentSafety -Argument @($bad)
    Write-Host ('ACCEPTED=' + $bad)
  } catch { Write-Host ('REJECTED=' + $bad) }
}
Test-SignToolArgumentSafety -Argument @('/fd', 'SHA256') | Out-Null
Write-Host 'SAFE-ARGS-OK'
");
        Assert.True(res.Succeeded, res.All);
        Assert.Contains("REJECTED=/p", res.StdOut);
        Assert.Contains("REJECTED=/pin", res.StdOut);
        Assert.Contains("REJECTED=--password", res.StdOut);
        Assert.Contains("REJECTED=pin=1234", res.StdOut);
        Assert.DoesNotContain("ACCEPTED=", res.StdOut);
        Assert.Contains("SAFE-ARGS-OK", res.StdOut);
    }

    // ---- Release stage order ---------------------------------------------------------

    [Fact]
    public void NewReleasePackage_SkipZip_DefersTheZipUntilAfterSigning()
    {
        // Required order: publish -> sign -> verify -> checksums -> manifest -> zip -> verify zip.
        // -SkipZip is what keeps an unsigned ZIP from ever existing on disk for a signed release.
        var work = _ps.NewTempDir();
        var res = _ps.Run(Fixtures.BuildUnsignedPackageNoZip(work, "1.0.0") + Fixtures.NewCodeSigningCert() + @"
$zip = Join-Path $work 'release\ThaiIdCardAgent-1.0.0-win-x64.zip'
Write-Host ('zipAfterBuild=' + (Test-Path -LiteralPath $zip))
try {
  & (Join-Path $ScriptsDir 'Sign-Release.ps1') -PackagePath $pkg -CertificateThumbprint $cert.Thumbprint -StoreLocation CurrentUser *> $null
  Write-Host ('zipAfterSign=' + (Test-Path -LiteralPath $zip))
  # The ZIP must contain the SIGNED binary, byte for byte.
  Add-Type -AssemblyName System.IO.Compression.FileSystem | Out-Null
  $archive = [System.IO.Compression.ZipFile]::OpenRead($zip)
  try {
    $entry = $archive.Entries | Where-Object { $_.FullName -eq 'app/ThaiIdCardAgent.Service.exe' }
    $stream = $entry.Open()
    $ms = [System.IO.MemoryStream]::new()
    try { $stream.CopyTo($ms) } finally { $stream.Dispose() }
    $sha = [System.Security.Cryptography.SHA256]::Create()
    $zipHash = ([System.BitConverter]::ToString($sha.ComputeHash($ms.ToArray()))).Replace('-','')
    $sha.Dispose(); $ms.Dispose()
  } finally { $archive.Dispose() }
  $diskHash = (Get-FileHash -LiteralPath (Join-Path $pkg 'app\ThaiIdCardAgent.Service.exe') -Algorithm SHA256).Hash
  Write-Host ('zipMatchesSignedBinary=' + ($zipHash -eq $diskHash))
} finally {" + Fixtures.RemoveCert + "}");
        Assert.True(res.Succeeded, res.All);
        Assert.Contains("zipAfterBuild=False", res.StdOut);
        Assert.Contains("zipAfterSign=True", res.StdOut);
        Assert.Contains("zipMatchesSignedBinary=True", res.StdOut);
    }

    [Fact]
    public void GeneratedPackage_ZipVerification_PassesAndCatchesTampering()
    {
        var work = _ps.NewTempDir();
        var res = _ps.Run(Fixtures.BuildUnsignedPackageNoZip(work, "1.0.0") + Fixtures.NewCodeSigningCert() + @"
try {
  & (Join-Path $ScriptsDir 'Sign-Release.ps1') -PackagePath $pkg -CertificateThumbprint $cert.Thumbprint -StoreLocation CurrentUser *> $null
  $zip = Join-Path $work 'release\ThaiIdCardAgent-1.0.0-win-x64.zip'
  $clean = Test-ReleaseZipIntegrity -ZipPath $zip -RequireSigned -SigningReportParameters @{ ExpectedThumbprint = $cert.Thumbprint }
  Write-Host ('cleanZipOk=' + $clean.Ok)

  # Rebuild a ZIP whose payload no longer matches the checksum manifest.
  Set-Content -LiteralPath (Join-Path $pkg 'app\appsettings.json') -Value '{""tampered"":true}' -NoNewline
  $badZip = Join-Path $work 'tampered.zip'
  New-ReleasePackageZip -PackageRoot $pkg -DestinationZip $badZip | Out-Null
  $bad = Test-ReleaseZipIntegrity -ZipPath $badZip -RequireSigned
  Write-Host ('tamperedZipOk=' + $bad.Ok)
} finally {" + Fixtures.RemoveCert + "}");
        Assert.True(res.Succeeded, res.All);
        Assert.Contains("cleanZipOk=True", res.StdOut);
        Assert.Contains("tamperedZipOk=False", res.StdOut);
    }

    // ---- Manifest signing evidence ---------------------------------------------------

    [Fact]
    public void Manifest_RecordsCompleteSigningEvidence_WithoutSecrets()
    {
        var work = _ps.NewTempDir();
        var res = _ps.Run(Fixtures.BuildUnsignedPackage(work, "3.1.4") + Fixtures.NewCodeSigningCert() + @"
try {
  & (Join-Path $ScriptsDir 'Sign-Release.ps1') -PackagePath $pkg -CertificateThumbprint $cert.Thumbprint -StoreLocation CurrentUser *> $null
  $raw = Get-Content -LiteralPath (Join-Path $pkg 'release-manifest.json') -Raw
  $m = $raw | ConvertFrom-Json
  Write-Host ('signingStatus=' + $m.signingStatus)
  Write-Host ('signerSubject=' + $m.signing.signerSubject)
  Write-Host ('signerIssuer=' + $m.signing.signerIssuer)
  Write-Host ('thumbprint=' + $m.signing.certificateThumbprint)
  Write-Host ('signatureAlgorithm=' + $m.signing.signatureAlgorithm)
  Write-Host ('timestamped=' + $m.signing.timestamped)
  Write-Host ('timestampKind=' + $m.signing.timestampKind)
  Write-Host ('notBefore=' + $m.signing.certificateValidity.notBeforeUtc)
  Write-Host ('notAfter=' + $m.signing.certificateValidity.notAfterUtc)
  Write-Host ('verificationResult=' + $m.signing.verification.result)
  Write-Host ('requiredFileCount=' + $m.signing.verification.requiredFileCount)
  Write-Host ('signedFileCount=' + $m.signing.verification.signedFileCount)
  Write-Host ('--RAW--')
  Write-Host $raw
} finally {" + Fixtures.RemoveCert + "}");
        Assert.True(res.Succeeded, res.All);
        Assert.Contains("signingStatus=Signed", res.StdOut);
        Assert.Contains("signerSubject=CN=ThaiIdCardAgent Pilot Test Signer", res.StdOut);
        Assert.Contains("signerIssuer=CN=", res.StdOut);
        Assert.Contains("signatureAlgorithm=SHA256", res.StdOut);
        Assert.Contains("timestampKind=", res.StdOut);
        Assert.Contains("verificationResult=Passed", res.StdOut);
        Assert.Contains("requiredFileCount=1", res.StdOut);
        Assert.Contains("signedFileCount=", res.StdOut);
        Assert.Matches(@"notBefore=\d{4}-\d{2}-\d{2}", res.StdOut);
        Assert.Matches(@"notAfter=\d{4}-\d{2}-\d{2}", res.StdOut);

        // The manifest is release evidence, not a credential store.
        var raw = res.StdOut[(res.StdOut.IndexOf("--RAW--", StringComparison.Ordinal) + 7)..];
        foreach (var forbidden in new[] { "password", "passphrase", "\"pin\"", "secret", "privateKey", ".pfx", ".p12" })
        {
            Assert.DoesNotContain(forbidden, raw, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Manifest_UnsignedPilot_HasNoSigningEvidence()
    {
        var work = _ps.NewTempDir();
        var res = _ps.Run(Fixtures.BuildUnsignedPackage(work, "1.0.0") + @"
& (Join-Path $ScriptsDir 'Sign-Release.ps1') -PackagePath $pkg -Unsigned *> $null
$m = Get-Content -LiteralPath (Join-Path $pkg 'release-manifest.json') -Raw | ConvertFrom-Json
Write-Host ('signingStatus=' + $m.signingStatus)
Write-Host ('hasSigning=' + [bool]($m.PSObject.Properties.Name -contains 'signing'))
");
        Assert.True(res.Succeeded, res.All);
        Assert.Contains("signingStatus=UnsignedPilot", res.StdOut);
        Assert.Contains("hasSigning=False", res.StdOut);
    }

    // ---- Signature inspection primitives ---------------------------------------------

    [Fact]
    public void SignatureDetail_ReportsDigestAlgorithmAndTimestampKind()
    {
        var work = _ps.NewTempDir();
        var res = _ps.Run(Fixtures.BuildUnsignedPackage(work, "1.0.0") + Fixtures.NewCodeSigningCert() + @"
try {
  $exe = Join-Path $pkg 'app\ThaiIdCardAgent.Service.exe'
  $before = Get-AuthenticodeSignatureDetail -LiteralPath $exe
  Write-Host ('beforeHasSignature=' + $before.HasSignature)
  Write-Host ('beforeAlgorithm=' + $before.DigestAlgorithm)

  Set-AuthenticodeSignature -FilePath $exe -Certificate $cert -HashAlgorithm SHA256 | Out-Null
  $after = Get-AuthenticodeSignatureDetail -LiteralPath $exe
  Write-Host ('afterHasSignature=' + $after.HasSignature)
  Write-Host ('afterAlgorithm=' + $after.DigestAlgorithm)
  Write-Host ('afterIntact=' + $after.SignatureIntact)
  Write-Host ('afterTimestamped=' + $after.Timestamped)
  Write-Host ('afterTimestampKind=' + $after.TimestampKind)
  Write-Host ('afterThumbprint=' + $after.SignerThumbprint)
  Write-Host ('certThumbprint=' + $cert.Thumbprint)
} finally {" + Fixtures.RemoveCert + "}");
        Assert.True(res.Succeeded, res.All);
        Assert.Contains("beforeHasSignature=False", res.StdOut);
        Assert.Contains("beforeAlgorithm=None", res.StdOut);
        Assert.Contains("afterHasSignature=True", res.StdOut);
        Assert.Contains("afterAlgorithm=SHA256", res.StdOut);
        Assert.Contains("afterIntact=True", res.StdOut);
        Assert.Contains("afterTimestamped=False", res.StdOut);
        Assert.Contains("afterTimestampKind=None", res.StdOut);

        // Identity comes from the embedded signature, so it must match the signing certificate.
        var embedded = Extract(res.StdOut, "afterThumbprint=");
        var expected = Extract(res.StdOut, "certThumbprint=");
        Assert.Equal(expected, embedded);
    }

    [Fact]
    public void Sha1Signature_IsRejectedWhenSha256IsRequired()
    {
        var work = _ps.NewTempDir();
        var res = _ps.Run(Fixtures.BuildUnsignedPackage(work, "1.0.0") + Fixtures.NewCodeSigningCert() + @"
try {
  $exe = Join-Path $pkg 'app\ThaiIdCardAgent.Service.exe'
  Set-AuthenticodeSignature -FilePath $exe -Certificate $cert -HashAlgorithm SHA1 | Out-Null
  $detail = Get-AuthenticodeSignatureDetail -LiteralPath $exe
  Write-Host ('algorithm=' + $detail.DigestAlgorithm)
  $r = Test-ReleaseSignatureFile -LiteralPath $exe -RelativePath 'app/ThaiIdCardAgent.Service.exe' -RequiredDigestAlgorithm 'SHA256'
  Write-Host ('ok=' + $r.Ok)
  Write-Host ('reasons=' + ($r.Reasons -join '|'))
} finally {" + Fixtures.RemoveCert + "}");
        Assert.True(res.Succeeded, res.All);
        Assert.Contains("algorithm=SHA1", res.StdOut);
        Assert.Contains("ok=False", res.StdOut);
        Assert.Contains("expected 'SHA256'", res.StdOut);
    }

    private static string Extract(string output, string key)
    {
        var index = output.IndexOf(key, StringComparison.Ordinal);
        Assert.True(index >= 0, $"'{key}' not found in output:\n{output}");
        var start = index + key.Length;
        var end = output.IndexOfAny(new[] { '\r', '\n' }, start);
        return (end < 0 ? output[start..] : output[start..end]).Trim();
    }
}
