# Whalefleet

> **Language / 语言**: [English](README.md) | **简体中文** ([README.zh-CN.md](README.zh-CN.md))

> A lightweight, Portainer-style Docker management platform. A web-based UI for managing containers, images, volumes, networks, and other resources on local and remote Docker hosts.
>
> **Backend** .NET 10 LTS + Dapper + SQLite + SignalR ｜ **Frontend** Vue 3.5+ + TypeScript 5.7+ + UnoCSS + Element Plus 2.14+ ｜ **Docker SDK** [Docker.DotNet.Enhanced](https://github.com/testcontainers/Docker.DotNet) 4.3.3 (Docker Engine API v29.4.1)

[![MIT License](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)
[![.NET 10 LTS](https://img.shields.io/badge/.NET-10_LTS-512BD4)](https://dotnet.microsoft.com)
[![Vue 3.5+](https://img.shields.io/badge/Vue-3.5+-42b883)](https://vuejs.org)
[![Docker Engine API v29.4.1](https://img.shields.io/badge/Docker_Engine_API-v29.4.1-2496ED)](https://github.com/testcontainers/Docker.DotNet)
[![GHCR](https://img.shields.io/badge/GHCR-blue--cloud--net%2Fwhalefleet-2496ED)](https://ghcr.io/blue-cloud-net/whalefleet)

> Current stage: **0.x feature iterations** — nothing is published to GHCR and no `0.x` tag is created.
> The only target version is **`1.0.0`**, preceded by `1.0.0-rc.N`. The public API is **not** frozen before
> `1.0.0`. See [docs/roadmap.md](docs/roadmap.md) for the version plan.

---

## ✨ Features

- 🐳 **Full container lifecycle**: create / start / stop / restart / rename / duplicate / remove / logs / inspect (0.0.9); bulk remove from 0.1.0; **bulk start / stop and resource-level prune** (0.6.0); **export / import / commit** (0.14.0)
- 🖼 **Image management**: pull / remove / inspect / tag (0.0.10+); bulk remove from 0.1.0; **Dockerfile build + private registries** (0.4.0); **save / load** and **update detection** (0.14.0 / 0.16.0)
- 💾 **Complete Volume / Network CRUD** (0.2.0)
- 🖥 **Multi-host management**: local socket + remote TCP (optional TLS / Basic auth), switchable from the top bar (from 0.0.8); **groups + tags, with group-scoped batch operations and disk usage** (0.10.0)
- 🔐 **Authentication from day one**: JWT + refresh token + Argon2id password hashing + login rate limiting (from 0.0.4); **audit log** (0.9.0)
- 👥 **Four built-in roles** (admin / operator / viewer / auditor) + host visibility (0.0.8, assignment from 0.1.0); **container-level RBAC** (0.12.0); **teams with bulk granting** (0.13.0)
- 🔔 **Real-time push** (0.7.0): SignalR live log streams / CPU, memory, and network metrics / container anomaly notifications
- 🚨 **Alerting rules** (0.11.0): three severity levels with configurable thresholds, cooldowns, and webhook / email channels
- 💻 **Web terminal** (0.8.0): xterm.js + a dedicated short-lived terminal token
- 📦 **Docker Compose / Stack management** (0.5.0), with **resource webhooks and `.env` variables** (0.15.0)
- 🤖 **MCP server** (0.18.0): expose Docker management to AI clients (VS Code Copilot, Claude Desktop) — read-only by default, with environment-variable redaction
- 🎨 **Modern UI**: UnoCSS + Element Plus + command palette (Ctrl+K) + global shortcuts + light/dark themes + three accent colors

---

## ⚠️ Security Notice: Docker Socket Mounting

> **Mounting `/var/run/docker.sock` grants processes inside the container root-level access to the host's Docker daemon** — a single container vulnerability means the host is compromised.

**Deploying in socket-proxy mode is strongly recommended**:

```yaml
services:
  socket-proxy:
    image: tecnativa/docker-socket-proxy:v0.5.0
    environment:
      CONTAINERS: 1
      IMAGES: 1
      NETWORKS: 1
      VOLUMES: 1
      POST: 1       # write switch — leave at 0 and the API becomes read-only
      SWARM: 0
      INFO: 1
      PING: 1
      VERSION: 1
    volumes:
      - /var/run/docker.sock:/var/run/docker.sock:ro

  whalefleet:
    image: whalefleet:local
    depends_on: [socket-proxy]
    ports:
      - "8080:8080"
    environment:
      - ASPNETCORE_ENVIRONMENT=Production
      # All optional: when omitted, the password and JWT key are generated on first
      # start and persisted under ./data (jwt.key / initial-admin.txt).
      - ADMIN__USER=admin
      # - ADMIN__PASSWORD=<initial-strong-password>
      # - JWT__SECRET=<your-strong-secret-≥32-bytes>
      # Hosts are no longer configuration items (they live in the hosts table since 0.0.8):
      # only `local` (/var/run/docker.sock) exists by default. Add hosts pointing at a
      # socket-proxy or elsewhere from the admin UI after startup.
    volumes:
      - ./data:/app/data
      - ./scripts:/app/scripts
    restart: unless-stopped
```

If you **must mount the socket directly**, make sure the Whalefleet instance is only exposed to trusted networks. The full risk analysis lives in [docs/constraints.md](docs/constraints.md#docker-socket-挂载-️-高危).

---

## 🚀 Quick Start

### Option 1: Socket-Proxy Mode (recommended)

> **Nothing is published to GHCR yet.** The project is in `0.x` feature iterations and no image is
> released, so the only image source right now is a local build. The first published image ships with
> `v1.0.0-rc.1`.

```bash
# 1. Clone and enter the repository
git clone https://github.com/blue-cloud-net/whalefleet.git
cd whalefleet

# 2. Build the image (Node + .NET SDK + runtime, three stages)
docker build -t whalefleet:local .

# 3. Start. **No configuration required** — the password and JWT key are both generated for you.
#    The default is socket-proxy mode (recommended).
docker compose up -d

#    To use the direct-socket mode instead, stop first, then:
#    docker compose --profile direct up -d

# 4. Retrieve the auto-generated admin password
docker compose exec whalefleet cat /app/data/initial-admin.txt

# 5. Open http://localhost:8080 and log in with the credentials in that file
#
# ⚠️ Direct-socket mode needs the container in the host's docker group, or you get error 1301:
#    export DOCKER_GID=$(stat -c '%g' /var/run/docker.sock)
#
# Prefer to set them yourself? Copy .env.example to .env and fill it in as commented.
# Leaving a value blank is equivalent to not passing it.
```

### Verifying the deployment

```bash
docker compose ps                              # expect STATUS "healthy"
docker compose exec whalefleet bash /app/scripts/backup.sh   # produces a verified backup

# ⚠️ Alongside the .db you should see a same-named .db.jwt.key —
#    omitting it on restore silently logs every online user out.
ls -1 data/backups/

# Scheduled backup (03:17 daily — off the hour to avoid IO contention):
#   17 3 * * * docker exec whalefleet bash /app/scripts/backup.sh \
#     >> /var/log/whalefleet-backup.log 2>&1
```

### Option 2: Direct Socket Mount (trusted environments only)

```bash
# ⚠️ --group-add is not optional: the in-image app user (UID 10001) is not in the
#    host's docker group, so it cannot open the socket — you get error 1301.
docker run -d \
  --name whalefleet \
  -p 8080:8080 \
  -v /var/run/docker.sock:/var/run/docker.sock \
  -v $(pwd)/data:/app/data \
  --group-add "$(stat -c '%g' /var/run/docker.sock)" \
  --restart unless-stopped \
  whalefleet:local

# Retrieve the auto-generated admin password
docker exec whalefleet cat /app/data/initial-admin.txt
```

### Option 3: Standalone Process

```bash
dotnet Whalefleet.Api.dll --urls http://0.0.0.0:8080
```

The current host user needs read/write access to `/var/run/docker.sock` (usually by joining the `docker` group).

---

## 📸 Screenshots

> _Placeholder: screenshots to be added during the 1.0.0-rc.N phase._

| Dashboard | Container List | Container Logs |
|-----------|---------------|----------------|
| _(to be added)_ | _(to be added)_ | _(to be added)_ |

---

## 📚 Documentation

> Note: the documents under `docs/` are currently maintained in **Chinese** only.

| Document | Description |
|----------|-------------|
| [docs/overview.md](docs/overview.md) | Background, positioning, goals, non-goals, release model, repository identifiers |
| [docs/constraints.md](docs/constraints.md) | Technology stack version baseline, security / CI-CD constraints, deployment constraints, boundary conditions |
| [docs/features.md](docs/features.md) | Complete feature list grouped by module (annotated by version) |
| [docs/roadmap.md](docs/roadmap.md) | Version plan targeting `1.0.0` (0.1.0 – 0.19.0 feature iterations, then RC and the official release) |
| [docs/architecture.md](docs/architecture.md) | System layering, multi-host DockerClient factory, SDK isolation, communication mechanisms |
| [docs/api-spec.md](docs/api-spec.md) | RPC-style API conventions + error code ranges + SignalR hubs + WebSocket terminal |
| [docs/ui-guidelines.md](docs/ui-guidelines.md) | UI layout specs (layout diagrams / breakpoints / component tiers / shortcuts / themes / routing) |
| [CHANGELOG.md](CHANGELOG.md) | Changelog (English) ｜ [CHANGELOG.zh-CN.md](CHANGELOG.zh-CN.md) (Chinese) |
| [AGENTS.md](AGENTS.md) | AI agent collaboration guide (build commands / architecture rules / pitfall checklist) |

---

## 🛠 Tech Stack

**Backend**: .NET 10 LTS / ASP.NET Core 10 / SQLite + Dapper / SignalR / Serilog / `Docker.DotNet.Enhanced`

**Frontend**: Vue 3.5+ / TypeScript 5.7+ / Vite 7+ / UnoCSS / Element Plus 2.14+ / Pinia 4 / vue-i18n 11 / xterm.js 5.5+

**Tooling**: Central Package Management (NuGet) / pnpm (frontend) / xUnit + Testcontainers (integration tests) / Vitest (frontend tests)

See [docs/constraints.md](docs/constraints.md) for the complete version constraints.

---

## 🤝 Contributing

Contributions, bug reports, and feature suggestions are welcome.

- Report issues: [GitHub Issues](https://github.com/blue-cloud-net/whalefleet/issues)
- Open a PR: follow the Conventional Commits rules in [AGENTS.md](AGENTS.md#提交规范)
- Report security vulnerabilities: **do not** disclose them in public issues — contact the maintainers privately

> This repository intentionally ships **without** `CONTRIBUTING.md`, `SECURITY.md`, or
> `CODE_OF_CONDUCT.md`; the rules that apply to contributors live in [AGENTS.md](AGENTS.md).

Development environment: .NET 10 SDK + Node.js 20+ + pnpm.

---

## 📋 Versioning and Releases

| Version range | Meaning | Tagged? | Published to GHCR? |
|---------------|---------|:---:|-----------------|
| `0.0.x` | Completed development iterations | ❌ | ❌ |
| `0.x.0` | Feature iterations | ❌ iteration label only | ❌ |
| `1.0.0-rc.N` | First public preview (bug fixes only) | ✅ | ✅ Pre-release, **no `latest`** |
| `1.0.0` | **First official release** — the public API freeze starts here | ✅ | ✅ including `latest` |
| `1.x.y` | Patch releases (from this level on, minor releases carry fixes) | ✅ | ✅ |

- Image registry: `ghcr.io/blue-cloud-net/whalefleet`
- Tag strategy: a stable release pushes four tags (`v1.2.3` / `1.2.3` / `1.2` / `latest`); an RC pushes only
  the two exact versions (`v1.0.0-rc.1` / `1.0.0-rc.1`)
- Changelog: **fully manual** in both languages. [Release Please](https://github.com/googleapis/release-please)
  runs with `skip-changelog`, so its release PR only bumps the version; the section is written **inside that
  PR** before merging — merging it is what creates the tag, and no window exists between the two
- Full strategy: [docs/overview.md](docs/overview.md#发布模型与版本号策略) — note that the anchor inside `docs/` is in Chinese

---

## ⚠️ Important Limitations

- **The project is in the 0.x feature-iteration phase** and is not officially released; the public API may change in breaking ways until `1.0.0`
- **Single-instance deployment**: SQLite does not support horizontal multi-instance scaling (see [docs/constraints.md](docs/constraints.md#sqlite-部署细节))
- **HTTPS**: the image listens on plain HTTP only; **production deployments must terminate TLS in front of it** — ready-to-adapt nginx and Traefik configs live in [examples/reverse-proxy/](examples/reverse-proxy/), and `ReverseProxy__*` must be set alongside them (see [.env.example](.env.example))
- **Authorization granularity**: host visibility is host-scoped; container-level RBAC is scheduled for [0.12.0](docs/roadmap.md#0120--资源粒度-rbac)
- **No native package**: container and standalone-process deployments only; a systemd/native package is tracked in [docs/backlog.md](docs/backlog.md)

---

## 📄 License

[MIT](LICENSE) © 2026 blue-cloud-net
