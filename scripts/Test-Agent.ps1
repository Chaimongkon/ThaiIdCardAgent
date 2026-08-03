Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

param([string]$BaseUrl = 'https://127.0.0.1:18443')
Invoke-RestMethod -Uri "$BaseUrl/api/v1/health" -Method Get
