import type { ImageDetail, ImageSummary } from '@/types/api'

/**
 * mock 用镜像数据。
 *
 * 语义夹具（`MOCK_IMAGES_DEFAULT` 的前 4 条）刻意覆盖三种形态，否则界面的分支走查不到：
 * 1. **多标签镜像**（`nginx:latest` + `nginx:1.27`）——验证折叠成「主名 +N」
 * 2. **悬挂镜像**（`repoTags: []`）——验证 `<none>` 占位与「按 ID 删除」
 * 3. **被容器引用的镜像**（`containerCount > 0`）——验证不带 force 删除时回 1409/409
 *
 * ## 为什么后面还有一张 60+ 条的长尾表
 * 语义夹具只要 4 条就够，但**列表页的问题几乎全藏在量上**：总数不足一页时，
 * 分页控件不出现、跨页选择选不出来、排序后的定位无从验证、搜索命中数永远是「全部」——
 * 页面看着「正常」只是因为它永远停在第一页。这类缺陷在夹具里看不出来，
 * 在真实主机（几十上百个镜像）上一眼就能撞见，所以 mock 必须**够大才能测**。
 *
 * 因此这里在语义夹具之后补了 `IMAGE_SEEDS`：默认场景的镜像总数稳定超过 60，
 * 于是 pageSize 选 10 / 20 / 50 都能翻页，排序 / 搜索 / 批量删除都有真实负载。
 *
 * ⚠️ 生成的 ID 与时间戳**必须确定性**（由 `ref` 哈希推导，不用随机数）：
 * 随机数会让每次刷新列表顺序与 ID 都变，走查截图对不上、并行测试还会互相污染。
 */

const NGINX_ID = 'sha256:3f2c0b1a9d8e7f6c5b4a39281706f5e4d3c2b1a09f8e7d6c5b4a39281706f5e4'
const REDIS_ID = 'sha256:9a8b7c6d5e4f3a2b1c0d9e8f7a6b5c4d3e2f1a0b9c8d7e6f5a4b3c2d1e0f9a8b'
const ALPINE_ID = 'sha256:1a2b3c4d5e6f7a8b9c0d1e2f3a4b5c6d7e8f9a0b1c2d3e4f5a6b7c8d9e0f1a2b'
const DANGLING_ID = 'sha256:deadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeef'

/** 语义夹具：每条都在验证一个具体的界面分支，删掉即删掉对应用例。 */
const SEMANTIC_IMAGES: ImageSummary[] = [
  {
    id: NGINX_ID,
    repoTags: ['nginx:latest', 'nginx:1.27'],
    repoDigests: ['nginx@sha256:0f2c1b3a4d5e6f708192a3b4c5d6e7f8091a2b3c4d5e6f708192a3b4c5d6e7f8'],
    created: '2026-09-28T03:12:45Z',
    size: 187_654_321,
    sharedSize: 12_345_678,
    containerCount: 2
  },
  {
    id: REDIS_ID,
    repoTags: ['redis:7-alpine'],
    repoDigests: ['redis@sha256:1a2b3c4d5e6f708192a3b4c5d6e7f8091a2b3c4d5e6f708192a3b4c5d6e7f809'],
    created: '2026-10-01T08:20:00Z',
    size: 41_943_040,
    sharedSize: 0,
    containerCount: 1
  },
  {
    id: ALPINE_ID,
    repoTags: ['alpine:3.20'],
    repoDigests: [],
    created: '2026-10-05T14:02:10Z',
    size: 7_864_320,
    sharedSize: 5_242_880,
    containerCount: 0
  },
  {
    // 悬挂镜像：构建中间层，没有任何标签
    id: DANGLING_ID,
    repoTags: [],
    repoDigests: [],
    created: '2026-10-06T09:15:00Z',
    size: 3_145_728,
    sharedSize: 0,
    containerCount: 0
  }
]

// ============================================================================
// 长尾数据集：下面这张表是「像真主机」的那部分
// ============================================================================

/** 生成表的一行。 */
interface ImageSeed {
  /**
   * 完整引用。**空格分隔多个标签 = 同一个镜像的多个 tag**（与 Docker 语义一致，
   * 不是多个镜像）；`dangling` 为 true 时整个字段只当种子用，不出现在界面上。
   */
  ref: string
  /** 未压缩字节数。刻意跨 700 KB ~ 3.2 GB，验证体积列的 KiB / MiB / GiB 换算。 */
  size: number
  /** 被容器引用的数量；>0 时不带 `force` 删除会回 409。 */
  usedBy?: number
  /** 悬挂镜像（构建中间层）：没有任何标签，界面上是 `<none>`，只能按 ID 操作。 */
  dangling?: boolean
}

const IMAGE_SEEDS: ImageSeed[] = [
  // ===== 数据库 =====
  { ref: 'postgres:16-alpine', size: 431_003_904, usedBy: 2 },
  { ref: 'postgres:16 postgres:16-bookworm', size: 1_432_414_208, usedBy: 1 },
  { ref: 'mysql:8.4', size: 611_606_272, usedBy: 1 },
  { ref: 'mariadb:11.4', size: 412_975_872 },
  { ref: 'mongodb:7.0', size: 803_447_552 },
  { ref: 'redis:7.2', size: 117_440_512, usedBy: 3 },
  { ref: 'valkey/valkey:8-alpine', size: 44_957_696 },
  { ref: 'memcached:1.6-alpine', size: 22_544_384 },
  { ref: 'influxdb:2.7', size: 496_803_840 },
  { ref: 'clickhouse/clickhouse-server:24.8', size: 2_217_842_432 },
  { ref: 'timescale/timescaledb:2.17-pg16', size: 1_204_883_456 },

  // ===== 中间件 / 队列 / 反向代理 =====
  { ref: 'rabbitmq:3.13-management-alpine', size: 145_981_440 },
  { ref: 'bitnami/kafka:3.8', size: 480_046_336 },
  { ref: 'traefik:v3.2', size: 130_023_424, usedBy: 1 },
  { ref: 'caddy:2.8-alpine', size: 48_234_496 },
  { ref: 'haproxy:3.0-alpine', size: 25_165_824 },
  { ref: 'envoyproxy/envoy:v1.31-latest', size: 268_632_064 },
  { ref: 'nginx:1.27-alpine', size: 76_341_248, usedBy: 4 },
  { ref: 'keycloak/keycloak:26.0', size: 462_438_400 },

  // ===== 监控 / 日志 / 链路 =====
  { ref: 'grafana/grafana:11.3.0', size: 411_041_792, usedBy: 1 },
  { ref: 'prom/prometheus:v3.0.1', size: 262_078_464 },
  { ref: 'prom/alertmanager:v0.27.0', size: 65_863_680 },
  { ref: 'prom/node-exporter:v1.8.2', size: 25_952_256 },
  { ref: 'prom/blackbox-exporter:v0.26.0', size: 21_487_616 },
  { ref: 'prom/postgres-exporter:v0.16.1', size: 23_068_672 },
  { ref: 'grafana/loki:3.1.1', size: 152_567_808 },
  { ref: 'grafana/tempo:2.6.0', size: 133_955_584 },
  { ref: 'grafana/prometheus:v2.55.1', size: 258_670_592 },
  { ref: 'victoriametrics/victoria-metrics:v1.101.0', size: 92_274_688 },

  // ===== CI / 语言运行时 / 基础镜像 =====
  { ref: 'jenkins/jenkins:lts-jdk21', size: 907_169_792 },
  { ref: 'node:22-alpine', size: 181_403_648, usedBy: 2 },
  { ref: 'node:20-bookworm-slim', size: 251_658_240 },
  { ref: 'python:3.12-slim', size: 129_761_280 },
  { ref: 'golang:1.23-alpine', size: 321_730_560 },
  { ref: 'rust:1.81-slim', size: 420_249_856 },
  { ref: 'openjdk:21-slim', size: 265_289_344 },
  { ref: 'maven:3.9-eclipse-temurin-21', size: 705_498_752 },
  { ref: 'mcr.microsoft.com/dotnet/aspnet:8.0', size: 224_395_264, usedBy: 1 },
  { ref: 'mcr.microsoft.com/dotnet/runtime:8.0-alpine', size: 34_603_008 },
  { ref: 'mcr.microsoft.com/dotnet/sdk:8.0', size: 803_500_800 },
  { ref: 'eclipse-temurin:21-jre-alpine', size: 187_695_104 },
  { ref: 'ubuntu:24.04', size: 81_788_928 },
  { ref: 'debian:12-slim', size: 74_809_344 },
  { ref: 'alpine:3.19', size: 7_798_784 },
  { ref: 'busybox:1.36', size: 4_194_304 },
  { ref: 'docker:27.3-cli', size: 55_574_528 },
  { ref: 'containrrr/watchtower:1.7.1', size: 25_952_256 },
  { ref: 'alpine/git:2.45.2', size: 11_141_120 },

  // ===== 应用 =====
  { ref: 'homeassistant/home-assistant:2024.9.3', size: 1_987_813_376 },
  { ref: 'linuxserver/plex:1.41.3.9314', size: 1_258_291_200 },
  { ref: 'jellyfin/jellyfin:10.9.11', size: 1_431_664_384 },
  { ref: 'minio/minio:RELEASE.2024-09-22T00-33-43Z', size: 191_397_888, usedBy: 1 },
  { ref: 'nextcloud:29.0.3', size: 712_261_632 },
  { ref: 'vaultwarden/server:1.32.0', size: 40_632_320 },
  { ref: 'nodered/node-red:3.4.0', size: 322_961_408 },
  { ref: 'portainer/portainer-ce:2.21.4', size: 264_241_152 },
  { ref: 'registry:2.8.3', size: 30_408_704 },
  { ref: 'gitea/gitea:1.22.3', size: 152_633_344 },
  { ref: 'vault:1.17.6', size: 351_477_760 },
  { ref: 'sonarqube:10.7-community', size: 723_517_440 },
  { ref: 'gitlab/gitlab-ce:17.3.1', size: 3_221_225_472 },

  // ===== 私有仓库：与官方镜像混杂，验证「仓库名 ≠ 标签」的显示与长仓库名换行 =====
  { ref: 'registry.example.com/whalefleet/api:0.1.0-rc.3', size: 184_549_376, usedBy: 2 },
  { ref: 'registry.example.com/whalefleet/ui:0.1.0-rc.3', size: 22_282_240, usedBy: 2 },
  { ref: 'registry.example.com/team/etl-runner:2.4.1', size: 342_097_152 },
  { ref: 'registry.example.com/team/base-images/jdk17-build:2024.09', size: 1_147_312_896 },
  {
    ref: 'registry.example.com/team/report-render:1.9.0 registry.example.com/team/report-render:2024.09-hotfix',
    size: 268_632_064
  },
  // 仓库名里带端口：验证「去掉标签」按最后一个冒号切而不是第一个
  { ref: 'registry.internal:5000/app/api:2024.10.1', size: 178_257_920, usedBy: 1 },

  // ===== 极小镜像：验证体积列不会塌成「0 B」 =====
  { ref: 'registry.k8s.io/pause:3.9', size: 742_400 },
  { ref: 'gcr.io/distroless/static-debian12:nonroot', size: 2_306_048 },

  // ===== 悬挂镜像：构建中间层，界面上是 `<none>` =====
  { ref: 'build-cache/frontend-deps:layer-4193', size: 1_604_759_552, dangling: true },
  { ref: 'build-cache/backend-deps:layer-4188', size: 892_289_024, dangling: true },
  { ref: 'build-cache/go-mod-cache', size: 412_975_872, dangling: true },
  { ref: 'build-cache/base:rootfs', size: 58_720_256, dangling: true },
  { ref: 'build-cache/frontend-final:tmp', size: 47_775_744, dangling: true }
]

/** 详情（inspect 投影）里那些「运行时画像」。表里没有的仓库走通用画像。 */
interface RuntimeProfile {
  entrypoint: string[]
  cmd: string[]
  exposedPorts: string[]
  volumes: string[]
  author: string
  env?: string[]
  workingDir?: string
  layerCount: number
}

/** 通用画像：只保证字段形状正确，内容对不上具体仓库（走查时看不出来是编的）。 */
const GENERIC_PROFILE: RuntimeProfile = {
  entrypoint: [],
  cmd: ['/bin/sh'],
  exposedPorts: [],
  volumes: [],
  author: '',
  env: ['PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin'],
  layerCount: 4
}

const RUNTIME_PROFILES: Record<string, RuntimeProfile> = {
  nginx: {
    entrypoint: ['/docker-entrypoint.sh'],
    cmd: ['nginx', '-g', 'daemon off;'],
    exposedPorts: ['80/tcp', '443/tcp'],
    volumes: ['/var/cache/nginx', '/etc/nginx/conf.d'],
    author: 'NGINX Docker Maintainers',
    env: ['NGINX_VERSION=1.27.0'],
    layerCount: 7
  },
  postgres: {
    entrypoint: ['docker-entrypoint.sh'],
    cmd: ['postgres'],
    exposedPorts: ['5432/tcp'],
    volumes: ['/var/lib/postgresql/data'],
    author: 'PostgreSQL Docker Community',
    env: ['PGDATA=/var/lib/postgresql/data/pgdata', 'PG_MAJOR=16'],
    layerCount: 11
  },
  mysql: {
    entrypoint: ['docker-entrypoint.sh'],
    cmd: ['mysqld'],
    exposedPorts: ['3306/tcp'],
    volumes: ['/var/lib/mysql'],
    author: 'Oracle and MySQL Docker Community',
    layerCount: 12
  },
  redis: {
    entrypoint: ['docker-entrypoint.sh'],
    cmd: ['redis-server'],
    exposedPorts: ['6379/tcp'],
    volumes: ['/data'],
    author: 'Redis Ltd.',
    layerCount: 6
  },
  'valkey/valkey': {
    entrypoint: ['docker-entrypoint.sh'],
    cmd: ['valkey-server'],
    exposedPorts: ['6379/tcp'],
    volumes: ['/data'],
    author: 'Redis Ltd.',
    layerCount: 6
  },
  traefik: {
    entrypoint: ['/entrypoint.sh'],
    cmd: [],
    exposedPorts: ['80/tcp', '443/tcp', '8080/tcp'],
    volumes: [],
    author: 'Traefik Authors',
    layerCount: 5
  },
  'grafana/grafana': {
    entrypoint: ['/run.sh'],
    cmd: [],
    exposedPorts: ['3000/tcp'],
    volumes: ['/var/lib/grafana'],
    author: 'Grafana Labs',
    env: ['GF_SECURITY_ADMIN_USER=admin'],
    layerCount: 9
  },
  'prom/prometheus': {
    entrypoint: ['/bin/prometheus'],
    cmd: [],
    exposedPorts: ['9090/tcp'],
    volumes: ['/prometheus'],
    author: 'Prometheus Authors',
    layerCount: 8
  },
  'minio/minio': {
    entrypoint: ['/usr/bin/docker-entrypoint.sh'],
    cmd: ['minio', 'server', '/data'],
    exposedPorts: ['9000/tcp', '9001/tcp'],
    volumes: ['/data'],
    author: 'MinIO',
    layerCount: 7
  },
  'homeassistant/home-assistant': {
    entrypoint: [],
    cmd: [],
    exposedPorts: ['8123/tcp'],
    volumes: ['/config'],
    author: 'Home Assistant',
    layerCount: 14
  },
  'jenkins/jenkins': {
    entrypoint: [],
    cmd: [],
    exposedPorts: ['8080/tcp', '50000/tcp'],
    volumes: ['/var/jenkins_home'],
    author: 'Jenkins Project',
    layerCount: 10
  },
  node: {
    entrypoint: ['docker-entrypoint.sh'],
    cmd: ['node'],
    exposedPorts: [],
    volumes: ['/home/node/app'],
    author: 'Node.js Docker',
    env: ['NODE_VERSION=22.9.0', 'YARN_VERSION=1.22.22'],
    workingDir: '/home/node/app',
    layerCount: 9
  },
  'mcr.microsoft.com/dotnet/aspnet': {
    entrypoint: ['/app/Api'],
    cmd: [],
    exposedPorts: ['8080/tcp'],
    volumes: [],
    author: 'Microsoft Corporation',
    env: ['ASPNETCORE_HTTP_PORTS=8080', 'DOTNET_VERSION=8.0.10'],
    workingDir: '/app',
    layerCount: 9
  },
  'mcr.microsoft.com/dotnet/runtime': {
    entrypoint: ['/app'],
    cmd: [],
    exposedPorts: [],
    volumes: [],
    author: 'Microsoft Corporation',
    env: ['DOTNET_VERSION=8.0.10'],
    workingDir: '/app',
    layerCount: 7
  },
  'registry.example.com/whalefleet/api': {
    entrypoint: ['/app/Whalefleet.Api'],
    cmd: [],
    exposedPorts: ['8080/tcp'],
    volumes: [],
    author: '',
    env: ['ASPNETCORE_URLS=http://+:8080', 'TZ=Asia/Shanghai'],
    workingDir: '/app',
    layerCount: 10
  },
  'registry.example.com/whalefleet/ui': {
    entrypoint: ['/docker-entrypoint.sh'],
    cmd: ['nginx', '-g', 'daemon off;'],
    exposedPorts: ['80/tcp'],
    volumes: [],
    author: '',
    layerCount: 4
  }
}

// ============================================================================
// 确定性生成：ID、时间戳、共享层、体积占比全部由 `ref` 推导，不含随机数
// ============================================================================

/** FNV-1a 32 位散列：夹具 ID 与时间抖动的唯一来源，必须跨运行稳定。 */
function hash32(seed: string): number {
  let hash = 0x811c9dc5

  for (let index = 0; index < seed.length; index += 1) {
    hash ^= seed.charCodeAt(index)
    hash = Math.imul(hash, 0x01000193) >>> 0
  }

  // 再混淆一轮：否则相邻种子（前缀相同）会得到肉眼可见的相邻输出
  hash ^= hash >>> 15
  hash = Math.imul(hash, 0x2545f491) >>> 0

  return hash >>> 0
}

/** 拼出与真实 `sha256:` 同形的 64 位十六进制 ID（8 段 × 8 位）。 */
function digestOf(seed: string): string {
  return Array.from({ length: 8 }, (_, block) =>
    hash32(`${seed}#${block}`).toString(16).padStart(8, '0')
  ).join('')
}

/**
 * 去掉标签拿仓库名。
 *
 * 必须按**最后一个冒号**切，而不是 `split(':')`：私有仓库可能自带端口
 * （`registry.internal:5000/app/api:2024.10.1`），按第一个冒号切会把端口留在标签里。
 */
function repositoryOf(tag: string): string {
  const colon = tag.lastIndexOf(':')
  const slash = tag.lastIndexOf('/')

  return colon > slash ? tag.slice(0, colon) : tag
}

/**
 * 生成镜像的创建时间。
 *
 * 以固定时刻为起点按固定步长**递减**，于是列表天然满足「创建时间降序」
 * （`MOCK_IMAGES_DEFAULT` 顶部注释承诺的顺序）。抖动上限 22 分钟 < 步长 43 分钟，
 * 保证相邻两条不会交叉。
 */
const CREATED_EPOCH = Date.parse('2026-10-16T09:12:00Z')
const CREATED_STEP_MINUTES = 43

function createdAt(index: number, seed: string): string {
  const jitter = hash32(`${seed}@t`) % 22
  const minutes = (CREATED_STEP_MINUTES - jitter) * index

  return new Date(CREATED_EPOCH - minutes * 60_000).toISOString()
}

/** 共享层体积：按体积的 6%~26% 取（镜像间共享的基底层），刻意不与真实算法对齐。 */
function sharedSizeOf(size: number, seed: string): number {
  return Math.round((size * ((hash32(`${seed}#s`) % 21) + 6)) / 100)
}

/** 由种子生成列表项。 */
const GENERATED_IMAGES: ImageSummary[] = IMAGE_SEEDS.map((seed, index) => {
  const key = seed.dangling ? `dangling:${seed.ref}` : seed.ref
  const repoTags = seed.dangling ? [] : seed.ref.split(' ')

  return {
    id: `sha256:${digestOf(key)}`,
    repoTags,
    repoDigests: repoTags.map((tag) => `${repositoryOf(tag)}@sha256:${digestOf(`${tag}@d`)}`),
    created: createdAt(index, key),
    size: seed.size,
    sharedSize: sharedSizeOf(seed.size, key),
    containerCount: seed.usedBy ?? 0
  }
})

/** 默认场景的镜像列表（顺序即服务端顺序：创建时间降序）。 */
export const MOCK_IMAGES_DEFAULT: ImageSummary[] = [...SEMANTIC_IMAGES, ...GENERATED_IMAGES]

/** 语义夹具的详情。`env` 刻意放一条看起来像凭据的变量，验证界面的敏感提示。 */
const SEMANTIC_IMAGE_DETAILS: ImageDetail[] = [
  {
    id: NGINX_ID,
    repoTags: ['nginx:latest', 'nginx:1.27'],
    repoDigests: ['nginx@sha256:0f2c1b3a4d5e6f708192a3b4c5d6e7f8091a2b3c4d5e6f708192a3b4c5d6e7f8'],
    created: '2026-09-28T03:12:45Z',
    size: 187_654_321,
    architecture: 'amd64',
    os: 'linux',
    osVersion: '',
    author: '',
    comment: '',
    env: ['PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin', 'NGINX_VERSION=1.27.0'],
    cmd: ['nginx', '-g', 'daemon off;'],
    entrypoint: ['/docker-entrypoint.sh'],
    exposedPorts: ['80/tcp', '443/tcp'],
    volumes: ['/var/cache/nginx', '/etc/nginx/conf.d'],
    labels: { maintainer: 'NGINX Docker Maintainers', 'org.opencontainers.image.version': '1.27.0' },
    workingDir: '',
    layerCount: 7
  },
  {
    id: REDIS_ID,
    repoTags: ['redis:7-alpine'],
    repoDigests: ['redis@sha256:1a2b3c4d5e6f708192a3b4c5d6e7f8091a2b3c4d5e6f708192a3b4c5d6e7f809'],
    created: '2026-10-01T08:20:00Z',
    size: 41_943_040,
    architecture: 'arm64',
    os: 'linux',
    osVersion: '',
    author: '',
    comment: '',
    env: ['REDIS_PASSWORD=change-me-in-prod', 'PATH=/usr/local/sbin:/usr/local/bin'],
    cmd: ['redis-server'],
    entrypoint: ['docker-entrypoint.sh'],
    exposedPorts: ['6379/tcp'],
    volumes: ['/data'],
    labels: {},
    workingDir: '/data',
    layerCount: 6
  },
  {
    id: ALPINE_ID,
    repoTags: ['alpine:3.20'],
    repoDigests: [],
    created: '2026-10-05T14:02:10Z',
    size: 7_864_320,
    architecture: 'amd64',
    os: 'linux',
    osVersion: '',
    author: 'Natanael Copa <ncopa@alpinelinux.org>',
    comment: '',
    env: ['PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin'],
    cmd: ['/bin/sh'],
    entrypoint: [],
    exposedPorts: [],
    volumes: [],
    labels: {},
    workingDir: '/',
    layerCount: 1
  },
  {
    id: DANGLING_ID,
    repoTags: [],
    repoDigests: [],
    created: '2026-10-06T09:15:00Z',
    size: 3_145_728,
    architecture: 'amd64',
    os: 'linux',
    osVersion: '',
    author: '',
    comment: '',
    env: [],
    cmd: ['/bin/sh', '-c', '#(nop) ADD file:…'],
    entrypoint: [],
    exposedPorts: [],
    volumes: [],
    labels: {},
    workingDir: '',
    layerCount: 3
  }
]

/**
 * 列表里每一条都必须有详情。
 *
 * 否则点进某一行会拿到 1401——列表能显示、详情打不开，比没有 mock 数据更难排查。
 * 因此这里是 `MOCK_IMAGES_DEFAULT` 的严格超集，而不是另外维护一张表。
 */
const GENERATED_IMAGE_DETAILS: ImageDetail[] = GENERATED_IMAGES.map((image) => {
  const seedKey = image.repoTags[0] ?? image.id
  const repository = image.repoTags.length > 0 ? repositoryOf(seedKey) : ''
  const profile = RUNTIME_PROFILES[repository] ?? GENERIC_PROFILE

  return {
    id: image.id,
    repoTags: image.repoTags,
    repoDigests: image.repoDigests,
    created: image.created,
    size: image.size,
    // 架构由哈希决定（amd64 为主、arm64 少量），验证详情页的架构字段不是硬编码
    architecture: hash32(`${seedKey}#a`) % 5 === 0 ? 'arm64' : 'amd64',
    os: 'linux',
    osVersion: '',
    author: profile.author,
    comment: '',
    env: [
      'PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin',
      ...(profile.env ?? [])
    ],
    cmd: profile.cmd,
    entrypoint: profile.entrypoint,
    exposedPorts: profile.exposedPorts,
    volumes: profile.volumes,
    labels: {
      'org.opencontainers.image.created': image.created.slice(0, 10),
      ...(repository.startsWith('registry.example.com') || repository.startsWith('registry.internal')
        ? { 'org.opencontainers.image.source': repository }
        : {})
    },
    workingDir: profile.workingDir ?? '',
    layerCount: profile.layerCount
  }
})

/** 与列表一一对应的详情夹具。 */
export const MOCK_IMAGE_DETAILS: ImageDetail[] = [
  ...SEMANTIC_IMAGE_DETAILS,
  ...GENERATED_IMAGE_DETAILS
]
