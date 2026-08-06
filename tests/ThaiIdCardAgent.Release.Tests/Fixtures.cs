namespace ThaiIdCardAgent.Release.Tests;

/// <summary>
/// Reusable PowerShell fixture snippets. Paths are embedded as single-quoted literals; the
/// harness pre-defines $ScriptsDir, $ModulePath and $UnsignedPe.
/// </summary>
internal static class Fixtures
{
    public static string Lit(string value) => "'" + value.Replace("'", "''") + "'";

    /// <summary>
    /// Builds an UnsignedPilot package from unsigned PE files under the given work dir and
    /// leaves $pkg / $pub set for the caller. Uses two unsigned assemblies plus a settings file.
    /// </summary>
    public static string BuildUnsignedPackage(string work, string version)
    {
        var w = Lit(work);
        return $@"
$work = {w}
$pub = Join-Path $work 'publish'
New-Item -ItemType Directory -Force -Path $pub | Out-Null
Copy-Item $UnsignedPe (Join-Path $pub 'ThaiIdCardAgent.Service.exe')
Copy-Item $UnsignedPe (Join-Path $pub 'ThaiIdCardAgent.Core.dll')
Set-Content -Path (Join-Path $pub 'appsettings.json') -Value '{{}}' -NoNewline
& (Join-Path $ScriptsDir 'New-ReleasePackage.ps1') -Version '{version}' -PublishPath $pub -OutputRoot (Join-Path $work 'release') -SkipPublish *> $null
$pkg = Join-Path $work ('release\ThaiIdCardAgent-{version}-win-x64')
";
    }

    /// <summary>
    /// Creates an ephemeral self-signed code-signing certificate in Cert:\CurrentUser\My and
    /// stores it in $cert. It is intentionally NOT added to any trust store: the signing tool
    /// judges a signature by presence + integrity (tamper), not OS publisher trust, which keeps
    /// the tests fast and free of CryptoAPI revocation/trust flakiness. The caller MUST wrap
    /// usage in try/finally with <see cref="RemoveCert"/>.
    /// </summary>
    public static string NewCodeSigningCert(string subject = "CN=ThaiIdCardAgent Pilot Test Signer", int validDays = 1)
        => $@"
$cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject '{subject}' -CertStoreLocation Cert:\CurrentUser\My -NotAfter (Get-Date).AddDays({validDays})
";

    /// <summary>
    /// Builds a real UnsignedPilot release ZIP from an unsigned PE under the given work dir and
    /// leaves $zip (the ZIP path) and $pkgdir (the built package folder) set for the caller.
    /// </summary>
    public static string BuildReleaseZip(string work, string version)
    {
        var w = Lit(work);
        return $@"
$work = {w}
$pub = Join-Path $work 'publish'
New-Item -ItemType Directory -Force -Path $pub | Out-Null
Copy-Item $UnsignedPe (Join-Path $pub 'ThaiIdCardAgent.Service.exe')
Set-Content -Path (Join-Path $pub 'appsettings.json') -Value '{{}}' -NoNewline
& (Join-Path $ScriptsDir 'New-ReleasePackage.ps1') -Version '{version}' -PublishPath $pub -OutputRoot (Join-Path $work 'release') -SkipPublish *> $null
$zip = Join-Path $work 'release\ThaiIdCardAgent-{version}-win-x64.zip'
$pkgdir = Join-Path $work 'release\ThaiIdCardAgent-{version}-win-x64'
";
    }

    public const string RemoveCert = @"
$__s = [System.Security.Cryptography.X509Certificates.X509Store]::new('My','CurrentUser')
$__s.Open('ReadWrite')
$__found = $__s.Certificates | Where-Object { $_.Thumbprint -eq $cert.Thumbprint }
foreach ($__c in $__found) { $__s.Remove($__c) }
$__s.Close()
";
}
