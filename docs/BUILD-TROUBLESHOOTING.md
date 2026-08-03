# Build Troubleshooting

## Default Parallel Build Fails With No Errors

Observed inside the Codex managed sandbox on 2026-08-03 with .NET SDK `10.0.302` / MSBuild `18.6.11`:

```powershell
dotnet clean
dotnet restore
dotnet build -c Release
```

The commands returned exit code `1` or `Build FAILED` with `0 Warning(s)` and `0 Error(s)`.

Diagnostic logs were captured locally under ignored files:

```powershell
dotnet restore -v diag *> artifacts/logs/restore-diag.log
dotnet build -c Release -v diag *> artifacts/logs/build-diag.log
```

The logs show the failure in the solution restore graph target `_FilterRestoreGraphProjectInputItems` while the MSBuild task invokes `_IsProjectRestoreSupported` with `BuildInParallel=True`. No child project logged a compiler, NuGet, duplicate project, circular reference, access denied, or file-in-use error.

## Findings

Checked items:

- ProjectReference graph: no duplicate or circular references found.
- Solution configuration: project paths and configurations are valid.
- `Directory.Build.props`: no shared output or intermediate paths.
- `Directory.Packages.props`: centralized package versions only.
- Custom MSBuild targets: none found in the repository.
- `BaseOutputPath` / `BaseIntermediateOutputPath`: not configured.
- `artifacts` collision: publish/log output is ignored and outside project `bin`/`obj` paths.
- Node/npm integration: none in the .NET build graph.

The same standard commands passed outside the sandbox without `-m:1`:

```powershell
dotnet clean
dotnet restore
dotnet build -c Release
```

Result: this is an execution-environment limitation of the managed Codex sandbox, not a repository build graph defect confirmed on the host.

## Workaround

For Codex sandbox runs only, use single-node MSBuild when the no-error failure appears:

```powershell
dotnet clean -m:1 -v minimal
dotnet restore -m:1 -v minimal
dotnet build -c Release -m:1 -v minimal
```

Do not add `-m:1` as a repository default unless the same failure is reproduced outside the sandbox on the target build machine.
