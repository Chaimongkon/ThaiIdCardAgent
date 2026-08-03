Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

param([string]$Name = 'ThaiIdCardAgent')
Start-Service -Name $Name
Get-Service -Name $Name
