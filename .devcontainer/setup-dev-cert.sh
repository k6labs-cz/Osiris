#!/usr/bin/env bash
# Sets up the ASP.NET Core HTTPS dev certificate so the same cert survives
# container rebuilds, and exports its public part for trusting on the host.
#
# Files in .devcontainer/.certs (gitignored):
#   anubis-dev.pfx  cert + private key, re-imported on every rebuild
#   anubis-dev.crt  public cert only - trust this one on the host
set -euo pipefail

CERT_DIR="$(cd "$(dirname "$0")" && pwd)/.certs"
PFX="$CERT_DIR/anubis-dev.pfx"
CRT="$CERT_DIR/anubis-dev.crt"
# Local dev-only cert; the password just satisfies the pfx format.
PFX_PASSWORD="anubis-dev"

mkdir -p "$CERT_DIR"

if [ -f "$PFX" ]; then
    echo "Importing existing dev certificate from $PFX"
    dotnet dev-certs https --clean --import "$PFX" -p "$PFX_PASSWORD"
else
    echo "Creating new dev certificate"
    dotnet dev-certs https >/dev/null
    dotnet dev-certs https -ep "$PFX" -p "$PFX_PASSWORD"
fi

# Trust inside the container (OpenSSL trust dir, see SSL_CERT_DIR in devcontainer.json).
# On Linux this always reports a partial error, so don't fail on it.
dotnet dev-certs https --trust >/dev/null 2>&1 || true

# Public cert for the host.
cp "$(ls -t "$HOME"/.aspnet/dev-certs/trust/aspnetcore-localhost-*.pem | head -n1)" "$CRT"
echo "Host certificate: $CRT"
