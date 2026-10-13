import type { ContainerDetail, ContainerSummary } from '@/types/api'

/**
 * mock 用容器数据。
 *
 * 与主机数据的处理一致（见 `data/hosts.ts`）：这些夹具刻意覆盖**每种状态各一个**，
 * 因此颜色映射、按钮互斥、退出码展示这些分支在没有后端的情况下都能走查到。
 *
 * ⚠️ 刻意保留一个 `paused` 容器：它是唯一能验证「过渡态用琥珀色、不落进 ok/down 任一桶」
 * 的夹具。删掉它等于删掉那个测试用例。
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
    healthStatus: null
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
    healthStatus: null
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

/** 全部夹具（详情形态）。 */
export const MOCK_CONTAINER_DETAILS: ContainerDetail[] = [RUNNING, CRASHED, PAUSED]

/**
 * 由详情派生出列表项。
 *
 * 刻意**派发**而不是手写第二份：详情与列表的形状必须一致（`state` 是同一个字符串、
 * 端口从哪来都一样），手写两份迟早漂移，而漂移的表现是「列表显示运行中、
 * 点进详情却是已停止」这种最费解的 bug。
 */
function toSummary(detail: ContainerDetail): ContainerSummary {
  return {
    id: detail.id,
    name: detail.name,
    image: detail.image,
    imageId: `sha256:${detail.id.slice(0, 32)}`,
    command: detail.config.cmd.join(' '),
    created: detail.created,
    state: detail.state.status,
    // 与后端一致：人类可读状态由守护进程给，mock 这里造一个近似值。
    status: detail.state.running ? 'Up 2 hours' : `Exited (${detail.state.exitCode}) 1 hour ago`,
    ports:
      detail.name === RUNNING.name
        ? [
            { privatePort: 80, publicPort: 8080, type: 'tcp', ip: '0.0.0.0' },
            // 只暴露不绑定：没有宿主端口，验证列表里只显示容器侧那一半。
            { privatePort: 443, publicPort: null, type: 'tcp', ip: null }
          ]
        : []
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
