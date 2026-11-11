# Security

## Reporting a vulnerability

Please report it privately through
[GitHub security advisories](https://github.com/k6labs-cz/Osiris/security/advisories/new), not as a public
issue. You'll get an answer within a week. Fixes are released for the latest version.

## What Osiris protects, and what it leaves to you

- **The agent acts with your model's output.** It reads and writes files in the workspace and calls the
  tools and MCP servers the workspace configures. It can't leave the workspace folder, reach hidden folders
  (`.osiris`, `.git`), or see a secret's value; on a server it also can't reach private network addresses.
- **Packages are code.** A NuGet package in `osiris.json` runs inside Osiris with its rights. On your own
  machine, add only packages you trust — as with any dependency. A server loads only the package ids its
  operator allows.
- **Secrets** are sealed (AES-GCM) with a key kept outside the workspace and filled into requests only for
  the hosts each secret allows. Whoever has that key and the workspace has the secrets: back the key up and
  protect it.
- **The desktop app** serves its UI on localhost behind a random token made at each start.
- **The server** trusts its reverse proxy's forwarded headers: expose it only through the proxy.
