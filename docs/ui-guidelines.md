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
| **Layout 层** | 顶级布局 | `AppLayout`（+ `AppBreadcrumb` / `AppSidebar` / `SidebarNav` / `UserMenu`） | `src/Docker.Manager.UI/src/layouts/` |
| **通用组件** | 跨页面复用 | `EmptyState`、`PlaceholderSection`、`Skeleton`（用 `el-skeleton`）、`SectionCard`、`StatCard`、`PageHeader`、`StatusDot`；待加 `StatusBadge`、`JsonTree`、`CopyableText`、`ConfirmDialog`、`CommandPalette` | `src/Docker.Manager.UI/src/components/common/` |
| **业务组件** | 领域专用 | `HostHero`、`ResourceTile`、`HostCard`、`HostGroupSection`、`HostSelector`；待加 `ContainerStatusBadge`、`ImageSize`、`PortMappingEditor`、`EnvVarEditor`、`VolumeMountEditor` | `src/Docker.Manager.UI/src/components/business/` |
| **View 层** | 路由对应的页面 | 每个路由一个 .vue 文件 | `src/Docker.Manager.UI/src/views/` |

> 新增组件前先确认真会有人用。仓库里曾有一个 `HostStatusPanel.vue`——
> 内容与 `HostStatusView.vue` 重复且全仓无引用，属于死代码，已删除。
> 删它时记得用 `grep` 确认零引用，而不是看文件名像在用。

---

## 导航约定

### 侧边栏菜单分组

菜单项**从路由表自动派生**（`AppLayout.navItems`），按 `meta.scope` 切换两套菜单：

| 作用域 | 菜单项 | 可见角色 | 阶段 |
|--------|--------|---------|------|
| `global` | 总览 / 全局设置 | admin + user | 0.0.7+ |
| `global` | 管理：主机管理 / 用户管理 | **admin only** | 0.0.8 |
| `host` | 概览（落地页，单独渲染，不从路由表派生） | admin + user | 0.0.7+ |
| `host` | 状态 / 容器 / 镜像 / 数据卷 / 网络 / 编排 / 事件 | admin + user | 0.0.7+ |

- 菜单项为「图标 + 标题」两列布局
- 当前路由对应菜单项**高亮**：品牌色底 + 文字加粗（`app-nav-active`）
- 折叠状态记 localStorage（key 在 `stores/theme.ts`，勿硬编码）
- **主机作用域的侧边栏顶部**渲染分组标题（服务器图标 + 主机名）、「概览」与「返回总览」
  - 展开态的分组标题取代了原来的「主机身份卡」——它与顶栏 `HostSelector` 完全重复，
    同一屏里主机名曾出现三次（顶栏 / 侧栏 / 面包屑），现收敛为两处（顶栏 / 页面主标题）
  - **折叠态仍保留可点的状态点**：此时它是菜单区内唯一能进主机概览的入口，不可一并删掉
- 路由声明顺序即菜单顺序；调整 `router/index.ts` 中的**排列顺序**即可改变菜单顺序
- **菜单顺序与能力交付节奏对齐**：容器 / 镜像排在 0.0.11 / 0.0.12，「编排」（Compose）
  依赖它们且后端排在 0.4.0，因此置于网络之后；`tests/routes.spec.ts` 钉住这个顺序
- 纯重定向路由（`hosts/:hostId`）与内容与其他菜单项重叠的页面（`host-dashboard`）
  **必须**声明 `meta.showInSidebar: false`，否则会被当成菜单项，因缺 `meta.title` 而在 `t(undefined)` 处抛错

### 总览页主机列表（长条形 + 分组）

总览页用**长条形**条目而非卡片网格（便于横向对比多台服务器的同类指标），并按**分组**聚合：

```
生产环境  1 台
┌────────────────────────────────────────────────────────────────────┐
│ ● prod-01  远程 TCP          ┃ ▢ ┃ ▢ ┃ ▢ ┃ ▢        在线 ✎ ℹ 📈  → │
│   tcp://10.0.0.11:2376        ┃容┃图┃卷┃网                          │
│   linux/aarch64 · Docker 27.3.1 · 已运行 3 天 17 小时                │
└────────────────────────────────────────────────────────────────────┘

测试环境  1 台
┌────────────────────────────────────────────────────────────────────┐
│ ● staging-01  远程 TCP      ┃ ▢ ┃ ▢ ┃ ▢ ┃ ▢           离线 ✎ ℹ 📈  → │
│   tcp://10.0.0.21:2375     （版本/运行时长缺失 → 整行省略）        │
└────────────────────────────────────────────────────────────────────┘

未分组  0 台  ← 无成员的分组不渲染
```

- 一行一台主机：`名称 + 类型` / `endpoint` / `版本与运行时长` ｜ 四个资源计数 ｜ `状态 + 行内按钮 + 箭头`
- **整行可点**进入主机（`role="button"` + `tabindex="0"` + `@keydown` 响应 Enter/Space）
- 行内两个图标按钮：**编辑**（admin-only，占位）/ **状态图**，均用 `@click.stop` 避免触发整行导航
  - 早先还有个「详情」按钮，但它与「状态图」**绑的是同一个处理函数**（详情本该指概览，
    而整行点击已经能进概览），属于纯冗余，已删除
- 分组按 `order` 升序；**无成员的分组不渲染**；未分组主机恒排在末尾
- 分组显示名与描述**均走 i18n**（store 存 `nameKey` / `descriptionKey`，不存字面文案）
- 窄屏（<1024px）用 `flex-wrap` 折行，不做横向滚动
- 版本 / 运行时长等元信息**缺失时整行省略**，不显示占位符
- 计数在后端聚合接口就绪前显示占位 `—`（含义见下方「缺失态」一节）

### 顶栏组件（从左到右）

| 组件 | 说明 | 阶段 |
|------|------|------|
| 折叠按钮 / 汉堡 | 桌面端切换侧栏折叠，移动端开抽屉。**并入 Logo 所在的左侧组**，不单独占位（早先它孤零零占在最左，与 Logo 之间隔了空隙，看起来像一个无主的图标） | 0.0.7 |
| Logo（点击回总览） | 点击跳 `/overview` | 0.0.14 引入品牌 |
| 面包屑（`el-breadcrumb`） | 由 `utils/breadcrumbs.ts` 的纯函数从路由 meta 派生：全局 `总览 / {当前页}`；主机作用域 `总览 / {主机名} / {当前页}`（主机名链到该主机概览）。各页面**不要**自己写面包屑 | 0.0.7 |
| HostSelector（下拉） | 主机作用域：显示当前主机名 + 状态点；全局作用域：显示「选择主机」占位。点开列出全部可访问主机，**切换后停留在当前资源页**；admin 可见「管理主机」入口 | 0.0.11 |
| 语言切换 / 主题切换 | 顶栏独立控件（切换频繁，不藏进下拉） | 0.0.7 |
| 通知按钮（铃铛） | 0.3.0+ 容器 died 通知，未读小红点 | 0.3.0 |
| 用户菜单（头像下拉） | 用户名 / 角色标签 / 修改密码 / 登出（修改密码已随 0.0.13 加进下拉） | 0.0.7 |

> 顶栏通知铃铛在当前代码中**尚未实现**（排在 0.3.0），不要按截图当 bug 修。

### 主机作用域两页的职责切分

| 页面 | 回答的问题 | 内容 |
|------|-----------|------|
| **概览** `/hosts/:hostId/dashboard` | 这台机器**是谁** | 主机名 / 状态 / 类型 / endpoint（可复制）/ 运行时长；四类资源计数作为通往各资源页的入口；Docker 版本等技术信息 |
| **状态** `/hosts/:hostId/status` | 它**现在怎么样** | 实时指标（CPU / 内存 / 网络 / 磁盘，SignalR 前占位）；近期事件（未接入） |

两页**不要重复**同一块内容。0.0.7 时它们几乎逐字相同（都含运行状态 + 四类计数 + 运行时长），
用户点「状态」看到的还是那四个破折号。

**概览页的三段式结构**：

1. `HostHero`（业务组件）——主机身份 + 操作位 + 离线提示
2. 四个 `ResourceTile`——资源计数，整块可点进入对应资源页
3. 技术信息卡——恒显，缺失字段显示弱化的「—」而非整行隐藏

### 缺失态：区分「没数据」与「真的是零」

计数与版本的真实来源是 0.0.8 的聚合接口，就绪前可能整体缺失。规则：

- **计数缺失**：`ResourceTile` 走缺失态——图标与数值降为 `content-faint`，
  并给 `missing-hint`（离线还是未同步）作为 `title` 与 `aria-label`。
  **不用整块 `opacity`**，那会把「容器 / 镜像」标签也压暗，反而更难读。
- **技术信息缺失**：卡片**恒显**，缺字段显示 `—`，末尾给一句统一说明。
  早先的写法是「字段缺失就整行不 push」，于是离线主机那块直接消失、页面只剩四个破折号，
  看起来像加载失败。
- **离线主机**：用独立提示条说明「数据来自上次成功同步，可能已过期」，
  否则用户会以为页面卡住。

---

## 全局快捷键（0.0.14 起）

单键裸用（`c` / `i` / `v`）会与输入法候选、切窗口、系统快捷键打架，因此一律走
**`g` 前缀的 chord**：按下 `g` 后 1.5 秒内按第二个键，超时作废（VS Code 的同款约定）。

| 快捷键 | 动作 | 生效范围 |
|--------|------|----------|
| `Ctrl+K` / `Cmd+K` | 打开命令面板（命令搜索 + 页面跳转） | 全局 |
| `?` | 弹出快捷键速查弹窗 | 全局 |
| `g` + `b` | 折叠 / 展开侧边栏 | 主机作用域 |
| `g` + `c` | 跳 Containers 页 | 主机作用域 |
| `g` + `i` | 跳 Images 页 | 主机作用域 |
| `g` + `v` | 跳 Volumes 页 | 主机作用域 |
| `g` + `n` | 跳 Networks 页 | 主机作用域 |
| `g` + `d` | 跳 Dashboard 页 | 主机作用域 |
| `g` + `s` | 跳主机 Status 页 | 主机作用域 |

**三条约束**（缺一条就会毁掉快捷键本身）：

1. **输入框内一律不劫持**。焦点在 `input` / `textarea` / `contenteditable` 时全部放行——
   用户在搜索框里打 `g` 是要打字母。
2. **不抢占带修饰键的组合**。除 `Ctrl/Cmd+K` 外一律交还浏览器与系统（复制 / 粘贴 /
   撤销…）。抢了会让用户「复制粘贴失灵」，比没有快捷键糟糕得多。
3. **chord 可撤销**。`g` 之后的第二个键无论是什么都先清空待定状态——用户按了 `g`
   又改主意继续打字，那个字母不能被当成跳转。误跳转比不跳转更糟。

> 主机关联键在**全局作用域不注册**，快捷键速查弹窗里也不会列出它们：主机作用域路由
> 都带 `:hostId`，缺参数会让 vue-router 抛 "Missing required param"（白屏级别）。

> **不做**：容器行内快捷键（如选中后按 r 重启）——避免误操作，所有写操作走显式按钮 + 二次确认。

---

## 主题系统

### 颜色 token（CSS 变量）

### 语义颜色令牌（CSS 变量）

> **组件不得直接写 `gray-500` / `bg-white` / `dark:` 变体**，一律用下表的语义类。
> 原因：裸色值不带语义，换主题或调暗度时要逐处改；亮暗两套值也极易漂移
> （历史上 `app-card` 就曾写死 `bg-white dark:bg-gray-800`，而 `--surface-card` 早已存在）。

令牌定义在 `src/styles/main.css` 的 `:root` / `html.dark` 两处，通过 `uno.config.ts` 的
`theme.colors` 映射为 UnoCSS 类名：

| 令牌 | UnoCSS 前缀 | 亮色 | 暗色 | 用途 |
|------|-------------|------|------|------|
| `--surface-page` | `bg-surface-page` | `#f6f8fa` | `#0b0f14` | 页面底色 |
| `--surface-card` | `bg-surface-card` | `#ffffff` | `#141a21` | 卡片、顶栏、侧栏 |
| `--surface-card-hover` | `bg-surface-card-hover` | `#fbfcfe` | `#1a222b` | 可交互卡片悬浮 |
| `--surface-sunken` | `bg-surface-sunken` | `#f1f5f9` | `#1b232c` | 磁贴底、chip 底 |
| `--surface-hover` | `bg-surface-hover` | `#eef2f7` | `#212b35` | 导航项 / 图标按钮悬浮 |
| `--surface-elevated` | — | `#ffffff` | `#1a222b` | 浮层（映射给 Element Plus） |
| `--border-subtle` | `border-line-subtle` | `#e2e8f0` | `#2a3441` | 卡片描边、分隔线 |
| `--border-strong` | `border-line-strong` | `#cbd5e1` | `#3b4857` | 可交互元素 hover 描边 |
| `--text-strong` | `text-content-strong` | `15 23 42` | `226 232 240` | 标题、强调数值 |
| `--text-body` | `text-content-body` | `51 65 85` | `203 213 225` | 正文 |
| `--text-muted` | `text-content-muted` | `100 116 139` | `148 163 184` | 次要说明、标签 |
| `--text-faint` | `text-content-faint` | `148 163 184` | `100 116 139` | 占位符、禁用态 |
| `--status-ok` | `text/bg-status-ok` | `22 163 74` | `74 222 128` | 在线 / 成功 |
| `--status-down` | `text/bg-status-down` | `220 38 38` | `248 113 113` | 离线 / 错误 |
| `--status-idle` | `text/bg-status-idle` | `148 163 184` | `100 116 139` | 未知 / 中性 |
| `--status-*-soft` | `bg-status-*-soft` | 淡色铺底 | 同值低透明度 | 状态药丸底色 |

- **文本与状态令牌是 RGB 三元组**（`15 23 42` 而非 `#0f172a`），
  这样才能用 `text-content-muted/70` 这类透明度修饰符
- `--surface-*` / `--border-*` 是不透明值，不需要透明度
- 层级刻度 `--z-sticky/--z-dropdown/--z-drawer/--z-toast` 同样收口，不再随手写 `z-20`
- Element Plus 走 `--el-*` 变量，`main.css` 末尾做**变量桥接**（含 `--el-text-color-*`
  直接引用语义令牌），因此暗色只需翻转令牌值，EP 组件自动跟随，不会两处漂移
- 圆角、阴影、动效时长令牌（`--radius-*` / `--shadow-*` / `--duration-*`）保持原样

### 主机状态语义

`online / offline / unknown` 三态的颜色与文案**统一取自 `utils/host-status.ts`**：

```ts
hostTone(status)                    // → 'ok' | 'down' | 'idle'（未登记一律 idle）
HOST_TONE_LABEL_KEY[tone]           // → i18n key（函数返回 key 而非文案，可脱开组件单测）
HOST_TONE_CHIP[tone]                // 药丸：柔和底色 + 同色系文字 + 同色系描边
HOST_TONE_DOT[tone]                 // 状态点实心色
HOST_TONE_RAIL[tone]                // 卡片左侧色条
```

> 历史上这套映射在 `StatusDot` / `HostCard` / `HostSelector` 三处各写一遍且已漂移
> （有的带 `dark:` 变体、有的没有）。**新增状态色一律走这个工具**，勿再内联色值。
> `tests/host-status.spec.ts` 钉住三态一一对应且各色调不缺项。

### 主题切换

- 「主题模式」（亮 / 暗 / 跟随系统）与「主题色」（蓝 / 绿 / 紫）在设置页调整，顶栏留图标入口
- 状态存 localStorage，**由 Pinia 持久化插件托管**（key: `docker-manager:theme`），
  勿在组件里手写 `localStorage.setItem('theme_mode', …)`——那个键名并不存在
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
| **加载态** | 列表用骨架屏（`el-skeleton`，底色已在 `main.css` 桥接到 `--border-subtle`）；按钮用 loading 态禁用；禁止「按钮无反馈」 |
| **空状态** | 用 `EmptyState`（图标 + 文案 + 可选 CTA）。`tone` 区分「正常为空」(`neutral`) 与「出错了」(`warning` / `error`)——两者都画成灰色卡片会让用户以为是自己操作的问题 |
| **功能未实现** | 用 `PlaceholderSection`（版本徽章 + 能力清单 + 排期说明），**不要**用空卡片 |
| **主机离线** | 用独立提示条说明「数据来自上次成功同步，可能已过期」，不要只把数字换成 `—` |
| **错误提示** | 业务错误通过 `ElMessage.error`；字段校验用 `el-form` 内联 |
| **成功反馈** | 短操作 `ElMessage.success`；长操作（pull / build / exec）走进度条对话框 |
| **功能未就绪的点击** | 统一 `ElMessage.info` 说明「X 接口就绪后开放」，**不要**静默无响应（参照 `HostCard` 的编辑按钮） |
| **复制** | `navigator.clipboard` 在非安全上下文不可用，必须 `try/catch` 并降级提示 |
| **表单** | 必填项标 `*`；提交按钮在表单未变 dirty 时禁用 |
| **表格** | 默认显示「名称 / 状态 / 创建时间 / 操作」四列；操作列固定右侧；分页（默认 20 条/页，可在用户偏好设置中调整 10/20/50/100） |
| **状态标签** | 容器/镜像状态用统一 `StatusBadge` 组件（颜色 + 图标 + 文本）；主机三态色走 `utils/host-status.ts` |
| **时间格式** | 列表显示相对时间（「2 分钟前」）；详情显示绝对时间（带时区） |
| **数字格式** | 字节大小自动单位（B / KB / MB / GB / TB）；时间 ms / s / min 自动选择 |
| **定义列表** | 标签**定宽、值紧随其后**，不要 `justify-between` 右对齐——两列布局下会让标签与值隔出四百多像素，眼睛必须横跨整列才能关联 |

### 三种「非正常态」不可混用

| 状态 | 含义 | 用什么 |
|------|------|---------|
| **加载中** | 请求在途，内容马上会来 | `el-skeleton`，形状与真实布局一致，**不出文案** |
| **空** | 请求成功，确实没有数据 | `EmptyState`，给出原因与下一步 |
| **离线** | 拿不到数据，但上次有 | 提示条 + 弱化占位值，说明数据可能已过期 |
| **未实现** | 功能还在排期里 | `PlaceholderSection`，标版本与能力清单 |

混淆这四者的代价是用户分不清「页面坏了 / 功能没做 / 这台机器正好没数据 / 还在加载」。

---

## 资源视觉标识（UnoCSS 图标）

通过 `presetIcons` + Iconify 按需加载，**统一用 carbon 图标集**（`@iconify-json/carbon`）：

| 资源 / 元素 | 图标 class |
|------|------------|
| 总览（所有服务器） | `i-carbon-dashboard` |
| 全局设置 | `i-carbon-settings-adjust` |
| 主机管理 / 服务器分组标题 | `i-carbon-data-center` |
| 主机（总览统计磁贴） | `i-carbon-data-center` |
| 主机分组 / 未分组 | `i-carbon-folder` / `i-carbon-folder-off` |
| 容器 | `i-carbon-container-software` |
| 镜像 | `i-carbon-image` |
| 数据卷 | `i-carbon-data-volume` |
| 网络 | `i-carbon-network-3`（状态页指标用 `i-carbon-network-4`） |
| 编排 | `i-carbon-layers` |
| 事件（操作审计） | `i-carbon-events` |
| 用户管理 / 用户头像 | `i-carbon-user-multiple` / `i-carbon-user-avatar` |
| 本地 Socket 型主机 | `i-carbon-terminal` |
| 远程 TCP 型主机 | `i-carbon-cloud-satellite` |
| 品牌标识 | `i-carbon-cube` |
| 在线 / 离线 / 未知状态点 | 颜色由 `utils/host-status.ts` 给，形状统一为圆点 |
| 主题切换 | `i-carbon-sun` / `i-carbon-moon` / `i-carbon-asleep` |
| 折叠侧边栏 | `i-carbon-side-panel-close` / `i-carbon-side-panel-open` |
| 移动端菜单 | `i-carbon-menu` |
| 复制 / 刷新 / 警告 / 信息 | `i-carbon-copy` / `i-carbon-renew` / `i-carbon-warning-alt` / `i-carbon-information` |

> 动态拼接的图标类（写在 `router/index.ts` 的 `meta.icon` 等处）**必须进 `uno.config.ts` 的
> `safelist`**，否则构建时被摇树移除，表现为「图标不显示」。

> **图标名必须是 Carbon 里真实存在的名字。** Carbon 没有 `-alt` 变体
> （`data-center-alt`、`server`、`cluster` 全部不存在），UnoCSS 对不存在的图标
> **只告警不报错**，编译照过，运行时表现为「底色方块在、图标不见」。
> `uno.config.ts` 的 `assertIconsExist()` 会在 `vite dev/build` 时对全部 safelist
> 图标做存在性校验并直接抛错，新增图标时用这条兜住。
>
> ```bash
> # 想确认某个名字存在与否：
> node -e "const i=require('@iconify-json/carbon/icons.json');console.log(!!i.icons['<名字>'])"
> ```

---

## 路由结构

```
/login                              → LoginView（独立 AuthLayout，无顶栏侧栏）
/                                   → AppLayout 容器，redirect → /overview

  ─── 全局作用域（scope: 'global'）───
  /overview                         → OverviewView（所有服务器总览，默认页）
  /settings                         → SettingsView（主题 / 语言 / 每页条数）
  /hosts                            → HostsView        (admin, 主机 CRUD)
  /users                            → UsersView        (admin)

  ─── 主机作用域（scope: 'host'）───
  /hosts/:hostId                    → redirect → /hosts/:hostId/dashboard
  /hosts/:hostId/dashboard          → HostDashboardView（落地页，承载「主机信息」卡片；不进侧边栏）
  /hosts/:hostId/status             → HostStatusView（状态图，菜单第一项）
  /hosts/:hostId/stacks             → StacksView（编排 / Compose，0.4.0 占位）
  /hosts/:hostId/containers         → ContainersView
    /hosts/:hostId/containers/:id   → ContainerDetailView (tab: info / logs / stats / inspect)
  /hosts/:hostId/images             → ImagesView
  /hosts/:hostId/volumes            → VolumesView      (0.2.0+)
  /hosts/:hostId/networks           → NetworksView     (0.2.0+)
  /hosts/:hostId/events             → EventsView       (操作审计，1.0.0+ 占位)
  /hosts/:hostId/stacks/:name       → StackDetailView  (0.4.0+)
  /hosts/:hostId/registries         → RegistryListView (0.4.0+)
```

> **注意**：`/hosts`（主机管理，admin CRUD）与 `/hosts/:hostId/**`（某台主机的资源作用域）
> 是两个不同语义，共存不冲突。`/hosts/:hostId` 是纯重定向，必须声明 `meta.showInSidebar: false`。

### 路由 meta

```ts
interface RouteMeta {
  title: string;             // i18n key，用于面包屑与浏览器标题
  icon?: string;             // UnoCSS icon class
  scope?: 'global' | 'host'; // 默认 'global'；决定侧边栏渲染哪套菜单
  group?: 'manage';          // 全局作用域的分组标题（当前仅管理组）
  requiresAdmin?: boolean;   // true → 仅 admin 可见，路由守卫拦截
  showInSidebar?: boolean;   // 默认 true；false 表示不在侧边栏显示（详情页 / 纯重定向）
  breadcrumb?: 'auto' | 'none';
}
```

### 作用域与 API 路径的关系

| 场景 | `useHostStore.currentId` | 请求路径 |
|------|--------------------------|----------|
| 全局作用域路由 | `''`（守卫调 `clearCurrent()`） | `/api/v1/auth/...`、`/api/v1/admin/...`（**无** `hostId` 段） |
| 主机作用域路由 | URL 的 `:hostId`（守卫调 `setCurrent()`） | `/api/v1/hosts/{hostId}/...` |
| 未知 hostId | 守卫直接重定向 `/overview` | — |

`src/api/*.ts` 的每个方法**显式接收 `hostId`** 并拼进 URL；不再通过请求头传递主机标识。
无 `hostId` 的全局端点与主机作用域端点在**路径形态上就不同**，因此不存在「把总览请求误当作某台机器的资源操作」这类问题。

### 令牌与会话续期

| 关注点 | 约定 |
|--------|------|
| 存储 | 令牌对 + 用户摘要存 `localStorage` 的 `docker-manager:auth`（Pinia 持久化插件） |
| 注入 | 请求拦截器按 `auth.accessToken` 注入 `Authorization`；**不再**注入 `X-Host-Id` |
| 401 | 拦截器**静默刷新一次**并重放原请求；刷新不可用才清会话并整页跳 `/login?redirect=...` |
| 启动 | **不做预刷新**——只在请求真的收到 401 时才续期 |
| 登录成功 | 用 `router.replace(redirect \|\| '/overview')`：回退不应回到登录页 |
| 失败文案 | 按**错误码**映射 i18n，不透出后端 `message`（后端文案是中文，英文界面会中英夹杂） |

三条容易踩的规则：

1. **刷新必须整体覆盖两个令牌**。服务端每次刷新都轮换刷新令牌，沿用旧值再刷一次会被判定为**重放**，
   进而撤销该用户全部会话（见 [api-spec.md](./api-spec.md) 的轮换语义表）。
2. **并发 401 只能触发一次刷新**。刷新令牌是一次性的：两个请求各刷一次，第二个就是重放。
   `api/index.ts` 用模块级 `refreshInFlight` Promise 复用在途结果。
3. **认证端点自身的 401 不重放、也不跳转**。否则「密码输错」会变成「先刷一次再报错」，
   「刷新令牌失效」还会表现成「被登出」。

> `auth.bootstrap()` 刻意留空（状态由持久化插件还原）：多标签页共用同一份 `localStorage`，
> 若每个标签页启动都预刷一次，第二次就是重放 → 表现为「一开新标签页就被踢下线」。

---

## 文件组织约定

```
src/Docker.Manager.UI/src/
├── api/                   # API 封装（RPC 风格方法）
├── assets/                # 静态资源
├── components/
│   ├── common/            # 通用组件（跨业务复用）
│   └── business/          # 业务组件（领域专用）
├── composables/           # 复用 Composition 函数
│   ├── useTheme.ts
│   └── useShortcuts.ts
│   # 主机作用域请求的 URL 由各 api 模块按显式 hostId 参数拼出
├── layouts/               # 应用外壳及其组成部件（只在布局内部复用）
│   ├── AppLayout.vue      # 顶栏 + 侧边栏 + 内容区
│   ├── AppBreadcrumb.vue  # 顶栏面包屑
│   ├── UserMenu.vue       # 顶栏用户菜单（用户名 / 角色 / 登出）
│   ├── AppSidebar.vue     # 桌面侧栏 + 移动端抽屉（容器与断点）
│   ├── SidebarNav.vue     # 侧边栏菜单内容（两个容器共用同一份）
│   └── AuthLayout.vue
├── locales/               # i18n 语言文件（zh-CN / en-US 两份，键必须同步）
├── router/
│   ├── index.ts
│   └── guards.ts          # 鉴权 + 角色守卫
├── stores/                # Pinia
│   ├── auth.ts            # 令牌对 + 用户摘要（持久化）
│   ├── host.ts            # 主机列表 + 分组 + currentId（**不持久化**）
│   ├── theme.ts           # 主题 + 侧栏折叠（持久化）
│   └── preferences.ts     # 每页条数等偏好
├── styles/
│   └── main.css           # 设计令牌（语义色）+ Element Plus 变量桥接 + 页面级动画
├── types/                 # TypeScript 类型
├── utils/                 # 纯函数工具（可脱开组件单测）
│   ├── breadcrumbs.ts     # 面包屑层级派生
│   ├── datetime.ts        # 时间 / 时长格式化
│   └── host-status.ts     # 主机三态语义映射（ok / down / idle）
└── views/                 # 路由对应的页面组件
```

---

## CSS / UnoCSS 使用约定

- **布局类**优先使用 UnoCSS：`flex`、`grid`、`gap-*`、`p-*`、`m-*`、`w-*`、`h-*`
- **状态类**优先使用 UnoCSS：`hover:`、`focus:`、`active:`、`disabled:`
- **响应式**用 UnoCSS 前缀：`sm:`、`md:`、`lg:`、`xl:`、`2xl:`
- **颜色一律用语义类**（`bg-surface-card` / `text-content-muted` / `border-line-subtle`），
  见上文「语义颜色令牌」。写 `gray-500`、`bg-white` 或 `dark:` 变体都属于绕过令牌层，
  review 时应打回
- **共享外观抽成 shortcut**（`app-card` / `app-card-interactive` / `app-stat-tile` /
  `app-chip` / `app-nav-*` / `app-page-title`），页面里不要重复写卡片边框阴影这类组合
- **避免**写自定义 CSS 文件散落各处；确需全局动画或 Element Plus 覆盖时加到 `styles/main.css`
- **不要新建 `tokens.css` / `overrides.css`**：设计令牌与 EP 桥接已统一收口在 `main.css`
  （历史上 `tokens.css` 与 `uno.config.ts` 各存一份色值，正是漂移的源头）

### 卡片悬浮反馈

可交互卡片统一 `app-card-interactive`，其中包含三件事：

1. 抬升 `hover:-translate-y-px`
2. 描边转品牌色 `hover:border-brand-300`
3. **表面提亮 `hover:bg-surface-card-hover`**

第 3 条是暗色模式下的主要反馈信号——那里 `shadow-md` 几乎不可见，
只靠「阴影变化 + 边框变色」用户会看不出卡片能点。

### 主题色切换实现

品牌色通过 `--brand-50` … `--brand-900` 十个变量注入，UnoCSS 侧映射为
`brand-50` … `brand-900`，因此切换主题色无需改任何组件：

```vue
<!-- 跟随三色主题 -->
<el-button type="primary" class="bg-brand-500 hover:bg-brand-600">Primary Action</el-button>
```

---

## 设计参考

- Element Plus 默认风格（https://element-plus.org）
- Material Design 间距与字号规范
- 现代管理后台惯例：侧边栏 + 顶栏布局（Portainer CE / Rancher / Kuboard 等）
