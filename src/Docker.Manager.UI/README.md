# Docker.Manager 前端

Vue 3 + TypeScript + Vite + UnoCSS + Element Plus + Pinia + vue-i18n。

完整规范见仓库根目录 [`docs/ui-guidelines.md`](../../../docs/ui-guidelines.md)，
接口契约见 [`docs/api-spec.md`](../../../docs/api-spec.md)。

---

## 快速开始

```bash
pnpm install

pnpm dev:mock   # 推荐：不启动后端也能开发（见下方「Mock 模式」）
pnpm dev:real   # 连真实后端（默认代理到 http://localhost:8080）
```

| 脚本 | 说明 |
|------|------|
| `pnpm dev` / `dev:real` | 连真实后端；`/api`、`/hubs`、`/ws` 走 Vite 代理 |
| `pnpm dev:mock` | 强制开启 mock，无需设置环境变量 |
| `pnpm typecheck` | `vue-tsc --noEmit` |
| `pnpm lint` | ESLint，`--max-warnings 0` |
| `pnpm test` | Vitest（单元测试 + mock 契约守卫） |
| `pnpm build` | 类型检查 + 生产构建 |

---

## Mock 模式（隔离开后端开发）

主机列表、版本、资源计数等数据的后端接口尚未就绪（0.0.8）。开启 mock 后，
**无需启动后端**即可完整走查前端。

### 开启方式（两者任一）

| 方式 | 操作 | 生效时机 |
|------|------|---------|
| 运行时（推荐） | URL 加 `?mock=on` 或 `?mock=<场景>` | 刷新即生效 |
| 环境变量 | `.env.development.local` 里 `VITE_ENABLE_MOCK=true` | **需重启 dev server** |

> Vite 只在启动时读 `.env*`，所以日常切场景请用 URL 参数。

### 顶栏 MOCK 角标

只要 mock 开启，顶栏会出现橙色 **MOCK** 角标，点击可切换场景或关闭。

> 绝不静默 mock：前端在打假数据而你以为是真后端，是本方案最容易踩的坑。
> 看到角标就知道当前数据不是真实的。

### 可用场景

| 场景 | 用途 |
|------|------|
| `default` | 3 台主机，其中 `staging-01` **离线且无 version/counts**（验证缺失态与离线态） |
| `empty` | 零主机（验证空状态 + CTA） |
| `all-offline` | 全部离线（验证离线提示条） |
| `counts` | 带真实资源计数（验证 4 个磁贴的「有值」形态） |
| `slow` | 延迟 3s（验证骨架屏） |
| `error` | 列表返回 `9999`/500（验证错误态 + 重试） |
| `unauthorized` | 凭据错误（验证 `1100` → `1101` → `1102` 认证链路） |
| `disabled` | 含一台已停用主机（验证 `1302` 分支） |

访问 `http://localhost:30123/?mock=counts` 即可（端口见 `vite.config.ts` 的 `server.port`）。

### 账号

| 用户名 | 密码 | 角色 |
|--------|------|------|
| `admin` | `demo1234` | admin |
| `viewer` | `demo1234` | viewer（验证 admin-only 菜单与路由拦截） |

可用 `?role=operator` 等覆盖角色。

### 令牌轮换语义

mock 复刻了后端的**刷新令牌轮换**语义：每次刷新签发新的 `refreshToken`，
旧值立即作废，再拿来换会得到 `1102`。这不是为了炫技——
前端「并发 401 只刷一次」的单飞逻辑正靠它才能被测出来。

---

## 目录结构

```
src/
├── api/          # API 封装（index.ts = axios 实例 + 拦截器；auth.ts / host.ts = 端点）
├── components/
│   ├── common/   # 通用组件
│   └── business/ # 业务组件
├── composables/  # Composition 函数
├── layouts/      # 应用外壳
├── locales/      # i18n（zh-CN / en-US，两份 key 必须同步）
├── mocks/        # MSW mock（仅开发）
├── router/       # 路由与守卫
├── stores/       # Pinia
├── styles/       # main.css（设计令牌 + Element Plus 变量桥接）
├── types/        # TypeScript 类型
├── utils/        # 纯函数工具
└── views/        # 路由对应的页面
```

---

## 开发须知

- **颜色一律用语义类**（`bg-surface-card` / `text-content-muted` / `border-line-subtle`），
  不要写 `gray-500`、`bg-white` 或 `dark:` 变体。详见 ui-guidelines 的「语义颜色令牌」。
- **主机数据来自 API**，store 内不再内置种子。首次导航由路由守卫 `await ensureLoaded()` 拉取。
- **不要在 store 里 import `src/mocks/`**：那会让开发期的 mock 模块被打进生产主 chunk。