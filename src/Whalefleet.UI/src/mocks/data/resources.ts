import type { NetworkDetail, NetworkSummary, VolumeDetail, VolumeSummary } from '@/types/api'

/**
 * mock 用数据卷数据。
 *
 * 刻意覆盖三种形态，否则界面的分支走查不到：
 * 1. **用量为 `null`**（`nfs-shared`）——守护进程（或第三方驱动）不报用量，
 *    界面必须显示「—」；而真正的 `0`（`db-data`）应如实显示成「0 B」。
 * 2. **有引用的卷**（`refCount > 0`）——验证二次确认提示「先删掉使用它的容器」。
 * 3. **非 `local` 驱动**（`nfs-shared`）——验证驱动与挂载路径不是硬编码展示。
 */

/** 具名卷 ID：数据卷没有 ID，这里只是稳定的占位（界面按名字操作）。 */
export const MOCK_VOLUMES_DEFAULT: VolumeSummary[] = [
  {
    name: 'nginx-html',
    driver: 'local',
    mountpoint: '/var/lib/docker/volumes/nginx-html/_data',
    scope: 'local',
    created: '2026-09-20T06:30:00Z',
    size: 3_221_225_472,
    refCount: 1,
    labels: { app: 'nginx' }
  },
  {
    // 占用确实是 0（空卷），但仍有 2 个容器引用——两个字段相互独立
    name: 'db-data',
    driver: 'local',
    mountpoint: '/var/lib/docker/volumes/db-data/_data',
    scope: 'local',
    created: '2026-10-02T11:05:40Z',
    size: 0,
    refCount: 2,
    labels: {}
  },
  {
    // 第三方驱动不报用量 → 后端给 null。界面必须显示「—」，而不是「0 B」
    name: 'nfs-shared',
    driver: 'nfs',
    mountpoint: '/mnt/nfs/shared',
    scope: 'global',
    created: null,
    size: null,
    refCount: null,
    labels: { env: 'staging' }
  },
  {
    // 没有任何容器引用、也没有标签的普通卷
    name: 'build-cache',
    driver: 'local',
    mountpoint: '/var/lib/docker/volumes/build-cache/_data',
    scope: 'local',
    created: '2026-10-08T17:45:12Z',
    size: 1_073_741_824,
    refCount: 0,
    labels: {}
  }
]

/** 数据卷详情（inspect 投影）。刻意不含 `size` / `refCount`——后端也不会给。 */
export const MOCK_VOLUME_DETAILS: VolumeDetail[] = MOCK_VOLUMES_DEFAULT.map((volume) => ({
  name: volume.name,
  driver: volume.driver,
  mountpoint: volume.mountpoint,
  scope: volume.scope,
  created: volume.created,
  options:
    volume.driver === 'local'
      ? { device: volume.mountpoint, type: 'none', o: 'bind' }
      : { device: ':/mnt/nfs/shared', o: 'addr=10.0.0.9,rw,nfsvers=4' },
  labels: volume.labels
}))

/** 网络 ID：裸的 64 位十六进制，**不带 `sha256:` 前缀**（镜像才有）。 */
const BRIDGE_ID = 'eab806108fb18b30647e1c90c286f6b0ea765710284b0e309e27559087ebb1b0'
const HOST_ID = 'affea56f7cde6dbd4f26c0872101148579b34d5ff442cc8d122801f78bfb17d5'
const NONE_ID = 'ada7afdb39f9d5c1054b7ed328d8a629760d928b625db0d1aa7d590366531fb9'
const INTERNAL_ID = '931a77a234aa5406ee22457251f35276780acfc2bb050e66cef5aa925ebb83d6'
const MACVLAN_ID = '3eae31ffab38b46df7440797d717f2b54fb312ef65e6ce3da756aa07746da905'

/**
 * 网络列表。
 *
 * ⚠️ **刻意没有「容器数」字段**——Docker 的列表接口不返回 `Containers`，
 * 后端不会补一个恒为 0 的字段进来。mock 同样不给，
 * 否则前端会基于一个后端永远不会给的字段写逻辑。
 */
export const MOCK_NETWORKS_DEFAULT: NetworkSummary[] = [
  {
    id: BRIDGE_ID,
    name: 'bridge',
    driver: 'bridge',
    scope: 'local',
    attachable: false,
    internal: false,
    subnets: ['172.17.0.0/16'],
    gateways: ['172.17.0.1'],
    labels: {}
  },
  {
    id: HOST_ID,
    name: 'host',
    driver: 'host',
    scope: 'local',
    attachable: false,
    internal: false,
    // host 驱动没有 IPAM——自定义 / 特例驱动下 subnets 为空数组是常态
    subnets: [],
    gateways: [],
    labels: {}
  },
  {
    id: NONE_ID,
    name: 'none',
    driver: 'null',
    scope: 'local',
    attachable: false,
    internal: true,
    subnets: [],
    gateways: [],
    labels: {}
  },
  {
    id: INTERNAL_ID,
    name: 'app-internal',
    driver: 'bridge',
    scope: 'local',
    attachable: false,
    internal: true,
    subnets: ['172.20.0.0/16'],
    gateways: ['172.20.0.1'],
    labels: { env: 'prod' }
  },
  {
    // 自定义驱动：验证「没有 IPAM」时界面不会崩
    id: MACVLAN_ID,
    name: 'lan-macvlan',
    driver: 'macvlan',
    scope: 'local',
    attachable: true,
    internal: false,
    subnets: [],
    gateways: [],
    labels: {}
  }
]

/** 网络详情（inspect 投影）。只有这里才有 `containers`。 */
export const MOCK_NETWORK_DETAILS: NetworkDetail[] = [
  {
    id: BRIDGE_ID,
    name: 'bridge',
    driver: 'bridge',
    scope: 'local',
    attachable: false,
    internal: false,
    enableIPv4: true,
    enableIPv6: false,
    ipam: [{ subnet: '172.17.0.0/16', gateway: '172.17.0.1', ipRange: null }],
    containers: [
      {
        id: 'c8f1e2d3a4b5c6d7e8f90a1b2c3d4e5f6a7b8c9d0e1f2a3b4c5d6e7f8091a2b3c4',
        name: 'whalefleet-api',
        macAddress: '02:42:ac:11:00:02',
        ipv4Address: '172.17.0.2/16',
        ipv6Address: null
      },
      {
        id: 'd9a2b3c4d5e6f708192a3b4c5d6e7f8091a2b3c4d5e6f708192a3b4c5d6e7f809',
        name: 'whalefleet-db',
        macAddress: '02:42:ac:11:00:03',
        ipv4Address: '172.17.0.3/16',
        ipv6Address: null
      }
    ],
    options: {
      'com.docker.network.bridge.default_bridge': 'true',
      'com.docker.network.bridge.enable_icc': 'true'
    },
    labels: {}
  },
  {
    id: HOST_ID,
    name: 'host',
    driver: 'host',
    scope: 'local',
    attachable: false,
    internal: false,
    enableIPv4: false,
    enableIPv6: false,
    ipam: [],
    containers: [],
    options: {},
    labels: {}
  },
  {
    id: NONE_ID,
    name: 'none',
    driver: 'null',
    scope: 'local',
    attachable: false,
    internal: true,
    enableIPv4: false,
    enableIPv6: false,
    ipam: [],
    containers: [],
    options: {},
    labels: {}
  },
  {
    id: INTERNAL_ID,
    name: 'app-internal',
    driver: 'bridge',
    scope: 'local',
    attachable: false,
    internal: true,
    enableIPv4: true,
    enableIPv6: false,
    ipam: [{ subnet: '172.20.0.0/16', gateway: '172.20.0.1', ipRange: null }],
    // 有容器接入 → 删除必然 1404，这条正是用来验那个分支的
    containers: [
      {
        id: 'e1f2a3b4c5d6e7f8091a2b3c4d5e6f708192a3b4c5d6e7f8091a2b3c4d5e6f708',
        name: 'order-api',
        macAddress: '02:42:ac:14:00:02',
        ipv4Address: '172.20.0.2/16',
        ipv6Address: null
      }
    ],
    options: { 'com.docker.network.bridge.enable_icc': 'false' },
    labels: { env: 'prod' }
  },
  {
    id: MACVLAN_ID,
    name: 'lan-macvlan',
    driver: 'macvlan',
    scope: 'local',
    attachable: true,
    internal: false,
    enableIPv4: true,
    enableIPv6: true,
    // 自定义驱动：IPAM 为空数组，界面不能因此崩
    ipam: [],
    containers: [],
    options: { parent: 'eth0' },
    labels: {}
  }
]
