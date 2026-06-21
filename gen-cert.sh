#!/usr/bin/env bash
# Generate a local Root CA + a server certificate for the LAN HTTPS deployment
# (#222). Browsers reject a single self-signed cert that is used directly as the
# server leaf when it is marked CA:TRUE and/or lacks the serverAuth EKU — Firefox
# aborts the TLS handshake with a "bad certificate" alert. So this mirrors what
# mkcert does: a CA you trust once, and a short-lived leaf the servers present.
#
# Produces, under ./certs/ (gitignored):
#   ca.crt.pem / ca.key.pem      — the Root CA. TRUST ca.crt.pem on each device.
#   sights.crt.pem / sights.key.pem — the leaf (CA:FALSE, EKU=serverAuth, IP SAN),
#                                     for Keycloak (docker-compose.tls.yml)
#   sights.pfx                   — PKCS#12 (leaf key + leaf cert + CA), for Kestrel
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

echo "[gen-cert] Root CA + server cert for IP:$HOST_IP (valid ${DAYS}d) -> $CERT_DIR"

# 1. Root CA — the trust anchor. This is the ONLY file you import into trust
#    stores. It signs the leaf below; it is never presented by a server.
openssl req -x509 -newkey rsa:2048 -sha256 -days "$DAYS" -nodes \
  -keyout "$CERT_DIR/ca.key.pem" -out "$CERT_DIR/ca.crt.pem" \
  -subj "/CN=Sights and Sounds local CA ($HOST_IP)" \
  -addext "basicConstraints=critical,CA:TRUE,pathlen:0" \
  -addext "keyUsage=critical,keyCertSign,cRLSign"

# 2. Server (leaf) private key + CSR.
openssl req -newkey rsa:2048 -sha256 -nodes \
  -keyout "$CERT_DIR/sights.key.pem" -out "$CERT_DIR/sights.csr.pem" \
  -subj "/CN=$HOST_IP"

# 3. Sign the leaf with the CA. CA:FALSE + extendedKeyUsage=serverAuth + an IP
#    SAN are what make browsers accept it as a server cert (the old single
#    self-signed cert had CA:TRUE and no EKU, which is why the handshake failed).
openssl x509 -req -in "$CERT_DIR/sights.csr.pem" \
  -CA "$CERT_DIR/ca.crt.pem" -CAkey "$CERT_DIR/ca.key.pem" -CAcreateserial \
  -days "$DAYS" -sha256 \
  -extfile <(printf '%s\n' \
    "basicConstraints=critical,CA:FALSE" \
    "keyUsage=critical,digitalSignature,keyEncipherment" \
    "extendedKeyUsage=serverAuth" \
    "subjectAltName=IP:$HOST_IP,IP:127.0.0.1,DNS:localhost") \
  -out "$CERT_DIR/sights.crt.pem"

# 4. PFX for Kestrel (leaf key + leaf cert + CA chain).
openssl pkcs12 -export \
  -inkey "$CERT_DIR/sights.key.pem" -in "$CERT_DIR/sights.crt.pem" \
  -certfile "$CERT_DIR/ca.crt.pem" \
  -out "$CERT_DIR/sights.pfx" -passout pass:"$PFX_PASS"

rm -f "$CERT_DIR/sights.csr.pem" "$CERT_DIR/ca.srl"
chmod 600 "$CERT_DIR"/ca.key.pem "$CERT_DIR"/sights.key.pem "$CERT_DIR"/sights.pfx

# Sanity: the leaf must verify against the CA, or the handshake will fail.
openssl verify -CAfile "$CERT_DIR/ca.crt.pem" "$CERT_DIR/sights.crt.pem" >/dev/null \
  && echo "[gen-cert] leaf verifies against the CA — OK"

cat <<EOF

[gen-cert] done. Files in certs/ (gitignored):
  ca.crt.pem      ROOT CA — TRUST THIS on every device (Authorities / OS store)
  ca.key.pem      CA private key — keep secret, never distribute
  sights.crt.pem  server leaf  — used by Keycloak (KC_HTTPS_CERTIFICATE_FILE)
  sights.key.pem  leaf key
  sights.pfx      Kestrel bundle (password: the one you passed / "changeit")

Next:
  1. In .env (see .env.example):
       ASPNETCORE_URLS=https://0.0.0.0:5098
       Kestrel__Certificates__Default__Path=certs/sights.pfx
       Kestrel__Certificates__Default__Password=$PFX_PASS
       Network__ForceHttps=true
  2. Trust certs/ca.crt.pem (NOT sights.crt.pem) on each device. If you trusted
     the old sights.crt.pem, REMOVE it from the stores first to avoid confusion.
       - Fedora/host : sudo cp certs/ca.crt.pem /etc/pki/ca-trust/source/anchors/ && sudo update-ca-trust
       - Firefox     : Settings > Privacy & Security > Certificates > View Certificates
                       > Authorities > Import > certs/ca.crt.pem > "Trust to identify websites"
       - Windows     : import ca.crt.pem into "Trusted Root Certification Authorities"
       - Android/iOS : install ca.crt.pem as a CA certificate in Settings
  3. Restart so the servers pick up the new leaf:
       ./stop.sh && ./serve.sh
EOF
