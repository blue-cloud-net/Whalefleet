# Docker.Manager

> **Language / 语言**: [English](README.md) | **简体中文** ([README.zh-CN.md](README.zh-CN.md))

> A lightweight, Portainer-style Docker management platform. A web-based UI for managing containers, images, volumes, networks, and other resources on local and remote Docker hosts.
>
> **Backend** .NET 10 LTS + Dapper + SQLite + SignalR ｜ **Frontend** Vue 3.5+ + TypeScript 5.7+ + UnoCSS + Element Plus 2.14+ ｜ **Docker SDK** [Docker.DotNet.Enhanced](https://github.com/testcontainers/Docker.DotNet) 4.3.3 (Docker Engine API v29.4.1)

[![MIT License](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)
[![.NET 10 LTS](https://img.shields.io/badge/.NET-10_LTS-512BD4)](https://dotnet.microsoft.com)
[![Vue 3.5+](https://img.shields.io/badge/Vue-3.5+-42b883)](https://vuejs.org)
[![Docker Engine API v29.4.1](https://img.shields.io/badge/Docker_Engine_API-v29.4.1-2496ED)](https://github.com/testcontainers/Docker.DotNet)
[![GHCR](https://img.shields.io/badge/GHCR-blue--cloud--net%2Fdocker--manager-2496ED)](https://ghcr.io/blue-cloud-net/docker-manager)

> Current stage: **0.0.x development iterations** (not officially released; `0.0.*` tags are not pushed to GHCR). See [docs/roadmap.md](docs/roadmap.md) for the roadmap to the first semver-stable release, `v0.1.0`.

---

## ✨ Features

- 🐳 **Full container lifecycle**: create / start / stop / restart / remove / bulk remove / logs / inspect (0.0.9 + 0.2.0)
- 🖼 **Image management**: pull (with progress) / remove / bulk remove / inspect; Dockerfile build from 0.2.0; push to private registries from 0.4.0
- 💾 **Complete Volume / Network CRUD** (0.2.0)
- 🖥 **Multi-host management**: local socket + remote TCP (optional TLS / Basic auth), switchable from the top bar (from 0.0.8)
- 🔐 **Authentication from day one**: JWT + refresh token + Argon2id password hashing + login rate limiting (from 0.0.4)
- 👥 **Two roles (admin / user) + host-scoped visibility** (0.1.0); **resource-granular RBAC** (0.3.0)
- 🔔 **Real-time push** (0.3.0): SignalR live log streams / CPU, memory, and network metrics / container anomaly notifications
- 💻 **Web terminal** (0.4.0): xterm.js + a dedicated short-lived terminal token
- 📦 **Docker Compose / Stack management** (0.4.0)
- 🎨 **Modern UI**: UnoCSS + Element Plus + command palette (Ctrl+K) + global shortcuts + light/dark themes + three accent colors

---

## ⚠️ Security Notice: Docker Socket Mounting

> **Mounting `/var/run/docker.sock` grants processes inside the container root-level access to the host's Docker daemon** — a single container vulnerability means the host is compromised.

**Deploying in socket-proxy mode is strongly recommended**:

```yaml
services:
  socket-proxy:
    image: tecnativa/docker-socket-proxy
    environment:
      CONTAINERS: 1
      IMAGES: 1
      NETWORKS: 1
      VOLUMES: 1
    volumes:
      - /var/run/docker.sock:/var/run/docker.sock:ro

  docker-manager:
    image: docker-manager:local
    depends_on: [socket-proxy]
    ports:
      - "8080:8080"
    environment:
      - ASPNETCORE_ENVIRONMENT=Production
      - JWT__SECRET=<your-strong-secret-≥32-bytes>
      - ADMIN__USER=admin
      - ADMIN__PASSWORD=<initial-strong-password>
      # Hosts are no longer configuration items (they live in the hosts table since 0.0.8):
      # only `local` (/var/run/docker.sock) exists by default. Add hosts pointing at a
      # socket-proxy or elsewhere from the admin UI after startup.
    volumes:
      - ./data:/app/data
      - ./scripts:/app/scripts
    restart: unless-stopped
```

If you **must mount the socket directly**, make sure the Docker.Manager instance is only exposed to trusted networks. See [SECURITY.md](SECURITY.md) for the full security policy.

---

## 🚀 Quick Start

### Option 1: Socket-Proxy Mode (recommended)

> **0.0.x is not published to GHCR.** The project is in development iterations, so the only
> image source right now is a local build. The first published image ships with `v0.1.0`.

```bash
# 1. Clone and enter the repository
git clone https://github.com/blue-cloud-net/docker-manager.git
cd docker-manager

# 2. Build the image (Node + .NET SDK + runtime, three stages)
docker build -t docker-manager:local .

# 3. Provide the required secrets
cp .env.example .env
#    then edit .env — ADMIN__PASSWORD and JWT__SECRET are mandatory.
#    Generate a key with: openssl rand -base64 48

# 4. Start. The default is socket-proxy mode (recommended).
docker compose up -d

#    To use the direct-socket mode instead, stop first, then:
#    docker compose --profile direct up -d

# 5. Open http://localhost:8080 and log in with the credentials from .env
```

### Verifying the deployment

```bash
docker compose ps                              # expect STATUS "healthy"
docker compose exec docker-manager bash /app/scripts/backup.sh   # produces a verified backup

# Scheduled backup (03:17 daily — off the hour to avoid IO contention):
#   17 3 * * * docker exec docker-manager bash /app/scripts/backup.sh \
#     >> /var/log/docker-manager-backup.log 2>&1
```

### Option 2: Direct Socket Mount (trusted environments only)

```bash
docker run -d \
  --name docker-manager \
  -p 8080:8080 \
  -v /var/run/docker.sock:/var/run/docker.sock \
  -v $(pwd)/data:/app/data \
  -e JWT__SECRET=$(openssl rand -hex 32) \
  -e ADMIN__USER=admin \
  -e ADMIN__PASSWORD='StrongP@ss123' \
  --restart unless-stopped \
  docker-manager:local
```

### Option 3: Standalone Process

```bash
dotnet Docker.Manager.Api.dll --urls http://0.0.0.0:8080
```

The current host user needs read/write access to `/var/run/docker.sock` (usually by joining the `docker` group).

---

## 📸 Screenshots

> _Placeholder: screenshots to be added during the 0.1.0-rc.1 phase._

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
| [docs/roadmap.md](docs/roadmap.md) | Full roadmap for 20 version iterations (0.0.1 ~ 1.0.0) |
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

- Report issues: [GitHub Issues](https://github.com/blue-cloud-net/docker-manager/issues)
- Open a PR: read [CONTRIBUTING.md](CONTRIBUTING.md) for the development workflow, Conventional Commits rules, and PR process
- Report security vulnerabilities: **do not** disclose them in public issues — contact the maintainers privately following [SECURITY.md](SECURITY.md)

Development environment: .NET 10 SDK + Node.js 20+ + pnpm.

---

## 📋 Versioning and Releases

| Version range | Meaning | Public release? |
|---------------|---------|-----------------|
| `0.0.x` | Unreleased development iterations | ❌ Not published to GHCR |
| `0.1.0-rc.N` | MVP release candidates | ⚠️ Pre-release |
| `0.1.0` | **First semver-stable MVP release** | ✅ Official release |
| `0.2.0` / `0.3.0` / `0.4.0` | Feature milestone releases | ✅ Official release |
| `1.0.0` | **Production-ready release** | ✅ Official release |

- Image registry: `ghcr.io/blue-cloud-net/docker-manager`
- Tag strategy: semver only (`v0.1.0` / `0.1.0` / `0.1` / `latest`)
- Changelog: the English `CHANGELOG.md` is generated automatically by [Release Please](https://github.com/googleapis/release-please) starting from 0.0.15
- Full strategy: [docs/overview.md](docs/overview.md#发布模型与版本号策略) — note that the anchor inside `docs/` is in Chinese

---

## ⚠️ Important Limitations

- **The project is in the 0.0.x development iteration phase** and is not officially released; the API may change in breaking ways
- **Single-instance deployment**: SQLite does not support horizontal multi-instance scaling (see [docs/constraints.md](docs/constraints.md#sqlite-部署细节))
- **HTTPS**: the image listens on plain HTTP only; **production deployments must terminate TLS in front of it** (Caddy / Traefik / nginx)
- **Authorization granularity**: 0.1.0 is host-scoped only; container-level resource-granular RBAC is scheduled for [0.3.0](docs/roadmap.md#030--实时推送与资源粒度-rbac)

---

## 📄 License

[MIT](LICENSE) © 2026 blue-cloud-net
