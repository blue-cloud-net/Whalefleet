# ============================================================================
# Whalefleet — 多阶段构建
# ============================================================================
# 三个阶段，各自只带必要的东西：
#
#   ui      : Node + pnpm  →  src/Whalefleet.UI/dist
#   publish : .NET SDK     →  只含运行期所需的 Api 程序集
#   runtime : .NET runtime →  最终镜像（无 SDK、无 Node、无源码）
#
# ## 为什么必须分这几个阶段
# 把三个 SDK 装进一个镜像会让它膨胀到 1 GB 以上，而其中绝大部分是
# 永远用不到的编译期资产（.NET SDK 约 700 MB、Node 约 400 MB）。
# 分离之后 runtime 约 250 MB，且**不含源码**——
# 直挂 socket 的部署形态下，容器内多一份源码就多一份攻击面。
#
# ## 为什么 ui 与 publish 不合并成一个阶段
# .NET 镜像与 Node 镜像的体积都很大，合并后无法在后续阶段里只保留其中一个，
# 最终镜像就得同时携带两个 SDK 的残留。分成两阶段才能真正「只留产物」。
#
# ## 构建
#   docker build -t whalefleet:local .
#
# ## 注意：前端产物由 publish 阶段复制进 wwwroot
# Api 进程同源托管前端（见 SpaOptions），因此 dist 必须出现在**发布目录内**，
# 而不是作为独立的一层——否则 runtime 阶段无法把它放进应用目录。
# ============================================================================

# ---------- 参数 ----------
ARG DOTNET_SDK_VERSION=10.0
ARG DOTNET_RUNTIME_VERSION=10.0
ARG NODE_VERSION=22-alpine

# ---------- 阶段 1：构建前端 ----------
FROM node:${NODE_VERSION} AS ui
WORKDIR /ui

# pnpm 的版本来自 packageManager 字段，用 corepack 激活可避免「本机装的
# pnpm 版本」与「锁文件期望的版本」不一致导致的解析漂移。
COPY src/Whalefleet.UI/package.json src/Whalefleet.UI/pnpm-lock.yaml src/Whalefleet.UI/pnpm-workspace.yaml ./
RUN corepack enable && pnpm install --frozen-lockfile

# 先拷贝其余源码再构建：package.json 很少变，这样绝大多数构建都能命中
# 「依赖安装」层的缓存——否则每次改一个 .vue 都要重装几百 MB 的依赖。
COPY src/Whalefleet.UI/ ./

# ⚠️ 这一步失败必须让构建失败（不要 `|| true`）：
# 产物体积检查只能挡住「dist 不存在」，挡不住「dist 存在但不完整」。
RUN pnpm build \
    && test -f dist/index.html \
    && test -d dist/assets

# ---------- 阶段 2：发布后端 ----------
FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_SDK_VERSION} AS publish
WORKDIR /src

# 先只拷贝还原所需的文件，让 NuGet 还原单独成层——
# 改一个 .cs 不会触发重新还原。
COPY Directory.Build.props Directory.Build.targets Directory.Packages.props ./
COPY src/Whalefleet.Core/Whalefleet.Core.csproj src/Whalefleet.Core/
COPY src/Whalefleet.Application/Whalefleet.Application.csproj src/Whalefleet.Application/
COPY src/Whalefleet.Infrastructure/Whalefleet.Infrastructure.csproj src/Whalefleet.Infrastructure/
COPY src/Whalefleet.Api/Whalefleet.Api.csproj src/Whalefleet.Api/
COPY tests/Whalefleet.UnitTests/Whalefleet.UnitTests.csproj tests/Whalefleet.UnitTests/
COPY tests/Whalefleet.IntegrationTests/Whalefleet.IntegrationTests.csproj tests/Whalefleet.IntegrationTests/
RUN dotnet restore src/Whalefleet.Api/Whalefleet.Api.csproj

COPY . .
RUN dotnet publish src/Whalefleet.Api/Whalefleet.Api.csproj \
    -c Release \
    -o /app/publish \
    --no-restore \
    /p:UseAppHost=false

# 前端产物就位。放在 publish 阶段而非 runtime 阶段，
# 是为了让 runtime 无需再拷贝源码目录（那会重新把源码带进最终镜像）。
COPY --from=ui /ui/dist /app/publish/wwwroot

# 备份脚本随镜像发布：README 让人在容器内跑它，而它必须用 `.backup`
# （在线快照）——cp 出来的可能是一份撕裂的文件，事后才发现就晚了。
# 只拷这一个脚本而非整个 scripts/ 目录：其余是面向宿主机运维的。
COPY scripts/backup.sh /app/publish/scripts/backup.sh
RUN chmod +x /app/publish/scripts/backup.sh

# ---------- 阶段 3：运行时 ----------
FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_RUNTIME_VERSION} AS runtime

# ⚠️ .NET 8+ 的 aspnet 基础镜像**自带** app 用户，但 UID 是 1654。
# 这里改写而不是 groupadd 新建：groupadd --gid 10001 app 会直接失败
# （group 'app' already exists），把镜像构建卡死。
#
# 坚持写死 10001 而不是用它的 1654，是因为挂载宿主机目录时
# 生效的是**数字** uid：README 里的 `chown 10001:10001` 必须与镜像一致。
# 而 .NET 官方随时可能改这个值（历史上就改过）——写死在我们这边才稳定。
RUN groupmod --gid 10001 app \
    && usermod --uid 10001 --gid 10001 app

WORKDIR /app
COPY --from=publish --chown=app:app /app/publish ./

# 探针要发 HTTP 请求，而 aspnet 基础镜像默认**不含** curl / wget。
# 只为健康检查装 curl 是划算的：不装的话 HEALTHCHECK 只能退化成
# 「检查进程还在」——而那恰恰挡不住 0.0.15 要防的场景
# （进程活着，但 SQLite 打不开，每个接口都在 500）。
#
# sqlite3 同理：README 让人在容器内跑 scripts/backup.sh，而该脚本
# 必须用 `.backup`（在线快照）——cp 出来的可能是一份撕裂的文件。
# 缺了它，文档里那条命令只会得到「未找到 sqlite3」。
RUN apt-get update \
    && apt-get install --yes --no-install-recommends curl sqlite3 \
    && rm -rf /var/lib/apt/lists/*

# data 是唯一需要可写的目录：SQLite 文件、WAL 与滚动日志都在这里。
# 目录必须**在镜像内就存在并属于 app**——否则运行时才 chown 会失败，
# 而以只读根文件系统启动时根本没有机会补救。
RUN mkdir -p /app/data/logs && chown -R app:app /app/data

ENV ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_NOLOGO=true \
    DOTNET_CLI_TELEMETRY_OPTOUT=true

USER app

EXPOSE 8080

# HEALTHCHECK 用 /healthz（0.0.15 起它会真的探测 SQLite 连通性，失败返回 503）。
# start-period 给足时间：首次启动要跑迁移 + 可能要做管理员引导，
# 这期间探针必然失败，start-period 正是用来忽略这段窗口的。
HEALTHCHECK --interval=30s --timeout=5s --start-period=40s --retries=3 \
    CMD curl --fail --silent --show-error --output /dev/null http://localhost:8080/healthz || exit 1

# 用 exec 形式：shell 形式会让 dockerd 把 PID 1 变成 sh，
# 于是 SIGTERM 到不了 .NET 进程，容器停止要等满 10 秒超时才被 SIGKILL。
ENTRYPOINT ["dotnet", "Whalefleet.Api.dll"]