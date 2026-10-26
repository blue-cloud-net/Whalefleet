import type {
  ContainerDetail,
  ContainerPort,
  ContainerSummary
} from '@/types/api'

/**
 * mock 用容器数据。
 *
 * 与主机数据的处理一致（见 `data/hosts.ts`）：这些夹具刻意覆盖**每种状态各一个**，
 * 因此颜色映射、按钮互斥、退出码展示这些分支在没有后端的情况下都能走查到。
 *
 * ⚠️ 刻意保留一个 `paused` 容器：它是唯一能验证「过渡态用琥珀色、不落进 ok/down 任一桶」
 * 的夹具。删掉它等于删掉那个测试用例。
 *
 * ## 为什么后面还有一张 35+ 条的生成表
 * 前三个语义夹具只够验证「状态怎么渲染」，但**列表页真正藏 bug 的地方是量**：
 * 三条数据时永远只有第一页，分页、跨页选择、按名称/状态排序后的定位、
 * 「共 N 条」计数、批量操作的部分成功——一条都走不到。
 * 一台真实主机上跑二三十个容器是常态，所以 mock 也得是这个量级。
 *
 * ⚠️ 生成部分**不含随机数**：容器 ID、时间、IP、退出码、运行时长全部由 `name` 哈希推导。
 * 否则每次刷新 ID 与顺序都会变，走查截图对不上、并行测试还会互相污染。
 */

/** 运行中、有端口映射、有健康检查。 */
const RUNNING: ContainerDetail = {
  id: 'a1b2c3d4e5f6a7b8c9d0e1f2a3b4c5d6e7f8a9b0c1d2e3f4a5b6c7d8e9f0a1b2',
  name: 'web-frontend',
  created: '2026-10-01T08:12:33Z',
  image: 'nginx:alpine',
  state: {
    status: 'running',
    running: true,
    paused: false,
    restarting: false,
    startedAt: '2026-10-12T22:04:11Z',
    finishedAt: null,
    // 运行中后端刻意给 null（见 types/api.ts 的注释）——夹具必须照做，
    // 否则前端会在运行中的容器上显示一个假的「退出码 0」。
    exitCode: null,
    oomKilled: false,
    restartCount: 0,
    healthStatus: 'healthy'
  },
  config: {
    image: 'nginx:alpine',
    cmd: ['nginx', '-g', 'daemon off;'],
    entrypoint: ['/docker-entrypoint.sh'],
    env: ['PATH=/usr/local/sbin:/usr/local/bin', 'NGINX_VERSION=1.27.0', 'TZ=Asia/Shanghai'],
    labels: { maintainer: 'NGINX Docker Maintainers', 'com.example.owner': 'web' },
    workingDir: '',
    hostname: 'a1b2c3d4e5f6',
    user: '',
    tty: false
  },
  hostConfig: {
    restartPolicy: 'unless-stopped',
    autoRemove: false,
    networkMode: 'bridge',
    privileged: false,
    memoryLimitBytes: 536870912,
    nanoCpus: 1000000000
  },
  mounts: [
    {
      type: 'bind',
      source: '/srv/web/html',
      destination: '/usr/share/nginx/html',
      readWrite: true
    }
  ],
  networks: [
    {
      name: 'bridge',
      ipAddress: '172.17.0.2',
      gateway: '172.17.0.1',
      macAddress: '02:42:ac:11:00:02'
    }
  ]
}

/** 已停止、退出码非 0（验证「退出码小于 0 时染红」）。 */
const CRASHED: ContainerDetail = {
  id: 'b2c3d4e5f6a7b8c9d0e1f2a3b4c5d6e7f8a9b0c1d2e3f4a5b6c7d8e9f0a1b2c3',
  name: 'batch-worker',
  created: '2026-10-10T03:00:00Z',
  image: 'debian:13',
  state: {
    status: 'exited',
    running: false,
    paused: false,
    restarting: false,
    startedAt: '2026-10-10T03:00:05Z',
    finishedAt: '2026-10-10T03:00:42Z',
    exitCode: 137,
    oomKilled: true,
    restartCount: 5,
    healthStatus: 'none'
  },
  config: {
    image: 'debian:13',
    cmd: ['sh', '-c', 'exit 137'],
    entrypoint: [],
    env: ['PATH=/usr/local/sbin:/usr/local/bin'],
    labels: {},
    workingDir: '/app',
    hostname: 'b2c3d4e5f6a7',
    user: 'root',
    tty: false
  },
  hostConfig: {
    restartPolicy: 'no',
    autoRemove: false,
    networkMode: 'bridge',
    privileged: false,
    // 未限制内存 / CPU：夹具刻意留 null，验证界面显示「未限制」而不是 0 字节。
    memoryLimitBytes: null,
    nanoCpus: null
  },
  mounts: [],
  networks: []
}

/** 已暂停：过渡态，验证琥珀色与「启动/停止」按钮的互斥表现。 */
const PAUSED: ContainerDetail = {
  id: 'c3d4e5f6a7b8c9d0e1f2a3b4c5d6e7f8a9b0c1d2e3f4a5b6c7d8e9f0a1b2c3d4',
  name: 'diagnostics',
  created: '2026-10-11T12:00:00Z',
  image: 'alpine:latest',
  state: {
    status: 'paused',
    running: true,
    paused: true,
    restarting: false,
    startedAt: '2026-10-11T12:00:10Z',
    finishedAt: null,
    exitCode: null,
    oomKilled: false,
    restartCount: 0,
    healthStatus: 'none'
  },
  config: {
    image: 'alpine:latest',
    cmd: ['sleep', 'infinity'],
    entrypoint: [],
    env: [],
    labels: {},
    workingDir: '',
    hostname: 'c3d4e5f6a7b8',
    user: '',
    tty: false
  },
  hostConfig: {
    restartPolicy: 'no',
    autoRemove: false,
    networkMode: 'host',
    privileged: true,
    memoryLimitBytes: null,
    nanoCpus: null
  },
  mounts: [],
  networks: []
}

// ============================================================================
// 长尾数据集：下面这张表是「像真主机」的那部分
// ============================================================================

/** 生成表的一行。字段全部可选，缺省值在 `buildDetail` 里给。 */
interface ContainerSeed {
  name: string
  image: string
  /** Docker 的原始状态文本（前端不翻译，只按它上色） */
  state: 'running' | 'exited' | 'paused' | 'restarting' | 'created' | 'dead'
  /**
   * 端口：`宿主:容器`；宿主留空（`:443`）表示「已暴露但没映射到宿主」。
   * 这种条目后端照旧返回，但**列表里不显示**（见 `describePorts`）。
   */
  ports?: string[]
  /** 健康检查状态；没配健康检查就不传（对外是 `none`，不是 null） */
  health?: 'healthy' | 'unhealthy' | 'starting' | null
  /** 退出码（仅 `exited` / `dead` 用） */
  exitCode?: number
  oomKilled?: boolean
  /** 累计重启次数；省略为 0 */
  restartCount?: number
  restart?: 'no' | 'always' | 'on-failure' | 'unless-stopped'
  autoRemove?: boolean
  /** 内存上限，支持 `512m` / `2g`；`null` 表示未限制 */
  memory?: string | null
  /** CPU 上限（核），如 `1.5`；`null` 表示未限制 */
  cpus?: string | null
  privileged?: boolean
  network?: 'bridge' | 'host' | 'whalefleet-net'
  /** 挂载：`类型:源:目标`，以 `ro` 结尾表示只读 */
  mounts?: string[]
  /** 追加到 `env` 的变量（放密码 / Token 这类，走查时要能看出界面做了脱敏） */
  env?: string[]
  /** 标签里的归属人，默认取名字的第一段 */
  owner?: string
  /** 启动命令 */
  cmd?: string[]
  /** 是否分配了 tty（验证日志页的交互式形态） */
  tty?: boolean
}

const CONTAINER_SEEDS: ContainerSeed[] = [
  // ===== 运行中：本平台自己的服务 =====
  {
    name: 'whalefleet-api',
    image: 'registry.example.com/whalefleet/api:0.1.0-rc.3',
    state: 'running',
    ports: ['8080:8080'],
    health: 'healthy',
    restart: 'unless-stopped',
    memory: '1g',
    cpus: '2',
    network: 'whalefleet-net',
    mounts: ['volume:whalefleet-data:/app/data'],
    env: ['ASPNETCORE_URLS=http://+:8080', 'ConnectionStrings__Persistence__DatabasePath=/app/data/whalefleet.db'],
    owner: 'platform'
  },
  {
    name: 'whalefleet-ui',
    image: 'registry.example.com/whalefleet/ui:0.1.0-rc.3',
    state: 'running',
    ports: ['30123:80'],
    health: 'healthy',
    restart: 'unless-stopped',
    memory: '256m',
    cpus: '0.5',
    network: 'whalefleet-net',
    owner: 'platform'
  },
  {
    name: 'whalefleet-postgres',
    image: 'postgres:16-alpine',
    state: 'running',
    ports: ['5432:5432'],
    health: 'healthy',
    restart: 'unless-stopped',
    memory: '2g',
    cpus: '2',
    mounts: ['volume:pgdata:/var/lib/postgresql/data'],
    env: ['POSTGRES_PASSWORD=change-me-in-prod', 'POSTGRES_DB=whalefleet'],
    owner: 'platform'
  },
  {
    // ⚠️ **刻意带上一个未映射的端口**（33060 = MySQL 的 X Protocol）。
    // MySQL 官方镜像 `EXPOSE` 了 3306 与 33060，而实际部署只会映射 3306——
    // 守护进程仍会把 33060 报回来（`publicPort` 为 null）。
    // 界面上**不显示**它（见 `describePorts` 的「未映射的端口不显示」），
    // 留这个夹具是为了让「被滤掉」这条路径在 mock 模式下也能走查。
    name: 'whalefleet-mysql',
    image: 'mysql:8.4',
    state: 'running',
    ports: ['3306:3306', ':33060'],
    health: 'healthy',
    restart: 'unless-stopped',
    memory: '2g',
    cpus: '2',
    mounts: ['volume:mysqldata:/var/lib/mysql'],
    env: ['MYSQL_ROOT_PASSWORD=change-me-in-prod', 'MYSQL_DATABASE=whalefleet'],
    owner: 'platform'
  },
  {
    name: 'whalefleet-redis',
    image: 'redis:7.2',
    state: 'running',
    ports: [':6379'],
    health: 'healthy',
    restart: 'always',
    memory: '512m',
    mounts: ['volume:redisdata:/data'],
    owner: 'platform'
  },
  {
    name: 'whalefleet-traefik',
    image: 'traefik:v3.2',
    state: 'running',
    ports: ['80:80', '443:443', ':8080'],
    health: 'healthy',
    restart: 'unless-stopped',
    memory: '256m',
    network: 'whalefleet-net',
    mounts: ['bind:/var/run/docker.sock:/var/run/docker.sock:ro'],
    owner: 'platform'
  },
  {
    name: 'whalefleet-watchtower',
    image: 'containrrr/watchtower:1.7.1',
    state: 'running',
    restart: 'always',
    memory: '128m',
    network: 'whalefleet-net',
    mounts: ['bind:/var/run/docker.sock:/var/run/docker.sock:ro'],
    owner: 'platform'
  },

  // ===== 运行中：自托管应用 =====
  {
    name: 'home-assistant',
    image: 'homeassistant/home-assistant:2024.9.3',
    state: 'running',
    ports: ['8123:8123'],
    // 刻意 unhealthy：健康检查失败态在真实环境里很常见，必须能走查
    health: 'unhealthy',
    restart: 'unless-stopped',
    memory: '2g',
    mounts: ['volume:homeassistant:/config'],
    owner: 'home'
  },
  {
    name: 'grafana',
    image: 'grafana/grafana:11.3.0',
    state: 'running',
    ports: ['3000:3000'],
    health: 'healthy',
    restart: 'unless-stopped',
    memory: '512m',
    cpus: '1',
    mounts: ['volume:grafana:/var/lib/grafana'],
    env: ['GF_SECURITY_ADMIN_PASSWORD=admin'],
    owner: 'ops'
  },
  {
    name: 'prometheus',
    image: 'prom/prometheus:v3.0.1',
    state: 'running',
    ports: ['9090:9090'],
    health: 'healthy',
    restart: 'always',
    memory: '4g',
    cpus: '2',
    mounts: ['volume:prometheus:/prometheus'],
    owner: 'ops'
  },
  {
    name: 'node-exporter',
    image: 'prom/node-exporter:v1.8.2',
    state: 'running',
    ports: ['9100:9100'],
    health: 'healthy',
    restart: 'always',
    network: 'host',
    mounts: ['bind:/proc:/host/proc:ro', 'bind:/sys:/host/sys:ro'],
    owner: 'ops'
  },
  {
    name: 'loki',
    image: 'grafana/loki:3.1.1',
    state: 'running',
    ports: ['3100:3100'],
    restart: 'unless-stopped',
    memory: '2g',
    mounts: ['volume:loki:/loki'],
    owner: 'ops'
  },
  {
    name: 'minio',
    image: 'minio/minio:RELEASE.2024-09-22T00-33-43Z',
    state: 'running',
    ports: ['9000:9000', '9001:9001'],
    // starting：健康检查刚起来的中间态，走查要能稳定复现它
    health: 'starting',
    restart: 'unless-stopped',
    memory: '4g',
    mounts: ['volume:minio:/data'],
    owner: 'data'
  },
  {
    name: 'vault',
    image: 'vault:1.17.6',
    state: 'running',
    ports: ['8200:8200'],
    health: 'healthy',
    restart: 'unless-stopped',
    memory: '512m',
    mounts: ['volume:vault:/vault/file'],
    owner: 'sec'
  },
  {
    name: 'keycloak',
    image: 'keycloak/keycloak:26.0',
    state: 'running',
    ports: ['8180:8080'],
    health: 'healthy',
    restart: 'unless-stopped',
    memory: '2g',
    cpus: '2',
    network: 'whalefleet-net',
    env: ['KC_DB=postgres', 'KEYCLOAK_ADMIN=admin'],
    owner: 'sec'
  },
  {
    name: 'rabbitmq',
    image: 'rabbitmq:3.13-management-alpine',
    state: 'running',
    ports: ['5672:5672', '15672:15672'],
    health: 'healthy',
    restart: 'unless-stopped',
    memory: '1g',
    mounts: ['volume:rabbitmq:/var/lib/rabbitmq'],
    owner: 'platform'
  },
  {
    name: 'clickhouse',
    image: 'clickhouse/clickhouse-server:24.8',
    state: 'running',
    ports: ['8123:8123', '9000:9000'],
    health: 'healthy',
    restart: 'unless-stopped',
    memory: '8g',
    cpus: '4',
    mounts: ['volume:clickhouse:/var/lib/clickhouse'],
    owner: 'data'
  },
  {
    name: 'gitea',
    image: 'gitea/gitea:1.22.3',
    state: 'running',
    ports: ['3010:3000', ':2222'],
    health: 'healthy',
    restart: 'unless-stopped',
    memory: '512m',
    mounts: ['volume:gitea:/data'],
    env: ['GITEA__server__ROOT_URL=http://git.example.com/'],
    owner: 'dev'
  },
  {
    name: 'nodered',
    image: 'nodered/node-red:3.4.0',
    state: 'running',
    ports: ['1880:1880'],
    restart: 'unless-stopped',
    memory: '512m',
    mounts: ['volume:nodered:/data'],
    owner: 'home'
  },
  {
    name: 'uptime-kuma',
    image: 'louislam/uptime-kuma:1', // 占位：走查时它只是一个名字
    state: 'running',
    ports: ['3001:3001'],
    health: 'healthy',
    restart: 'unless-stopped',
    memory: '512m',
    mounts: ['volume:uptime-kuma:/app/data'],
    owner: 'ops'
  },
  {
    name: 'paperless',
    image: 'ghcr.io/paperless-ngx/paperless-ngx:2.14',
    state: 'running',
    ports: ['8010:8000'],
    health: 'healthy',
    restart: 'unless-stopped',
    memory: '1g',
    mounts: ['volume:paperless-data:/usr/src/paperless/data'],
    owner: 'office'
  },
  {
    name: 'jellyfin',
    image: 'jellyfin/jellyfin:10.9.11',
    state: 'running',
    ports: ['8096:8096'],
    health: 'healthy',
    restart: 'unless-stopped',
    memory: '4g',
    // 软解需要直通设备：验证「特权 + 设备挂载」在详情页的展示
    privileged: true,
    mounts: ['volume:jellyfin-config:/config', 'volume:jellyfin-cache:/cache', 'bind:/dev/dri:/dev/dri'],
    owner: 'home'
  },
  {
    name: 'plex',
    image: 'linuxserver/plex:1.41.3.9314',
    state: 'running',
    ports: ['32400:32400'],
    restart: 'unless-stopped',
    memory: '4g',
    privileged: true,
    mounts: ['volume:plex-config:/config', 'bind:/mnt/media:/media'],
    owner: 'home'
  },
  {
    name: 'portainer',
    image: 'portainer/portainer-ce:2.21.4',
    state: 'running',
    ports: ['9443:9443'],
    health: 'healthy',
    restart: 'always',
    memory: '256m',
    privileged: true,
    mounts: ['bind:/var/run/docker.sock:/var/run/docker.sock'],
    owner: 'ops'
  },
  {
    name: 'dozzle',
    image: 'amir20/dozzle:8.6', // 占位：走查时它只是一个名字
    state: 'running',
    ports: ['8082:8082'],
    restart: 'unless-stopped',
    memory: '128m',
    mounts: ['bind:/var/run/docker.sock:/var/run/docker.sock:ro'],
    owner: 'ops'
  },
  {
    name: 'caddy-edge',
    image: 'caddy:2.8-alpine',
    state: 'running',
    ports: ['8088:80', '8443:443'],
    health: 'healthy',
    restart: 'unless-stopped',
    memory: '256m',
    mounts: ['volume:caddy-data:/data', 'bind:/srv/caddy:/etc/caddy:ro'],
    owner: 'ops'
  },
  {
    name: 'registry-mirror',
    image: 'registry:2.8.3',
    state: 'running',
    ports: ['5000:5000'],
    restart: 'always',
    memory: '2g',
    mounts: ['volume:registry:/var/lib/registry'],
    owner: 'platform'
  },

  // ===== 已停止：退出码的每一种形态 =====
  {
    name: 'jenkins-runner-41',
    image: 'jenkins/jenkins:lts-jdk21',
    state: 'exited',
    exitCode: 143, // SIGTERM：正常停止，不是失败
    restart: 'on-failure',
    memory: '4g',
    mounts: ['volume:jenkins-home:/var/jenkins_home'],
    owner: 'ci'
  },
  {
    name: 'db-migration-2026-10-14',
    image: 'mcr.microsoft.com/dotnet/sdk:8.0',
    state: 'exited',
    exitCode: 1,
    restart: 'no',
    cpus: '2',
    owner: 'platform'
  },
  {
    name: 'nightly-backup',
    image: 'registry.example.com/team/etl-runner:2.4.1',
    state: 'exited',
    exitCode: 2,
    restart: 'on-failure',
    mounts: ['bind:/srv/backup:/backup'],
    owner: 'data'
  },
  {
    name: 'certbot-renew',
    image: 'certbot/certbot:latest', // 占位：走查时它只是一个名字
    state: 'exited',
    exitCode: 0,
    restart: 'no',
    tty: true,
    mounts: ['volume:certbot:/etc/letsencrypt'],
    owner: 'ops'
  },
  {
    name: 'legacy-grafana',
    image: 'grafana/grafana:10.4.2',
    state: 'exited',
    exitCode: 0,
    restart: 'no',
    mounts: ['volume:grafana-legacy:/var/lib/grafana'],
    owner: 'ops'
  },
  {
    name: 'oom-killed-search',
    image: 'clickhouse/clickhouse-server:24.3',
    state: 'exited',
    exitCode: 137,
    oomKilled: true,
    restart: 'always',
    memory: '512m',
    owner: 'data'
  },
  {
    name: 'debug-shell',
    image: 'ubuntu:24.04',
    state: 'exited',
    exitCode: 0,
    restart: 'no',
    tty: true,
    cmd: ['/bin/bash'],
    owner: 'dev'
  },
  {
    name: 'scratch-runner',
    image: 'alpine:3.20',
    state: 'exited',
    exitCode: 0,
    autoRemove: true,
    restart: 'no',
    owner: 'ci'
  },
  {
    name: 'report-render',
    image: 'registry.example.com/team/report-render:1.9.0',
    state: 'exited',
    exitCode: 1,
    restart: 'on-failure',
    cpus: '1.5',
    mounts: ['volume:reports:/reports'],
    owner: 'data'
  },

  // ===== 其余状态：过渡态与异常态 =====
  {
    name: 'paused-prewarm',
    image: 'python:3.12-slim',
    state: 'paused',
    restart: 'no',
    memory: '1g',
    owner: 'data'
  },
  {
    name: 'flapping-worker',
    image: 'node:22-alpine',
    state: 'restarting',
    restart: 'on-failure',
    memory: '512m',
    owner: 'dev'
  },
  {
    name: 'dns-resolver',
    image: 'coredns/coredns:1.11.3', // 占位：走查时它只是一个名字
    state: 'restarting',
    restart: 'always',
    network: 'host',
    owner: 'ops'
  },
  {
    name: 'newly-pulled-api',
    image: 'registry.example.com/whalefleet/api:0.1.0-rc.4',
    state: 'created',
    restart: 'no',
    owner: 'platform'
  },
  {
    name: 'staged-deploy',
    image: 'registry.example.com/whalefleet/ui:0.1.0-rc.4',
    state: 'created',
    restart: 'no',
    env: ['DEPLOY_SLOT=staging'],
    owner: 'platform'
  },
  {
    name: 'zombie-worker',
    image: 'debian:12-slim',
    state: 'dead',
    exitCode: 137,
    oomKilled: true,
    restart: 'no',
    owner: 'dev'
  }
]

// ============================================================================
// 确定性生成：ID / 时间 / IP / 退出码之外的字段全部由 `name` 哈希推导
// ============================================================================

/** FNV-1a 32 位散列：容器夹具的唯一随机源，必须跨运行稳定。 */
function hash32(seed: string): number {
  let hash = 0x811c9dc5

  for (let index = 0; index < seed.length; index += 1) {
    hash ^= seed.charCodeAt(index)
    hash = Math.imul(hash, 0x01000193) >>> 0
  }

  hash ^= hash >>> 15
  hash = Math.imul(hash, 0x2545f491) >>> 0

  return hash >>> 0
}

/** 64 位十六进制容器 ID（与 Docker 的完整 ID 同形，界面据此显示 short ID）。 */
function containerIdOf(name: string): string {
  return Array.from({ length: 8 }, (_, block) =>
    hash32(`container:${name}#${block}`).toString(16).padStart(8, '0')
  ).join('')
}

/** `'512m'` → 字节；`'2g'` → 字节；`null` → 未限制。解析不了就按未限制处理。 */
function memoryBytesOf(spec: string | null | undefined): number | null {
  if (typeof spec !== 'string') return null

  const match = spec.trim().match(/^(\d+(?:\.\d+)?)\s*([kmg])?b?$/i)
  if (match === null) return null

  const value = Number(match[1])
  const unit = (match[2] ?? '').toLowerCase()

  if (unit === 'k') return Math.round(value * 1024)
  if (unit === 'g') return Math.round(value * 1024 * 1024 * 1024)

  return Math.round(value * 1024 * 1024)
}

/** `'1.5'` → 1_500_000_000（十亿分之一核）；`null` → 未限制。 */
function nanoCpusOf(spec: string | null | undefined): number | null {
  if (typeof spec !== 'string') return null

  const value = Number(spec.trim())
  return Number.isFinite(value) && value > 0 ? Math.round(value * 1_000_000_000) : null
}

/**
 * `'8080:80'` → 映射到宿主；`':443'` → 只暴露未映射。
 *
 * 两种形态都要有：只暴露不映射（`publicPort: null`）在真实环境里非常常见
 * （MySQL 官方镜像的 `33060` 就是典型），而它在列表里**不应该出现**——
 * 列表要把这类条目滤掉，所以 mock 必须造得出来这条路径。
 */
function parsePorts(specs: string[] | undefined): ContainerPort[] {
  return (specs ?? []).map((spec) => {
    const [host, container, protocol] = spec.split(':')
    const privatePort = Number(container)

    return {
      privatePort,
      publicPort: host === '' || host === undefined ? null : Number(host),
      type: protocol === undefined ? 'tcp' : protocol,
      ip: host === '' || host === undefined ? null : '0.0.0.0'
    }
  })
}

/** `'bind:/a:/b:ro'` → 只读挂载；`'volume:pgdata:/var/lib/postgresql/data'` → 命名卷。 */
function parseMounts(specs: string[] | undefined): ContainerDetail['mounts'] {
  return (specs ?? []).map((spec) => {
    const parts = spec.split(':')
    const readOnly = parts[parts.length - 1] === 'ro'

    return {
      type: parts[0],
      source: parts[1],
      destination: readOnly ? parts.slice(2, -1).join(':') : parts.slice(2).join(':'),
      readWrite: !readOnly
    }
  })
}

/** Docker 的人类可读运行时长文案（`Up 3 hours` 那种），按分钟数确定性推导。 */
function humanDuration(minutes: number): string {
  if (minutes < 60) return `${Math.max(minutes, 1)} seconds`
  if (minutes < 60 * 24) {
    const hours = Math.floor(minutes / 60)

    return hours === 1 ? '1 hour' : `${hours} hours`
  }

  const days = Math.floor(minutes / (60 * 24))

  return days === 1 ? '1 day' : `${days} days`
}

/** 列表项的 `status` 文案（后端原样给，前端不翻译）。 */
function statusTextOf(seed: ContainerSeed, minutes: number): string {
  switch (seed.state) {
    case 'exited':
      return `Exited (${seed.exitCode ?? 0}) ${humanDuration(minutes)} ago`
    case 'dead':
      return `Dead (${seed.exitCode ?? 137}) ${humanDuration(minutes)} ago`
    case 'paused':
      return `Up ${humanDuration(minutes)} (Paused)`
    case 'restarting':
      return `Restarting (1) ${humanDuration(minutes)} ago`
    case 'created':
      return 'Created'
    default:
      return `Up ${humanDuration(minutes)}`
  }
}

/** 由种子构造详情夹具。 */
function buildDetail(seed: ContainerSeed, index: number): ContainerDetail {
  const id = containerIdOf(seed.name)
  const seedHash = hash32(`container:${seed.name}`)
  const isRunning = seed.state === 'running' || seed.state === 'paused'
  const createdMinutesAgo = 60 * 24 * (index % 21) + 37
  const upMinutes = seedHash % 20000

  const created = new Date(Date.parse('2026-10-16T09:12:00Z') - createdMinutesAgo * 60_000)
  const started = new Date(created.getTime() + 5000)
  const finished = new Date(started.getTime() + upMinutes * 60_000)
  const network = seed.network ?? 'bridge'
  const command = seed.cmd ?? ['/entrypoint.sh']

  return {
    id,
    name: seed.name,
    created: created.toISOString(),
    image: seed.image,
    state: {
      status: seed.state,
      // `restarting` 时守护进程给的是 running=false + restarting=true，
      // 别图省事写成 true——界面按 running 决定「启动/停止」按钮的互斥。
      running: isRunning,
      paused: seed.state === 'paused',
      restarting: seed.state === 'restarting',
      // 从未启动过（created）为 null——与后端「本次启动时间」的语义一致
      startedAt: seed.state === 'created' ? null : started.toISOString(),
      finishedAt: isRunning ? null : finished.toISOString(),
      exitCode: isRunning ? null : (seed.exitCode ?? 0),
      oomKilled: seed.oomKilled === true,
      // 健康检查状态：夹具不配就是 none（「没配」永远不会变，与「查不到」不同）
      healthStatus: seed.health ?? 'none',
      // 重启次数：只给「反复重启」那个夹具一个非零值，界面要能看出异常
      restartCount: seed.restartCount ?? 0
    },
    config: {
      image: seed.image,
      cmd: command,
      entrypoint: [],
      env: [
        'PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin',
        'TZ=Asia/Shanghai',
        ...(seed.env ?? [])
      ],
      labels: {
        'com.example.owner': seed.owner ?? seed.name.split('-')[0],
        'com.example.managed-by': 'whalefleet'
      },
      workingDir: '',
      hostname: id.slice(0, 12),
      user: seed.privileged ? 'root' : '',
      tty: seed.tty === true
    },
    hostConfig: {
      restartPolicy: seed.restart ?? 'no',
      autoRemove: seed.autoRemove === true,
      networkMode: network,
      privileged: seed.privileged === true,
      memoryLimitBytes: memoryBytesOf(seed.memory),
      nanoCpus: nanoCpusOf(seed.cpus)
    },
    mounts: parseMounts(seed.mounts),
    networks:
      network === 'host'
        ? []
        : [
            {
              name: network,
              ipAddress: `172.18.0.${(index % 250) + 2}`,
              gateway: '172.18.0.1',
              macAddress: `02:42:ac:12:00:${((index % 250) + 2).toString(16).padStart(2, '0')}`
            }
          ]
  }
}

/** 由种子表派生的详情夹具（顺序即列表顺序：创建时间降序）。 */
const GENERATED_DETAILS: ContainerDetail[] = CONTAINER_SEEDS.map((seed, index) =>
  buildDetail(seed, index)
).sort((a, b) => b.created.localeCompare(a.created))

/** 全部夹具（详情形态）。 */
export const MOCK_CONTAINER_DETAILS: ContainerDetail[] = [
  RUNNING,
  CRASHED,
  PAUSED,
  ...GENERATED_DETAILS
]

/**
 * 端口映射表（按容器名索引）。
 *
 * `ContainerDetail` 里**没有**端口字段（后端 inspect 的精选投影不返回），
 * 所以列表项的端口只能另存一份。但它必须与详情同源——从同一张种子表生成，
 * 否则会出现「列表显示 8080 → 80、详情页找不到任何端口」这种自相矛盾，
 * 而这类矛盾在真实后端上表现为「列表和详情对不上」，极难定位。
 */
const PORTS_BY_NAME: Record<string, ContainerPort[]> = {
  [RUNNING.name]: [
    // ⚠️ 刻意给同一个端口配 **IPv4 + IPv6** 两条：`-p 8080:80` 在真实 Docker 上
    // 就是这样（`0.0.0.0` 与 `::` 各一条）。不这么配，界面上的「端口合并」
    // 在 mock 模式下永远走不到——那正是最容易被误以为「已经做好了」的地方。
    { privatePort: 80, publicPort: 8080, type: 'tcp', ip: '0.0.0.0' },
    { privatePort: 80, publicPort: 8080, type: 'tcp', ip: '::' },
    // 只暴露不映射：镜像 EXPOSE 了它，但没绑到宿主。
    // 界面上**不显示**这一条（见 `describePorts` 的「未映射的端口不显示」），
    // 留着它是为了让「被滤掉」这条路径在 mock 模式下也能走查。
    { privatePort: 443, publicPort: null, type: 'tcp', ip: null }
  ],
  ...Object.fromEntries(
    CONTAINER_SEEDS.map((seed) => [seed.name, parsePorts(seed.ports)])
  )
}

/** 已运行时长（分钟）：列表 `status` 文案用，与详情里的 `startedAt` 保持一致。 */
const UP_MINUTES_BY_NAME: Record<string, number> = Object.fromEntries(
  CONTAINER_SEEDS.map((seed) => [seed.name, hash32(`container:${seed.name}`) % 20000])
)

/** `web-frontend` 的运行时长单独给（语义夹具不动，它的文案手写）。 */
UP_MINUTES_BY_NAME[RUNNING.name] = 120
UP_MINUTES_BY_NAME[CRASHED.name] = 60
UP_MINUTES_BY_NAME[PAUSED.name] = 90

/**
 * 由详情派生出列表项。
 *
 * 刻意**派发**而不是手写第二份：详情与列表的形状必须一致（`state` 是同一个字符串、
 * 端口从哪来都一样），手写两份迟早漂移，而漂移的表现是「列表显示运行中、
 * 点进详情却是已停止」这种最费解的 bug。
 */
function toSummary(detail: ContainerDetail): ContainerSummary {
  const seed = CONTAINER_SEEDS.find((item) => item.name === detail.name)

  return {
    id: detail.id,
    name: detail.name,
    image: detail.image,
    imageId: `sha256:${detail.id.slice(0, 32)}`,
    command: detail.config.cmd.join(' '),
    created: detail.created,
    state: detail.state.status,
    // 与后端一致：人类可读状态由守护进程给，mock 这里造一个近似值。
    status: seed
      ? statusTextOf(seed, UP_MINUTES_BY_NAME[detail.name] ?? 0)
      : detail.state.running
        ? 'Up 2 hours'
        : `Exited (${detail.state.exitCode}) 1 hour ago`,
    ports: PORTS_BY_NAME[detail.name] ?? []
  }
}

/** 默认场景的容器列表。 */
export const MOCK_CONTAINERS_DEFAULT: ContainerSummary[] = MOCK_CONTAINER_DETAILS.map(toSummary)

/** 容器日志夹具（含多行堆栈与长行，验证换行与横向溢出）。 */
export const MOCK_CONTAINER_LOGS = `2026/10/12 22:04:11 [notice] 1#1: using the "epoll" event method
2026/10/12 22:04:11 [notice] 1#1: nginx/1.27.0
2026/10/12 22:04:11 [notice] 1#1: OS: Linux 6.8.0-45-generic
2026/10/12 22:04:11 [notice] 1#1: start worker processes
2026/10/12 22:04:11 [notice] 1#1: start worker process 21
2026/10/12 23:19:02 [error] 21#21: *14 connect() failed (111: Connection refused) while connecting to upstream, client: 10.0.0.7, server: _, request: "GET /api/health HTTP/1.1", upstream: "http://172.17.0.3:8080/api/health", host: "web.example.com"
2026/10/12 23:19:02 [warn] 21#21: *14 upstream server temporarily disabled while connecting to upstream, client: 10.0.0.7, server: _, request: "GET /api/health HTTP/1.1"
`
