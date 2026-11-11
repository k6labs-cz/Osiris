# Releasing

A release is a git tag `vX.Y.Z` on `main`; the [Release workflow](../.github/workflows/release.yml) does the rest.

1. Move the "Unreleased" notes in `CHANGELOG.md` under the new version; set the same version in
   `Directory.Build.props` (`<Version>`) and in `deploy/helm/osiris/Chart.yaml` (`version`, `appVersion`),
   so local builds say what they are.
2. `git tag v1.2.0 && git push origin v1.2.0`

The workflow tests, then publishes version `1.2.0` of:

| What | Where |
| --- | --- |
| `k6labs.Osiris.Abstractions`, `.Core`, `.Tools.Database`, `.Cli` (NuGet) | `https://nuget.pkg.github.com/k6labs-cz` — and nuget.org when the repository has a `NUGET_API_KEY` secret |
| Terminal and desktop apps: Linux `tar.gz` + `.flatpak`, Windows `zip` + installer, macOS `tar.gz` | the GitHub release |
| The server image | `ghcr.io/k6labs-cz/osiris:1.2.0` (and `latest` for a stable version) |
| The Helm chart | `oci://ghcr.io/k6labs-cz/charts/osiris` and the GitHub release |

The GitHub release is a draft until every job has attached its files; a failed job leaves the draft, and
re-running the workflow completes it. A tag with a suffix (`v1.2.0-rc.1`) makes a prerelease.

The macOS builds are unsigned and untested (the job may fail without holding the release). The Windows
installer and the Flatpak are unsigned too; signing and a Flathub submission are open tasks.

Repository settings the workflow needs: Actions may create packages (the default `GITHUB_TOKEN` with
`packages: write`), and the packages `k6labs.Hathor*` must be readable by this repository's Actions
(public packages are).
