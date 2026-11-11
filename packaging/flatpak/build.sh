#!/usr/bin/env bash
# Builds the Flatpak bundle artifacts/Osiris-<version>-x86_64.flatpak (needs flatpak and flatpak-builder):
#   packaging/flatpak/build.sh [version]
# Install it with: flatpak install --user artifacts/Osiris-<version>-x86_64.flatpak
set -euo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
root="$(cd "$here/../.." && pwd)"
version="${1:-$(sed -n 's|.*<Version>\(.*\)</Version>.*|\1|p' "$root/Directory.Build.props" | head -1)}"

"$root/packaging/publish.sh" linux-x64 "$version"
rm -rf "$root/artifacts/flatpak"
cp -r "$root/artifacts/publish/linux-x64" "$root/artifacts/flatpak"

flatpak remote-add --user --if-not-exists flathub https://dl.flathub.org/repo/flathub.flatpakrepo
flatpak-builder --user --force-clean --install-deps-from=flathub \
  --state-dir="$root/artifacts/flatpak-state" --repo="$root/artifacts/flatpak-repo" \
  "$root/artifacts/flatpak-build" "$here/cz.k6labs.Osiris.yml"
flatpak build-bundle "$root/artifacts/flatpak-repo" "$root/artifacts/Osiris-$version-x86_64.flatpak" cz.k6labs.Osiris \
  --runtime-repo=https://dl.flathub.org/repo/flathub.flatpakrepo

echo "Built $root/artifacts/Osiris-$version-x86_64.flatpak"
