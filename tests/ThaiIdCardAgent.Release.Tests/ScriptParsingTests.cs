namespace ThaiIdCardAgent.Release.Tests;

public sealed class ScriptParsingTests : IDisposable
{
    private readonly PowerShellHarness _ps = new();

    public void Dispose() => _ps.Dispose();

    [Fact]
    public void AllScripts_ParseUnderWindowsPowerShell51()
    {
        // -Include is ignored when -LiteralPath is used, so the extension filter is applied
        // explicitly: the scripts folder also holds JSON data files, which are not PowerShell.
        // The counter is incremented from a foreach loop rather than a ForEach-Object block so it
        // actually accumulates in this scope.
        var res = _ps.Run(@"
Write-Host ('PSVersion=' + $PSVersionTable.PSVersion.ToString())
$failed = 0
$scripts = @(Get-ChildItem -LiteralPath $ScriptsDir -Recurse -File |
    Where-Object { $_.Extension -ieq '.ps1' -or $_.Extension -ieq '.psm1' -or $_.Extension -ieq '.psd1' })
Write-Host ('SCRIPTS=' + $scripts.Count)
foreach ($script in $scripts) {
  $tokens = $null; $errors = $null
  [System.Management.Automation.Language.Parser]::ParseFile($script.FullName, [ref]$tokens, [ref]$errors) | Out-Null
  if ($errors -and $errors.Count -gt 0) {
    $failed++
    Write-Host ('PARSE-FAIL ' + $script.Name + ' : ' + $errors[0].Message)
  }
}
Write-Host ('FAILED=' + $failed)
");
        Assert.True(res.Succeeded, res.All);
        Assert.Contains("PSVersion=5.", res.StdOut);
        Assert.Contains("FAILED=0", res.StdOut);
        Assert.DoesNotContain("PARSE-FAIL", res.StdOut);
        // Guard against the filter silently matching nothing and passing vacuously.
        Assert.DoesNotContain("SCRIPTS=0", res.StdOut);
    }
}
