import type { StackDetail, StackSummary } from '@/types/api'
/**
 * mock 用编排数据（0.10.0）。
 *
 * ## 刻意覆盖的四种形态
 * 1. **全在跑**（`shop`）——正常态。
 * 2. **部分在跑**（`blog`）——「部分服务没起来」是编排最常见的问题，
 *    只有 running / stopped 两档时它会被误报成 stopped。
 * 3. **全停**（`lab`）——停止操作后的常态。
 * 4. **不受管**（`external`）——用户自己在命令行 `docker compose up` 的项目：
 *    界面上必须能看出它「不能更新」，否则用户点了必然失败。
 *
 * ## 为什么要 mock「服务级状态」
 * 编排页的主体是**服务**而不是容器：用户关心「哪个服务没起来」，
 * 只给一个项目级的 running / stopped 是答不上这个问题的。
 */

/**
 * 夹具里几个具名的编排。
 *
 * ⚠️ **用具名常量而不是数组下标**：`noUncheckedIndexedAccess` 下
 * `MOCK_STACKS_DEFAULT[0]` 的类型是 `StackSummary | undefined`，
 * 直接塞进 `summary` 会报类型错误——而「数组第 0 项是 shop」这件事
 * 在下标写法里完全看不出来。
 */
const SHOP_SUMMARY: StackSummary = {
  name: 'shop',
  managed: true,
  status: 'running',
  serviceCount: 2,
  containerCount: 2,
  runningCount: 2,
  networkCount: 1,
  volumeCount: 1,
  composeAvailable: true,
  webhookEnabled: false,
  webhookTokenHint: null,
  webhookLastUsedAt: null,
  updatedAt: '2026-10-18T08:12:40Z'
}

const BLOG_SUMMARY: StackSummary = {
  name: 'blog',
  managed: true,
  status: 'partial',
  serviceCount: 3,
  containerCount: 3,
  runningCount: 2,
  networkCount: 1,
  volumeCount: 2,
  composeAvailable: true,
  webhookEnabled: true,
  webhookTokenHint: 'ab12cd34',
  webhookLastUsedAt: '2026-10-20T02:41:05Z',
  updatedAt: '2026-10-20T02:41:05Z'
}

const LAB_SUMMARY: StackSummary = {
  name: 'lab',
  managed: true,
  status: 'stopped',
  serviceCount: 1,
  containerCount: 1,
  runningCount: 0,
  networkCount: 1,
  volumeCount: 0,
  composeAvailable: true,
  webhookEnabled: false,
  webhookTokenHint: null,
  webhookLastUsedAt: null,
  updatedAt: '2026-10-15T13:07:22Z'
}

const EXTERNAL_SUMMARY: StackSummary = {
  // 不受管：外部 compose 项目。我们没有它的 YAML，因此只能停 / 删，不能更新。
  name: 'external',
  managed: false,
  status: 'running',
  serviceCount: 1,
  containerCount: 1,
  runningCount: 1,
  networkCount: 1,
  volumeCount: 0,
  composeAvailable: false,
  webhookEnabled: false,
  webhookTokenHint: null,
  webhookLastUsedAt: null,
  updatedAt: null
}

/** 全在跑的编排。 */
export const MOCK_STACKS_DEFAULT: StackSummary[] = [
  SHOP_SUMMARY,
  BLOG_SUMMARY,
  LAB_SUMMARY,
  EXTERNAL_SUMMARY
]

/** 编排详情（服务级）。 */
export const MOCK_STACK_DETAILS: StackDetail[] = [
  {
    summary: SHOP_SUMMARY,
    composeContent: [
      'services:',
      '  api:',
      '    image: nginx:1.27-alpine',
      '    ports:',
      '      - "8080:80"',
      '    environment:',
      '      APP_ENV: production',
      '  worker:',
      '    image: busybox:1.37',
      '    command: ["/bin/sh", "-c", "sleep 3600"]',
      '    volumes:',
      '      - worker-data:/data',
      'volumes:',
      '  worker-data:'
    ].join('\n'),
    services: [
      {
        name: 'api',
        image: 'nginx:1.27-alpine',
        containerId: 'c4a1f0e2b7d94a1c8f3e2d5b6a7c8d9e0f1a2b3c4d5e6f708192a3b4c5d6e7f',
        containerName: 'shop-api-1',
        state: 'running',
        status: 'Up 3 hours',
        health: 'healthy',
        ports: [{ containerPort: 80, protocol: 'tcp', hostPort: 8080, hostIp: '0.0.0.0' }],
        restartPolicy: 'unless-stopped'
      },
      {
        name: 'worker',
        image: 'busybox:1.37',
        containerId: 'd5b2a1f3c8e04b2d9f4f3e6c7b8d9e0f1a2b3c4d5e6f708192a3b4c5d6e7f809',
        containerName: 'shop-worker-1',
        state: 'running',
        status: 'Up 3 hours',
        health: 'none',
        ports: [],
        restartPolicy: 'unless-stopped'
      }
    ],
    warnings: []
  },
  {
    summary: BLOG_SUMMARY,
    composeContent: [
      'services:',
      '  web:',
      '    image: nginx:1.27-alpine',
      '    ports:',
      '      - "8081:80"',
      '  db:',
      '    image: postgres:16-alpine',
      '    environment:',
      '      POSTGRES_PASSWORD: example',
      '  cache:',
      '    image: redis:7-alpine',
      '    command: ["/bin/sh", "-c", "sleep 3600"]'
    ].join('\n'),
    services: [
      {
        name: 'web',
        image: 'nginx:1.27-alpine',
        containerId: 'e6c3b2a4d9f15c3e0a5f4f7d8c9e0f1a2b3c4d5e6f708192a3b4c5d6e7f8091a',
        containerName: 'blog-web-1',
        state: 'running',
        status: 'Up 2 days',
        health: 'healthy',
        ports: [{ containerPort: 80, protocol: 'tcp', hostPort: 8081, hostIp: '0.0.0.0' }],
        restartPolicy: 'always'
      },
      {
        name: 'db',
        image: 'postgres:16-alpine',
        containerId: 'f7d4c3b5e0a26d4f1b6a5f8e9d0f1a2b3c4d5e6f708192a3b4c5d6e7f8091a2b',
        containerName: 'blog-db-1',
        state: 'running',
        status: 'Up 2 days (healthy)',
        health: 'healthy',
        ports: [],
        restartPolicy: 'unless-stopped'
      },
      {
        // 这一服务没起来——「部分在跑」的由来
        name: 'cache',
        image: 'redis:7-alpine',
        containerId: 'a8e5d4c6f1b37e5f2c7b6a9f0e1a2b3c4d5e6f708192a3b4c5d6e7f8091a2b3c',
        containerName: 'blog-cache-1',
        state: 'exited',
        status: 'Exited (1) 6 minutes ago',
        health: 'none',
        ports: [],
        restartPolicy: 'unless-stopped'
      }
    ],
    warnings: ['顶层「version」字段已被 Compose 规范废弃（当前值：3.8），已忽略']
  },
  {
    summary: LAB_SUMMARY,
    composeContent: [
      'services:',
      '  notebook:',
      '    image: jupyter/base-notebook:latest',
      '    command: ["/bin/sh", "-c", "sleep 3600"]'
    ].join('\n'),
    services: [
      {
        name: 'notebook',
        image: 'jupyter/base-notebook:latest',
        containerId: 'b9f6e5d7a2c48f6f3d8c7b0a1f2b3c4d5e6f708192a3b4c5d6e7f8091a2b3c4d',
        containerName: 'lab-notebook-1',
        state: 'exited',
        status: 'Exited (0) 2 days ago',
        health: 'none',
        ports: [],
        restartPolicy: 'no'
      }
    ],
    warnings: []
  },
  {
    // 外部项目：没有 YAML（composeContent 为 null）
    summary: EXTERNAL_SUMMARY,
    composeContent: null,
    services: [
      {
        name: 'app',
        image: 'traefik:v3.1',
        containerId: 'ca070618b3d59a7a4e9d8c1b2a3b4c5d6e7f8091a2b3c4d5e6f708192a3b4c5d',
        containerName: 'external-app-1',
        state: 'running',
        status: 'Up 5 days',
        health: 'none',
        ports: [{ containerPort: 8080, protocol: 'tcp', hostPort: 9090, hostIp: '0.0.0.0' }],
        restartPolicy: 'always'
      }
    ],
    warnings: []
  }
]
