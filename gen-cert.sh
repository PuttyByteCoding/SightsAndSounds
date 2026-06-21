#!/usr/bin/env bash
# Generate a self-signed TLS certificate for the LAN deployment (#222).
#
# Produces, under ./certs/ (gitignored):
#   sights.crt.pem / sights.key.pem  — PEM pair, for Keycloak (docker-compose.tls.yml)
#   sights.pfx                       — PKCS#12 bundle, for Kestrel (the API)
#
# The cert carries an IP SAN so browsers accept it when you reach the host by
# raw IP (no DNS name needed). It is still self-signed, so each client device
# must trust sights.crt.pem once (instructions printed at the end).
#
# Usage:
#   ./gen-cert.sh [HOST_IP] [PFX_PASSWORD]
#   ./gen-cert.sh 192.168.4.38
#
# HOST_IP defaults to 192.168.4.38. PFX_PASSWORD defaults to "changeit" — set a
# real one and put the SAME value in .env as Kestrel__Certificates__Default__Password.
set -euo pipefail
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

HOST_IP="${1:-192.168.4.38}"
PFX_PASS="${2:-changeit}"
CERT_DIR="$SCRIPT_DIR/certs"
DAYS=3650

mkdir -p "$CERT_DIR"

echo "[gen-cert] generating self-signed cert for IP:$HOST_IP (valid ${DAYS}d) -> $CERT_DIR"

# x509 v3 cert with subjectAltName covering the LAN IP + loopback. The SAN is
# what modern browsers validate (CN alone is ignored), so it MUST list the exact
# address clients type into the URL bar.
openssl req -x509 -newkey rsa:2048 -sha256 -days "$DAYS" -nodes \
  -keyout "$CERT_DIR/sights.key.pem" \
  -out "$CERT_DIR/sights.crt.pem" \
  -subj "/CN=$HOST_IP" \
  -addext "subjectAltName=IP:$HOST_IP,IP:127.0.0.1,DNS:localhost"

# Bundle into a PFX for Kestrel (Kestrel__Certificates__Default__Path + __Password).
openssl pkcs12 -export \
  -inkey "$CERT_DIR/sights.key.pem" \
  -in "$CERT_DIR/sights.crt.pem" \
  -out "$CERT_DIR/sights.pfx" \
  -passout pass:"$PFX_PASS"

chmod 600 "$CERT_DIR"/sights.key.pem "$CERT_DIR"/sights.pfx

cat <<EOF

[gen-cert] done. Files written to certs/ (gitignored):
  certs/sights.crt.pem   public cert  — trust this on each client device + Keycloak overlay
  certs/sights.key.pem   private key  — keep secret
  certs/sights.pfx       Kestrel bundle (password: the one you passed / "changeit")

Next:
  1. In .env set (see .env.example for the full block):
       ASPNETCORE_URLS=https://0.0.0.0:5098
       Kestrel__Certificates__Default__Path=certs/sights.pfx
       Kestrel__Certificates__Default__Password=$PFX_PASS
       Network__ForceHttps=true
  2. Trust certs/sights.crt.pem on each device that will browse the app:
       - Fedora/host : sudo cp certs/sights.crt.pem /etc/pki/ca-trust/source/anchors/ && sudo update-ca-trust
       - Firefox     : Settings > Privacy & Security > Certificates > View Certificates > Authorities > Import
       - Windows     : import into "Trusted Root Certification Authorities"
       - Android/iOS : install the .pem/.crt as a CA profile in Settings
EOF
