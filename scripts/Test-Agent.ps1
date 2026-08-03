param([string]$BaseUrl = 'https://localhost:18443')

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Invoke-RestMethod -Uri "$BaseUrl/api/v1/health" -Method Get