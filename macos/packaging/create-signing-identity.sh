#!/bin/zsh
# Creates the self-signed "DocuClick Signing" code-signing identity (free,
# no Apple Developer account) in its own keychain under ~/.docuclick-signing.
#
# Why self-signed instead of ad-hoc: macOS ties the Accessibility, Input
# Monitoring and Screen Recording permissions to the app's code signature.
# An ad-hoc signature changes with every build, so users would have to
# grant all three again after each update. A certificate-based signature
# stays the same across builds (verified in the feasibility test).
#
# IMPORTANT: back up DocuClick-Signing.p12 and p12-password.txt. If the
# certificate is lost, every user has to grant the permissions again once.
set -euo pipefail

IDENTITY="DocuClick Signing"
DIR="$HOME/.docuclick-signing"
KEYCHAIN="$DIR/docuclick-signing.keychain-db"

if [[ -f "$KEYCHAIN" ]]; then
  echo "Existiert bereits: $KEYCHAIN — nichts zu tun."
  exit 0
fi

mkdir -p "$DIR" && chmod 700 "$DIR"
umask 077
PASSWORD="$(openssl rand -hex 24)"
print -r -- "$PASSWORD" > "$DIR/p12-password.txt"

cat > "$DIR/cert.cnf" <<CNF
[req]
distinguished_name = dn
x509_extensions = ext
prompt = no
[dn]
CN = $IDENTITY
[ext]
basicConstraints = critical,CA:false
keyUsage = critical,digitalSignature
extendedKeyUsage = critical,codeSigning
CNF

# LibreSSL (/usr/bin/openssl): its PKCS#12 encryption is what `security import` understands.
/usr/bin/openssl req -x509 -newkey rsa:3072 -nodes -days 7300 \
  -keyout "$DIR/key.pem" -out "$DIR/cert.pem" -config "$DIR/cert.cnf" 2>/dev/null
/usr/bin/openssl pkcs12 -export -inkey "$DIR/key.pem" -in "$DIR/cert.pem" \
  -out "$DIR/DocuClick-Signing.p12" -passout pass:"$PASSWORD" -name "$IDENTITY"
rm "$DIR/key.pem"

security create-keychain -p "$PASSWORD" "$KEYCHAIN"
security set-keychain-settings "$KEYCHAIN"
security import "$DIR/DocuClick-Signing.p12" -k "$KEYCHAIN" -P "$PASSWORD" -T /usr/bin/codesign >/dev/null
security set-key-partition-list -S apple-tool:,apple: -s -k "$PASSWORD" "$KEYCHAIN" >/dev/null

echo "Angelegt: $DIR"
echo "  DocuClick-Signing.p12 + p12-password.txt  → bitte sicher aufbewahren (Backup)."
