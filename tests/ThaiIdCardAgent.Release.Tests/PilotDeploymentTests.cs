namespace ThaiIdCardAgent.Release.Tests;

// These tests drive scripts/Test-PilotDeployment.ps1 and scripts/Get-AgentDiagnostics.ps1 in
// modes that never touch a real service or certificate (VerifyOnly / Tamper / Rollback /
// Full -WhatIf) plus the module integrity gate, all against temp fixtures.
//
// The acceptance script calls `exit`, so it is invoked as a CHILD powershell.exe process; that
// isolates its exit code (captured via an "exit=N" marker) from the test harness process.
public sealed class PilotDeploymentTests : IDisposable
{
    private readonly PowerShellHarness _ps = new();

    public void Dispose() => _ps.Dispose();

    // The acceptance script emits Write-Warning (UNSIGNED PILOT) to the child's stderr; under
    // the harness's ErrorActionPreference=Stop that native-command stderr would throw, so relax
    // to Continue around the child invocation.
    private const string PilotInvoke =
        "$ErrorActionPreference = 'Continue'\n$Pilot = Join-Path $ScriptsDir 'Test-PilotDeployment.ps1'\n";

    [Fact]
    public void VerifyOnly_ValidPackage_Passes()
    {
        var work = _ps.NewTempDir();
        var res = _ps.Run(Fixtures.BuildReleaseZip(work, "1.0.0") + PilotInvoke + @"
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $Pilot -ReleaseZipPath $zip -Mode VerifyOnly
Write-Host ('exit=' + $LASTEXITCODE)
");
        Assert.True(res.Succeeded, res.All);
        Assert.Contains("Failed=0", res.StdOut);
        Assert.Contains("exit=0", res.StdOut);
    }

    [Fact]
    public void MissingZip_IsRejected()
    {
        var work = _ps.NewTempDir();
        var res = _ps.Run($@"
$ErrorActionPreference = 'Continue'
$Pilot = Join-Path $ScriptsDir 'Test-PilotDeployment.ps1'
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $Pilot -ReleaseZipPath (Join-Path {Fixtures.Lit(work)} 'does-not-exist.zip') -Mode VerifyOnly
Write-Host ('exit=' + $LASTEXITCODE)
");
        Assert.True(res.Succeeded, res.All);
        Assert.Contains("exit=1", res.StdOut);
        Assert.Contains("Failed", res.StdOut);
    }

    [Fact]
    public void RequireSigned_RejectsUnsignedPilotPackage()
    {
        var work = _ps.NewTempDir();
        var res = _ps.Run(Fixtures.BuildReleaseZip(work, "1.0.0") + PilotInvoke + @"
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $Pilot -ReleaseZipPath $zip -Mode VerifyOnly -RequireSigned
Write-Host ('exit=' + $LASTEXITCODE)
");
        Assert.True(res.Succeeded, res.All);
        Assert.Contains("exit=1", res.StdOut);
        Assert.Contains("RequireSigned", res.All);
    }

    [Fact]
    public void Tamper_DetectsModifiedPackageAndLeavesZipUnmodified()
    {
        var work = _ps.NewTempDir();
        var res = _ps.Run(Fixtures.BuildReleaseZip(work, "1.0.0") + PilotInvoke + @"
$before = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $Pilot -ReleaseZipPath $zip -Mode Tamper
Write-Host ('exit=' + $LASTEXITCODE)
$after = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash
Write-Host ('zipUnchanged=' + ($before -eq $after))
");
        Assert.True(res.Succeeded, res.All);
        Assert.Contains("exit=0", res.StdOut);
        Assert.Contains("Modified package rejected", res.StdOut);
        Assert.Contains("Original ZIP unmodified", res.StdOut);
        Assert.Contains("zipUnchanged=True", res.StdOut);
    }

    [Fact]
    public void Rollback_RestoresPreviousFilesAndRetainsConfigLogs()
    {
        var work = _ps.NewTempDir();
        var res = _ps.Run(Fixtures.BuildReleaseZip(work, "1.0.0") + PilotInvoke + @"
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $Pilot -ReleaseZipPath $zip -Mode Rollback
Write-Host ('exit=' + $LASTEXITCODE)
");
        Assert.True(res.Succeeded, res.All);
        Assert.Contains("exit=0", res.StdOut);
        Assert.Contains("Previous binary restored", res.StdOut);
        Assert.Contains("Config/log retention", res.StdOut);
        Assert.Contains("Invalid manifest / checksum mismatch rejected", res.StdOut);
        // Service-start-failure rollback must be reported Not Tested here (never Passed).
        Assert.Contains("[Not Tested] Service start failure rollback", res.StdOut);
    }

    [Fact]
    public void FullWhatIf_HasNoSideEffectsAndHardwareIsNotTested()
    {
        var work = _ps.NewTempDir();
        var res = _ps.Run(Fixtures.BuildReleaseZip(work, "1.0.0") + PilotInvoke + @"
$before = @(Get-ChildItem $env:TEMP -Directory -Filter 'tia-pilot-*' -ErrorAction SilentlyContinue).Count
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $Pilot -ReleaseZipPath $zip -Mode Full -WhatIf -CertificateThumbprint 'DEADBEEF'
Write-Host ('exit=' + $LASTEXITCODE)
$after = @(Get-ChildItem $env:TEMP -Directory -Filter 'tia-pilot-*' -ErrorAction SilentlyContinue).Count
Write-Host ('tempResidue=' + ($after - $before))
");
        Assert.True(res.Succeeded, res.All);
        Assert.Contains("exit=0", res.StdOut);
        // Hardware/install steps must be reported Not Tested under WhatIf, never Passed.
        Assert.Contains("[Not Tested] Install service", res.StdOut);
        Assert.Contains("[Not Tested] SSE events", res.StdOut);
        Assert.DoesNotContain("[Passed] Install service", res.StdOut);
        Assert.Contains("tempResidue=0", res.StdOut);
    }

    [Fact]
    public void Integrity_MalformedManifest_IsRejected()
    {
        var work = _ps.NewTempDir();
        var res = _ps.Run(Fixtures.BuildReleaseZip(work, "1.0.0") + @"
Set-Content -Path (Join-Path $pkgdir 'release-manifest.json') -Value 'not-json'
$r = Test-ReleasePackageIntegrity -PackageRoot $pkgdir
Write-Host ('ok=' + $r.Ok + ' manifest=' + $r.ManifestPresent)
");
        Assert.True(res.Succeeded, res.All);
        Assert.Contains("ok=False", res.StdOut);
        Assert.Contains("manifest=False", res.StdOut);
    }

    [Fact]
    public void Integrity_ChecksumMismatch_IsRejected()
    {
        var work = _ps.NewTempDir();
        var res = _ps.Run(Fixtures.BuildReleaseZip(work, "1.0.0") + @"
Add-Content -LiteralPath (Join-Path $pkgdir 'app\appsettings.json') -Value 'x'
$r = Test-ReleasePackageIntegrity -PackageRoot $pkgdir
Write-Host ('ok=' + $r.Ok + ' checksum=' + $r.ChecksumOk)
");
        Assert.True(res.Succeeded, res.All);
        Assert.Contains("ok=False", res.StdOut);
        Assert.Contains("checksum=False", res.StdOut);
    }

    [Fact]
    public void Integrity_SecretInPayload_IsRejected()
    {
        var work = _ps.NewTempDir();
        var res = _ps.Run(Fixtures.BuildReleaseZip(work, "1.0.0") + @"
Set-Content -Path (Join-Path $pkgdir 'app\signing.pfx') -Value 'secret' -NoNewline
New-ReleaseChecksumManifest -PackageRoot $pkgdir | Out-Null
$r = Test-ReleasePackageIntegrity -PackageRoot $pkgdir
Write-Host ('ok=' + $r.Ok + ' violations=' + ($r.SecretViolations -join ','))
");
        Assert.True(res.Succeeded, res.All);
        Assert.Contains("ok=False", res.StdOut);
        Assert.Contains("signing.pfx", res.StdOut);
    }

    [Fact]
    public void Integrity_UnexpectedTopLevelEntry_IsRejected()
    {
        var work = _ps.NewTempDir();
        var res = _ps.Run(Fixtures.BuildReleaseZip(work, "1.0.0") + @"
Set-Content -Path (Join-Path $pkgdir 'rogue.txt') -Value 'x' -NoNewline
$r = Test-ReleasePackageIntegrity -PackageRoot $pkgdir
Write-Host ('ok=' + $r.Ok + ' topLevelOk=' + $r.TopLevelOk + ' unexpected=' + ($r.UnexpectedTop -join ','))
");
        Assert.True(res.Succeeded, res.All);
        Assert.Contains("ok=False", res.StdOut);
        Assert.Contains("topLevelOk=False", res.StdOut);
        Assert.Contains("rogue.txt", res.StdOut);
    }

    [Fact]
    public void Integrity_FileCountMismatch_IsRejected()
    {
        var work = _ps.NewTempDir();
        var res = _ps.Run(Fixtures.BuildReleaseZip(work, "1.0.0") + @"
$mp = Join-Path $pkgdir 'release-manifest.json'
$m = Get-Content -LiteralPath $mp -Raw | ConvertFrom-Json
$m.fileCount = [int]$m.fileCount + 5
$json = $m | ConvertTo-Json -Depth 6
[System.IO.File]::WriteAllText($mp, $json, ([System.Text.UTF8Encoding]::new($false)))
$r = Test-ReleasePackageIntegrity -PackageRoot $pkgdir
Write-Host ('ok=' + $r.Ok + ' countOk=' + $r.CountOk)
");
        Assert.True(res.Succeeded, res.All);
        Assert.Contains("ok=False", res.StdOut);
        Assert.Contains("countOk=False", res.StdOut);
    }

    [Fact]
    public void ZipEntryValidation_RejectsTraversalAbsoluteAndDuplicate()
    {
        var work = _ps.NewTempDir();
        var res = _ps.Run($@"
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$work = {Fixtures.Lit(work)}
function New-Zip($name, [scriptblock]$add) {{
  $p = Join-Path $work $name
  $fs = [System.IO.File]::Open($p, 'CreateNew'); $za = [System.IO.Compression.ZipArchive]::new($fs, 'Create')
  & $add $za
  $za.Dispose(); $fs.Dispose(); return $p
}}
function Add-Entry($za, $entryName) {{ $e = $za.CreateEntry($entryName); $w = $e.Open(); $w.WriteByte(65); $w.Dispose() }}
$trav = New-Zip 'trav.zip' {{ param($za) Add-Entry $za '..\evil.txt' }}
$abs  = New-Zip 'abs.zip'  {{ param($za) Add-Entry $za 'C:\evil.txt' }}
$dup  = New-Zip 'dup.zip'  {{ param($za) Add-Entry $za 'app/x.txt'; Add-Entry $za 'app/x.txt' }}
foreach ($z in @(@('traversal',$trav),@('absolute',$abs),@('duplicate',$dup))) {{
  try {{ Test-ReleaseZipEntry -ZipPath $z[1] | Out-Null; Write-Host ($z[0] + '=NOT-REJECTED') }}
  catch {{ Write-Host ($z[0] + '=rejected') }}
}}
");
        Assert.True(res.Succeeded, res.All);
        Assert.Contains("traversal=rejected", res.StdOut);
        Assert.Contains("absolute=rejected", res.StdOut);
        Assert.Contains("duplicate=rejected", res.StdOut);
    }

    [Fact]
    public void Diagnostics_JsonOutput_ContainsNoSecretValues()
    {
        // Read-only; runs against whatever state the machine is in and must never emit secrets.
        var res = _ps.Run(@"
$json = & (Join-Path $ScriptsDir 'Get-AgentDiagnostics.ps1') -AsJson
$text = ($json -join ""`n"")
$parsed = $json | ConvertFrom-Json
Write-Host ('parses=' + [bool]$parsed)
$leaks = [regex]::Matches($text, '-----BEGIN|eyJ[A-Za-z0-9_-]{10,}\.|Bearer\s+[A-Za-z0-9]')
Write-Host ('secretValueHits=' + $leaks.Count)
");
        Assert.True(res.Succeeded, res.All);
        Assert.Contains("parses=True", res.StdOut);
        Assert.Contains("secretValueHits=0", res.StdOut);
    }

    [Fact]
    public void RetentionMarker_Lifecycle_CreatesValidatesAndCleansUpSafely()
    {
        var work = _ps.NewTempDir();
        var res = _ps.Run($@"
$dir = Join-Path {Fixtures.Lit(work)} 'config'
New-Item -ItemType Directory -Force -Path $dir | Out-Null
$marker = New-RetentionMarker -TargetDirectory $dir -Prefix 'test-marker'
Write-Host ('fileCreated=' + (Test-Path -LiteralPath $marker.Path))
Write-Host ('guidInName=' + ($marker.FileName -match '^[a-z0-9-]+-[0-9a-f]{{32}}\.marker$'))

$verifyOk = Test-RetentionMarker -MarkerPath $marker.Path -ExpectedHash $marker.Hash -ExpectedParentDirectory $dir
Write-Host ('exists=' + $verifyOk.Exists + ' hashMatch=' + $verifyOk.HashMatch)

# Modify content -> hashMatch should become False
Set-Content -Path $marker.Path -Value 'tampered' -NoNewline
$verifyBad = Test-RetentionMarker -MarkerPath $marker.Path -ExpectedHash $marker.Hash -ExpectedParentDirectory $dir
Write-Host ('tamperedHashMatch=' + $verifyBad.HashMatch)

# Safe cleanup
Remove-RetentionMarker -MarkerPath $marker.Path -ExpectedParentDirectory $dir
Write-Host ('fileDeleted=' + (-not (Test-Path -LiteralPath $marker.Path)))
");
        Assert.True(res.Succeeded, res.All);
        Assert.Contains("fileCreated=True", res.StdOut);
        Assert.Contains("guidInName=True", res.StdOut);
        Assert.Contains("exists=True hashMatch=True", res.StdOut);
        Assert.Contains("tamperedHashMatch=False", res.StdOut);
        Assert.Contains("fileDeleted=True", res.StdOut);
    }

    [Fact]
    public void RetentionMarker_PathEscapeAndCollision_AreRejected()
    {
        var work = _ps.NewTempDir();
        var res = _ps.Run($@"
$dir = Join-Path {Fixtures.Lit(work)} 'logs'
New-Item -ItemType Directory -Force -Path $dir | Out-Null

# Traversal / escape prefix
try {{
    New-RetentionMarker -TargetDirectory $dir -Prefix '..\escape' | Out-Null
    Write-Host 'escape=NOT_REJECTED'
}} catch {{
    Write-Host 'escape=rejected'
}}

# Collision rejection
$fixedFile = Join-Path $dir 'fixed.marker'
Set-Content -Path $fixedFile -Value 'existing'
try {{
    # Remove-RetentionMarker outside expected directory must fail
    $otherDir = Join-Path {Fixtures.Lit(work)} 'other'
    New-Item -ItemType Directory -Force -Path $otherDir | Out-Null
    Remove-RetentionMarker -MarkerPath $fixedFile -ExpectedParentDirectory $otherDir
    Write-Host 'outsideDelete=NOT_REJECTED'
}} catch {{
    Write-Host 'outsideDelete=rejected'
}}
");
        Assert.True(res.Succeeded, res.All);
        Assert.Contains("escape=rejected", res.StdOut);
        Assert.Contains("outsideDelete=rejected", res.StdOut);
    }

    [Fact]
    public void ToolingManifest_ValidAndCorrupted_DetectedAccurately()
    {
        var work = _ps.NewTempDir();
        var res = _ps.Run($@"
$bundle = Join-Path {Fixtures.Lit(work)} 'bundle'
New-Item -ItemType Directory -Force -Path (Join-Path $bundle 'sub') | Out-Null
Set-Content -Path (Join-Path $bundle 'script.ps1') -Value 'Write-Host 1' -NoNewline
Set-Content -Path (Join-Path $bundle 'sub\helper.ps1') -Value 'Write-Host 2' -NoNewline

$manifestPath = New-ToolingChecksumManifest -BundleRoot $bundle
$verify1 = Test-ToolingChecksumManifest -BundleRoot $bundle
Write-Host ('validManifest=' + $verify1.Ok)

# Tamper a file
Set-Content -Path (Join-Path $bundle 'script.ps1') -Value 'Write-Host 999' -NoNewline
$verifyTampered = Test-ToolingChecksumManifest -BundleRoot $bundle
Write-Host ('tamperedDetected=' + (-not $verifyTampered.Ok) + ' modCount=' + $verifyTampered.Modified.Count)

# Restore file and add untracked file
Set-Content -Path (Join-Path $bundle 'script.ps1') -Value 'Write-Host 1' -NoNewline
Set-Content -Path (Join-Path $bundle 'untracked.txt') -Value 'extra' -NoNewline
$verifyExtra = Test-ToolingChecksumManifest -BundleRoot $bundle
Write-Host ('extraDetected=' + (-not $verifyExtra.Ok) + ' extraCount=' + $verifyExtra.Extra.Count)
");
        Assert.True(res.Succeeded, res.All);
        Assert.Contains("validManifest=True", res.StdOut);
        Assert.Contains("tamperedDetected=True modCount=1", res.StdOut);
        Assert.Contains("extraDetected=True extraCount=1", res.StdOut);
    }

    [Fact]
    public void ToolingManifest_RejectsTraversalAndAbsolutePaths()
    {
        var work = _ps.NewTempDir();
        var res = _ps.Run($@"
$bundle = Join-Path {Fixtures.Lit(work)} 'bundle-test'
New-Item -ItemType Directory -Force -Path $bundle | Out-Null
Set-Content -Path (Join-Path $bundle 'valid.ps1') -Value 'valid' -NoNewline

$manifestPath = Join-Path $bundle 'TOOLING-SHA256.txt'
# Write traversal and absolute lines manually
$sha = '0000000000000000000000000000000000000000000000000000000000000000'
$lines = @(
    ""$sha  valid.ps1"",
    ""$sha  ../escape.txt"",
    ""$sha  C:\absolute.txt""
)
Set-Content -Path $manifestPath -Value ($lines -join ""`n"")

$verify = Test-ToolingChecksumManifest -BundleRoot $bundle
Write-Host ('rejectedTraversal=' + (-not $verify.Ok))
Write-Host ('messages=' + ($verify.Messages -join ';'))
");
        Assert.True(res.Succeeded, res.All);
        Assert.Contains("rejectedTraversal=True", res.StdOut);
        Assert.Contains("Unsafe or traversal path in tooling manifest", res.StdOut);
    }

    [Fact]
    public void NewPilotAcceptanceBundle_BuildsValidStandaloneStructure()
    {
        var work = _ps.NewTempDir();
        var res = _ps.Run($@"
$work = {Fixtures.Lit(work)}
$pkgDir = Join-Path $work 'dummy-packages'
New-Item -ItemType Directory -Force -Path $pkgDir | Out-Null
$zip010 = Join-Path $pkgDir '010.zip'
$zip011 = Join-Path $pkgDir '011.zip'
Set-Content -Path $zip010 -Value 'dummy-zip-010'
Set-Content -Path $zip011 -Value 'dummy-zip-011'

$bundleOut = Join-Path $work 'acceptance-bundle'
& (Join-Path $ScriptsDir 'New-PilotAcceptanceBundle.ps1') `
    -OutputPath $bundleOut `
    -Version010ZipPath $zip010 `
    -Version011ZipPath $zip011 `
    -SkipPublishTestJwt

Write-Host ('bundleExists=' + (Test-Path -LiteralPath $bundleOut))
Write-Host ('readmeExists=' + (Test-Path -LiteralPath (Join-Path $bundleOut 'README.md')))
Write-Host ('manifestExists=' + (Test-Path -LiteralPath (Join-Path $bundleOut 'TOOLING-SHA256.txt')))
Write-Host ('scriptExists=' + (Test-Path -LiteralPath (Join-Path $bundleOut 'Test-PilotDeployment.ps1')))
Write-Host ('pkg010Exists=' + (Test-Path -LiteralPath (Join-Path $bundleOut 'packages\ThaiIdCardAgent-0.1.0-pilot-win-x64.zip')))
Write-Host ('pkg011Exists=' + (Test-Path -LiteralPath (Join-Path $bundleOut 'packages\ThaiIdCardAgent-0.1.1-pilot-win-x64.zip')))

$verify = Test-ToolingChecksumManifest -BundleRoot $bundleOut
Write-Host ('bundleVerified=' + $verify.Ok)
");
        Assert.True(res.Succeeded, res.All);
        Assert.Contains("bundleExists=True", res.StdOut);
        Assert.Contains("readmeExists=True", res.StdOut);
        Assert.Contains("manifestExists=True", res.StdOut);
        Assert.Contains("scriptExists=True", res.StdOut);
        Assert.Contains("pkg010Exists=True", res.StdOut);
        Assert.Contains("pkg011Exists=True", res.StdOut);
        Assert.Contains("bundleVerified=True", res.StdOut);
    }
}

