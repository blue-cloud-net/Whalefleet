import 'vue-router'

declare module 'vue-router' {
  interface RouteMeta {
    /** 是否在侧边栏显示（默认 true） */
    showInSidebar?: boolean
    /** 需要的角色列表，包含其一即放行 */
    requiresAdmin?: boolean
    /** 是否要求登录态（默认 true） */
    requiresAuth?: boolean
    /** 侧边栏图标（Iconify class） */
    icon?: string
    /** 侧边栏标题（i18n key） */
    title?: string
  }
}

export {}