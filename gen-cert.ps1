# Generate a self-signed TLS certificate for the LAN deployment (#222).
# PowerShell twin of gen-cert.sh. Requires OpenSSL on PATH (ships with Git for
# Windows, or `winget install ShiningLight.OpenSSL`).
#
# Produces, under .\certs\ (gitignored):
#   sights.crt.pem / sights.key.pem  — PEM pair, for Keycloak (docker-compose.tls.yml)
#   sights.pfx                       — PKCS#12 bundle, for Kestrel (the API)
#
# Usage:
#   .\gen-cert.ps1 [-HostIp 192.168.4.38] [-PfxPassword changeit]
param(
    [string]$HostIp = '192.168.4.38',
    [string]$PfxPassword = 'changeit'
)
$ErrorActionPreference = 'Stop'
$ScriptDir = $PSScriptRoot
$CertDir = Join-Path $ScriptDir 'certs'
$Days = 3650

New-Item -ItemType Directory -Force -Path $CertDir | Out-Null

Write-Host "[gen-cert] generating self-signed cert for IP:$HostIp (valid ${Days}d) -> $CertDir"

& openssl req -x509 -newkey rsa:2048 -sha256 -days $Days -nodes `
    -keyout (Join-Path $CertDir 'sights.key.pem') `
    -out    (Join-Path $CertDir 'sights.crt.pem') `
    -subj "/CN=$HostIp" `
    -addext "subjectAltName=IP:$HostIp,IP:127.0.0.1,DNS:localhost"

& openssl pkcs12 -export `
    -inkey (Join-Path $CertDir 'sights.key.pem') `
    -in    (Join-Path $CertDir 'sights.crt.pem') `
    -out   (Join-Path $CertDir 'sights.pfx') `
    -passout "pass:$PfxPassword"

Write-Host ""
Write-Host "[gen-cert] done. Files written to certs\ (gitignored)."
Write-Host "  In .env set ASPNETCORE_URLS=https://0.0.0.0:5098, Kestrel__Certificates__Default__Path=certs/sights.pfx,"
Write-Host "  Kestrel__Certificates__Default__Password=$PfxPassword, Network__ForceHttps=true."
Write-Host "  Then trust certs\sights.crt.pem on each client device (Trusted Root store / browser Authorities)."
