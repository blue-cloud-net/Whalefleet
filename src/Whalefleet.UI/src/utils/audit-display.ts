import type { AuditOutcome } from '@/types/api'

/**
 * 审计动作 → i18n key 的映射（0.9.0）。
 *
 * ## 为什么是显式 Record 而不是拼字符串
 * 动作标识是 `domain.object.verb` 形式（`container.start`），若在模板里拼
 * `` t(`audit.action.${action}`) ``，一旦某个动作没有对应词条，vue-i18n **只告警不报错**，
 * 界面上就显示成原始 key 路径——而这样的偏差在开发时几乎看不出来
 * （见 AGENTS.md 关于「禁止在模板里拼 i18n key」的约定）。
 *
 * ## 为什么未知动作要**回退原值**
 * 新增动作而忘了加翻译时，用户看到的是 `container.start` 这样一串英文标识；
 * 这比空白好得多——空白会被当成「数据坏了」。
 */

/** 动作 → 完整 i18n key。 */
const ACTION_KEYS: Record<string, string> = {
  // 认证
  'auth.login': 'audit.action.login',
  'auth.logout': 'audit.action.logout',
  'auth.refresh.replayed': 'audit.action.refreshReplayed',

  // 容器
  'container.inspect': 'audit.action.containerInspect',
  'container.logs.download': 'audit.action.containerLogsDownload',
  'container.exec': 'audit.action.containerExec',
  'container.create': 'audit.action.containerCreate',
  'container.start': 'audit.action.containerStart',
  'container.stop': 'audit.action.containerStop',
  'container.restart': 'audit.action.containerRestart',
  'container.kill': 'audit.action.containerKill',
  'container.pause': 'audit.action.containerPause',
  'container.resume': 'audit.action.containerResume',
  'container.remove': 'audit.action.containerRemove',
  'container.recreate': 'audit.action.containerRecreate',
  'container.rename': 'audit.action.containerRename',
  'container.update': 'audit.action.containerUpdate',
  'container.duplicate': 'audit.action.containerDuplicate',

  // 镜像 / 卷 / 网络
  'image.pull': 'audit.action.imagePull',
  'image.build': 'audit.action.imageBuild',
  'image.push': 'audit.action.imagePush',
  'image.tag': 'audit.action.imageTag',
  'image.remove': 'audit.action.imageRemove',
  'image.prune': 'audit.action.imagePrune',
  'volume.create': 'audit.action.volumeCreate',
  'volume.remove': 'audit.action.volumeRemove',
  'volume.prune': 'audit.action.volumePrune',
  'network.create': 'audit.action.networkCreate',
  'network.remove': 'audit.action.networkRemove',
  'network.connect': 'audit.action.networkConnect',
  'network.disconnect': 'audit.action.networkDisconnect',

  // 主机 / 用户 / 角色
  'host.create': 'audit.action.hostCreate',
  'host.update': 'audit.action.hostUpdate',
  'host.remove': 'audit.action.hostRemove',
  'user.create': 'audit.action.userCreate',
  'user.remove': 'audit.action.userRemove',
  'user.update-password': 'audit.action.userUpdatePassword',
  'role.create': 'audit.action.roleCreate',
  'role.update': 'audit.action.roleUpdate',
  'role.remove': 'audit.action.roleRemove',
  'role.set-permissions': 'audit.action.roleSetPermissions',
  'role.assign': 'audit.action.roleAssign',
  'role.revoke': 'audit.action.roleRevoke',

  // 审计自身
  'audit.export': 'audit.action.auditExport',
  'http.error': 'audit.action.httpError',
}

/**
 * 取动作的 i18n key。
 *
 * @param action 动作标识。
 * @returns 已登记的完整 key；未登记时返回 `null`（调用方应原样显示动作标识）。
 */
export function auditActionKey(action: string): string | null {
  return ACTION_KEYS[action] ?? null
}

/**
 * 动作的可展示文本。
 *
 * @param action 动作标识。
 * @param translate i18n 的 `t` 函数。
 * @returns 已登记动作返回翻译结果；否则返回原始标识（**不返回空串**）。
 */
export function auditActionLabel(
  action: string,
  translate: (key: string) => string
): string {
  const key = auditActionKey(action)

  return key === null ? action : translate(key)
}

/**
 * 结果的 i18n key。
 *
 * @param outcome 结果。
 * @returns 完整 key。
 */
export function auditOutcomeKey(outcome: AuditOutcome): string {
  switch (outcome) {
    case 'success':
      return 'audit.outcome.success'
    case 'denied':
      return 'audit.outcome.denied'
    default:
      return 'audit.outcome.failure'
  }
}

/**
 * 结果 → 色调（供状态药丸使用）。
 *
 * @param outcome 结果。
 * @returns `ok` / `warn` / `down`。
 * @remarks
 * `denied` 用 **warn** 而不是 down：它不是故障，是「被规则挡住了」——
 * 用表示故障的红色会让人误以为系统出了问题。
 */
export function auditOutcomeTone(outcome: AuditOutcome): 'ok' | 'warn' | 'down' {
  switch (outcome) {
    case 'success':
      return 'ok'
    case 'denied':
      return 'warn'
    default:
      return 'down'
  }
}

/**
 * 结果的候选值（下拉框用）。
 */
export const AUDIT_OUTCOMES: readonly AuditOutcome[] = ['success', 'denied', 'failure']

/**
 * 目标类型的候选值（下拉框用）。
 *
 * @remarks
 * 刻意**不做成枚举**：后端的目标类型会随新资源增加，
 * 前端写死一份清单会让「新类型」在筛选里选不到。这里只列常用的几个。
 */
export const AUDIT_TARGET_TYPES: readonly string[] = [
  'container',
  'image',
  'volume',
  'network',
  'host',
  'user',
  'role',
  'role-assignment',
  'session',
  'audit'
]
