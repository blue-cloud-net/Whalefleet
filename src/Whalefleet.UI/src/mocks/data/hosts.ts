import type { HostCounts, HostDaemon, HostGroup, HostOverview, HostTag } from '@/types/api'
import { MOCK_CONTAINER_DETAILS, MOCK_CONTAINERS_DEFAULT } from './containers'
import { MOCK_IMAGES_DEFAULT } from './images'

/**
 * mock 用主机数据（**从 `stores/host.ts` 迁出**）。
 *
 * 迁移原因：原先它们是 store 的初始 state，等于「前端自带一份真相」——
 * 既无法在后端未启动时验证真实数据流，也让新手以为数据本来就长这样。
 * 现在它们只是 mock 场景的默认数据集。
 *
 * ⚠️ `staging-01` 刻意**不提供** `version` / `daemon` / `counts` / 标签：
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

/**
 * 主机标签夹具（0.6.0）。
 *
 * 标签不参与授权：它们只是过滤与批量选择用的自由标记。
 * 前两个挂在 `local` 上，第三个没人用（界面要能显示 `hostCount = 0` 的标签）。
 */
export const MOCK_HOST_TAGS: HostTag[] = [
  {
    id: '10000000-0000-0000-0000-000000000001',
    name: 'gpu',
    description: '带 GPU 的机器',
    color: '#8B5CF6',
    hostCount: 1
  },
  {
    id: '10000000-0000-0000-0000-000000000002',
    name: 'internal',
    description: null,
    color: '#0EA5E9',
    hostCount: 1
  },
  {
    id: '10000000-0000-0000-0000-000000000003',
    name: 'unused',
    description: '还没有主机挂它',
    color: null,
    hostCount: 0
  }
]

/**
 * 守护进程静态信息的夹具。
 *
 * ⚠️ `cpuCount` / `memoryBytes` 是**静态上限**：Docker Engine API 没有宿主机级实时指标端点，
 * 界面不得把它们标成「当前占用」。
 */
const MOCK_DAEMON: HostDaemon = {
  operatingSystem: 'Ubuntu 24.04.5 LTS',
  osVersion: '24.04',
  osType: 'linux',
  architecture: 'x86_64',
  kernelVersion: '6.17.0-40-generic',
  cpuCount: 24,
  memoryBytes: 67_161_847_808,
  storageDriver: 'overlayfs',
  storageDriverStatus: [
    ['driver-type', 'io.containerd.snapshotter.v1'],
    ['Backing Filesystem', 'extfs']
  ],
  loggingDriver: 'json-file',
  cgroupDriver: 'systemd',
  cgroupVersion: '2',
  defaultRuntime: 'runc',
  runtimes: ['io.containerd.runc.v2', 'runc'],
  initBinary: 'docker-init',
  containerdVersion: 'b7a25261e029d68978bbf290df0a62fc819b3c51',
  runcVersion: 'v1.5.2-0-g29dd3dc2',
  initVersion: 'de40ad0',
  plugins: {
    volume: ['local'],
    network: ['bridge', 'host', 'ipvlan', 'macvlan', 'null', 'overlay'],
    log: ['awslogs', 'fluentd', 'json-file', 'local', 'syslog'],
    authorization: []
  },
  registryMirrors: ['https://dockerhubcache.example.com/'],
  insecureRegistries: ['127.0.0.0/8'],
  indexServerAddress: 'https://index.docker.io/v1/',
  dockerRootDir: '/var/lib/docker',
  securityOptions: ['name=apparmor', 'name=seccomp,profile=builtin'],
  liveRestoreEnabled: false,
  firewallBackend: 'iptables',
  cdiSpecDirs: ['/etc/cdi', '/var/run/cdi'],
  // 代理的 userinfo 已在服务端遮蔽：mock 也只给遮蔽后的形态，
  // 否则「界面上看不到密码」这条会被 mock 验成假的
  httpProxy: 'http://proxy.internal:3128',
  httpsProxy: null,
  noProxy: 'localhost,127.0.0.1',
  warnings: []
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
    version: { ...LOCAL_VERSION },
    daemon: { ...MOCK_DAEMON },
    tags: [
      { id: MOCK_HOST_TAGS[0]!.id, name: MOCK_HOST_TAGS[0]!.name, color: MOCK_HOST_TAGS[0]!.color },
      { id: MOCK_HOST_TAGS[1]!.id, name: MOCK_HOST_TAGS[1]!.name, color: MOCK_HOST_TAGS[1]!.color }
    ]
  },
  {
    id: 'prod-01',
    name: 'prod-01',
    type: 'tcp',
    endpoint: 'tcp://10.0.0.11:2376',
    enabled: true,
    status: 'online',
    groupId: '00000000-0000-0000-0000-0000000000a1',
    version: { ...PROD_VERSION },
    daemon: { ...MOCK_DAEMON, architecture: 'aarch64' },
    tags: []
  },
  {
    // 离线夹具：无 version / daemon / counts / tags
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
 * 容器细分计数：运行 / 停止 / 健康 / 不健康。
 *
 * **由容器夹具算出**，不手写：主机概况卡片上的「12 运行 / 1 不健康」与容器列表里
 * 实际列出来的条数对不上，是那种会被当成前端 bug 报上来的幽灵问题。
 *
 * 口径与后端一致：
 * - 运行 / 停止取自容器状态（后端取自 `/info` 的 `ContainersRunning` / `ContainersStopped`）
 * - 健康 / 不健康只算**配了健康检查**的容器；`starting` 与没配的都两边都不计
 */
const CONTAINER_BREAKDOWN = {
  // ⚠️ `ContainerDetail.state` 是**对象**（`{ status, running, paused, healthStatus… }`），
  // 不是字符串——写成 `c.state === 'running'` 会恒为 false，界面上四个数全是 0。
  running: MOCK_CONTAINER_DETAILS.filter((c) => c.state.status === 'running').length,
  stopped: MOCK_CONTAINER_DETAILS.filter(
    (c) => c.state.status === 'exited' || c.state.status === 'dead'
  ).length,
  healthy: MOCK_CONTAINER_DETAILS.filter((c) => c.state.healthStatus === 'healthy').length,
  unhealthy: MOCK_CONTAINER_DETAILS.filter((c) => c.state.healthStatus === 'unhealthy').length
}

/**
 * 镜像层占用的磁盘字节数。
 *
 * 后端取的是 `/system/df?type=image` 的 `TotalSize`（**去重后**的实际占用）。
 * mock 只能近似：`size` 是逻辑大小、`sharedSize` 是与别的镜像共享的部分，
 * 逐条相减再求和即「每条镜像独占的层」之和——与去重后的真实占用同量级，
 * 但**不会**精确相等（Docker 的层共享是图结构，不是简单减法）。
 */
const IMAGES_SIZE_BYTES = MOCK_IMAGES_DEFAULT.reduce(
  (sum, image) => sum + Math.max(image.size - image.sharedSize, 0),
  0
)

/**
 * 每台主机在**单台概览端点**（`POST /hosts/{hostId}/overview`）返回的资源计数。
 *
 * ⚠️ 列表端点**不带计数**（后端刻意如此：每台要额外若干次守护进程调用）。
 * 计数只经概览端点下发——早先 mock 把计数挂在列表上，于是 `?mock=counts` 里磁贴有值、
 * 一接真后端全变「—」，等于用 mock 验收了一个假状态。
 *
 * 容器 / 镜像计数**由夹具长度算出**而不是手写：主机卡片上的「容器 12」
 * 与容器列表里实际列出来的条数对不上，是那种会被当成前端 bug 报上来的幽灵问题。
 * 卷与网络没有列表夹具，仍是手写值。
 */
export const MOCK_HOST_COUNTS: Record<string, HostCounts> = {
  local: {
    containers: MOCK_CONTAINERS_DEFAULT.length,
    images: MOCK_IMAGES_DEFAULT.length,
    volumes: 5,
    networks: 4,
    containersRunning: CONTAINER_BREAKDOWN.running,
    containersStopped: CONTAINER_BREAKDOWN.stopped,
    containersHealthy: CONTAINER_BREAKDOWN.healthy,
    containersUnhealthy: CONTAINER_BREAKDOWN.unhealthy,
    imagesSizeBytes: IMAGES_SIZE_BYTES
  },
  'prod-01': {
    containers: MOCK_CONTAINERS_DEFAULT.length,
    images: MOCK_IMAGES_DEFAULT.length,
    volumes: 11,
    networks: 7,
    containersRunning: CONTAINER_BREAKDOWN.running,
    containersStopped: CONTAINER_BREAKDOWN.stopped,
    containersHealthy: CONTAINER_BREAKDOWN.healthy,
    containersUnhealthy: CONTAINER_BREAKDOWN.unhealthy,
    imagesSizeBytes: IMAGES_SIZE_BYTES
  }
}

/**
 * `counts` 场景：带资源计数，用于验证 4 个磁贴的「有值」形态。
 *
 * 计数本身与概览端点同源（`MOCK_HOST_COUNTS`），这里只是把它挂到列表数据上，
 * 供 `hostsFor()` 的场景测试使用——**真实列表端点不带计数**。
 */
export const MOCK_HOSTS_WITH_COUNTS: HostOverview[] = [
  {
    ...MOCK_HOSTS_DEFAULT[0],
    counts: MOCK_HOST_COUNTS.local
  },
  {
    ...MOCK_HOSTS_DEFAULT[1],
    counts: MOCK_HOST_COUNTS['prod-01']
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