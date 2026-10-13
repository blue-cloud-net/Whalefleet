import {
  MOCK_HOSTS_ALL_OFFLINE,
  MOCK_HOSTS_DEFAULT,
  MOCK_HOSTS_WITH_COUNTS,
  MOCK_HOSTS_WITH_DISABLED
} from './data/hosts'
import type { HostOverview } from '@/types/api'

/**
 * mock 场景。
 *
 * 目的是让「后端还没实现」这件事**可测试**：缺失态、离线态、空态、错误态、
 * 加载态都能在无后端的情况下稳定复现，而不是靠删数据/断网去凑。
 *
 * 切换优先级：`?mock=` query > `VITE_MOCK_SCENARIO` 环境变量 > `default`。
 * query 优先是因为它能直接分享链接与截图，不需要重启 dev server
 * （Vite 只在启动时读 `.env*`）。
 */
export type MockScenario =
  | 'default'
  | 'empty'
  | 'all-offline'
  | 'counts'
  | 'slow'
  | 'error'
  | 'unauthorized'
  | 'disabled'

export const MOCK_SCENARIOS: MockScenario[] = [
  'default',
  'empty',
  'all-offline',
  'counts',
  'slow',
  'error',
  'unauthorized',
  'disabled'
]

/** 场景 → 中文说明，渲染在 MOCK 角标的 tooltip 里。 */
export const MOCK_SCENARIO_LABELS: Record<MockScenario, string> = {
  default: '默认：3 台主机，其中 staging-01 离线且无 version/counts',
  empty: '空态：零主机（走 EmptyState + CTA）',
  'all-offline': '全离线：验证离线提示条',
  counts: '带计数：验证 4 个资源磁贴的「有值」形态',
  slow: '慢响应：延迟 3s（验证骨架屏）',
  error: '错误态：主机列表返回 9999 / 500',
  unauthorized: '未授权：access token 过期 → 刷新失败（验证 1101 → 1102 链路）',
  disabled: '停用主机：验证后端 1302「主机已停用」分支'
}

const DEFAULT_SCENARIO: MockScenario = 'default'

/** 传入值是否是一个合法场景名（供 mock 开关判定复用，避免两处各写一份白名单）。 */
export function isScenario(value: string | null | undefined): value is MockScenario {
  return !!value && (MOCK_SCENARIOS as string[]).includes(value)
}

/**
 * 解析当前场景。
 *
 * query（`?mock=<场景>`）优先于环境变量（`VITE_MOCK_SCENARIO`），再回退 `default`。
 * 非法值**静默回退**而非抛错——输错一个字符不该让整个应用起不来。
 *
 * 注意：只要 query 带了一个**合法场景名**，它同时隐含「开启 mock」，
 * 这层判定在 `switch.ts` 的 `isMockRuntimeEnabled`。
 */
export function resolveScenario(search: string, env?: string): MockScenario {
  const fromQuery = new URLSearchParams(search).get('mock')
  if (isScenario(fromQuery)) return fromQuery
  if (isScenario(env)) return env
  return DEFAULT_SCENARIO
}

/** 场景 → 主机列表。`error` / `unauthorized` 仍返回基础数据（供 /me 等其他端点使用）。 */
export function hostsFor(scenario: MockScenario): HostOverview[] {
  switch (scenario) {
    case 'empty':
      return []
    case 'all-offline':
      return MOCK_HOSTS_ALL_OFFLINE
    case 'counts':
    case 'slow':
      return MOCK_HOSTS_WITH_COUNTS
    case 'disabled':
      return MOCK_HOSTS_WITH_DISABLED
    default:
      return MOCK_HOSTS_DEFAULT
  }
}

/** `slow` 场景的响应延迟（毫秒）；其余场景立即返回。 */
export function delayFor(scenario: MockScenario): number {
  return scenario === 'slow' ? 3000 : 0
}