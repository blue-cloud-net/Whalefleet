# Security Policy

## Supported versions

Docker.Manager is in **0.0.x development iterations** — it is not released to GHCR and the
API may change in breaking ways between iterations. Security fixes land on `dev`, which is
the integration branch.

| Version | Supported |
|---------|-----------|
| `dev` (integration branch) | ✅ |
| `0.0.x` tags | ⚠️ best-effort; not published to GHCR |
| `0.1.0+` | ✅ (once released) |

## Reporting a vulnerability

**Please do not open a public issue.** A public issue makes the vulnerability visible to
everyone before a fix exists.

Instead, use GitHub's private reporting:

1. Open the repository's **Security** tab → **Report a vulnerability**
2. Or use GitHub's [private vulnerability reporting](https://docs.github.com/en/code-security/security-advisories/guidance-on-reporting-and-writing/privately-reporting-a-security-vulnerability)

Include, as far as you can:

- what an attacker can achieve (not just the steps)
- affected version or commit
- configuration required to reach it

### What to expect

| Stage | Target |
|-------|--------|
| Acknowledgement | 3 business days |
| Initial assessment | 14 days |

We will tell you when a fix is available and credit you in the advisory unless you prefer
otherwise.

> There is **no** bug bounty. We will prioritise based on impact, not on reward.

---

## Deployment: the one thing you must get right

Docker.Manager's security posture is decided almost entirely by **how you deploy it**, not by
the code. Read this section before exposing the service to any network.

### 1. Mounting the Docker socket is equivalent to giving away root

```yaml
volumes:
  - /var/run/docker.sock:/var/run/docker.sock:ro
```

Mounting the socket gives **every process inside the container** the same authority as root
on the host: it can start a privileged container, bind-mount `/`, and read or overwrite any
file on the machine. A single container vulnerability — or a single vulnerable transitive
dependency — means **the host is compromised**.

> The `:ro` suffix does **not** reduce this. Socket permissions are enforced by the Docker
> daemon's API, not by the mount flags; `:ro` only prevents accidentally writing to the
> socket file itself.

### 2. Prefer the socket-proxy mode

`docker-compose.yml` ships a second mode that routes Docker API calls through
[`tecnativa/docker-socket-proxy`](https://github.com/Tecnativa/docker-socket-proxy), which grants
only `CONTAINERS`, `IMAGES`, `NETWORKS` and `VOLUMES`:

```bash
docker compose up -d socket-proxy docker-manager
```

This does **not** make the proxy safe to compromise — it still holds the full socket. What it
does is shrink the attack surface from *every container on the host* to *one small proxy
container that contains no application code*.

### 3. Never expose the service to the public internet without a reverse proxy

There is no TLS termination in the application. Put a reverse proxy in front of it and
provide TLS. When you do, configure the trusted proxy list — see below, because getting this
wrong breaks **login rate limiting** for every user at once.

### 4. Keep the data volume on a dedicated, non-shared path

`/app/data` holds the SQLite database (users, refresh tokens, host credentials) and the logs.
It is the only writable path in the container. Back it up with `scripts/backup.sh`; treat the
backups as credentials.

### 5. Secrets

- `JWT__SECRET` — required, must be long and random (`openssl rand -base64 48`).
  **The application refuses to start without it.**
- `ADMIN__PASSWORD` — required for the initial bootstrap. Weak passwords fail validation and
  the service **refuses to start**; this is intentional.
- After the first login, **change the admin password in the UI** and remove
  `ADMIN__USER` / `ADMIN__PASSWORD` from the environment. They are only read while the `users`
  table is empty.

Never commit `.env` to version control. `.dockerignore` excludes it so it cannot be baked into
an image by accident, but that is a backstop, not a plan.

---

## Trusted reverse proxies (rate limiting)

Login rate limiting keys on the client IP. Behind a reverse proxy, the TCP peer is the **proxy**,
not the user — so unless configured, every user's failed logins land in one bucket and five
failures lock out everyone for a minute.

Set the proxy's address explicitly:

```yaml
environment:
  ReverseProxy__Enabled: "true"
  ReverseProxy__KnownProxies__0: "172.18.0.2"      # 单个代理
  # ReverseProxy__KnownNetworks__0: "172.18.0.0/16" # 或整个网段
```

Why an explicit list rather than trusting the header by default:

- `X-Forwarded-For` is **client-controllable**. Trusting it with no allowlist hands the
  rate-limit key to an attacker, who defeats the limiter by rotating a fake IP.
- Enabling the switch **without** an allowlist is refused at startup — that is strictly worse
  than leaving it off.
- A malformed address fails startup rather than being silently ignored, because the symptom of
  a silently-ignored proxy ("everyone shares one bucket") is identical to not configuring it
  at all.

## Application-level defaults

| Area | Default | Note |
|------|---------|------|
| CORS | Deny all | `Cors:AllowedOrigins` is empty by default |
| Forwarded headers | Disabled | See above |
| Login rate limit | 5 failures / minute / IP + username | 429 after the 5th |
| Password hashing | Argon2id | |
| Security headers | Always sent | Also on error responses |
| Container user | non-root (uid 10001) | Verified in CI |
| Root filesystem | works read-only | Mount only `/app/data` writable |

## What is explicitly out of scope

- **Running without authentication.** There is no anonymous mode; every endpoint requires a
  JWT.
- **Multi-tenancy or network isolation between users.** Access control is per-host and
  per-container *within* one instance; anyone with network access to the port can reach the
  login page.
- **Protecting a host where the Docker socket is mounted to an untrusted container.** The
  socket is the boundary; there is nothing inside the application that can repair that.