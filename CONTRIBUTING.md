# Contributing to Osiris

Thank you for helping. Issues and pull requests are welcome.

## Getting started

You need the .NET 10 SDK and a GitHub token with `read:packages` (for the UI library Hathor from k6labs'
GitHub Packages):

```bash
export NuGetPackageSourceCredentials_k6labs='Username=<github login>;Password=<token>'
dotnet build
dotnet test
```

The repository has a dev container (`.devcontainer/`) with everything installed.

## Ground rules

- **One agent for every host.** Behaviour belongs in `Osiris.Core`; the terminal, desktop and server apps
  only show it and decide who may use it. A feature that works in one host only is a bug.
- **Configuration lives in `.osiris/`** and stays as small as it can be: prefer a sensible default to a new
  setting.
- **What the agent can't work without is built in; the rest is a package** against `Osiris.Abstractions`.
- **The agent authorizes nothing**, and nothing it's given comes from an untrusted workspace unchecked:
  paths go through `Workspace.Resolve`, URLs through `SafeUrl`, secrets are filled only for their hosts.
- Tests come with the change (`tests/Osiris.Tests`); they run without a network or a model.
- Warnings are errors. Match the code around you: file-scoped namespaces, primary constructors, comments
  that say why.
- Changing `Osiris.Abstractions` in a breaking way needs a very good reason: packages are built against it.

## Pull requests

Keep them small and say what changes for a user or an extension author. Add a line to `CHANGELOG.md` under
"Unreleased" for anything user-visible. CI must pass.

## Security

Please don't open an issue for a vulnerability: see [SECURITY.md](SECURITY.md).

By contributing you agree that your work is released under the [MIT license](LICENSE).
