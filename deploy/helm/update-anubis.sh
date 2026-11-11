#!/usr/bin/env bash
# Refreshes the copy of Anubis's Helm chart that the Osiris chart depends on
# (deploy/helm/osiris/charts/anubis) from the Anubis repository.
#   deploy/helm/update-anubis.sh [git ref, default main]
set -euo pipefail

ref="${1:-main}"
here="$(cd "$(dirname "$0")" && pwd)"
tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT

git clone --quiet --depth 1 --branch "$ref" https://github.com/k6labs-cz/Anubis "$tmp/anubis"
rm -rf "$here/osiris/charts/anubis"
cp -r "$tmp/anubis/deploy/helm/anubis" "$here/osiris/charts/anubis"

version="$(sed -n 's/^version: *//p' "$here/osiris/charts/anubis/Chart.yaml")"
echo "Anubis chart $version copied. Set the same version under dependencies in osiris/Chart.yaml."
