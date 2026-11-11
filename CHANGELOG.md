# Changelog

All notable changes to Osiris are written here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/); versions follow [Semantic Versioning](https://semver.org/).

## [Unreleased]

## [0.1.0]

The first release.

### Added

- One agent (`Osiris.Core`) hosted by three apps: `osiris` (terminal), `osiris-desktop` (desktop) and
  `osiris-server` (multi-user server).
- Workspaces marked by `.osiris/`, found from any subfolder; `osiris init`; a `.gitignore` inside that keeps
  temporary files out of git.
- `.osiris/osiris.json`: model, NuGet packages, package sources and MCP servers in one file, with a JSON schema.
- The extension API `k6labs.Osiris.Abstractions` (`IToolProvider`, `IGuard`, `IMcpServerProvider`) and loading
  of extensions from NuGet packages (nuget.org, k6labs' GitHub Packages, own sources).
- The database tools as the first package, `k6labs.Osiris.Tools.Database`.
- Built-in tools: files, memory, skills, knowledge search, `http_request` with sealed secrets, scheduled
  tasks, questions to the user, workflows; the built-in rule guard.
- The web UI on Hathor components, shared by the desktop app and the server.
- Server sign-in with Anubis: a workspace is a tenant, access by the roles `Administrator` and `User` of
  Osiris's client in that tenant, tokens issued for Osiris and checked again during a session.
- Releases: NuGet packages, Flatpak bundle, Windows installer, archives, container image and Helm chart
  (with Anubis included).

[Unreleased]: https://github.com/k6labs-cz/Osiris/compare/v0.1.0...HEAD
[0.1.0]: https://github.com/k6labs-cz/Osiris/releases/tag/v0.1.0
