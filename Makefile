# =============================================
# Whalefleet — 统一入口（开发 / 构建 / 部署）
#
# 常用：
#   make dev            本机并行跑 dotnet watch(后端热重载) + pnpm dev(前端 HMR)
#   make dev-up         容器化开发环境（dotnet watch + Vite 全在容器内）
#   make docker-up      一键生产部署（自动准备 .env → 构建 → 启动 → 健康检查）
#   make help           查看全部目标
#
# ## 与仓库其它约定的关系（改之前先读）
#   · 前端在 src/Whalefleet.UI/（pnpm），后端在 src/Whalefleet.Api/（dotnet）。
#     命令都在**各自的目录**里跑——前端的 tsconfig / vite 配置以该目录为根。
#   · **部署文件全在 deployments/**：.env / .env.example / Dockerfile /
#     docker-compose.yml / docker-compose.dev.yml。
#     compose 以 deployments/ 为项目根（相对路径据此解析），但**构建上下文仍是仓库根**
#     ——Dockerfile 要拷 src/ 与 tests/，所以构建必须写成
#     `docker build -f deployments/Dockerfile .`。
#   · 生产编排 = deployments/docker-compose.yml。默认是 socket-proxy 模式；
#     直挂 socket 模式用 `docker compose up -d direct`（两种模式**互斥**：共用同一个
#     数据卷与同一个宿主机端口，无法同时跑）。make 侧用 `MODE=direct|proxy` 选择。
#   · 开发编排 = deployments/docker-compose.dev.yml（dotnet watch + Vite，均在容器内）。
#   · `.env` 缺失**不阻塞**：compose 的默认值即可启动（密钥自动生成进数据卷）。
#   · 本地开发时数据库与日志被显式指到仓库根的 data/。原因是 sqlite 文件与
#     日志文件一旦落在 src/Whalefleet.Api/ 里，dotnet watch 会把它们当成
#     「源码变更」而无限重启（写一次日志 = 触发一次重编译）。
#   · `docker build` **必须带 --load**：本机默认 builder 是 docker-container
#     driver，不带 --load 时构建产物只留在 build cache 里，docker images 中
#     还是旧镜像——症状是「构建成功、容器也重建了，但改动一点没生效」。
# =============================================

SHELL := /bin/bash
.DEFAULT_GOAL := help

# ---------- 路径 ----------
ROOT_DIR    := $(shell cd $(dir $(abspath $(lastword $(MAKEFILE_LIST)))) && pwd)
UI_DIR      := $(ROOT_DIR)/src/Whalefleet.UI
API_PROJ    := $(ROOT_DIR)/src/Whalefleet.Api/Whalefleet.Api.csproj
DATA_DIR    := $(ROOT_DIR)/data
BUILD_DIR   ?= $(ROOT_DIR)/dist
# 部署目录：.env / .env.example / Dockerfile / docker-compose*.yml 全在这里。
DEPLOY_DIR  := $(ROOT_DIR)/deployments
# .env 与 .env.example 都在 deployments/（与编排文件、镜像定义同放一处）
ENV_FILE    := $(DEPLOY_DIR)/.env
ENV_EXAMPLE := $(DEPLOY_DIR)/.env.example
# 镜像定义文件在 deployments/，但**构建上下文是仓库根**（要拷 src/ 与 tests/）。
DOCKERFILE  := $(DEPLOY_DIR)/Dockerfile

# ---------- 镜像 ----------
# 0.x 不对外发布镜像，本地 tag 是当前唯一可用的来源。
IMAGE       ?= whalefleet:local

# ---------- 端口 ----------
# 三层含义，**不要混**：
#   · 宿主机端口（本文件的 API_PORT / UI_PORT）——对外暴露的那个，可改。
#   · 容器内端口——**固定 8080**（appsettings.json 的 "Urls"、Dockerfile 的
#     HEALTHCHECK、examples/reverse-proxy/ 的 upstream 三处都写死了它）。
#   · 反代 / 终端等内部端口——由各自的服务名 + 固定端口决定，不受本文件影响。
#
# ⚠️ 优先级：命令行 / 进程环境变量 > deployments/.env > 内置默认值。
#    读 .env 是为了**让 `make` 与 `docker compose` 读到同一个值**——
#    否则改了 .env 里的端口，直接跑 compose 生效、跑 make 不生效。
#    ⚠️ `make dev` 时前端 Vite 的代理目标必须跟着后端端口走，
#    否则 Vite 代理到 8080 而后端在 9090，页面全 404。
#
# ⚠️ 用 `origin` 判定来源而不是直接 `?=`：环境变量在 make 里默认**不**优先于
#    makefile 内的赋值，直接 `:=` 会把 `API_PORT=9090 make dev` 静默吃掉。
read_port = $(strip $(shell sed -n 's/^$(1)=//p' $(ENV_FILE) 2>/dev/null | head -n1 | tr -d "\"' "))
ifeq ($(filter command line environment,$(origin API_PORT)),)
API_PORT := $(call read_port,WHALEFLEET_PORT)
endif
ifeq ($(filter command line environment,$(origin UI_PORT)),)
UI_PORT := $(call read_port,WHALEFLEET_UI_PORT)
endif
API_PORT := $(if $(API_PORT),$(API_PORT),8080)
UI_PORT := $(if $(UI_PORT),$(UI_PORT),30123)

# ---------- 编排 ----------
# --project-directory 固定为 deployments/：compose 以它为项目根解析相对路径，
# 且 .env 也在这里。
# ⚠️ --env-file 只在文件存在时才传：文件缺失时 compose 会直接报错退出，
#    而本项目刻意支持「零配置启动」。
ENV_FLAG    := $(if $(wildcard $(ENV_FILE)),--env-file $(ENV_FILE),)
COMPOSE     := docker compose --project-directory $(DEPLOY_DIR) $(ENV_FLAG)
COMPOSE_PROD := $(COMPOSE) -f $(DEPLOY_DIR)/docker-compose.yml
COMPOSE_DEV  := $(COMPOSE) -f $(DEPLOY_DIR)/docker-compose.dev.yml

# ---------- 部署模式（两种互斥） ----------
#   direct —— 直挂 Docker socket。功能完整，但等价于把宿主机 root 权限交给容器内
#             任何进程（含本项目自身与将来任何拉进来的依赖）。适合本机 / 单机使用。
#   proxy  —— 经 tecnativa/docker-socket-proxy 中介，只放行 CONTAINERS / IMAGES /
#             NETWORKS / VOLUMES 四个分区（且必须开 POST 这道写操作总闸，
#             否则全程只读）。对外暴露时推荐。
#
# ⚠️ 两种模式**共用同一个数据卷**（whalefleet-data）与**同一个宿主机端口**
#    （${WHALEFLEET_PORT}），因此无法共存——同时起必然是端口冲突。
#    Makefile 在启目标模式前会先停掉另一种模式。
# ⚠️ compose 的 profile 是**加法**不是开关：`--profile direct` 会把「无 profile 的
#    proxy」也一起启起来 → 两个容器抢同一个端口 → `port is already allocated`。
#    真正互斥要**点名 service**：`up -d direct` / `up -d proxy socket-proxy`。
MODE ?= direct
ifeq ($(MODE),direct)
  APP_SERVICES   := direct
  PROFILE_FLAG   := --profile direct
  OTHER_SERVICES := proxy socket-proxy
  APP_SERVICE    := direct
else ifeq ($(MODE),proxy)
  APP_SERVICES   := proxy socket-proxy
  PROFILE_FLAG   :=
  OTHER_SERVICES := direct
  APP_SERVICE    := proxy
else
  $(error MODE 只能是 direct 或 proxy（当前：$(MODE)）)
endif
# 生产编排的全部 service。清理时必须**全列**：带 profile 的 direct 容器不会被
# 普通的 `docker compose down` 清掉（down 只处理当前 profile 下可见的服务），
# 残留容器会继续占着宿主机端口。
ALL_SERVICES := direct proxy socket-proxy

.PHONY: help env-file deploy-env deps check check-docker \
        dev dev-server dev-client \
        dev-up dev-down dev-logs dev-build dev-restart dev-reset \
        build build-dotnet restore \
        docker-build docker-up docker-start docker-smoke docker-logs docker-ps \
        docker-shell docker-down docker-clean docker-admin-password backup \
        test test-unit test-ui lint typecheck format clean

# ---------- 帮助 ----------
help: ## 显示所有可用目标
	@echo "🐋  Whalefleet —— 可用命令"
	@echo ""
	@grep -hE '^[a-zA-Z_-]+:.*?## .*$$' $(MAKEFILE_LIST) \
		| sort \
		| awk -F':.*?## ' '{printf "  \033[36m%-16s\033[0m %s\n", $$1, $$2}'
	@echo ""

# ---------- 前置准备 ----------
env-file: ## 确保 .env 存在（缺失时从 .env.example 复制）
	@test -f $(ENV_FILE) || { \
		echo "⚠️  未找到 .env，正在从 .env.example 创建..."; \
		cp $(ENV_EXAMPLE) $(ENV_FILE); \
	}

deploy-env: env-file ## 准备 .env（首次自动写入与宿主机 docker 组一致的 DOCKER_GID）
	@if [ -S /var/run/docker.sock ]; then \
		sock_gid=$$(stat -c '%g' /var/run/docker.sock); \
		cur_gid=$$(sed -n 's/^DOCKER_GID=//p' $(ENV_FILE) | tail -n 1); \
		if [ "$$cur_gid" != "$$sock_gid" ]; then \
			echo "🔧 DOCKER_GID: $$cur_gid → $$sock_gid（取自 /var/run/docker.sock）"; \
			if grep -qE '^DOCKER_GID=' $(ENV_FILE); then \
				sed -i "s/^DOCKER_GID=.*/DOCKER_GID=$$sock_gid/" $(ENV_FILE); \
			else \
				printf '\nDOCKER_GID=%s\n' "$$sock_gid" >> $(ENV_FILE); \
			fi; \
		fi; \
	else \
		echo "⚠️  未找到 /var/run/docker.sock，DOCKER_GID 保持原值"; \
	fi

deps: ## 安装前端依赖（src/Whalefleet.UI/node_modules）
	@if [ ! -d "$(UI_DIR)/node_modules" ]; then \
		echo "📦 安装前端依赖..."; \
		cd "$(UI_DIR)" && CI=true pnpm install --frozen-lockfile; \
	fi

check: ## 检查本机开发所需工具（dotnet / pnpm）
	@command -v dotnet >/dev/null 2>&1 || { echo "❌ 缺少 dotnet SDK（需要 10.0）"; exit 1; }
	@command -v pnpm   >/dev/null 2>&1 || { echo "❌ 缺少 pnpm：corepack enable pnpm"; exit 1; }
	@echo "✅ 工具齐备"

check-docker: ## 检查 docker 是否可用（容器化目标需要）
	@command -v docker >/dev/null 2>&1 || { echo "❌ 缺少 docker"; exit 1; }
	@docker info >/dev/null 2>&1 || { echo "❌ docker 守护进程未运行"; exit 1; }
	@echo "✅ docker 可用"

# ---------- 本机开发（dotnet watch + Vite） ----------
# 数据库与日志一律指到仓库根的 data/：
#   · 落在项目目录里会被 dotnet watch 当成源码变更 → 无限重启；
#   · 仓库根的 data/ 已被 .gitignore 忽略（内含 jwt.key 等 0600 密钥）。
dev: deps check ## 本机并行跑 dotnet watch(后端热重载) + pnpm dev(前端 HMR)
	@echo "🚀 后端 dotnet watch → http://localhost:$(API_PORT)"
	@echo "🚀 前端 Vite         → http://localhost:$(UI_PORT)"
	@echo "   Ctrl+C 一并停止"
	@set -m; \
	trap 'kill -TERM -- -$$api_pid -$$ui_pid 2>/dev/null; exit 130' INT TERM; \
	( cd "$(UI_DIR)" && CI=true VITE_DEV_PORT=$(UI_PORT) VITE_API_PROXY_TARGET="http://localhost:$(API_PORT)" pnpm dev ) & ui_pid=$$!; \
	( cd "$(ROOT_DIR)" && \
		ASPNETCORE_ENVIRONMENT=Development \
		Persistence__DatabasePath="$(DATA_DIR)/whalefleet.db" \
		Serilog__WriteTo__1__Args__path="$(DATA_DIR)/logs/app-.log" \
		dotnet watch run --project "$(API_PROJ)" --no-launch-profile -- --urls "http://0.0.0.0:$(API_PORT)" \
	) & api_pid=$$!; \
	wait

dev-server: deps check ## 仅启动后端 dotnet watch（热重载）
	@cd "$(ROOT_DIR)" && \
		ASPNETCORE_ENVIRONMENT=Development \
		Persistence__DatabasePath="$(DATA_DIR)/whalefleet.db" \
		Serilog__WriteTo__1__Args__path="$(DATA_DIR)/logs/app-.log" \
		dotnet watch run --project "$(API_PROJ)" --no-launch-profile -- --urls "http://0.0.0.0:$(API_PORT)"

dev-client: deps ## 仅启动前端 pnpm dev
	@cd "$(UI_DIR)" && CI=true VITE_DEV_PORT=$(UI_PORT) VITE_API_PROXY_TARGET="http://localhost:$(API_PORT)" pnpm dev

# ---------- 容器化开发环境 ----------
dev-up: check-docker ## 容器化开发环境（dotnet watch + Vite 全在容器内）
	@WHALEFLEET_PORT=$(API_PORT) $(COMPOSE_DEV) up -d --build
	@echo ""
	@echo " 前端 http://localhost:$(UI_PORT)   后端 http://localhost:$(API_PORT)"
	@echo " 日志: make dev-logs    停止: make dev-down"

dev-down: check-docker ## 停止容器化开发环境（数据卷保留）
	@$(COMPOSE_DEV) down --remove-orphans

dev-logs: check-docker ## 跟踪容器化开发环境日志
	@$(COMPOSE_DEV) logs -f --tail=100 api ui

dev-build: check-docker ## 重建容器化开发环境
	@$(COMPOSE_DEV) up -d --build

dev-restart: check-docker ## 重启容器化开发环境
	@$(COMPOSE_DEV) restart

dev-reset: check-docker ## 重置容器化开发环境（连同开发用数据卷一起删除）
	@$(COMPOSE_DEV) down -v --remove-orphans

# ---------- 构建 ----------
build: deps ## 本地一体化构建（前端 → dist/wwwroot，后端 → dist/）
	@echo "🔨 构建前端..."
	@cd "$(UI_DIR)" && CI=true pnpm build
	@echo "🔨 发布后端..."
	@dotnet publish "$(API_PROJ)" -c Release -o "$(BUILD_DIR)" --nologo
	@rm -rf "$(BUILD_DIR)/wwwroot" && mkdir -p "$(BUILD_DIR)/wwwroot" "$(BUILD_DIR)/scripts"
	@cp -r "$(UI_DIR)/dist/." "$(BUILD_DIR)/wwwroot/"
	@cp "$(ROOT_DIR)/scripts/backup.sh" "$(BUILD_DIR)/scripts/backup.sh"
	@chmod +x "$(BUILD_DIR)/scripts/backup.sh"
	@echo "✅ 产物：$(BUILD_DIR)"

build-dotnet: ## 仅构建后端（Release；警告即错误）
	@dotnet build -c Release --nologo

restore: ## 还原依赖（NuGet + pnpm）
	@dotnet restore
	@cd "$(UI_DIR)" && CI=true pnpm install --frozen-lockfile

# ---------- 生产部署（Docker） ----------
docker-build: check-docker ## 构建生产镜像（docker build --load，context = 仓库根）
	@echo "🔨 构建镜像 $(IMAGE)..."
	@docker build --load -f "$(DOCKERFILE)" -t "$(IMAGE)" "$(ROOT_DIR)"
	@echo "✅ 镜像已构建：$(IMAGE)"

docker-up: deploy-env docker-build ## 一键生产部署（构建 → 启动 → 等待健康；MODE=direct|proxy）
	@# 先停掉另一种模式：两者共用同一个宿主机端口与数据卷，无法共存
	@$(COMPOSE_PROD) stop $(OTHER_SERVICES) >/dev/null 2>&1 || true
	@$(COMPOSE_PROD) rm -f $(OTHER_SERVICES) >/dev/null 2>&1 || true
	@WHALEFLEET_IMAGE=$(IMAGE) $(COMPOSE_PROD) $(PROFILE_FLAG) up -d $(APP_SERVICES)
	@echo "⏳ 等待服务就绪 (http://localhost:$(API_PORT)/healthz)..."
	@READY=0; \
	for i in $$(seq 1 60); do \
		if curl -fsS "http://127.0.0.1:$(API_PORT)/healthz" >/dev/null 2>&1; then READY=1; break; fi; \
		sleep 2; \
	done; \
	echo ""; \
	if [ "$$READY" = "1" ]; then \
		echo "✅ 部署完成（$(MODE) 模式）：http://localhost:$(API_PORT)"; \
		echo "   初始管理员密码：make docker-admin-password"; \
	else \
		echo "⚠️  服务未在预期时间内就绪，请查看日志：make docker-logs"; \
		exit 1; \
	fi

docker-start: check-docker ## 启动生产环境（复用已有镜像，不重新构建；MODE=direct|proxy）
	@$(COMPOSE_PROD) stop $(OTHER_SERVICES) >/dev/null 2>&1 || true
	@$(COMPOSE_PROD) rm -f $(OTHER_SERVICES) >/dev/null 2>&1 || true
	@WHALEFLEET_IMAGE=$(IMAGE) $(COMPOSE_PROD) $(PROFILE_FLAG) up -d $(APP_SERVICES)
	@echo "✅ 已启动（$(MODE) 模式）：http://localhost:$(API_PORT)"

docker-smoke: check-docker ## 部署后冒烟检查（健康探针 / 容器状态）
	@curl -fsS "http://127.0.0.1:$(API_PORT)/healthz" >/dev/null \
		&& echo "✅ healthz: ok" \
		|| { echo "❌ healthz: 不可用"; exit 1; }
	@$(COMPOSE_PROD) ps

docker-logs: check-docker ## 跟踪生产环境日志（两种模式的容器都会跟）
	@$(COMPOSE_PROD) logs -f --tail=100 proxy direct socket-proxy

docker-ps: check-docker ## 查看生产环境容器状态
	@$(COMPOSE_PROD) ps

docker-shell: check-docker ## 进入应用容器（排障用）
	@$(COMPOSE_PROD) exec $(APP_SERVICE) bash

docker-admin-password: check-docker ## 打印初始管理员密码（首次启动自动生成）
	@$(COMPOSE_PROD) exec $(APP_SERVICE) cat /app/data/initial-admin.txt

docker-down: check-docker ## 停止生产环境（保留数据卷）
	@# ⚠️ 必须显式 stop + rm 全部 service：带 profile 的 direct 容器不会被普通的
	@# `docker compose down` 清掉（down 只处理当前 profile 下可见的服务），
	@# 残留的容器会继续占着宿主机端口。
	@$(COMPOSE_PROD) stop $(ALL_SERVICES) >/dev/null 2>&1 || true
	@$(COMPOSE_PROD) rm -f $(ALL_SERVICES) >/dev/null 2>&1 || true
	@$(COMPOSE_PROD) down --remove-orphans

docker-clean: check-docker ## 停止并删除数据卷（数据库与密钥一并丢失，需确认）
	@printf '将删除数据卷 whalefleet-data（SQLite 数据库与 jwt.key 一并丢失），继续？[y/N] '; \
	read -r ans; \
	case "$$ans" in \
		y|Y) $(COMPOSE_PROD) stop $(ALL_SERVICES) >/dev/null 2>&1 || true; \
		     $(COMPOSE_PROD) rm -f $(ALL_SERVICES) >/dev/null 2>&1 || true; \
		     $(COMPOSE_PROD) down -v ;; \
		*) echo "已取消" ;; \
	esac

backup: check-docker ## 在运行中的容器内执行 SQLite 在线备份（.backup，不是 cp）
	@$(COMPOSE_PROD) exec $(APP_SERVICE) bash /app/scripts/backup.sh

# ---------- 质量检查 ----------
test: ## 后端单测 + 集成测试（dotnet test）
	@dotnet test -c Release --nologo

test-unit: ## 仅后端单测
	@dotnet test "$(ROOT_DIR)/tests/Whalefleet.UnitTests/Whalefleet.UnitTests.csproj" -c Release --nologo

test-ui: deps ## 前端单测（vitest）
	@cd "$(UI_DIR)" && CI=true pnpm test

lint: deps ## 前端 ESLint 检查
	@cd "$(UI_DIR)" && pnpm lint

typecheck: deps ## 前端类型检查（真正检查 tsconfig.app.json）
	@cd "$(UI_DIR)" && npx vue-tsc -p tsconfig.app.json --noEmit

format: deps ## 前端 Prettier 格式化
	@cd "$(UI_DIR)" && pnpm format

clean: ## 清理本地构建产物
	@rm -rf "$(BUILD_DIR)" \
		"$(ROOT_DIR)/src/Whalefleet.Core/bin" "$(ROOT_DIR)/src/Whalefleet.Core/obj" \
		"$(ROOT_DIR)/src/Whalefleet.Application/bin" "$(ROOT_DIR)/src/Whalefleet.Application/obj" \
		"$(ROOT_DIR)/src/Whalefleet.Infrastructure/bin" "$(ROOT_DIR)/src/Whalefleet.Infrastructure/obj" \
		"$(ROOT_DIR)/src/Whalefleet.Api/bin" "$(ROOT_DIR)/src/Whalefleet.Api/obj" \
		"$(ROOT_DIR)/tests/Whalefleet.UnitTests/bin" "$(ROOT_DIR)/tests/Whalefleet.UnitTests/obj" \
		"$(ROOT_DIR)/tests/Whalefleet.IntegrationTests/bin" "$(ROOT_DIR)/tests/Whalefleet.IntegrationTests/obj"
	@echo "✅ 清理完成"
