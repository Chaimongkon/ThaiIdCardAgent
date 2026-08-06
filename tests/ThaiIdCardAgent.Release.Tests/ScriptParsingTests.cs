namespace ThaiIdCardAgent.Release.Tests;

public sealed class ScriptParsingTests : IDisposable
{
    private readonly PowerShellHarness _ps = new();

    public void Dispose() => _ps.Dispose();

    [Fact]
    public void AllScripts_ParseUnderWindowsPowerShell51()
    {
        var res = _ps.Run(@"
Write-Host ('PSVersion=' + $PSVersionTable.PSVersion.ToString())
$failed = 0
Get-ChildItem -LiteralPath $ScriptsDir -Include '*.ps1', '*.psm1' -Recurse | ForEach-Object {
  $tokens = $null; $errors = $null
  [System.Management.Automation.Language.Parser]::ParseFile($_.FullName, [ref]$tokens, [ref]$errors) | Out-Null
  if ($errors -and $errors.Count -gt 0) {
    $failed++
    Write-Host ('PARSE-FAIL ' + $_.Name + ' : ' + $errors[0].Message)
  }
}
Write-Host ('FAILED=' + $failed)
");
        Assert.True(res.Succeeded, res.All);
        Assert.Contains("PSVersion=5.", res.StdOut);
        Assert.Contains("FAILED=0", res.StdOut);
        Assert.DoesNotContain("PARSE-FAIL", res.StdOut);
    }
}
