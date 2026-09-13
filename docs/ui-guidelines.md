# UI 布局规范

> 本文档约束所有 view 组件的视觉、导航与交互约定。从 **0.0.7** 一次性定义，后续所有迭代复用。
> UI 基建：UnoCSS（utility-first）+ Element Plus 2.14+（复杂组件）。详见 [constraints.md](./constraints.md#前端)。

---

## 整体布局（桌面端 ≥1280px）

```
┌──────────────────────────────────────────────────────────────────┐
│ TopBar (64px 固定顶栏)                                            │
│ ┌────┬─────────────────────────────────────────────────────────┐ │
│ │Logo│  面包屑 Home / Containers / nginx                       │ │
│ │    │              HostSelector ▼      通知🔔   👤user ▼     │ │
│ └────┴─────────────────────────────────────────────────────────┘ │
│ Sidebar        │ Content（带滚动，主内容区）                      │
│ (200px,        │                                                   │
│  可折叠→64px)  │  <RouterView />                                   │
│                │                                                   │
│ 📊 Dashboard   │                                                   │
│ 📦 Containers  │                                                   │
│ 🖼  Images     │                                                   │
│ 💾 Volumes     │                                                   │
│ 🌐 Networks    │                                                   │
│ ─── ADMIN ──── │                                                   │
│ 🖥  Hosts      │                                                   │
│ 👥 Users       │                                                   │
│                │                                                   │
│ [折叠按钮]     │                                                   │
└──────────────────────────────────────────────────────────────────┘
```

---

## 断点

UnoCSS 默认 + 自定义：

| 断点 | 宽度 | 行为 |
|------|------|------|
| `sm` | ≥640px | 侧边栏图标列模式 |
| `md` | ≥768px | 侧边栏图标+文字 |
| `lg` | ≥1024px | 侧边栏完整文字+描述 |
| `xl` | ≥1280px | 默认设计宽度 |
| `2xl` | ≥1536px | 内容区居中、最大宽度限制 |

**移动端（<640px）**：
- 顶栏简化为 Logo + 标题 + 汉堡菜单按钮（点开显示全屏菜单）
- 侧边栏变抽屉式（drawer），默认隐藏
- 表格自动转卡片列表（Element Plus `el-table` → 自建 `el-card` 列表）
- 命令面板按钮常驻顶栏右下角

---

## 组件分层

| 层 | 用途 | 示例 | 位置 |
|---|---|---|---|
| **Layout 层** | 顶级布局 | `AppLayout`、`AuthLayout` | `src/web/src/layouts/` |
| **通用组件** | 跨页面复用 | `EmptyState`、`StatusBadge`、`JsonTree`、`CopyableText`、`ConfirmDialog`、`CommandPalette` | `src/web/src/components/common/` |
| **业务组件** | 领域专用 | `ContainerStatusBadge`、`ImageSize`、`PortMappingEditor`、`EnvVarEditor`、`VolumeMountEditor` | `src/web/src/components/business/` |
| **View 层** | 路由对应的页面 | 每个路由一个 .vue 文件 | `src/web/src/views/` |

---

## 导航约定

### 侧边栏菜单分组

| 分组 | 菜单项 | 可见角色 | 阶段 |
|------|--------|---------|------|
| 运维 | Dashboard / Containers / Images / Volumes / Networks | admin + user | 0.0.7+ |
| 管理 | Hosts / Users | **admin only** | 0.0.13 / 0.0.8 |
| 系统（0.3.0+） | Stacks / Registries | admin | 0.4.0 |

- 菜单项**图标 + 标题 + 可选描述**三列布局（>1024px 显示描述）
- 当前路由对应菜单项**高亮**：左侧蓝色指示条 + 文字加粗
- 折叠状态记 localStorage（key: `sidebar_collapsed`）

### 顶栏组件（从左到右）

| 组件 | 说明 | 阶段 |
|------|------|------|
| Logo（点击回 Dashboard） | 64×64 区域 | 0.0.13 引入品牌 |
| 面包屑（`el-breadcrumb`） | 自动从路由 meta 生成；支持点击上级跳转 | 0.0.7 |
| HostSelector（下拉） | 显示当前主机名 + 在线状态点；点开显示所有可访问主机列表 + 「管理」入口（admin 可见） | 0.0.11 |
| 全局搜索按钮（Ctrl+K 触发） | 0.0.13 起实现命令面板 | 0.0.13 |
| 通知按钮（铃铛） | 0.3.0+ 容器 died 通知，未读小红点 | 0.3.0 |
| 用户菜单（头像下拉） | 用户名 / 角色标签 / 切换主题 / 切换语言 / 修改密码 / 登出 | 0.0.7 |

---

## 全局快捷键（0.0.13 起）

| 快捷键 | 动作 |
|--------|------|
| `Ctrl+K` / `Cmd+K` | 打开命令面板（命令搜索 + 页面跳转） |
| `g` + `c` | 跳 Containers 页（先按 g 再按 c） |
| `g` + `i` | 跳 Images 页 |
| `g` + `v` | 跳 Volumes 页（0.2.0+） |
| `g` + `n` | 跳 Networks 页（0.2.0+） |
| `g` + `d` | 跳 Dashboard 页 |
| `?` | 弹出快捷键速查弹窗 |

> **不做**：容器行内快捷键（如选中后按 r 重启）——避免误操作，所有写操作走显式按钮 + 二次确认。

---

## 主题系统

### 颜色 token（CSS 变量）

```css
:root {
  --color-primary: #409EFF;       /* 可调：蓝/绿/紫 */
  --color-bg-base: #ffffff;       /* 暗色下：#1d1e1f */
  --color-bg-elevated: #ffffff;
  --color-bg-overlay: #ffffff;
  --color-text-primary: #303133;
  --color-text-regular: #606266;
  --color-text-secondary: #909399;
  --color-border-base: #dcdfe6;
  --color-success: #67c23a;
  --color-warning: #e6a23c;
  --color-danger: #f56c6c;
  --color-info: #909399;
}

@media (prefers-color-scheme: dark) {
  :root.theme-system { /* 应用到 <html> */ ... }
}
```

### 主题切换

- 顶栏用户菜单提供「主题模式」（亮 / 暗 / 跟随系统）与「主题色」（蓝 / 绿 / 紫）两个独立选项
- 设置存 localStorage（key: `theme_mode` + `theme_color`）
- 切换时通过 `<html>` 上的 class 控制（`dark` / `theme-blue` / `theme-green` / `theme-purple`）

| 模式 | 值 |
|------|----|
| 亮色 | `<html>` 无 `dark` class |
| 暗色 | `<html>` 含 `dark` class |
| 跟随系统 | `<html>` 含 `theme-system` class，根据 `@media (prefers-color-scheme: dark)` 自动切换 |

| 主题色 | 主色 |
|--------|------|
| 蓝 | `#409EFF` |
| 绿 | `#67C23A` |
| 紫 | `#909399`（Element Plus 默认 primary） |

---

## 通用交互约定

| 场景 | 约定 |
|------|------|
| **危险操作**（删除、停止） | 必须二次确认（`el-popconfirm` 或 `el-dialog`）；按钮用 `danger` 类型 |
| **加载态** | 列表用骨架屏（`el-skeleton`）；按钮用 loading 态禁用；禁止「按钮无反馈」 |
| **空状态** | 用统一 `EmptyState` 组件（图标 + 文案 + 可选 CTA 按钮） |
| **错误提示** | 业务错误通过 `ElMessage.error`；字段校验用 `el-form` 内联 |
| **成功反馈** | 短操作 `ElMessage.success`；长操作（pull / build / exec）走进度条对话框 |
| **表单** | 必填项标 `*`；提交按钮在表单未变 dirty 时禁用 |
| **表格** | 默认显示「名称 / 状态 / 创建时间 / 操作」四列；操作列固定右侧；分页（默认 20 条/页，可在用户偏好设置中调整 10/20/50/100） |
| **状态标签** | 容器/镜像状态用统一 `StatusBadge` 组件（颜色 + 图标 + 文本） |
| **时间格式** | 列表显示相对时间（「2 分钟前」）；详情显示绝对时间（带时区） |
| **数字格式** | 字节大小自动单位（B / KB / MB / GB / TB）；时间 ms / s / min 自动选择 |

---

## 资源视觉标识（UnoCSS 图标）

通过 `presetIcons` + Iconify 按需加载：

| 资源 | 图标 class |
|------|------------|
| Dashboard | `i-mdi-view-dashboard` |
| Containers | `i-mdi-docker` |
| Images | `i-mdi-package-variant` |
| Volumes | `i-mdi-database` |
| Networks | `i-mdi-lan` |
| Hosts | `i-mdi-server` |
| Users | `i-mdi-account-multiple` |
| Stacks (0.4.0) | `i-mdi-layers` |
| Registries (0.4.0) | `i-mdi-cloud-key` |
| 命令面板 | `i-mdi-magnify` |
| 通知 | `i-mdi-bell` |
| 主题切换 | `i-mdi-theme-light-dark` |
| 折叠侧边栏 | `i-mdi-menu-open` / `i-mdi-menu-close` |

---

## 路由结构

```
/login                              → LoginView（独立 AuthLayout，无顶栏侧栏）
/                                   → AppLayout 容器
  /dashboard                        → DashboardView（默认页）
  /containers                       → ContainerListView
    /containers/:id                 → ContainerDetailView（tab: info / logs / stats / inspect）
    /containers/:id/logs            → ContainerLogsView（0.0.11 起可直链）
  /images                           → ImageListView
    /images/:id                     → ImageInspectView
  /volumes   (0.2.0+)               → VolumeListView
  /networks  (0.2.0+)               → NetworkListView
  /stacks    (0.4.0+)               → StackListView
  /registries (0.4.0+)              → RegistryListView
  ─── admin ───
  /admin/hosts                      → HostManagementView
  /admin/users                      → UserManagementView
  /admin/permissions (0.3.0+)       → PermissionManagementView
```

### 路由 meta

```ts
interface RouteMeta {
  title: string;             // i18n key，用于面包屑与浏览器标题
  icon?: string;             // UnoCSS icon class
  requiresAdmin?: boolean;   // true → 仅 admin 可见，路由守卫拦截
  showInSidebar?: boolean;   // 默认 true；false 表示不在侧边栏显示（如详情页）
  breadcrumb?: 'auto' | 'none';
}
```

---

## 文件组织约定

```
src/web/src/
├── api/                   # API 封装（RPC 风格方法）
├── assets/                # 静态资源
├── components/
│   ├── common/            # 通用组件（跨业务复用）
│   └── business/          # 业务组件（领域专用）
├── composables/           # 复用 Composition 函数
│   ├── useTheme.ts
│   ├── useShortcuts.ts
│   └── useDockerRequest.ts  # 自动注入 X-Host-Id
├── layouts/
│   ├── AppLayout.vue
│   └── AuthLayout.vue
├── locales/               # i18n 语言文件
├── router/
│   ├── index.ts
│   └── guards.ts          # 鉴权 + 角色守卫
├── stores/                # Pinia
├── styles/
│   ├── tokens.css         # CSS 变量（颜色/间距/字号）
│   ├── uno.css            # UnoCSS 入口
│   └── overrides.css      # Element Plus 主题覆盖
├── types/                 # TypeScript 类型
├── utils/                 # 工具函数
└── views/                 # 路由对应的页面组件
```

---

## CSS / UnoCSS 使用约定

- **布局类**优先使用 UnoCSS：`flex`、`grid`、`gap-*`、`p-*`、`m-*`、`w-*`、`h-*`
- **状态类**优先使用 UnoCSS：`hover:`、`focus:`、`active:`、`disabled:`
- **响应式**用 UnoCSS 前缀：`sm:`、`md:`、`lg:`、`xl:`、`2xl:`
- **Element Plus 组件**通过 `styles/overrides.css` 用 CSS 变量统一覆盖默认色（主题色切换时联动）
- **避免**写自定义 CSS 文件散落各处；通用样式可扩展 UnoCSS preset 或抽到 `tokens.css`

### 主题色切换实现

```vue
<!-- 例：按钮使用主题色 -->
<el-button type="primary" class="bg-primary hover:bg-primary/90">
  Primary Action
</el-button>
```

`bg-primary` 通过 UnoCSS shortcuts 配置：

```ts
// uno.config.ts
export default defineConfig({
  shortcuts: {
    'bg-primary': 'bg-[var(--color-primary)]',
    'text-primary': 'text-[var(--color-primary)]',
    // ...
  },
})
```

---

## 设计参考

- Element Plus 默认风格（https://element-plus.org）
- Material Design 间距与字号规范
- 现代管理后台惯例：侧边栏 + 顶栏布局（Portainer CE / Rancher / Kuboard 等）
