#requires -Version 5.1
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

param([string]$Name = 'ThaiIdCardAgent')
Stop-Service -Name $Name
Get-Service -Name $Name
