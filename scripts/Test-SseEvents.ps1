#requires -Version 5.1
[CmdletBinding()]
param(
    [string]$BaseUrl = 'https://localhost:18443',
    [string]$JwtPublicKeyPath,
    [string]$JwtPrivateKeyPath,
    [string]$AllowedOrigin = 'https://localhost:3000',
    [int]$TimeoutSeconds = 30,
    [int]$RepeatConnections = 3
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$JwtPublicKeyPath = if ([string]::IsNullOrWhiteSpace($JwtPublicKeyPath)) { Join-Path $root 'artifacts\test-secrets\thai-id-agent-test-signing.public.pem' } else { $JwtPublicKeyPath }
$JwtPrivateKeyPath = if ([string]::IsNullOrWhiteSpace($JwtPrivateKeyPath)) { Join-Path $root 'artifacts\test-secrets\thai-id-agent-test-signing.private.pem' } else { $JwtPrivateKeyPath }
$JwtPublicKeyPath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($JwtPublicKeyPath)
$JwtPrivateKeyPath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($JwtPrivateKeyPath)

$results = New-Object System.Collections.Generic.List[object]

function Add-Result {
    param([string]$Name, [string]$Status, [string]$Message = '')
    $script:results.Add([pscustomobject]@{ Step = $Name; Status = $Status; Message = $Message })
    Write-Host "[$Status] $Name $Message"
}

function Complete-Test {
    param([int]$ExitCode = 0)
    Write-Host ''
    Write-Host 'SSE acceptance summary'
    $script:results | Format-Table -AutoSize
    exit $ExitCode
}

function Test-KeyFile {
    param([string]$Name, [string]$Path)
    if ([string]::IsNullOrWhiteSpace($Path) -or $Path -match '<[^>]+>') {
        throw "$Name path is missing or contains placeholder text."
    }

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "$Name file was not found."
    }

    $item = Get-Item -LiteralPath $Path
    if ($item.Length -le 0) {
        throw "$Name file is empty."
    }
}

function New-TestJwtToken {
    param([string]$Purpose)

    $tokenPath = Join-Path $env:TEMP ("thai-id-agent-sse-{0}-{1}.jwt" -f $Purpose, [guid]::NewGuid().ToString('N'))
    try {
        $jwtArgs = @(
            '-NoProfile',
            '-ExecutionPolicy',
            'Bypass',
            '-File',
            (Join-Path $script:root 'scripts\New-TestJwt.ps1'),
            '-PrivateKeyPath',
            $script:JwtPrivateKeyPath,
            '-PublicKeyPath',
            $script:JwtPublicKeyPath,
            '-TokenOutputPath',
            $tokenPath,
            '-LifetimeSeconds',
            '60',
            '-Force'
        )

        $jwtOutput = & powershell.exe @jwtArgs 2>&1
        $jwtExitCode = $LASTEXITCODE
        if ($jwtExitCode -ne 0) {
            throw "JWT tool failed with exit code $jwtExitCode."
        }

        if (-not (Test-Path -LiteralPath $tokenPath -PathType Leaf)) {
            throw 'JWT tool did not create a token file.'
        }

        $token = (Get-Content -LiteralPath $tokenPath -Raw).Trim()
        if ([string]::IsNullOrWhiteSpace($token)) {
            throw 'JWT token was empty.'
        }

        return $token
    }
    finally {
        if (Test-Path -LiteralPath $tokenPath) {
            Remove-Item -LiteralPath $tokenPath -Force -ErrorAction SilentlyContinue
        }
    }
}

function Open-SseConnection {
    param([string]$Purpose)

    $token = New-TestJwtToken -Purpose $Purpose
    $request = [System.Net.HttpWebRequest]::Create(("{0}/api/v1/events" -f $script:BaseUrl.TrimEnd('/')))
    $request.Method = 'GET'
    $request.Accept = 'text/event-stream'
    $request.Headers['Authorization'] = "Bearer $token"
    $request.Headers['Origin'] = $script:AllowedOrigin
    $request.Timeout = 15000
    $request.ReadWriteTimeout = 15000

    $response = $request.GetResponse()
    $stream = $response.GetResponseStream()
    $reader = New-Object System.IO.StreamReader($stream)
    return [pscustomobject]@{ Response = $response; Reader = $reader }
}

function Close-SseConnection {
    param([object]$Connection)
    if ($null -eq $Connection) { return }
    if ($Connection.Reader) { $Connection.Reader.Dispose() }
    if ($Connection.Response) { $Connection.Response.Dispose() }
}

function Read-LineWithTimeout {
    param([System.IO.StreamReader]$Reader, [DateTime]$Deadline)

    $remaining = [int]([Math]::Max(1, ($Deadline - (Get-Date)).TotalMilliseconds))
    $task = $Reader.ReadLineAsync()
    if (-not $task.Wait($remaining)) {
        throw 'SSE read timed out.'
    }

    return $task.Result
}

function Assert-SafeSseEvent {
    param([object]$Event)

    if ([string]::IsNullOrWhiteSpace([string]$Event.readerName)) {
        throw 'SSE event readerName was missing.'
    }

    if ([string]::IsNullOrWhiteSpace([string]$Event.eventType)) {
        throw 'SSE event eventType was missing.'
    }

    $parsedTime = [DateTimeOffset]::MinValue
    if (-not [DateTimeOffset]::TryParse([string]$Event.occurredAtUtc, [ref]$parsedTime)) {
        throw 'SSE event occurredAtUtc was invalid.'
    }

    if ($null -ne $Event.atr -and -not [string]::IsNullOrWhiteSpace([string]$Event.atr)) {
        if ([string]$Event.atr -notmatch '^([0-9A-F]{2})(-[0-9A-F]{2})*$') {
            throw 'SSE event ATR was not uppercase hex bytes.'
        }
    }
}

function Wait-SseEvent {
    param(
        [object]$Connection,
        [string]$ExpectedEventType,
        [int]$TimeoutSeconds
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $eventName = $null
    $dataLines = New-Object System.Collections.Generic.List[string]
    $latest = '<none>'

    while ((Get-Date) -lt $deadline) {
        try {
            $line = Read-LineWithTimeout -Reader $Connection.Reader -Deadline $deadline
        }
        catch {
            Add-Result $ExpectedEventType 'Failed' "Timed out waiting for $ExpectedEventType. Latest event: $latest."
            Complete-Test 1
        }

        if ($null -eq $line) {
            Add-Result $ExpectedEventType 'Failed' 'SSE stream closed before expected event.'
            Complete-Test 1
        }

        if ($line.Length -eq 0) {
            if ($dataLines.Count -gt 0) {
                $json = $dataLines -join "`n"
                $event = $json | ConvertFrom-Json
                Assert-SafeSseEvent -Event $event
                $latest = [string]$event.eventType
                if ($eventName -and $eventName -ne $event.eventType) {
                    throw "SSE event line '$eventName' did not match data eventType '$($event.eventType)'."
                }

                if ($event.eventType -eq $ExpectedEventType) {
                    Add-Result $ExpectedEventType 'Passed' "readerName=$($event.readerName); occurredAtUtc=$($event.occurredAtUtc)"
                    return $event
                }
            }

            $eventName = $null
            $dataLines.Clear()
            continue
        }

        if ($line.StartsWith('event:')) {
            $eventName = $line.Substring(6).Trim()
            continue
        }

        if ($line.StartsWith('data:')) {
            [void]$dataLines.Add($line.Substring(5).TrimStart())
            continue
        }
    }

    Add-Result $ExpectedEventType 'Failed' "Timed out waiting for $ExpectedEventType. Latest event: $latest."
    Complete-Test 1
}

try {
    Test-KeyFile -Name 'JWT public key' -Path $JwtPublicKeyPath
    Test-KeyFile -Name 'JWT private key' -Path $JwtPrivateKeyPath
    if ([string]::Equals($JwtPublicKeyPath, $JwtPrivateKeyPath, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw 'JWT public and private key paths must be different.'
    }
    Add-Result 'JWT key preflight' 'Passed' 'Public and private JWT key files are present and non-empty.'

    $connection = $null
    try {
        $connection = Open-SseConnection -Purpose 'events'
        Add-Result 'SSE connect' 'Passed' 'Connected to /api/v1/events without certificate-validation bypass.'

        Read-Host 'Remove the card, then press Enter'
        [void](Wait-SseEvent -Connection $connection -ExpectedEventType 'CardRemoved' -TimeoutSeconds $TimeoutSeconds)

        Read-Host 'Insert the card, then press Enter'
        [void](Wait-SseEvent -Connection $connection -ExpectedEventType 'CardInserted' -TimeoutSeconds $TimeoutSeconds)
    }
    finally {
        Close-SseConnection -Connection $connection
    }
    Add-Result 'SSE disconnect' 'Passed' 'Client disconnected and closed the stream.'

    for ($i = 1; $i -le $RepeatConnections; $i++) {
        $repeatConnection = $null
        try {
            $repeatConnection = Open-SseConnection -Purpose "repeat-$i"
            Start-Sleep -Milliseconds 250
        }
        finally {
            Close-SseConnection -Connection $repeatConnection
        }
    }
    Add-Result 'Repeated connect/disconnect' 'Passed' "Opened and closed SSE $RepeatConnections additional times."

    Complete-Test 0
}
catch {
    Add-Result 'SSE acceptance' 'Failed' $_.Exception.Message
    Complete-Test 1
}