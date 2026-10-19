import type { HostGroup, HostOverview } from '@/types/api'
import { MOCK_CONTAINERS_DEFAULT } from './containers'
import { MOCK_IMAGES_DEFAULT } from './images'

/**
 * mock 用主机数据（**从 `stores/host.ts` 迁出**）。
 *
 * 迁移原因：原先它们是 store 的初始 state，等于「前端自带一份真相」——
 * 既无法在后端未启动时验证真实数据流，也让新手以为数据本来就长这样。
 * 现在它们只是 mock 场景的默认数据集。
 *
 * ⚠️ `staging-01` 刻意**不提供** `version` / `startedAt` / `counts`：
 * 它是验证「离线 + 数据缺失」在 UI 上表现为可解释的占位而非塌版的固定夹具。
 * 改动它等于删掉这个测试用例。
 */

/**
 * 分组定义（与后端 `host_groups` 表同形状）。
 *
 * 名字用英文标识（与迁移种入的三个内置分组一致）：它们是**数据**，
 * 前端仅对这三个已知名字取 i18n 文案，自定义分组原样显示。
 */
export const MOCK_HOST_GROUPS: HostGroup[] = [
  {
    id: '00000000-0000-0000-0000-0000000000a1',
    name: 'production',
    description: null,
    sortOrder: 10,
    hostCount: 1
  },
  {
    id: '00000000-0000-0000-0000-0000000000a2',
    name: 'staging',
    description: null,
    sortOrder: 20,
    hostCount: 0
  },
  {
    id: '00000000-0000-0000-0000-0000000000a3',
    name: 'local',
    description: null,
    sortOrder: 30,
    hostCount: 1
  }
]

const LOCAL_VERSION = {
  version: '27.3.1',
  apiVersion: '1.47',
  os: 'linux',
  arch: 'x86_64',
  goVersion: 'go1.22.7'
}

const PROD_VERSION = {
  version: '27.3.1',
  apiVersion: '1.47',
  os: 'linux',
  arch: 'aarch64',
  goVersion: 'go1.22.7'
}

/**
 * `default` 场景：与原 `DEFAULT_HOSTS` 完全一致的数据集。
 *
 * 刻意保留 `counts` 全部缺失——这样默认场景下 4 个资源磁贴显示「—」，
 * 正好是走查缺失态的目标形态；要看真实数字请切 `counts` 场景。
 */
export const MOCK_HOSTS_DEFAULT: HostOverview[] = [
  {
    id: 'local',
    name: 'local',
    type: 'socket',
    endpoint: '/var/run/docker.sock',
    enabled: true,
    status: 'online',
    groupId: '00000000-0000-0000-0000-0000000000a3',
    startedAt: '2026-09-28T03:12:45Z',
    version: { ...LOCAL_VERSION }
  },
  {
    id: 'prod-01',
    name: 'prod-01',
    type: 'tcp',
    endpoint: 'tcp://10.0.0.11:2376',
    enabled: true,
    status: 'online',
    groupId: '00000000-0000-0000-0000-0000000000a1',
    startedAt: '2026-10-06T11:40:08Z',
    version: { ...PROD_VERSION }
  },
  {
    // 离线夹具：无 version / startedAt / counts
    id: 'staging-01',
    name: 'staging-01',
    type: 'tcp',
    endpoint: 'tcp://10.0.0.21:2375',
    enabled: true,
    status: 'offline',
    groupId: '00000000-0000-0000-0000-0000000000a2'
  }
]

/**
 * `counts` 场景：带真实资源计数，用于验证 4 个磁贴的「有值」形态。
 *
 * 容器 / 镜像计数**由夹具长度算出**而不是手写：主机卡片上的「容器 12」
 * 和容器列表里实际列出来的条数对不上，是那种会被当成前端 bug 报上来的幽灵问题——
 * 磁贴与列表读的是同一个 mock 数据源，就该由它自己回答。
 * 卷与网络暂时没有 mock 端点，仍是手写值（0.0.x 里它们还不是列表页）。
 */
export const MOCK_HOSTS_WITH_COUNTS: HostOverview[] = [
  {
    ...MOCK_HOSTS_DEFAULT[0],
    counts: {
      containers: MOCK_CONTAINERS_DEFAULT.length,
      images: MOCK_IMAGES_DEFAULT.length,
      volumes: 5,
      networks: 4
    }
  },
  {
    ...MOCK_HOSTS_DEFAULT[1],
    counts: {
      containers: MOCK_CONTAINERS_DEFAULT.length,
      images: MOCK_IMAGES_DEFAULT.length,
      volumes: 11,
      networks: 7
    }
  },
  {
    // 离线主机的 counts 刻意缺失：离线时后端拿不到实时数据
    ...MOCK_HOSTS_DEFAULT[2]
  }
]

/** `all-offline` 场景：全部离线，用于验证离线提示条。 */
export const MOCK_HOSTS_ALL_OFFLINE: HostOverview[] = MOCK_HOSTS_DEFAULT.map((h) => ({
  ...h,
  status: 'offline' as const
}))

/** `disabled` 场景：一台被停用的主机，用于验证后端 `1302` 主机已停用分支。 */
export const MOCK_HOSTS_WITH_DISABLED: HostOverview[] = [
  ...MOCK_HOSTS_DEFAULT,
  {
    id: 'retired-01',
    name: 'retired-01',
    type: 'tcp',
    endpoint: 'tcp://10.0.0.31:2375',
    enabled: false,
    status: 'offline',
    groupId: '00000000-0000-0000-0000-0000000000a2'
  }
]

/** `error` 场景下仍可返回的主机（配合 9999 让页面进入错误态）。 */
export const MOCK_HOSTS_FOR_ERROR: HostOverview[] = MOCK_HOSTS_DEFAULT