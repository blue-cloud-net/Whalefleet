# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

> **Language / 语言**: English | [简体中文](CHANGELOG.zh-CN.md)
>
> **Maintained by hand — automation never writes to this file.** [Release
> Please](https://github.com/googleapis/release-please) is configured with `skip-changelog`, so the
> release PR it opens contains only the version bump (`.github/release-please-manifest.json` and
> `src/Whalefleet.UI/package.json`). **Write the new version's section inside that PR, in both
> languages, before merging it** — merging the PR is precisely what creates the tag, and no window
> exists between the two. See the release process in [AGENTS.md](AGENTS.md).
>
> Entries for `0.0.1` – `0.0.15` were aggregated from the milestones marked complete in
> [docs/roadmap.md](docs/roadmap.md).

> **Note on `0.x`**: every `0.x` version is an internal development milestone. No `0.x.*` tag is
> created, nothing is published to GHCR, and the API may change in breaking ways between versions.
> The first public release is `1.0.0`, preceded by `1.0.0-rc.N`.
> `0.0.6` was renumbered to `0.0.15` (deployment groundwork and the release pipeline) and is
> therefore an **unused number** — it does not represent a milestone.

---

## [Unreleased]

Work in progress. The next milestone is `0.1.0` — authorization baseline and MVP consolidation
(its per-version notes accumulate here as internal drafts and are rewritten into the `1.0.0` entry
before the first public release).

### `0.1.0` — Authorization baseline and MVP consolidation (internal draft)

#### Added

- **A permission catalog**: a **static, closed set** of permissions named `domain.level`
  (`host.read`, `container.write`, `user.manage`, …), split into Global and Host scopes.
  - It is deliberately **not a table**. "Does this build have this feature" is a compile-time fact,
    and a table for it would let the runtime-only roles drift away from what the code can enforce.
  - `PermissionScope` reserves a **Container** value now, but this milestone only opens the global
    and host assignment paths — see `0.12.0` for the container scope.
- **`role_permissions` table** (migration `0006`). A role now **carries** its permission set — a role's
  name on its own decides nothing, so the set is what makes an operator actually *be* one.
  - Permissions are stored as their `domain.level` **text**, one row per (role, permission) pair:
    a new permission is a new catalog entry plus a row, not a new column every existing row has to
    be widened to hold.
  - The **built-in** roles' permission sets are **immutable**. Administrators may add, edit and delete
    their own roles freely, but `admin` / `operator` / `viewer` / `auditor` are seeded with a fixed
    meaning — a role whose permission set can be emptied is no longer the role its name claims to be.
  - `admin` is seeded with **every** permission row *and* still short-circuits on its role name. The
    rows keep the permission matrix honest in the UI; the short-circuit means a permission added in a
    later release does not silently fail to reach administrators.
- **`user_role_assignments` table** (migration `0006`) — the user-to-role link is now **many-to-many**
  and **scoped**. `host_id` is nullable: `NULL` is a global assignment, otherwise the assignment
  applies to that one host.
  - A **global** assignment contributes the role's Global permissions. A **host** assignment
    contributes only that role's Host-scope permissions, **on that host**, and its Global permissions
    are ignored — otherwise assigning `admin` to one host would silently be a system-wide promotion.
  - The same user may hold several roles on the same host; the effective set is their **union**. The
    uniqueness index is partial (`WHERE is_deleted = 0`) so a revoked assignment can be re-created,
    and it is keyed on `(user_id, role_id, host_id)` rather than on the pair.
  - `users.role_id` is **dropped**. Roles are no longer something a user *has*; they are something a
    user is *assigned*.
- **`SqliteRoleRepository` now reads and writes permissions**, and
  **`SqliteUserRoleAssignmentRepository`** is registered in DI. Both go through the shared
  `RepositoryBase`, so audit stamps and soft deletion behave like every other entity.
- **Role management endpoints** under `api/v1/admin/roles` — `list` / `create` / `update` / `remove` /
  `set-default` / `set-permissions`. Deleting a role that still has **live assignments** is refused
  with `1512` rather than cascading: a cascade silently revokes access nobody asked to revoke, and
  "this role is in use" is a question the administrator can act on.
- **Role assignment endpoints**, host-centred and user-centred:
  `api/v1/admin/hosts/{hostId}/roles/{list,assign,revoke}` and
  `api/v1/admin/users/{userId}/roles/{list,assign,revoke}`.
  - Both are kept because they answer two different questions — "who can reach this machine?" and
    "what does this person have?" — and a UI that has to filter one list by the other's answer is a
    UI that renders a half-truth.
- **Authorization now resolves from the database on every request**, cached for the request's
  lifetime. `[Authorize(Roles = "admin")]` is replaced by a permission-based `AuthorizationHandler`.
  - Scoped assignments **cannot** fit in a single JWT claim: the same person may be an `operator` on
    one host and a `viewer` on another, so any claim snapshot is a partial and eventually wrong answer.
  - Side effect worth having: role and permission edits now take effect **immediately** instead of
    waiting for the access token to expire.
  - The `role` claim is still issued — the UI shows roles and hides menu items from them — but it no
    longer takes part in an authorization decision.
- **New error codes `1510`–`1515`** (role not found, role name taken, role still assigned,
  built-in role protected, unknown permission, assignment not found), wired into the catalog, the HTTP
  status mapping, both guard tests, the API spec, and the frontend error-code tables.
- **Batch removal for containers and images** (`POST .../containers/batch-remove` and
  `.../images/batch-remove`). Up to **100** ids per request, trimmed, de-duplicated, order preserved.
  - The response is `200` with a per-item result rather than a single status: half a batch is expected
    to fail on a busy Docker host, and collapsing that into one 4xx/5xx throws away the part that did
    work. A total failure still reports `IsTotalFailure` so the caller can raise a toast instead of
    a silent no-op.
  - Image removal is submitted as **two** batches, one per force mode. A batch carries a single `force`
    flag, and splitting it client-side is the only way to express "force-remove the safe ones, refuse
    on the running ones" without a second endpoint.
- **Role management page** (roles → permission matrix; built-in roles render read-only) and the
  host-centred "who can reach this machine" and user-centred "what does this person have" views.
  Login and `auth/me` now return the user's roles together with their effective **global** permission
  set, which is what menu items and action buttons are shown from.
- **`examples/reverse-proxy/`** — nginx (`whalefleet.conf` + proxy-params include) and Traefik
  (label-only) configurations, plus the `REVERSEPROXY__ENABLED` / `KNOWNPROXIES` / `KNOWNNETWORKS`
  keys documented in `.env.example`. Forwarded-header handling has to match the proxy that sets the
  headers, and getting it wrong is invisible until a login rate-limit starts locking out every user
  from one shared `X-Forwarded-For` bucket.

#### Changed

- **`ResourcePermissionService` resolves from roles instead of per-user grants.** Its per-host
  predicate became `FilterVisibleAsync` (reads the user's assignments **once**, filters in memory —
  per-host querying turns a host list into N round trips), and visibility is now the question "does
  this user's effective permission set on this host contain `host.read`" rather than "does a row with
  the read bit exist". Administrators still short-circuit.
- **`User` no longer carries a `RoleId`.** Creating a user now creates an assignment of the default
  role (`auditor`) with `host_id = NULL`; "keep at least one administrator" counts global assignments
  of `admin` rather than users pointing at it.
- **`ICurrentUser.Role` becomes `ICurrentUser.Roles`**, and the access token carries **multiple** role
  claims instead of one. It remains a display concern only.

> **Deployment acceptance for `0.1.0` is still open**: the socket-proxy profile's
> create → start → stop → delete + pull round trip needs a real `docker compose up` run. CI does not
> exercise the composed stack.
>
> **Migration note**: `0006` builds the authorization tables and reshapes `users`; it does **not**
> carry historical authorization rows across. This milestone has never produced a tagged release,
> so there is no production data to migrate.

---

## [0.0.15] — Deployment groundwork and release pipeline

Milestone goal: make `docker run` a single line that comes up logged-in, and make the result
publishable.

### Added

- **Multi-stage `Dockerfile`** (`Node → .NET SDK → .NET runtime`): the runtime image carries only the
  published Api assemblies plus the frontend bundle — no SDK, no Node, and **no source**. Source in
  the image is attack surface in the direct-socket deployment mode, where a single container
  vulnerability is host compromise.
- **Non-root runtime user `app` (UID 10001)**; `/app/data` is the only writable path, and a
  read-only root filesystem was verified to work.
  - `.NET 8+` `aspnet` base images already ship an `app` user, but with **UID 1654** — so
    `groupadd --gid 10001 app` fails outright with `group 'app' already exists` and stalls the build.
    The image rewrites the account with `groupmod` / `usermod`. The fixed `10001` is deliberate:
    bind mounts resolve **numeric** UIDs, so the `chown 10001:10001` in the README must match the
    image.
  - `curl` and `sqlite3` are installed in the runtime stage. Without `curl` the `HEALTHCHECK`
    degrades to "is the process alive", which cannot catch "process alive but SQLite unopenable";
    without `sqlite3` the backup command the README tells users to run does not exist.
- **Single-origin SPA hosting**: the Api process serves `wwwroot` (`Spa` configuration section,
  enabled in production). Enabled but missing the bundle directory fails at **startup** — shipping a
  container that boots but 404s in the browser is worse than one that refuses to start.
  - Deliberately **not** `MapFallbackToFile`: it routes *every* unmatched path to `index.html`,
    including mistyped `/api` paths. The symptom is "API returns 200 + HTML, logs show only
    successes", which surfaces as a JSON parse error in the browser and is effectively
    un-diagnosable from logs. The fallback therefore excludes `/api` and `/healthz`.
  - The fallback is registered **before** `UseCurrentHost`: the frontend route `/hosts/{hostId}/…`
    shares its prefix with `/api/v1/hosts/{hostId}/…`, and `UseCurrentHost` treats any `hostId`
    route value as a host to look up. Registered after it, every deep-link refresh fails with
    "host does not exist".
- **Health check backed by SQLite** (`SELECT 1`), returning 503 with the exception on failure.
  Previously `/healthz` returned 200 unconditionally — inside a container that is a permanently green
  probe: an unmounted volume, a full disk, or wrong file permissions all leave it green while every
  business endpoint returns 500.
  - The Docker daemon is deliberately **not** probed. When a host is down for maintenance the UI must
    stay reachable; counting it as unhealthy makes the orchestrator restart-loop a container whose
    restart cannot bring Docker back.
  - Registration must go through `AddCheck(name)` — registering an `IHealthCheck` directly indexes the
    result under the **implementation class name**, and `HealthCheckService` looks results up by name,
    so `/healthz` never finds the check and falls back to reporting unhealthy.
- **Trusted-proxy support for `ForwardedHeaders`** (`ReverseProxy` configuration section, off by
  default). Without it every user's login failures land in one rate-limit bucket, and five failures
  lock out all users for a minute. Three constraints: disabled by default (`X-Forwarded-For` is
  client-controllable); enabling it without an allow-list fails at startup; a malformed address throws
  rather than being silently ignored. Networks are parsed with `System.Net.IPNetwork` instead of the
  deprecated `HttpOverrides.IPNetwork` (ASPDEPR005).
- **`docker-compose.yml` with two modes** isolated by `profiles`: socket-proxy (default, recommended)
  and direct socket mount.
  - Mounting the socket `:ro` does **not** reduce risk. What a process may do is decided by the
    daemon's API, not by the mount's read-only flag; `:ro` only prevents writing to the socket file.
- **`scripts/backup.sh`** — an **online** backup built on `sqlite3 .backup`, because a plain `cp`
  captures one instant of the file and can produce a torn copy while a transaction is in flight, a
  corruption that only surfaces at restore time. Each run is followed by `integrity_check`, and
  backups rotate by filename order.
- **Release Please + image publishing**: `.github/release-please-config.json` derives the version from
  Conventional Commits; `release.yml` builds and pushes **only** on a tag, and smoke-tests before
  pushing to GHCR — a tag that is already referenced cannot be quietly withdrawn.
- **`.dockerignore`** expansion. It **must** exclude `.env*`: the `ADMIN__PASSWORD` / `JWT__SECRET`
  that compose consumes usually live there.

### Changed

- **Project renamed to `Whalefleet`** (from `Docker.Manager`), including the image name.
- **Zero-configuration deployment**: `JWT__SECRET` and `ADMIN__PASSWORD` are now *optional overrides*.
  When absent they are generated on first start and persisted to the data volume (`jwt.key` /
  `initial-admin.txt`, both `0600`). An explicitly configured value always wins, which keeps
  multi-instance shared keys, key rotation, and test injection possible.
  - The JWT key **must** be persisted rather than regenerated per start. A new key invalidates every
    outstanding access token, and the refresh tokens sitting in the database must be exchanged with the
    *old* key — so a restart would silently log out every online user, with no error at all.
  - The generated password is written to a file and **never to the log**: logs get shipped to
    centralised collectors, so printing it would spread the credential. A test pins this.
  - `ADMIN__AUTOBOOTSTRAP=false` disables the bootstrap entirely, for deployments whose data volume is
    pre-populated (an account restored from a backup, or provisioned by an ops script) — otherwise the
    bootstrap races the existing `admin` and collides on the `users.username` unique index.
  - `direct` mode no longer declares `container_name`. Compose validates that name globally at *config
    parse* time, so sharing it with `proxy` broke `docker compose --profile direct up -d` outright.
    `proxy` keeps the name, since the docs and `backup.sh` lean on `exec whalefleet`.
  - Direct-socket mode otherwise reports **1301**: the host socket is `srw-rw---- root:docker` (GID
    commonly 983) and the image's `app` user is in no such group. Use
    `--group-add $(stat -c '%g' /var/run/docker.sock)`.
  - `.gitignore` gained `.env` rules. It previously had none, and `.dockerignore` only keeps secrets
    out of the image — one `cp .env.example .env` followed by `git add .` would have committed them.
- **CI** now builds the image and smoke-tests it (`/healthz`, the served index contains
  `<div id="app">`, and the process is non-root). It builds but never pushes — pushing is
  `release.yml`'s job at tag time. It lives in CI because the frontend build inside the `Dockerfile`
  is never exercised by the .NET build-and-test job, and it is the step most likely to break (Node
  version, lockfile, output directory convention). "All backend tests green but the image does not
  build" has happened.
- Actions were upgraded to Node 24 runtimes and **Dependabot** was enabled for `github-actions`
  (weekly, PR-only, minor/patch grouped). All of them require Actions Runner ≥ v2.327.1 — GitHub-hosted
  `ubuntu-latest` qualifies; **self-hosted runners must be upgraded first**.

### Fixed

- `release.yml` carried a duplicate `name` key.
- `.dockerignore` used `**/data`, which also swallowed `src/Whalefleet.UI/src/mocks/data/` — frontend
  mock source. The build then died on `Could not resolve "./data/hosts"` with no hint pointing at
  `.dockerignore`. Only the repository-root `/data` runtime directory is excluded now.

### Security

- The backup script copies `jwt.key` next to the database as `<name>.db.jwt.key`. Restoring a backup
  without it makes the app generate a fresh key on startup, which invalidates every issued token and
  logs out all online users — **with no error at all**.
- Restored backups need `docker exec -u 0 whalefleet chown 10001:10001 /app/data/jwt.key
  /app/data/whalefleet.db`: `docker cp` applies the *executing host user's* UID (commonly 1000), and
  `jwt.key` is `0600`, so the in-image `app` user cannot read it. Documented in the header of
  `scripts/backup.sh`.
- `dotnet format` runs in CI but **does not block**: the repository carries 328 violations that
  predate this milestone (`.editorconfig` has required `this.` on members since 0.0.1, and the code
  has 160 missing ones). Fixing them in one pass touches 40+ files, and `IDE0049` (unused field)
  **deletes code** — not something a formatter should do silently given the Dapper
  convention-mapping dependency. The count and breakdown are written to the job summary as a recorded
  baseline.

### Notes

- `0.0.x` versions are not published to GHCR and **carry no tag at all** — they are iteration labels
  only. The first published image ships with `1.0.0`.
- `SECURITY.md`, `CONTRIBUTING.md`, and `CODE_OF_CONDUCT.md` were drafted during this milestone and
  later removed again. The repository intentionally ships without them, and no document may link to
  them.
- The release pipeline was redesigned after this milestone (the first public release moved from `0.1.0`
  to `1.0.0`). [AGENTS.md](AGENTS.md) documents the current flow.

---

## [0.0.14] — MVP Polish (frontend)

Milestone goal: fill in the interaction and non-functional details needed for `0.1.0`, in preparation for the release candidate.

### Added

- **Command palette** (`components/common/CommandPalette.vue`): toggled with Ctrl+K / Cmd+K; searches all
  routes plus common actions (jump to Containers, jump to Images, create container, pull image) using
  Fuse.js fuzzy matching. The match threshold was relaxed to `0.35` because palette queries are
  typically very short (`img`, `镜`), and Fuse.js's default threshold matches nothing for those.
  fuse.js is loaded via dynamic import (26 kB / 9.8 kB gzipped, its own chunk) rather than slowing the
  first paint. Also added `g+b` (toggle sidebar) and `g+s` (jump to host status).
- **Global shortcuts** (`composables/useShortcuts.ts`): `g+c` / `g+i` / `g+v` / `g+n` / `g+d` / `?` for
  navigation and the help dialog. Shortcuts are never hijacked inside input fields, never take any
  modifier-key combination other than Ctrl+K, and the chord timeout is 1.5 s. The help dialog is
  generated from the **same chord registry**, so a shortcut can never be missing from the help text.
- **Common components**: `StatusBadge`, `JsonTree`, `CopyableText`. `CopyableText` falls back
  gracefully in non-secure contexts because `navigator.clipboard` is unavailable over HTTP — a common
  self-hosting scenario. `JsonTree` colors leaf scalars by type, replacing the previous inspect view
  that could only render a full `<pre>` block.
- **Business components**: `ContainerStatusBadge`, `ImageSize`, `PortMappingEditor`, `EnvVarEditor`,
  `VolumeMountEditor`. The three editors support both bulk paste and per-line editing, and their
  parsing logic moved to `utils/container-input.ts` with unit tests. On a parse failure the editors
  report only the valid lines preceding the offending one, so `errorLine` must be propagated through
  `v-model:error-line` to disable submission — otherwise a container gets built with a silently
  missing environment variable.
- **Repository**: finalised `README.md` (badges, screenshot placeholders, documentation matrix,
  `docker run` command, Docker socket mounting risk disclosure, recommended socket-proxy mode) and the
  first complete set of `docs/*.md`. Several **inaccurate claims** were corrected: the previous README
  told users to run `docker compose up` and pull images, but `Dockerfile` / `docker-compose.yml` are
  not delivered until 0.0.15, and `SECURITY.md` / `CONTRIBUTING.md` never existed.

### Changed

- The three input editors are **wired into the pages** rather than left unused:
  `ContainerStatusBadge` is used in the container list and inspect views, `ImageSize` in the image list
  and inspect views (distinguishing exclusive-layer size from logical total size), and `JsonTree`
  replaced the full `<pre>` dump in the image inspect view.
- The mount-type selector was **removed**: the backend `ContainerVolumeMapping` has no `type` field
  (bind mounts versus named volumes are decided by Docker based on whether the source path exists), so
  a dropdown in the UI would only produce a switch that has no effect.
- `ui-guidelines.md`'s shortcut table was synchronised with the actual implementation.

### Security

- Full log-redaction audit across the authentication flows (login / refresh / logout / password
  change / admin bootstrap) came back clean. It did surface one **P1** issue: `HostService` logged the
  raw endpoint at Information level, and an endpoint is a credential carrier
  (`tcp://admin:s3cr3t@host` was accepted and persisted to disk). `HostEndpoint.Redact()` was added as
  a second line of defence.

### Performance

- Performance baseline established: listing 100 containers measures **P50 = 12 ms / P95 = 35 ms**
  (threshold 500 ms). The baseline runs against a loopback HTTP fake daemon rather than a mocked
  `IDockerClient`, so it also covers Docker.DotNet's HTTP round trips and JSON deserialization.

---

## [0.0.13] — Management UI (frontend)

Milestone goal: administrators can manage hosts and users entirely from the UI.

### Added

- **User management** (`views/UsersView.vue`): user list + create dialog + password-reset dialog +
  delete confirmation. The delete button on your own row is **disabled outright** — the backend has
  the same guardrail, but making a user click only to be told they cannot is poor interaction.
- **Host management** (`views/HostsView.vue`): host list with a **three-state** connectivity
  indicator (reachable / unreachable / not probed) + create/edit dialog + delete confirmation +
  enable toggle (disabling requires a second confirmation).
- **Host group management UI**: create / rename / delete groups and reassign hosts to them. The
  backend `host_groups` table and `hosts.group_id` column were delivered with this milestone. Built-in
  group names resolve through i18n; custom group names are shown exactly as the administrator typed
  them.
- **Change-password dialog** (`ChangePasswordDialog.vue`): any user can change their own password
  from the top-bar user menu. On success the **local session is cleared and the user is returned to
  the login page** — the backend already revoked every refresh token on password change, and leaving
  the user looking logged in would kick them out without warning.
- **Route guard enhancements**: menu items are hidden per `route.meta.requiresAdmin` and direct access
  is intercepted (the guard landed in 0.0.7; this milestone adds the real admin pages).

### Changed

- The **test-connectivity button lives inside the form** rather than on the list row, because
  `admin/hosts/test` receives **a configuration**, not a `hostId` — the list rows hold no credential
  contents, so testing from a row cannot produce a real verdict.

---

## [0.0.12] — Image UI (frontend)

Milestone goal: visual image management.

### Added

- **Image list** (`views/ImagesView.vue`): table (repository, tag, image ID, creation time, size,
  reference count) with a delete button. Search runs **server-side** (the backend filters in memory by
  repository name / tag; the frontend no longer filters a second time). The list deliberately **does not
  poll** — images do not drift at second granularity, and rebuilding the table only interrupts reading.
- **Pull-image dialog** (`components/business/PullImageDialog.vue`): a single input field, split into
  repository and tag by `splitImageReference()`. The backend rejects "reference already contains a tag
  plus a separate `tag` argument", and two input fields would force users to understand the backend's
  parameter split first.
- **Image inspect view**: a structured projection (overview / build config / declared ports and
  volumes / labels) rather than a raw JSON tree. The backend inspect endpoint already returns a
  curated projection, so laying the fields out is more readable than wrapping them in another JSON
  tree; the raw JSON is kept as a collapsible appendix.
- Delete semantics are reported faithfully: images referenced by a container require a forced delete
  (`1404`), and the UI distinguishes "only a tag was untagged" from "the image itself was deleted".

---

## [0.0.11] — Container UI (frontend)

Milestone goal: visual container management.

### Added

- **API layer** (`src/api/container.ts`): RPC-style wrappers covering all eight container endpoints.
- **Container status semantics** (`utils/container-status.ts`): status-to-hue mapping (with a separate
  **transitional** tier), port rendering, and byte formatting.
- **`HostSelector`** (`components/business/HostSelector.vue`): a top-bar dropdown showing the current
  host name plus a status dot in host scope, and a "select a host" placeholder in global scope. It
  lists reachable hosts and, after switching, **stays on the current resource page**; the selection is
  written to the `useHostStore` and the router navigates to `/hosts/{newHostId}/{currentResourcePage}`.
  Administrators also get a "manage hosts" entry.
- **Container list** (`views/ContainersView.vue`): table (name, image, status, ports, creation time) +
  action buttons (start / stop / restart / remove) + automatic refresh (polling every **5 seconds**,
  paused while the page is hidden, and failures never interrupt the polling loop).
- **Create-container dialog** (`CreateContainerDialog.vue`): visual creation form (image + name + port
  mappings + environment variables + volume mounts + restart policy + collapsible advanced options).
  Line-oriented text inputs get **pre-submission validation with line-number localisation**, and the
  container can be started immediately after creation.
- **Container logs view**: line-count selection + tail loading + timestamps / stderr toggles
  (auto-scroll is out of scope: the backend has no `follow` support; live streaming is scheduled for
  0.7.0).
- **Container inspect view**: grouped cards (basic info / host config / startup config / mounts /
  networks / environment variables / labels) rather than a JSON tree.
- **`stores/host.ts`**: Pinia store for the current host selection.

---

## [0.0.10] — Image API (backend)

Milestone goal: image CRUD + pull with progress reporting.

### Added

- **Image endpoints**:
  - `POST /api/v1/hosts/{hostId}/images/list` (`all` / `search` / pagination; pagination runs in server
    memory because Docker exposes no paging parameters, `pageSize` is capped at 100 and the clamped
    value is echoed back)
  - `POST /api/v1/hosts/{hostId}/images/pull` (`image` + `tag?` + `platform?`)
  - `POST /api/v1/hosts/{hostId}/images/remove` (`imageId` + `force?`; **`noPrune` is not sent**)
  - `POST /api/v1/hosts/{hostId}/images/inspect` (curated projection)
- Pull failures have two distinct paths: "image does not exist" surfaces as an SDK exception mapped to
  `1401`, while "registry refused / network interrupted" only appears in the progress stream and is
  detected by inspecting it, then mapped to `1402`.

### Notes

- Pull progress is aggregated server-side via `IProgress<JSONMessage>` and returned **once in the
  response body**; progress is also written to the Serilog log. **SignalR live progress is deferred to
  0.7.0.**
- Integration tests cover all four endpoints, including a real pull against the host daemon and the
  "removing only untags the image, the image itself is kept" semantics.

---

## [0.0.9] — Container API (backend)

Milestone goal: every container CRUD operation is reachable through the API.

### Added

- **Container endpoints**:
  - `POST /api/v1/hosts/{hostId}/containers/create` (`CreateContainerParameters` with a **curated field
    set**: image / name / command / entrypoint / environment variables / labels / ports / mounts /
    restart policy / auto-remove / network / hostname / working directory / TTY / stdin)
  - `POST /api/v1/hosts/{hostId}/containers/list` (supports `all` + pagination — server-side, `pageSize`
    capped at 100 with the clamped value echoed back)
  - `POST /api/v1/hosts/{hostId}/containers/inspect` (curated projection: status / startup config /
    host config / mounts / networks)
  - `POST /api/v1/hosts/{hostId}/containers/logs` (`tail` / `since` / `timestamps` / `includeStderr`;
    **`follow` is not supported** — live streaming is scheduled for 0.7.0)
  - `start` / `stop` / `restart` / `remove` — the first three are **idempotent** (the daemon's `304` is
    treated as success); `remove` without `force` against a running container returns `1404` / 409
- All endpoints obtain the client through `IDockerHostAccessor` and perform host-scoped visibility
  checks via `CurrentHostMiddleware`.
- `Docker.DotNet.Enhanced` exceptions are mapped uniformly (`DockerErrorTranslator`):
  `DockerApiException` → generic Docker error (1402), `DockerContainerNotFoundException` /
  `DockerImageNotFoundException` → `1401` (resource not found), `TaskCanceledException` → `1403`
  (operation timed out). `409` → `1404` was added in this milestone.

### Notes

- Covered by 11 query + 19 mutation integration tests, including a full
  create → start → stop → remove → 1401 lifecycle running against a real daemon.

---

## [0.0.8] — Docker Host Abstraction

Milestone goal: register multiple Docker hosts and switch the target host per operation.

### Added

- **`hosts` table** (id, name, type=socket|tcp, endpoint, auth_kind, auth_config_json, enabled, audit
  columns). `name` is case-insensitively unique (partial index `WHERE is_deleted = 0`) and is
  **immutable** — it is the externally visible host identifier.
- On startup, when the `hosts` table is empty, a default host is seeded: `name="local"`,
  `type=socket`, `endpoint="/var/run/docker.sock"`.
- **`IDockerClientPool`** (Singleton): pools clients by **normalised connection string**, so hosts
  sharing an endpoint share a connection. Idle clients are reaped (`ReapIdleClients` plus a background
  task), entries can be invalidated, and everything is disposed on process exit. A failed build always
  clears its entry (`Lazy<T>` caches exceptions permanently, which would otherwise make a retry
  impossible).
- **`IDockerHostAccessor`** (Scoped): maps `hostId` (the host name) → `hosts` row → client;
  `Enabled=false` → error `1302`. Connectivity testing uses a temporary client plus Ping and **does not
  go through the pool**.
- **Current-host middleware**: an Api middleware resolves the current host from the **route value**
  `hostId` into the scoped `ICurrentHost`. Paths look like `/api/v1/hosts/{hostId}/...` with **no
  default value**, so an unknown host becomes `1300` / 404 before reaching a controller and host-scoped
  endpoints need no per-endpoint existence checks.
- **Resource visibility**: `IResourcePermissionService.IsHostVisible` (administrators see everything,
  everyone else fails closed with an empty list until the grant tables land). Per-resource grant tables
  landed in `0.1.0` (host scope) with the container scope at `0.12.0`.
- **`IDockerClientFactory`** (stateless): creates clients with `DockerClientBuilder` only — socket via
  `WithEndpoint(new Uri("unix:///var/run/docker.sock"))`, plain TCP via `tcp://host:2375`, TCP + TLS via
  an `https://` endpoint (default port 2376) with a client certificate (all three of CA, certificate,
  and key are mandatory — omitting the CA is equivalent to accepting any server certificate), and
  TCP + Basic via `BasicAuthCredentials(user, pass, tlsEnabled)`, where a plaintext endpoint logs a
  warning but is not blocked. `Infrastructure` additionally references
  `Docker.DotNet.Enhanced.X509` and `Docker.DotNet.Enhanced.BasicAuth`.
- **Host management API** (admin only): `POST /api/v1/admin/hosts/list` (parallel liveness probing) /
  `create` / `{hostId}/update` / `{hostId}/delete` / `test`. New error code `1303` (host name already
  taken). Updates invalidate the client pool using the **pre-update** connection description.
- **Host overview API** (any authenticated user): `POST /api/v1/hosts/list` (hosts visible to the
  current user + connectivity status) and `POST /api/v1/hosts/{hostId}/overview` (version / resource
  counts / uptime; an offline host returns `status=offline` rather than 502).
- **User management API** (admin only): `POST /api/v1/admin/users/list` (paginated) / `create` /
  `delete` / `update-password`. Self-service password changes require the current password while an
  administrator resetting someone else's does not; both password changes and account deletion revoke
  all of that user's tokens.
- **`DockerFactAttribute`** probes the host's `/var/run/docker.sock` and skips the test when it is
  unreachable. **Testcontainers is deliberately not used** — the system under test is the host's Docker
  daemon itself, not a third-party service that needs containerising.

### Changed

- **Hosts moved from configuration into the database**: the `Docker:Hosts` configuration section was
  removed and `Docker:ClientCache` was renamed to `Docker:Pool`.

### Notes

- From this milestone onward, **every new API endpoint requires integration tests**; CI enforces it
  (both host and user APIs ship with tests). No line-coverage threshold is set.
- The management UI for hosts shipped later, in 0.0.13.

---

## [0.0.7] — Frontend Login Page and Base Layout

Milestone goal: a visual login experience, a global navigation skeleton, and the UnoCSS / theme /
shortcut foundations.

### Added

- **UnoCSS**: `unocss` + `presetUno` + `presetTypography` + `presetIcons` (Iconify on demand).
- **Theme system** (`composables/useTheme.ts`): light / dark / follow-system, plus blue / green / purple
  accent colors.
- **Login page** (`LoginView.vue`): centred card + Element Plus form + theme toggle. It calls the real
  `POST /api/v1/auth/login`, and failure messages are mapped from **error codes** to i18n keys (backend
  Chinese messages are never surfaced).
- **Main layout** (`layouts/AppLayout.vue`): 64 px top bar + a collapsible 200 px sidebar + content area;
  the sidebar becomes a drawer on mobile.
  - **Two-scope information architecture**: global scope (`meta.scope='global'`: overview / global
    settings / host management / user management) and host scope (`meta.scope='host'`:
    `/hosts/:hostId/{dashboard,containers,images,volumes,networks}`) use **different sidebar menus**.
  - **Host scope lives in the URL** (`/hosts/:hostId/**`), so views are deep-linkable and shareable. A
    route guard syncs `hostId` into `useHostStore` and redirects unknown host IDs to `/overview`.
  - Host scope also enters API paths (`/api/v1/hosts/{hostId}/...`); global-scope endpoints omit that
    segment.
  - The sidebar menu is **derived automatically from the route table** (filtered by `meta.scope` /
    `showInSidebar` / `requiresAdmin`), so adding a route requires no layout change.
- **Overview page** (`OverviewView.vue`): the post-login landing page. It lists every visible host as a
  **long row** (name / type / endpoint / version and uptime / four resource counts / status + inline
  actions) plus a total-and-online summary; **the entire row is clickable** to enter host scope. Hosts
  are grouped by `order` (production / staging / local / ungrouped) and empty groups are not rendered.
  Inline actions (edit for admins, view details, status chart) use `@click.stop` so they do not trigger
  row navigation.
- **Host status page** (`HostStatusView.vue` + `HostStatusPanel.vue`): runtime status (online/offline),
  uptime, four resource counts and CPU/memory placeholders (real data arrives with SignalR in 0.7.0).
  The overview card's "details / status chart" buttons **reuse the same component**.
- **Top-bar components**: `AppBreadcrumb.vue` (hierarchy derived by the pure function
  `utils/breadcrumbs.ts`, covered by unit tests) and `UserMenu.vue` (username / role tag / logout).
- **Sidebar components**: `AppSidebar.vue` (desktop sidebar + mobile drawer, responsible only for
  container and breakpoints) and `SidebarNav.vue` (menu contents, reused in both places).
- **Internationalisation**: vue-i18n with `locales/zh-CN.json` / `locales/en-US.json`; dotted key
  naming, missing keys fall back to English.
- Route guards for unauthenticated redirects to `/login` and for `requiresAdmin` interception.

### Changed

- The top bar and sidebar were extracted out of `AppLayout.vue` into dedicated components, and
  breadcrumbs were added.
- **`useHostStore` is not persisted**: hosts and groups come from the backend, so any cached snapshot
  is necessarily stale; `currentId` is driven by the URL and needs no localStorage (the legacy
  persistence key is cleared once at startup).
- **Semantic colour tokens consolidated**: `main.css` gained surface / text / status / elevation token
  families, mapped in `uno.config.ts` as `surface-*` / `content-*` / `line-*` / `status-*`. Components no
  longer write `gray-500` or `bg-white dark:bg-gray-800` light/dark pairs — the hardcoded
  `dark:bg-gray-800` in `app-card` had already drifted away from the existing `--surface-card`.
- **Missing-data states redesigned**: the host landing page previously rendered nothing at all when data
  was missing, leaving four bare "—", which looked like a load failure. It now always shows an info
  card with a de-emphasised placeholder, a reason, and an offline banner.
- **Overview and status pages have distinct responsibilities**: they were previously word-for-word
  duplicates; overview now carries identity and technical information, status carries live metrics and
  events.
- The host sidebar menu order is "status → containers → images → volumes → networks → orchestration →
  events", aligned with the delivery sequence (orchestration depends on containers and images and is
  scheduled for 0.5.0).
- Placeholder pages became **feature announcements** (`PlaceholderSection`): icon + version badge +
  capability list + scheduling rationale, instead of a blank card with one sentence.
- Cleanups: removed a duplicate "details" button (bound to the same function as "status chart") and the
  dead `HostStatusPanel.vue`; the sidebar host identity card became a group heading (the host name went
  from four occurrences down to two); group descriptions moved from hardcoded Chinese to i18n.

---

## [0.0.5] — Authentication Completion and Frontend Foundations

Milestone goal: a complete authentication loop and a frontend that can talk to the backend.

### Added

- **`POST /api/v1/auth/refresh`**: rotates the refresh token (the old one is invalidated immediately and
  the token family is marked via `replaced_by_token_hash`).
- **`POST /api/v1/auth/logout`**: revokes the current refresh token, reusing the `revoked_at` column in
  the token table rather than adding a separate blacklist table.
- **Security response headers** middleware: `X-Content-Type-Options=nosniff`, `X-Frame-Options=DENY`,
  `Referrer-Policy=no-referrer`. These are registered through `Response.OnStarting` because the
  exception-handling middleware's `Response.Clear()` wipes directly assigned headers.
- **Password policy** utility (≥10 characters, containing letters plus digits or symbols), shared by
  login and the initial-administrator bootstrap.
- **Frontend workspace** `src/Whalefleet.UI/`: a Vite + Vue 3 + TypeScript + Element Plus skeleton,
  with `/api` and `/hubs` proxied to `http://localhost:8080` in `vite.config.ts`.
- **Pinia `useAuthStore`** (user, accessToken, refreshToken persisted to localStorage).
- **Axios instance with interceptors**: injects `Authorization`, and on `401` silently refreshes once
  and replays the original request. **Concurrent 401s trigger only one refresh** — refresh tokens are
  single-use, so refreshing more than once is judged a replay by the server.
- **Vue Router global guard**: redirects unauthenticated users to `/login`, plus `requiresAdmin`
  interception and `hostId` synchronisation.
- Login page wired to the real `POST /api/v1/auth/login`, with failure text mapped from **error codes**
  to i18n.

### Fixed

- Integration-test hosts switched to **non-pooled connections** (`Persistence:Pooling=false`). The
  previous cleanup called `SqliteConnection.ClearAllPools()`, which is a process-wide operation that
  releases connection handles other tests are using concurrently — observed as flaky failures at roughly
  a 1-in-14 rate.

### Notes

- **Rotation semantics** (full table in [docs/api-spec.md](docs/api-spec.md)): presenting an
  already-rotated refresh token is judged a replay and revokes **all** of that user's refresh tokens;
  presenting a token after logout is only rejected, without affecting other sessions.

---

## [0.0.4] — Authentication API (core)

Milestone goal: obtain a JWT through the API.

### Added

- **JWT access token** (HS256, 15 minutes) + **refresh token** (random 32-byte base64url, 7 days).
  - Claims `sub` / `unique_name` / `role` / `jti` are centrally defined in
    `Application/Abstractions/JwtClaimNames`, because the issuer lives in `Infrastructure` and the
    validator in `Api` — the claim names need a single shared definition.
  - The secret comes from `Jwt:Secret`; **a missing secret or one shorter than 32 bytes fails at
    startup** (`ValidateOnStart`).
  - What is persisted is the **SHA-256 hash** of the refresh token (base64url, part of the persistence
    contract); the plaintext is returned to the client only at the moment of issuance.
- **`POST /api/v1/auth/login`**: validates username/password and returns access + refresh tokens.
  - **Two layers of anti-enumeration**: an unknown username and a wrong password produce **exactly the
    same error code and message**, and an unknown user still triggers an equivalent-cost **dummy
    verification** so the response latency is indistinguishable.
  - **Failure counting**: 5 attempts per minute per IP + username → 429; the window starts at the first
    failure and is cleared on a successful login.
- **`POST /api/v1/auth/me`**: requires authentication; returns the current user summary so the frontend
  can validate the persisted token on page refresh.
- **`[Authorize]` middleware** with role checks via `[Authorize(Roles = "admin")]`. The Api-layer
  `ICurrentUser` implementation replaces the Infrastructure placeholder via `services.Replace`.
  Authentication failures also return the unified `ApiResult`, distinguishing `1100` (not
  authenticated), `1101` (**expired**), and `1500` (forbidden).
- **Initial administrator bootstrap**, pulled forward from the deployment milestone because of the
  chicken-and-egg problem.

### Notes

- Covered by integration tests: successful login, wrong credentials, indistinguishable enumeration,
  rate-limit lockout and window expiry, and token expiry / tampering / wrong signing key / malformed
  format.
- **Known gap (deferred to 0.0.15)**: behind a reverse proxy `Connection.RemoteIpAddress` is the proxy's
  IP, and `ForwardedHeaders` is not yet enabled (it requires a trusted-proxy allowlist) — without it,
  every user's login failures land in the same rate-limit bucket.

---

## [0.0.3] — Data Layer and Password Security

Milestone goal: the user table can be created and passwords can be hashed and verified.

### Added

- **SQLite initialisation** (`/app/data/whalefleet.db`, directory created when missing) with
  foreign keys and `busy_timeout` enabled, and Dapper wired in with repository abstractions
  (`IUserRepository`, plus `IHostRepository` / `IRoleRepository` / `IRefreshTokenRepository`).
- **Schema migrations `0001`–`0004`** applied at startup, driven by `PRAGMA user_version` with embedded
  scripts; versions must be contiguous from 1 or startup fails.
- **`users` table** (id, username, password_hash, role_id, audit columns). Column names follow the
  "property name ↔ column name, differing only in casing convention" auditing rule
  (`creation_time`, `creator_id`, `last_modification_time`, `last_modifier_id`, `is_deleted`,
  `deletion_time`, `deleter_id`).
- **Dapper materialises rich entities directly**: an entity only needs a **non-public parameterless
  constructor** — **no Row DTO and no `InternalsVisibleTo`** — and `MatchNamesWithUnderscores` must be
  enabled for `creation_time` to map onto `CreationTime`.
- **`RepositoryBase`** fills audit fields on insert and update by calling `MarkCreated` /
  `MarkModified`, sourcing values from `TimeProvider` and `ICurrentUser`.
- **Entity auditing and soft delete**: `Core/Common/Auditing/` provides nine read-only interfaces and
  three base classes (`CreationAuditedEntity` → `AuditedEntity` → `FullAuditedEntity`; there is
  deliberately **no** `SoftDeleteEntity`). Audit properties use `internal set` with `public`
  `MarkCreated` / `MarkModified` / `Delete` / `Restore` writers, timestamps are always **UTC** and always
  **passed in explicitly** (entities never read the clock themselves), and `TUserKey` carries **no type
  constraint** so `Guid`, `long`, and `string` all work — "no user" is the `default(TUserKey)` sentinel.
- **Roles became data**: the `UserRole` enum was **removed** and replaced by a `roles` table plus
  `IRoleRepository` (`users.role_id` foreign key), with four built-in roles (admin / operator / viewer /
  auditor, the last being the default). At most one role may be the default, enforced by the partial
  unique index `ux_roles_default` **in the data layer rather than in code**. Built-in roles cannot be
  deleted, guaranteed twice over (the entity's `Retire()` throws `InvalidOperationException`, and the
  repository filters on `WHERE is_built_in = 0`).
- **`ICurrentUser`** abstraction in `Application/Abstractions/`, with a placeholder implementation that
  the Api layer replaces once authentication lands.
- **Argon2id password hashing** via `Konscious.Security.Cryptography.Argon2` (MIT-licensed); plaintext
  passwords must never be persisted or logged.
  - The PHC string (`$argon2id$v=19$m=…,t=…,p=…$salt$hash`) is encoded and decoded by the
    `Core/Auth/PasswordHash` value object, and algorithm parameters travel with the hash — so tuning
    them later never invalidates existing hashes.
  - `IPasswordHasher` (in `Application/Abstractions`) abstracts it; `Argon2PasswordHasher`
    (in `Infrastructure/Security`) implements it. Digest comparisons always go through
    `CryptographicOperations.FixedTimeEquals`.
  - Parameters come from `Security:Argon2` and out-of-range values **fail at startup**
    (`ValidateOnStart`), reusing `PasswordHash`'s own validation rules so the two cannot drift.

### Notes

- Design details are documented in [docs/architecture.md](docs/architecture.md) under "Entity Auditing
  and Soft Delete" and "Persistence Entity Materialization (Dapper)".
- `Whalefleet.Core` still has **no NuGet dependencies at all** (dependency-direction guard).

---

## [0.0.2] — Backend Infrastructure

Milestone goal: the shared baseline for every endpoint that follows.

### Added

- **Unified response model**: `ApiResult` (no data) and `ApiResult<T>` (with data), plus static factory
  methods in a non-generic `ApiResults` class. Failure responses **always include an explicit
  `"data": null`** — `WhenWritingNull` must stay off.
- **Layered error codes**: constants, semantic categories, and default messages live in
  `Application/Common` (`ErrorCodes` / `ErrorCategory` / `ErrorCodeCatalog`); the mapping from semantic
  category to HTTP status lives in `Api/Errors` (`ErrorHttpStatus`). This avoids making the application
  layer depend on web concepts while keeping both sides unit-testable.
- **Global exception handling middleware**: `BusinessException` carries its own code;
  `DomainException` / `ArgumentException` / `JsonException` / `BadHttpRequestException` map to 1000/400,
  `KeyNotFoundException` to 1001/404, `TaskCanceledException` to 1003/408, and anything else falls back
  to 9999/500. The middleware returns a bool from `HandleExceptionAsync` so that `throw;` stays inside
  the `catch` block.
- **Middleware pipeline order** (do not reorder): `ExceptionHandlingMiddleware` →
  `UseSerilogRequestLogging` (downgraded to Debug when a request threw) → `UseStatusCodePages` →
  `UseCors` → endpoints.
- **CORS allowlist policy**: registered lazily through `IConfigureOptions<CorsOptions>` — registering it
  during DI setup would read a not-yet-materialised value and silently apply no policy at all. By
  default the allowlist is empty, which means **no `Access-Control-*` headers are emitted at all**, i.e.
  cross-origin requests are fully rejected; a malformed origin (an absolute `http`/`https` URI with a
  trailing slash) fails at startup.
- **JSON encoding policy**: `ApiJsonDefaults.Apply(options)` is the single entry point shared by MVC and
  the error-response writer so the two cannot drift. The encoder is
  `JavaScriptEncoder.Create(UnicodeRanges.All)`, so Chinese text is emitted as-is instead of `\uXXXX`
  escapes.
- **Serilog**: console plus rolling file output; production logs go to `/app/data/logs/app-.log` and
  development logs to `logs/app-.log` (daily rolling, 14 files retained, UTF-8 without BOM). 5xx logs
  at Error with a full stack trace; 4xx logs at Warning **without** a stack trace.
- **Configuration**: the hardcoded `UseUrls("http://+:8080")` became the `Urls` key in
  `appsettings.json` so `TestServer` can take over listening. Note that `Urls` takes **precedence over**
  `ASPNETCORE_URLS`; change the port with `--urls` or `URLS`.
- **Integration test carrier**: `tests/Whalefleet.IntegrationTests/` with `WebApplicationFactory`
  landed early (originally scheduled for 0.0.8) to verify HTTP-pipeline-level contracts.
- **Environment variable naming**: `__` separates configuration levels and numeric indices address
  arrays; key comparison is case-insensitive, and new keys are written in ALL_CAPS style.
- **Dependency upgrades**: `Microsoft.Extensions.*` → 10.0.12; the Serilog stack →
  AspNetCore 10.0.0 / Settings.Configuration 10.0.1 / Sinks.File 7.0.0 / Sinks.Console 6.1.1; the test
  stack → Microsoft.NET.Test.Sdk 18.10.0 / xunit 2.9.3 / xunit.runner.visualstudio 4.0.0 /
  coverlet.collector 10.0.1.
- **`.editorconfig` cleanup**: unified LF (consistent with `.gitattributes`' `* text=auto`), unified final
  newlines, removed six duplicated keys, and declared LF explicitly for `[*.sh]` to protect
  `scripts/backup.sh`.

### Changed

- **`Docker.DotNet.Enhanced` 3.x → 4.3.3 (breaking)**: 4.x removed `DockerClientConfiguration` and
  `IDockerClient`; `DockerClientFactory` now uses `DockerClientBuilder.WithEndpoint(...).Build()`. A new
  `DockerClientFactoryTests` upgrades the guarantee from "it compiles" to "a usable client can be
  constructed".

### Notes

- **Exception — `FluentAssertions` stays on 7.2.2**: 8.x moved to a commercial Xceed license and 7.x is
  the last Apache-2.0 release. The project is MIT-licensed and does not want a commercial dependency.

---

## [0.0.1] — Project Skeleton and CI Skeleton

Milestone goal: a minimal backend that builds, runs, and has a first-cut CI pipeline.

### Added

- **Four layered projects** (`Whalefleet.slnx`):
  - `src/Whalefleet.Core/` — business entities, value objects, and enums. **BCL only, no NuGet
    packages at all**.
  - `src/Whalefleet.Application/` — use-case orchestration, DTOs, and abstractions over external
    resources (e.g. `IDockerClientFactory`). Depends on Core.
  - `src/Whalefleet.Infrastructure/` — third-party SDK adapters (`Docker.DotNet.Enhanced`), repository
    implementations, and external service integrations. Depends on Core + Application.
  - `src/Whalefleet.Api/` — the ASP.NET Core 10 host, hosting Controllers / SignalR hubs / the static
    frontend. Depends on Application + Infrastructure.
  - `tests/Whalefleet.UnitTests/` and `tests/Whalefleet.IntegrationTests/`.
- **Central Package Management** enabled in `Directory.Packages.props`, so every NuGet version number
  lives in one file.
- **External-resource interfaces live in the application layer**, not the domain layer — for example
  `IDockerClientFactory` is defined in `Application/Abstractions`.
- **`Directory.Build.props`**: `TargetFramework=net10.0`, `LangVersion=latest` (C# 14), nullable
  reference types enabled, implicit usings enabled, and **`TreatWarningsAsErrors=true`** — CI requires
  zero warnings.
- **`GET /healthz`** returning 200 OK. This one endpoint uses Controller style, deliberately contrasting
  with the RPC-style `POST /api/v1/...` endpoints that follow. Its success response is **bare JSON, not
  wrapped in `ApiResult`**.
- **Serilog** console output (JSON configuration via `ReadFrom.Configuration`).

### Changed

- The original three-project split (Api / Core / Infrastructure) was revised to four layers after
  review.

### Notes

- **CI**: `.github/workflows/ci.yml` runs on pushes and pull requests targeting `main` / `dev`, and its
  steps are `dotnet restore` → `dotnet build -c Release` (warn-as-error) → `dotnet test -c Release`, with
  NuGet caching keyed on `*.csproj` and `Directory.Packages.props`.
- **DDD tactical patterns were later removed** (0.0.2): the `Domain` project was renamed to `Core`, and
  aggregate roots, domain events, and `DomainException` were dropped while the layering and the rich
  model were kept. See [docs/architecture.md](docs/architecture.md).
- Note that `0.0.6` is an **unused number** — see the note at the top of this file.

[Unreleased]: https://github.com/blue-cloud-net/whalefleet/compare/v0.0.14...HEAD
