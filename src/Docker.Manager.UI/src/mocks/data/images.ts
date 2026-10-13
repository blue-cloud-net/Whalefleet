import type { ImageDetail, ImageSummary } from '@/types/api'

/**
 * mock 用镜像数据。
 *
 * 刻意覆盖三种形态，否则界面的分支走查不到：
 * 1. **多标签镜像**（`nginx:latest` + `nginx:1.27`）——验证折叠成「主名 +N」
 * 2. **悬挂镜像**（`repoTags: []`）——验证 `<none>` 占位与「按 ID 删除」
 * 3. **被容器引用的镜像**（`containerCount > 0`）——验证不带 force 删除时回 1409/409
 */

const NGINX_ID = 'sha256:3f2c0b1a9d8e7f6c5b4a39281706f5e4d3c2b1a09f8e7d6c5b4a39281706f5e4'
const REDIS_ID = 'sha256:9a8b7c6d5e4f3a2b1c0d9e8f7a6b5c4d3e2f1a0b9c8d7e6f5a4b3c2d1e0f9a8b'
const ALPINE_ID = 'sha256:1a2b3c4d5e6f7a8b9c0d1e2f3a4b5c6d7e8f9a0b1c2d3e4f5a6b7c8d9e0f1a2b'
const DANGLING_ID = 'sha256:deadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeef'

/** 默认场景的镜像列表（顺序即服务端顺序：创建时间降序）。 */
export const MOCK_IMAGES_DEFAULT: ImageSummary[] = [
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

/** 镜像详情（inspect 投影）。`env` 刻意放一条看起来像凭据的变量，验证界面的敏感提示。 */
export const MOCK_IMAGE_DETAILS: ImageDetail[] = [
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
