namespace ThaiIdCardAgent.Release.Tests;

public sealed class ReleasePackagingTests : IDisposable
{
    private readonly PowerShellHarness _ps = new();

    public void Dispose() => _ps.Dispose();

    [Fact]
    public void Checksum_CorrectPackage_Verifies()
    {
        var work = _ps.NewTempDir();
        var res = _ps.Run(Fixtures.BuildUnsignedPackage(work, "1.0.0") + @"
$r = Test-ReleaseChecksum -PackageRoot $pkg
Write-Host ('OK=' + $r.Ok)
");
        Assert.True(res.Succeeded, res.All);
        Assert.Contains("OK=True", res.StdOut);
    }

    [Fact]
    public void Checksum_TamperedFile_IsRejected()
    {
        var work = _ps.NewTempDir();
        var res = _ps.Run(Fixtures.BuildUnsignedPackage(work, "1.0.0") + @"
$target = Join-Path $pkg 'app\appsettings.json'
Set-Content -Path $target -Value '{""tampered"":true}' -NoNewline
$r = Test-ReleaseChecksum -PackageRoot $pkg
Write-Host ('OK=' + $r.Ok)
Write-Host ('MOD=' + ($r.Modified -join ','))
");
        Assert.True(res.Succeeded, res.All);
        Assert.Contains("OK=False", res.StdOut);
        Assert.Contains("app/appsettings.json", res.StdOut);
    }

    [Fact]
    public void Checksum_MissingManifest_Throws()
    {
        var work = _ps.NewTempDir();
        var res = _ps.Run(Fixtures.BuildUnsignedPackage(work, "1.0.0") + @"
Remove-Item -LiteralPath (Join-Path $pkg 'checksums.sha256') -Force
Test-ReleaseChecksum -PackageRoot $pkg
");
        Assert.False(res.Succeeded, res.All);
        Assert.Contains("manifest", res.All, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Checksum_MalformedManifest_Throws()
    {
        var work = _ps.NewTempDir();
        var res = _ps.Run(Fixtures.BuildUnsignedPackage(work, "1.0.0") + @"
Set-Content -Path (Join-Path $pkg 'checksums.sha256') -Value 'this-is-not-a-valid-manifest-line'
Test-ReleaseChecksum -PackageRoot $pkg
");
        Assert.False(res.Succeeded, res.All);
        Assert.Contains("Malformed", res.All, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SecretExclusion_PfxAndPrivateKey_AreDetected()
    {
        var work = _ps.NewTempDir();
        var res = _ps.Run($@"
$dir = {Fixtures.Lit(work)}
New-Item -ItemType Directory -Force -Path $dir | Out-Null
Set-Content -Path (Join-Path $dir 'signing.pfx') -Value 'x' -NoNewline
Set-Content -Path (Join-Path $dir 'server.key') -Value 'x' -NoNewline
Set-Content -Path (Join-Path $dir 'ok.dll') -Value 'x' -NoNewline
$v = @(Test-ReleaseSecretExclusion -Path $dir)
Write-Host ('COUNT=' + $v.Count)
Write-Host ('LIST=' + ($v -join ','))
");
        Assert.True(res.Succeeded, res.All);
        Assert.Contains("COUNT=2", res.StdOut);
        Assert.Contains("signing.pfx", res.StdOut);
        Assert.Contains("server.key", res.StdOut);
    }

    [Fact]
    public void SecretExclusion_EnvLocal_IsDetected()
    {
        var work = _ps.NewTempDir();
        var res = _ps.Run($@"
$dir = {Fixtures.Lit(work)}
New-Item -ItemType Directory -Force -Path $dir | Out-Null
Set-Content -Path (Join-Path $dir '.env.local') -Value 'SECRET=1' -NoNewline
$v = @(Test-ReleaseSecretExclusion -Path $dir)
Write-Host ('LIST=' + ($v -join ','))
");
        Assert.True(res.Succeeded, res.All);
        Assert.Contains(".env.local", res.StdOut);
    }

    [Fact]
    public void NewReleasePackage_WithSecretInPublish_RefusesToPackage()
    {
        var work = _ps.NewTempDir();
        var res = _ps.Run($@"
$work = {Fixtures.Lit(work)}
$pub = Join-Path $work 'publish'
New-Item -ItemType Directory -Force -Path $pub | Out-Null
Copy-Item $UnsignedPe (Join-Path $pub 'ThaiIdCardAgent.Service.exe')
Set-Content -Path (Join-Path $pub 'leaked.pfx') -Value 'secret' -NoNewline
& (Join-Path $ScriptsDir 'New-ReleasePackage.ps1') -Version '1.0.0' -PublishPath $pub -OutputRoot (Join-Path $work 'release') -SkipPublish
");
        Assert.False(res.Succeeded, res.All);
        Assert.Contains("secret", res.All, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ChecksumManifest_FileOrdering_IsDeterministic()
    {
        var work = _ps.NewTempDir();
        var res = _ps.Run($@"
$work = {Fixtures.Lit(work)}
function Build($n) {{
  $root = Join-Path $work $n
  $app = Join-Path $root 'app'
  New-Item -ItemType Directory -Force -Path (Join-Path $app 'z') | Out-Null
  New-Item -ItemType Directory -Force -Path (Join-Path $app 'a') | Out-Null
  # Create files in different order for the two builds.
  Set-Content -Path (Join-Path $app 'z\zeta.txt') -Value 'z' -NoNewline
  Set-Content -Path (Join-Path $app 'a\alpha.txt') -Value 'a' -NoNewline
  Set-Content -Path (Join-Path $app 'middle.txt') -Value 'm' -NoNewline
  New-ReleaseChecksumManifest -PackageRoot $root | Out-Null
  return (Get-Content -LiteralPath (Join-Path $root 'checksums.sha256') -Raw)
}}
$one = Build 'p1'
$two = Build 'p2'
Write-Host ('IDENTICAL=' + ($one -ceq $two))
Write-Host '--MANIFEST--'
Write-Host $one
");
        Assert.True(res.Succeeded, res.All);
        Assert.Contains("IDENTICAL=True", res.StdOut);
        // Ordinal order: a/alpha.txt before middle.txt before z/zeta.txt
        var idxA = res.StdOut.IndexOf("app/a/alpha.txt", StringComparison.Ordinal);
        var idxM = res.StdOut.IndexOf("app/middle.txt", StringComparison.Ordinal);
        var idxZ = res.StdOut.IndexOf("app/z/zeta.txt", StringComparison.Ordinal);
        Assert.True(idxA >= 0 && idxA < idxM && idxM < idxZ, res.StdOut);
    }

    [Fact]
    public void NewReleasePackage_ExcludesPdbAndDevelopmentSettingsFromPayload()
    {
        var work = _ps.NewTempDir();
        var res = _ps.Run($@"
$work = {Fixtures.Lit(work)}
$pub = Join-Path $work 'publish'
New-Item -ItemType Directory -Force -Path $pub | Out-Null
Copy-Item $UnsignedPe (Join-Path $pub 'ThaiIdCardAgent.Service.exe')
Set-Content -Path (Join-Path $pub 'ThaiIdCardAgent.Service.pdb') -Value 'symbols' -NoNewline
Set-Content -Path (Join-Path $pub 'appsettings.json') -Value '{{}}' -NoNewline
Set-Content -Path (Join-Path $pub 'appsettings.Development.json') -Value '{{}}' -NoNewline
& (Join-Path $ScriptsDir 'New-ReleasePackage.ps1') -Version '1.0.0' -PublishPath $pub -OutputRoot (Join-Path $work 'release') -SkipPublish *> $null
$app = Join-Path $work 'release\ThaiIdCardAgent-1.0.0-win-x64\app'
$names = (Get-ChildItem -LiteralPath $app -File).Name
Write-Host ('files=' + ($names -join ','))
");
        Assert.True(res.Succeeded, res.All);
        Assert.Contains("ThaiIdCardAgent.Service.exe", res.StdOut);
        Assert.Contains("appsettings.json", res.StdOut);
        Assert.DoesNotContain(".pdb", res.StdOut);
        Assert.DoesNotContain("appsettings.Development.json", res.StdOut);
    }

    [Fact]
    public void NewReleasePackage_WhatIf_HasNoSideEffects()
    {
        var work = _ps.NewTempDir();
        var res = _ps.Run($@"
$work = {Fixtures.Lit(work)}
$pub = Join-Path $work 'publish'
New-Item -ItemType Directory -Force -Path $pub | Out-Null
Copy-Item $UnsignedPe (Join-Path $pub 'ThaiIdCardAgent.Service.exe')
$rel = Join-Path $work 'release'
& (Join-Path $ScriptsDir 'New-ReleasePackage.ps1') -Version '1.0.0' -PublishPath $pub -OutputRoot $rel -SkipPublish -WhatIf
Write-Host ('RELEASE_EXISTS=' + (Test-Path $rel))
");
        Assert.True(res.Succeeded, res.All);
        Assert.Contains("RELEASE_EXISTS=False", res.StdOut);
    }

    [Fact]
    public void ReleaseMetadata_UnsignedPilot_HasExpectedFieldsAndNoSigningBlock()
    {
        var work = _ps.NewTempDir();
        var res = _ps.Run(Fixtures.BuildUnsignedPackage(work, "2.3.4") + @"
$m = Get-Content -LiteralPath (Join-Path $pkg 'release-manifest.json') -Raw | ConvertFrom-Json
Write-Host ('product=' + $m.product)
Write-Host ('version=' + $m.version)
Write-Host ('signingStatus=' + $m.signingStatus)
Write-Host ('runtime=' + $m.targetRuntime)
Write-Host ('hasSigning=' + [bool]($m.PSObject.Properties.Name -contains 'signing'))
Write-Host ('hasCommit=' + (-not [string]::IsNullOrWhiteSpace($m.gitCommit)))
");
        Assert.True(res.Succeeded, res.All);
        Assert.Contains("product=ThaiIdCardAgent", res.StdOut);
        Assert.Contains("version=2.3.4", res.StdOut);
        Assert.Contains("signingStatus=UnsignedPilot", res.StdOut);
        Assert.Contains("runtime=win-x64", res.StdOut);
        Assert.Contains("hasSigning=False", res.StdOut);
        Assert.Contains("hasCommit=True", res.StdOut);
    }
}
