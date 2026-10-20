import 'vue-router'

declare module 'vue-router' {
  interface RouteMeta {
    /** 是否在侧边栏显示（默认 true） */
    showInSidebar?: boolean
    /** 需要的角色列表，包含其一即放行 */
    requiresAdmin?: boolean
    /**
     * 进入本页所需的**权限名**（如 `registry.read`）。
     *
     * 与 `requiresAdmin` 判的不是同一套东西：`requiresAdmin` 是「管理面三项权限」
     * 的合取，而这里只要求单项。给了就**只**看它，不再叠加 `requiresAdmin`
     * （两者同时写等于要求用户既有管理面又有该权限，往往比后端更严）。
     */
    requiresPermission?: string
    /** 是否要求登录态（默认 true） */
    requiresAuth?: boolean
    /** 侧边栏图标（Iconify class） */
    icon?: string
    /** 侧边栏标题（i18n key） */
    title?: string
  }
}

export {}