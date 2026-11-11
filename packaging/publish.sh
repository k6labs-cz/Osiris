#!/usr/bin/env bash
# Publishes the terminal and the desktop app, self-contained, for one platform:
#   packaging/publish.sh <rid> [version]      e.g. packaging/publish.sh linux-x64 1.2.0
# into artifacts/publish/<rid>/{cli,desktop} (no .NET needed where they run).
set -euo pipefail

rid="${1:?usage: packaging/publish.sh <rid> [version]}"
version="${2:-$(sed -n 's|.*<Version>\(.*\)</Version>.*|\1|p' "$(dirname "$0")/../Directory.Build.props" | head -1)}"
root="$(cd "$(dirname "$0")/.." && pwd)"
out="$root/artifacts/publish/$rid"
rm -rf "$out"

# The terminal app is one file (plus the bundled skills and native libraries beside it).
dotnet publish "$root/src/Osiris.Cli" -c Release -r "$rid" --self-contained \
  -p:PublishSingleFile=true -p:Version="$version" -o "$out/cli"
# The desktop app keeps its folder: the web UI's static files are served from it.
dotnet publish "$root/src/Osiris.Desktop" -c Release -r "$rid" --self-contained \
  -p:Version="$version" -o "$out/desktop"

echo "Published Osiris $version for $rid to $out"
