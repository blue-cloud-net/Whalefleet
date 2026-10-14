# Contributing to Whalefleet

Thanks for your interest. This document is the short version; the authoritative rules live in
[`docs/constraints.md`](docs/constraints.md).

## Before you start

- **Small PRs are much easier to review.** One concern per PR.
- **Open an issue first** for anything beyond a bug fix. It costs you five minutes and saves
  both of us an afternoon of work on a change that was never wanted.
- **Do not open a public issue for security vulnerabilities** — see [SECURITY.md](SECURITY.md).

## Development setup

Requires **.NET 10 SDK**, **Node.js 20+** and **pnpm**.

```bash
git clone https://github.com/blue-cloud-net/whalefleet
cd whalefleet

# Backend
dotnet restore
dotnet build -c Release

# Frontend (separate terminal)
cd src/Whalefleet.UI
pnpm install
pnpm dev:mock        # fake data, no backend or Docker needed
# or
pnpm dev             # proxies /api to localhost:8080
```

> `appsettings.Development.json` already provides a JWT secret and a local database path, so
> the API starts with no configuration on your machine.

## Checks you must pass

Both are enforced by CI; running them locally first avoids a wasted round trip.

```bash
# Backend — warnings are errors (Directory.Build.props sets TreatWarningsAsErrors)
dotnet build -c Release --nologo
dotnet test  -c Release

# Frontend
cd src/Whalefleet.UI
pnpm typecheck    # vue-tsc --noEmit
pnpm lint         # eslint . --max-warnings 0
pnpm test         # vitest run
pnpm build        # typecheck + production build
```

### Warnings are errors

`TreatWarningsAsErrors=true`. This catches typos in argument names, nullable misuse and
deprecated API usage at compile time — worth the strictness.

`CA*` analyzer warnings are **not** fatal (`CodeAnalysisTreatWarningsAsErrors=false`), but
the goal is zero of those too.

> **Incremental builds only report warnings for files they recompiled.** After changing the
> Api project, a build may appear to show only that project's warnings. Use
> `dotnet build -c Release -t:Rebuild` to see everything.

### Frontend styles

Changing a theme variable can **silently** break every utility class built on it — no error,
just classes that vanish from the output. Always run `pnpm build` and grep the generated CSS
before concluding a change worked.

## Commit messages

[Conventional Commits](https://www.conventionalcommits.org/). Release Please parses these to
generate the changelog, so the type and scope are not cosmetic.

**Format:** `type(scope): 正文（中文）`

**Allowed types:** `feat` / `fix` / `docs` / `style` / `refactor` / `perf` / `test` / `build` /
`ci` / `chore`

**Scopes** are lowercase module names: `core` / `application` / `infra` / `api` / `ui` / `docs` /
`build` / `ci` / `scaffold`

**Rules for the subject:**

- Chinese, ≤ 30 characters
- one scope only — no comma-separated lists
- no `+` / `/` / `§x` / `Phase N` work-item markers

Breaking changes use `feat!:` / `fix!:`.

```
feat(infra): 新增主机分组端点
fix(auth): 刷新令牌重放时撤销该用户全部会话
docs: 补记镜像详情与主机分组的契约
```

## Pull requests

- Branch from `dev`. `main` is stable releases only.
- PR **title** follows Conventional Commits — it becomes the main commit after squash merge.
- One PR = one change point. Do not mix a refactor with a feature.
- The description should say **why**, not restate the diff. If you fixed a bug, include the
  failing symptom so a reviewer can tell whether the fix addresses the real problem.

**A merge must leave the tree buildable.** This matters most for changes that touch several
layers in sequence: if a commit references a type that a later commit in the same PR deletes,
the intermediate state does not compile and the squash cannot be verified. When in doubt,
land separate PRs.

### Required tests

- **Every new API endpoint needs an integration test.** CI enforces this.
- Use `ApiFactory` (`WebApplicationFactory`) so the test exercises the real pipeline —
  middleware order and DI are where most bugs hide.
- `docker compose`-free: integration tests use the host's `/var/run/docker.sock` directly.
  Tests needing Docker are marked with `DockerFactAttribute`, which skips cleanly when the
  socket is unreachable.

## Architecture you should know before writing code

Dependency direction — arrows point in the direction of reference:

```
Core ← Application ← Infrastructure ← Api
Core ← Infrastructure
Core / Application ← both test projects
```

- `Whalefleet.Core` is **BCL only, zero NuGet**. Adding a package reference there breaks
  the build by design.
- **`Whalefleet.UnitTests` must not reference Api or Infrastructure.** If you need to test
  Api-internal code, put the test in `IntegrationTests`.
- **Entities are never returned directly as HTTP responses.** Map to DTOs in the application
  service.
- **Rich domain model, not anemic**: behaviour and invariants live on the entity
  (private constructor + static factory is the recommended shape). Invalid arguments throw
  `ArgumentException`; an operation that fails for business reasons throws `BusinessException`.

### Sync points that are easy to miss

Adding an error code means updating **five** places. The guard tests will fail if you miss one:

1. `Application/Common/ErrorCodes.cs`
2. `Application/Common/ErrorCodeCatalog.cs`
3. `Api/Errors/ErrorHttpStatus.cs`
4. the guard tests (`UnitTests/Common/ErrorCodesTests.cs`,
   `IntegrationTests/Errors/ErrorHttpStatusTests.cs`)
5. `docs/api-spec.md`

### Document language

| File | Language |
|------|----------|
| `README.md` | English |
| `README.zh-CN.md` | Chinese, section-for-section identical to the above |
| `CHANGELOG.md` | English (auto-generated from 0.0.15) |
| `CHANGELOG.zh-CN.md` | Chinese mirror, **updated by hand** |
| `docs/*.md` | Chinese only |

> If you change one side, change the other. README badge blocks, section counts and link sets
> should correspond one-to-one; the two changelogs must have identical version sequences and
> entry counts.

## Code of conduct

Participation is governed by [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md).