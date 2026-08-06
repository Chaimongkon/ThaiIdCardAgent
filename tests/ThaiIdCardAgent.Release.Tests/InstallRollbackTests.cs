namespace ThaiIdCardAgent.Release.Tests;

public sealed class InstallRollbackTests : IDisposable
{
    private readonly PowerShellHarness _ps = new();

    public void Dispose() => _ps.Dispose();

    [Fact]
    public void Rollback_OnCopyFailure_RestoresPreviousInstall()
    {
        var work = _ps.NewTempDir();
        var res = _ps.Run($@"
$work = {Fixtures.Lit(work)}
$src = Join-Path $work 'src'; $dst = Join-Path $work 'dst'; $bk = Join-Path $work 'backup'
New-Item -ItemType Directory -Force -Path $src, $dst | Out-Null
Set-Content -Path (Join-Path $src 'new.txt') -Value 'NEW' -NoNewline
Set-Content -Path (Join-Path $dst 'old.txt') -Value 'OLD' -NoNewline
try {{
  Copy-ReleasePayloadWithRollback -SourceDir $src -DestinationDir $dst -BackupRoot $bk -SimulateFailure | Out-Null
  Write-Host 'NO-THROW'
}} catch {{
  Write-Host 'THREW'
}}
Write-Host ('files=' + ((Get-ChildItem $dst -File).Name -join ','))
Write-Host ('content=' + (Get-Content (Join-Path $dst 'old.txt')))
");
        Assert.True(res.Succeeded, res.All);
        Assert.Contains("THREW", res.StdOut);
        Assert.Contains("files=old.txt", res.StdOut);
        Assert.Contains("content=OLD", res.StdOut);
    }

    [Fact]
    public void Rollback_OnSuccess_ReplacesPayloadAndCleansBackup()
    {
        var work = _ps.NewTempDir();
        var res = _ps.Run($@"
$work = {Fixtures.Lit(work)}
$src = Join-Path $work 'src'; $dst = Join-Path $work 'dst'; $bk = Join-Path $work 'backup'
New-Item -ItemType Directory -Force -Path $src, $dst | Out-Null
Set-Content -Path (Join-Path $src 'new.txt') -Value 'NEW' -NoNewline
Set-Content -Path (Join-Path $dst 'old.txt') -Value 'OLD' -NoNewline
Copy-ReleasePayloadWithRollback -SourceDir $src -DestinationDir $dst -BackupRoot $bk | Out-Null
Write-Host ('files=' + ((Get-ChildItem $dst -File).Name -join ','))
Write-Host ('backups=' + (@(Get-ChildItem $bk -Directory -ErrorAction SilentlyContinue).Count))
");
        Assert.True(res.Succeeded, res.All);
        Assert.Contains("files=new.txt", res.StdOut);
        Assert.Contains("backups=0", res.StdOut);
    }

    [Fact]
    public void SignRelease_WhatIf_HasNoSideEffects()
    {
        var work = _ps.NewTempDir();
        var res = _ps.Run(Fixtures.BuildUnsignedPackage(work, "1.0.0") + Fixtures.NewCodeSigningCert() + @"
try {
  & (Join-Path $ScriptsDir 'Sign-Release.ps1') -PackagePath $pkg -CertificateThumbprint $cert.Thumbprint -StoreLocation CurrentUser -WhatIf
  $m = Get-Content -LiteralPath (Join-Path $pkg 'release-manifest.json') -Raw | ConvertFrom-Json
  Write-Host ('signingStatus=' + $m.signingStatus)
} finally {" + Fixtures.RemoveCert + "}");
        Assert.True(res.Succeeded, res.All);
        // WhatIf must not flip the package to Signed.
        Assert.Contains("signingStatus=UnsignedPilot", res.StdOut);
    }
}
