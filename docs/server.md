# Running the server

`osiris-server` is Osiris for a team: many workspaces (tenants), many users, a web UI and an HTTP API.
Users sign in with [Anubis](https://github.com/k6labs-cz/Anubis), an OpenID Connect identity provider.

## Who gets in

Every folder under `<DataDirectory>/workspaces` whose name is lowercase letters, digits and dashes is a
workspace; the name is its id. **A workspace is a tenant in Anubis with the same id.** Osiris's client in
Anubis defines two roles, and Anubis gives them to users and groups, in one tenant or in all:

| Role of the `osiris` client | In the tenant `acme` | In all tenants |
| --- | --- | --- |
| `User` | chats with the agent of the workspace `acme` | … of every workspace |
| `Administrator` | also changes `acme`'s settings: configuration, secrets, MCP servers, rules, skills, memory, schedules | … every workspace's |

Without a role in a workspace's tenant, the workspace doesn't exist for the user.

Osiris asks for the scope `roles` at sign-in. Anubis then issues the access token **for Osiris** (the
client id is its audience) with the user's roles in it: `acme:Administrator` for a role in the tenant `acme`,
plain `Administrator` for a role in all tenants. Osiris accepts only tokens issued for it and reads the roles
from them. Access tokens are encrypted for Anubis, so Osiris asks Anubis about them (introspection), and asks
again every few minutes during a browser session: when a role is taken away, Anubis revokes the tokens that
carry it and the session ends. A role newly given shows up at the next sign-in.

To open a workspace `acme`, in Anubis's admin UI (or over its SCIM API: `/api/Tenants`, `/api/RoleAssignments`):

1. **Tenants** → create `acme`.
2. **Roles** → assign `osiris: User` or `osiris: Administrator` to a user or a group, in the tenant `acme`.
3. On the server, make the folder `workspaces/acme`.

Osiris's client in Anubis needs the grant types `authorization_code` and `client_credentials` (the second
is what allows introspection), the scopes `openid profile email roles`, the roles `Administrator` and `User`,
and the redirect addresses `https://<osiris>/signin-oidc` and `https://<osiris>/signout-callback-oidc`.
The Helm chart seeds all of that.

API clients send `Authorization: Bearer <access token>`; the same rule applies.

## Kubernetes (Helm)

The chart [`deploy/helm/osiris`](../deploy/helm/osiris) installs Osiris and, by default, Anubis with the
`osiris` client already seeded; the client's secret is generated once and shared by both.

```bash
helm install osiris oci://ghcr.io/k6labs-cz/charts/osiris -n osiris --create-namespace -f values.yaml
```

```yaml
model:
  apiKey: "…"                      # the default model's key (Mistral unless model.endpoint/name say otherwise)
ingress:
  enabled: true
  className: nginx
  hosts: [osiris.example.com]
  tls: [{ secretName: osiris-tls, hosts: [osiris.example.com] }]
anubis:
  config:
    Anubis:
      AdminEmail: admin@example.com
      Clients:
        - ClientId: osiris
          DisplayName: Osiris
          RedirectUris: ["https://osiris.example.com/signin-oidc"]
          PostLogoutRedirectUris: ["https://osiris.example.com/signout-callback-oidc"]
          GrantTypes: [authorization_code, client_credentials]
          Scopes: [openid, profile, email, roles]
          RequireConsent: false
          Roles: [{ Name: Administrator }, { Name: User }]
      Tenants: [{ Id: acme, DisplayName: Acme Ltd. }]
  ingress:
    enabled: true
    className: nginx
    hosts: [id.example.com]
    tls: [{ secretName: anubis-tls, hosts: [id.example.com] }]
```

- Both run as **one replica** each on a ReadWriteOnce volume (their state is files and SQLite).
- `helm install` prints how to read Anubis's generated admin password.
- A workspace is a tenant in Anubis and a folder here: `kubectl exec deploy/osiris -- mkdir -p /data/workspaces/acme`.
- To use an Anubis you already run: `anubis.enabled=false`, `auth.authority`, `auth.clientId`, `auth.clientSecret`.
- All values: [`values.yaml`](../deploy/helm/osiris/values.yaml).

## A container without Kubernetes

```bash
docker run -p 8080:8080 -v osiris-data:/data \
  -e Auth__Authority=https://id.example.com -e Auth__ClientId=osiris -e Auth__ClientSecret=… \
  -e Osiris__Model__ApiKey=… \
  ghcr.io/k6labs-cz/osiris
```

Put it behind a reverse proxy that ends TLS and sets `X-Forwarded-Proto` (the image trusts forwarded headers,
so don't expose it directly).

## Settings

From `appsettings.json`, environment variables (`Section__Key`) or the command line (`--Section:Key value`).

| Key | Default | |
| --- | --- | --- |
| `Osiris:DataDirectory` | `data` (`/data` in the image) | workspaces, the key sealing secrets, cookie keys, downloaded packages |
| `Osiris:Model:Endpoint`, `Name`, `Embedding`, `ApiKey` | Mistral | the model of workspaces that don't configure their own |
| `Osiris:SecretKey` | made once in the data directory | base64 32-byte key sealing the workspaces' secrets; back it up |
| `Osiris:AllowedPackages` | `k6labs.Osiris.*` | package ids workspaces may load (`*` at the end matches a prefix) |
| `OSIRIS_PACKAGES_TOKEN` | – | GitHub token with `read:packages`, for packages from GitHub Packages |
| `Auth:Authority` | – (required) | Anubis's public address |
| `Auth:ClientId`, `Auth:ClientSecret` | `osiris`, – | Osiris's client in Anubis |
| `Auth:Audience` | `Auth:ClientId` | the audience an access token must be issued for |
| `Auth:AdministratorRole`, `Auth:UserRole` | `Administrator`, `User` | the client's roles in Anubis |
| `Auth:RecheckAfter` | `00:05:00` | how often a browser session's access token is checked again |
| `Auth:Scopes` | – | more scopes to ask for at sign-in, besides `openid profile roles` |
| `Auth:RequireHttps` | `true` | `false` only for an Anubis on plain http on your own machine |

What a workspace may do differently on a server: its MCP servers and `http_request` reach only public https
addresses; `{{env:…}}` and the workspace's own package `sources` don't work; the server's model key is used
only with the server's model endpoint (a workspace that names another endpoint brings its own key as a secret).
