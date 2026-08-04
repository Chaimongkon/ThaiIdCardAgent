# TLS Diagnostics

Date: 2026-08-04
Repository: `D:\1.FrontEnd Framework\ThaiIdCardAgent`
Status: **Resolved by Administrator verification**

## Summary

Production HTTPS failed because the server certificate was installed in `Cert:\LocalMachine\My` for Kestrel, but the public certificate was trusted only in `Cert:\CurrentUser\Root`. It was missing from `Cert:\LocalMachine\Root`, so machine-level certificate chain verification failed with `CERT_E_UNTRUSTEDROOT` and Schannel clients failed before HTTP with `SEC_E_NO_CREDENTIALS`.

After exporting only the public certificate and importing that public certificate into `Cert:\LocalMachine\Root`, HTTPS health succeeded without disabling certificate validation.

No private key, PFX/P12, JWT, password, or secret is recorded in this report.

## Server Command

Production console-mode command used during diagnostics:

```powershell
$env:ASPNETCORE_ENVIRONMENT = 'Production'
$env:Agent__AllowedOrigins__0 = 'https://localhost:3000'
$env:Agent__Https__Certificate__Thumbprint = '79A7A07FDF6BC7EEBD3BC6F113659B79537A7101'
$env:Agent__Jwt__PublicKeyPath = '<ignored local public key path>'
.\artifacts\publish\win-x64\ThaiIdCardAgent.Service.exe
```

Windows Service was not installed as part of this diagnostic run.

## Server Process And Account

Observed during console-mode evidence capture:

- Process: `ThaiIdCardAgent.Service.exe`
- Account: `DOHCOOP\chaimongkon.on`
- Mode: console process, not Windows Service

## Bind Address

Observed bind:

```text
https://localhost:18443
TCP 127.0.0.1:18443 LISTENING
TCP [::1]:18443 LISTENING
```

The client target must be `localhost` because the certificate SAN contains `localhost`. Do not use `127.0.0.1` unless the certificate has IP SAN `127.0.0.1`.

## Certificate

Server certificate:

- Store: `Cert:\LocalMachine\My`
- Subject: `CN=localhost`
- SAN: `DNS Name=localhost`
- EKU: `Server Authentication (1.3.6.1.5.5.7.3.1)`
- HasPrivateKey: `True`
- Thumbprint: `79A7A07FDF6BC7EEBD3BC6F113659B79537A7101`

Diagnostics performs a private-key sign test using random data and does not print signature or private material.

## Client Certificate Requirement

Server mTLS is not enabled.

Source/config search found no:

- `ClientCertificateMode.RequireCertificate`
- `ClientCertificateMode.AllowCertificate`
- `CertificateAuthentication`
- certificate forwarding middleware
- mTLS policy

The service now configures Kestrel explicitly:

```csharp
httpsOptions.ClientCertificateMode = ClientCertificateMode.NoCertificate;
```

Diagnostics reports:

```text
[Pass] Client certificate required: false
```

## Evidence Before Fix

Trust store state before Administrator fix:

```text
Cert:\LocalMachine\My        FOUND CN=localhost
Cert:\CurrentUser\Root       FOUND CN=localhost
Cert:\LocalMachine\Root      not found
Cert:\CurrentUser\My         not found
```

Machine verification:

```powershell
certutil -verify -urlfetch <public-certificate.cer>
```

Result:

```text
HCCE_LOCAL_MACHINE
CERT_TRUST_IS_UNTRUSTED_ROOT (0x20)
CERT_E_UNTRUSTEDROOT
Verifies against UNTRUSTED root
```

Current user verification:

```powershell
certutil -user -verify -urlfetch <public-certificate.cer>
```

Result: trusted for CurrentUser.

Client errors before fix:

```powershell
curl.exe -v https://localhost:18443/api/v1/health
curl.exe --tlsv1.2 -v https://localhost:18443/api/v1/health
Invoke-WebRequest -Uri "https://localhost:18443/api/v1/health" -UseBasicParsing
Invoke-RestMethod -Uri "https://localhost:18443/api/v1/health"
```

`curl.exe` error:

```text
schannel: disabled automatic use of client certificate
schannel: AcquireCredentialsHandle failed: SEC_E_NO_CREDENTIALS (0x8009030e)
curl: (35) schannel: AcquireCredentialsHandle failed: SEC_E_NO_CREDENTIALS (0x8009030e)
```

PowerShell error:

```text
The underlying connection was closed: An unexpected error occurred on a receive.
```

Kestrel server log before fix:

```text
Connection accepted
Failed to authenticate HTTPS connection.
System.IO.IOException: Received an unexpected EOF or 0 bytes from the transport stream.
```

No HTTP request reached middleware/authentication before the fix.

## Fix Applied By Administrator

The Administrator verification performed these actions:

1. Exported only the public certificate:

```powershell
$thumb = '79A7A07FDF6BC7EEBD3BC6F113659B79537A7101'
$cert = Get-ChildItem Cert:\LocalMachine\My\$thumb
Export-Certificate -Cert $cert -FilePath .\artifacts\localhost-public.cer -Force
```

2. Imported only that public certificate into machine trusted roots:

```powershell
Import-Certificate -FilePath .\artifacts\localhost-public.cer -CertStoreLocation Cert:\LocalMachine\Root
```

or equivalently:

```powershell
certutil -addstore Root .\artifacts\localhost-public.cer
```

No private key was exported or committed.

## Evidence After Fix

Administrator verification result:

- `certutil -verify` passed in machine context.
- `curl.exe -v https://localhost:18443/api/v1/health` passed.
- `Invoke-WebRequest -Uri "https://localhost:18443/api/v1/health" -UseBasicParsing` passed.
- `curl` returned `HTTP/1.1 200 OK`.
- `Invoke-WebRequest` returned `StatusCode 200`.
- No `-k`, `--insecure`, `SkipCertificateCheck`, or custom certificate-validation callback was used.

## Root Cause

**Machine-level certificate trust mismatch.**

The HTTPS server certificate and private key were correctly installed for Kestrel in `Cert:\LocalMachine\My`, but the public certificate was trusted only in `Cert:\CurrentUser\Root`. Schannel clients in this production/service validation path required machine-level trust. Because `Cert:\LocalMachine\Root` did not contain the public certificate, machine chain validation failed with `CERT_E_UNTRUSTEDROOT`, and client TLS credential/chain setup failed with `SEC_E_NO_CREDENTIALS` before HTTP.

After the public certificate was imported into `Cert:\LocalMachine\Root`, HTTPS worked without certificate-validation bypass.

## Verification Result

HTTPS verification: **Passed** in Administrator verification.

Passing commands:

```powershell
curl.exe -v https://localhost:18443/api/v1/health
Invoke-WebRequest -Uri "https://localhost:18443/api/v1/health" -UseBasicParsing
```

Expected result:

```text
HTTP/1.1 200 OK
StatusCode 200
```

## Remaining Scope

The following are not proven by this TLS verification and must not be reported as passed yet:

- Windows Service installation.
- `NT AUTHORITY\LOCAL SERVICE` PC/SC access.
- Reader/card/ATR through the installed Windows Service.
- CardInserted/CardRemoved through the installed Windows Service.
- Service restart, upgrade, uninstall, and reinstall acceptance.