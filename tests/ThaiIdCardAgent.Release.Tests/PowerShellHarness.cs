using System.Diagnostics;
using System.Text;

namespace ThaiIdCardAgent.Release.Tests;

/// <summary>
/// Result of running a Windows PowerShell 5.1 script.
/// </summary>
public sealed record PsResult(int ExitCode, string StdOut, string StdErr)
{
    public string All => StdOut + "\n" + StdErr;
    public bool Succeeded => ExitCode == 0;
}

/// <summary>
/// Drives the real repository PowerShell scripts under Windows PowerShell 5.1 so the
/// production scripts (not a C# re-implementation) are what gets tested.
/// </summary>
public sealed class PowerShellHarness : IDisposable
{
    private readonly List<string> _tempDirs = new();

    public string RepoRoot { get; }
    public string ScriptsDir { get; }
    public string ModulePath { get; }

    /// <summary>
    /// An unsigned PE file that is always present: this test assembly itself. Signing tests
    /// copy it into a package so an unsigned baseline (Authenticode NotSigned) is guaranteed.
    /// </summary>
    public string UnsignedPe { get; }

    public PowerShellHarness()
    {
        RepoRoot = FindRepoRoot();
        ScriptsDir = Path.Combine(RepoRoot, "scripts");
        ModulePath = Path.Combine(ScriptsDir, "ReleasePackaging.psm1");
        UnsignedPe = typeof(PowerShellHarness).Assembly.Location;
    }

    public string ScriptPath(string name) => Path.Combine(ScriptsDir, name);

    public string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tia-rel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    /// <summary>
    /// Runs a PowerShell 5.1 script body. The body may reference $ScriptsDir and $ModulePath,
    /// which are pre-defined. Windows PowerShell (powershell.exe) is used deliberately so the
    /// 5.1 parser and behavior are what is exercised.
    /// </summary>
    public PsResult Run(string body)
    {
        var header = new StringBuilder();
        header.AppendLine("$ErrorActionPreference = 'Stop'");
        header.AppendLine("Set-StrictMode -Version Latest");
        header.AppendLine($"$ScriptsDir = '{ScriptsDir.Replace("'", "''")}'");
        header.AppendLine($"$ModulePath = '{ModulePath.Replace("'", "''")}'");
        header.AppendLine($"$UnsignedPe = '{UnsignedPe.Replace("'", "''")}'");
        header.AppendLine("Import-Module $ModulePath -Force -DisableNameChecking");
        var full = header.ToString() + body;

        var scriptFile = Path.Combine(NewTempDir(), "test.ps1");
        File.WriteAllText(scriptFile, full, new UTF8Encoding(false));

        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");
        psi.ArgumentList.Add("-File");
        psi.ArgumentList.Add(scriptFile);

        using var process = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data != null) { stdout.AppendLine(e.Data); } };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) { stderr.AppendLine(e.Data); } };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        if (!process.WaitForExit(120_000))
        {
            try { process.Kill(true); } catch { /* best effort */ }
            throw new TimeoutException("PowerShell script timed out.");
        }
        process.WaitForExit();
        return new PsResult(process.ExitCode, stdout.ToString(), stderr.ToString());
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "ThaiIdCardAgent.sln")))
        {
            dir = dir.Parent;
        }
        if (dir == null)
        {
            throw new InvalidOperationException("Could not locate repository root (ThaiIdCardAgent.sln).");
        }
        return dir.FullName;
    }

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
        {
            try
            {
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, recursive: true);
                }
            }
            catch
            {
                // Best-effort cleanup of temp fixtures.
            }
        }
    }
}
