# Osiris

An AI agent that works over a folder of yours — in the terminal, in a desktop app, or on a server for a
team. Free and open source (MIT), written in C# on .NET 10.

Osiris reads and writes the files of a **workspace**, searches its documents, remembers facts, asks you when
something is unclear, runs scheduled tasks, and follows the rules, skills, tools and MCP servers the workspace
configures. It talks to any OpenAI-compatible model API with your own key.

| | Runs as | For |
| --- | --- | --- |
| **Terminal** | `osiris` | one user; the agent runs inside the command |
| **Desktop** | `osiris-desktop` | one user; the web UI in a window of its own |
| **Server** | `osiris-server` (container, Helm chart) | many workspaces and users, signed in with [Anubis](https://github.com/k6labs-cz/Anubis) |

All three host the same agent (`Osiris.Core`); they differ only in how you reach it and who may.

## A workspace

A workspace is a folder marked by `.osiris/` inside it, the way `.git/` marks a repository. `osiris` finds it
from any subfolder. Everything about the agent lives there:

```
my-project/
├── .osiris/
│   ├── osiris.json      the agent's configuration (below)
│   ├── .gitignore       keeps conversations and other temporary files out of git
│   ├── guards/          rules the agent keeps to above everything else (<name>.md)
│   ├── skills/          procedures it loads when a task matches (<name>/SKILL.md)
│   ├── memory/          facts it remembers across conversations
│   ├── secrets/         access keys, sealed with a key that never enters the workspace
│   ├── schedules.json   tasks it runs by itself
│   └── threads/ …       conversations (not versioned)
├── knowledge/           Markdown documents the agent searches (a vector index is built from them)
└── … your files
```

Commit `.osiris/` with the project: configuration, rules, skills, memory and the sealed secrets are meant to
be shared; its `.gitignore` leaves out what's temporary. The agent's own file tools can't reach `.osiris/`
(or any hidden folder), so it can't change its rules or read its secrets.

## Configuration: `.osiris/osiris.json`

Everything is optional — an empty file is a working agent with the default model and the built-in tools.

```jsonc
{
  "$schema": "https://raw.githubusercontent.com/k6labs-cz/Osiris/main/schemas/osiris.schema.json",

  // Any OpenAI-compatible API. The key is never written here: a workspace secret or an environment variable.
  "model": {
    "endpoint": "https://api.openai.com/v1",
    "name": "gpt-5",
    "embedding": "text-embedding-3-small",
    "apiKey": "{{env:OPENAI_API_KEY}}"
  },

  // Tools, guards and MCP servers from NuGet packages: id and version.
  "packages": {
    "k6labs.Osiris.Tools.Database": "0.1.0",
    "Acme.Osiris.Crm": { "version": "2.1.0", "settings": { "region": "eu" } }
  },

  // More NuGet sources besides nuget.org and k6labs' GitHub Packages (a feed or a local folder).
  "sources": [ "https://nuget.pkg.github.com/acme/index.json" ],

  // MCP servers over HTTP; their tools appear as <name>_<tool>.
  "mcp": {
    "crm": { "url": "https://crm.example/mcp", "headers": { "Authorization": "Bearer {{secret:CRM_TOKEN}}" } }
  }
}
```

The same file in your configuration folder (`~/.config/osiris/osiris.json`, `%AppData%\osiris\osiris.json`)
gives the defaults for all your workspaces. Without any `model`, Osiris uses Mistral
(`mistral-small-latest`) with the key from `OSIRIS_API_KEY` (or `MISTRAL_API_KEY`, `OPENAI_API_KEY`).
A changed file applies from the next message; no restart.

### Packages

What the agent needs for its own work is built in: files, memory, skills, knowledge search, `http_request`,
scheduled tasks, questions to the user, workflows, and the rule guard. Everything else is a NuGet package
added in `packages` — for example the database tools, [`k6labs.Osiris.Tools.Database`](src/Osiris.Tools.Database).

Packages are looked up on **nuget.org** and on **k6labs' GitHub Packages**
(`https://nuget.pkg.github.com/k6labs-cz/index.json`, where k6labs' packages are released), plus the
workspace's own `sources`. Packages named `k6labs.*` are never taken from nuget.org. GitHub Packages asks for
a token even for public packages: set `OSIRIS_PACKAGES_TOKEN` (or `GITHUB_TOKEN`) to one with `read:packages`,
or keep the credentials for that source in your NuGet.Config.

A package is code that runs inside Osiris. On your machine you decide what to trust; a server loads only the
ids its operator allows (`k6labs.Osiris.*` by default).

Writing one takes an interface and a `dotnet pack`: see [docs/extensions.md](docs/extensions.md).

## Install

Downloads are on the [releases page](https://github.com/k6labs-cz/Osiris/releases).

- **Linux** — `Osiris-<version>-x86_64.flatpak`: `flatpak install --user Osiris-<version>-x86_64.flatpak`.
  The desktop app appears in your launcher; the terminal app is
  `flatpak run --command=osiris cz.k6labs.Osiris`. Or unpack the `tar.gz` archives (no .NET needed).
- **Windows** — `Osiris-<version>-win-x64-setup.exe` installs the desktop app and puts `osiris` on PATH;
  the `zip` archives need no installation.
- **macOS** — `tar.gz` archives for Apple silicon, unsigned and untested (we have no Mac; reports welcome).
- **With .NET 10** on any platform: `dotnet tool install -g k6labs.Osiris.Cli` (from k6labs' GitHub Packages)
  gives the `osiris` command.

```bash
export OSIRIS_API_KEY=…          # your model's key (Mistral by default)
cd my-project
osiris init                      # makes .osiris/
osiris                           # chat; /new, /threads, /help, /quit
osiris -p "Summarize notes/"     # one answer, for scripts
osiris-desktop my-project        # the desktop app (asks for a folder without an argument)
```

## The server

`osiris-server` serves many workspaces to many users: every folder under `<data>/workspaces` is a workspace.
Users sign in with [Anubis](https://github.com/k6labs-cz/Anubis) (OpenID Connect), where a workspace is a
**tenant** of the same id and Osiris's client defines two roles:

- **User** in a workspace's tenant: chats with its agent;
- **Administrator**: also changes the workspace's settings (configuration, secrets, MCP servers, rules).

A role can be given to a user or a group, in one tenant or in all. Osiris accepts access tokens issued for it
and reads the roles from them; taking a role away in Anubis ends the user's session within minutes.

The Helm chart installs Osiris with Anubis included:

```bash
helm install osiris oci://ghcr.io/k6labs-cz/charts/osiris -n osiris --create-namespace \
  --set model.apiKey=… \
  --set ingress.enabled=true --set 'ingress.hosts[0]=osiris.example.com' \
  --set anubis.ingress.enabled=true --set 'anubis.ingress.hosts[0]=id.example.com' \
  --set 'anubis.config.Anubis.Clients[0].RedirectUris[0]=https://osiris.example.com/signin-oidc'
```

Setting up Anubis (tenants and roles), every option and running the container without Kubernetes:
[docs/server.md](docs/server.md).

## Building

Needs the .NET 10 SDK. The UI library [Hathor](https://github.com/k6labs-cz/Hathor) comes from k6labs' GitHub
Packages, which wants a token with `read:packages` even though the packages are public:

```bash
export NuGetPackageSourceCredentials_k6labs='Username=<github login>;Password=<token>'
dotnet build
dotnet test

export OSIRIS_API_KEY=…
dotnet run --project src/Osiris.Cli -- ~/my-project
dotnet run --project src/Osiris.Desktop -- ~/my-project                 # a window (needs WebKitGTK on Linux)
dotnet run --project src/Osiris.Desktop -- ~/my-project --no-window     # prints an address for your browser

packaging/publish.sh linux-x64        # self-contained apps in artifacts/publish/
packaging/flatpak/build.sh            # the Flatpak bundle
```

| Project | What it is |
| --- | --- |
| [`src/Osiris.Abstractions`](src/Osiris.Abstractions) | The extension API (`IToolProvider`, `IGuard`, `IMcpServerProvider`) — NuGet `k6labs.Osiris.Abstractions` |
| [`src/Osiris.Core`](src/Osiris.Core) | The agent: the ReAct loop, built-in tools and guard, configuration, package loading — NuGet `k6labs.Osiris.Core` |
| [`src/Osiris.Tools.Database`](src/Osiris.Tools.Database) | The database tools as a package — NuGet `k6labs.Osiris.Tools.Database` |
| [`src/Osiris.Ui`](src/Osiris.Ui) | The web UI (Blazor Server with Hathor) and HTTP API, shared by the desktop app and the server |
| [`src/Osiris.Cli`](src/Osiris.Cli) | `osiris`: the terminal app (Spectre.Console), also a .NET tool |
| [`src/Osiris.Desktop`](src/Osiris.Desktop) | `osiris-desktop`: the UI on localhost in a Photino window |
| [`src/Osiris.Web`](src/Osiris.Web) | `osiris-server`: the multi-user server with Anubis sign-in |
| [`tests/Osiris.Tests`](tests/Osiris.Tests) | xUnit tests |
| [`deploy/helm/osiris`](deploy/helm/osiris), [`Dockerfile`](Dockerfile) | The server's Helm chart (with Anubis) and image |
| [`packaging`](packaging) | Flatpak and Windows installer |

How a release is made: [docs/releasing.md](docs/releasing.md). Contributions are welcome: [CONTRIBUTING.md](CONTRIBUTING.md).

## License

[MIT](LICENSE) © k6labs
