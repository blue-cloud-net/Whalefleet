import { http, HttpResponse, delay } from 'msw'
import { ErrorCode, fail, httpStatusOf, ok } from '../envelope'
import type { MockScenario } from '../scenarios'
import type {
  ComposeExportResult,
  ContainerRunSpec,
  FileEntry,
  FileListResult,
  TransferJobAccepted,
  TransferJobStatus
} from '@/types/api'

/**
 * 文件传输与文件浏览的 mock（0.14.0）。
 *
 * ## 为什么这些 mock 要「有记忆」
 * - 导出任务必须先 `running` 再 `succeeded`：只回 `succeeded` 的话，
 *   前端的进度条与「关掉继续跑」的说明都测不到。
 * - 上传 / 下载走的是**二进制**，但 mock 只需要让前端拿到一个可保存的 Blob，
 *   内容语义不重要；关键是**路径语义**要对（`404`/`1406`、`403`/`1409`）。
 *
 * ## 刻意复刻的易忽略语义
 * - 列目录时**目录排在文件前面**，且根目录的 `parentPath` 是 `null`
 *   （前端据此禁用「上一级」）。
 * - 空路径等价于根目录。
 * - 写入被禁用时回 `1409`（而不是静默成功）——这条在真实环境里
 *   由管理员配置触发，界面必须能展示。
 */

/** mock 里的假目录树：路径 → 条目。 */
const TREE: Record<string, FileEntry[]> = {
  '/': [
    {
      name: 'etc',
      path: '/etc',
      kind: 'directory',
      sizeBytes: 0,
      modifiedAt: '2026-01-01T00:00:00Z',
      linkTarget: null,
      mode: 'drwxr-xr-x'
    },
    {
      name: 'var',
      path: '/var',
      kind: 'directory',
      sizeBytes: 0,
      modifiedAt: '2026-01-01T00:00:00Z',
      linkTarget: null,
      mode: 'drwxr-xr-x'
    },
    {
      name: 'app.conf',
      path: '/app.conf',
      kind: 'file',
      sizeBytes: 128,
      modifiedAt: '2026-01-02T03:04:05Z',
      linkTarget: null,
      mode: '-rw-r--r--'
    }
  ],
  '/etc': [
    {
      name: 'nginx',
      path: '/etc/nginx',
      kind: 'directory',
      sizeBytes: 0,
      modifiedAt: '2026-01-01T00:00:00Z',
      linkTarget: null,
      mode: 'drwxr-xr-x'
    },
    {
      name: 'hostname',
      path: '/etc/hostname',
      kind: 'file',
      sizeBytes: 12,
      modifiedAt: '2026-01-01T00:00:00Z',
      linkTarget: null,
      mode: '-rw-r--r--'
    }
  ],
  '/etc/nginx': [
    {
      name: 'nginx.conf',
      path: '/etc/nginx/nginx.conf',
      kind: 'file',
      sizeBytes: 2048,
      modifiedAt: '2026-01-01T00:00:00Z',
      linkTarget: null,
      mode: '-rw-r--r--'
    }
  ],
  '/var': []
}

/** 本次会话里写入的文件（路径 → 大小），用于让上传后的列举看得到。 */
const writtenInSession = new Map<string, number>()

/** 导出任务状态表（jobId → 轮询次数）。 */
const jobs = new Map<string, { polls: number; operation: string }>()

/** 生成目录列举结果。 */
function listingOf(path: string, volumeName?: string, containerId?: string): FileListResult | null {
  if (!path.startsWith('/')) {
    return null
  }

  const entries = TREE[path]

  if (!entries) {
    return null
  }

  const extra: FileEntry[] = []

  for (const [writtenPath, size] of writtenInSession) {
    const parent = writtenPath.slice(0, writtenPath.lastIndexOf('/')) || '/'

    if (parent !== path) {
      continue
    }

    extra.push({
      name: writtenPath.slice(writtenPath.lastIndexOf('/') + 1),
      path: writtenPath,
      kind: 'file',
      sizeBytes: size,
      modifiedAt: new Date().toISOString(),
      linkTarget: null,
      mode: '-rw-r--r--'
    })
  }

  const merged = [...entries, ...extra].sort((left, right) => {
    if (left.kind === 'directory' && right.kind !== 'directory') {
      return -1
    }

    if (left.kind !== 'directory' && right.kind === 'directory') {
      return 1
    }

    return left.name.localeCompare(right.name)
  })

  const parentPath = path === '/' ? null : path.slice(0, path.lastIndexOf('/')) || '/'

  // 未使用的参数在这里显式消耗：容器与卷走不同后端路径，但 mock 的目录内容一致
  void volumeName
  void containerId

  return { path, parentPath, entries: merged, truncated: false }
}

/** 规范化请求里的 `path`（空 → 根）。 */
function normalizePath(value: unknown): string {
  if (typeof value !== 'string' || value.trim().length === 0) {
    return '/'
  }

  return value.trim()
}

/** 组装文件相关 handler。 */
export function transferHandlers(scenario: () => MockScenario) {
  void scenario

  return [
    // ===== 导出：发起 =====
    http.post('/api/v1/hosts/:hostId/:kind(images|containers)/export', async ({ params }) => {
      await delay(120)

      const jobId = `mock-export-${Date.now().toString(36)}`

      jobs.set(jobId, { polls: 0, operation: params.kind === 'images' ? 'image.export' : 'container.export' })

      const accepted: TransferJobAccepted = { jobId, status: 'queued', reused: false }

      return HttpResponse.json(ok(accepted))
    }),

    // ===== 导出：轮询状态（先 running 两次，再 succeeded）=====
    http.post('/api/v1/hosts/:hostId/:kind(images|containers)/export/status', async ({ request }) => {
      const body = (await request.json().catch(() => ({}))) as { jobId?: string }
      const jobId = body.jobId ?? ''
      const record = jobs.get(jobId)

      if (!record) {
        return HttpResponse.json(fail(ErrorCode.TransferJobNotFound), {
          status: httpStatusOf(ErrorCode.TransferJobNotFound)
        })
      }

      record.polls += 1

      const done = record.polls >= 3
      const status: TransferJobStatus = {
        jobId,
        operation: record.operation,
        status: done ? 'succeeded' : 'running',
        percent: done ? 100 : Math.min(90, record.polls * 30),
        step: done ? null : '正在打包',
        errorCode: null,
        errorMessage: null,
        downloadUrl: done ? `/api/v1/hosts/local/mock-download/${jobId}?token=mock` : null,
        fileName: done ? `${record.operation.replace('.', '-')}.tar` : null,
        sizeBytes: done ? 1024 * 1024 : null,
        expiresAt: new Date(Date.now() + 3_600_000).toISOString()
      }

      return HttpResponse.json(ok(status))
    }),

    // ===== 导入（镜像 / 容器）=====
    http.post('/api/v1/hosts/:hostId/images/import', async () => {
      await delay(200)

      return HttpResponse.json(ok({ images: ['mock/imported:latest'], messageCount: 1 }))
    }),

    http.post('/api/v1/hosts/:hostId/containers/import', async ({ request }) => {
      await delay(200)

      const url = new URL(request.url)

      return HttpResponse.json(
        ok({
          imageId: `sha256:${'0'.repeat(64)}`,
          repository: url.searchParams.get('repository'),
          tag: url.searchParams.get('tag'),
          warnings: []
        })
      )
    }),

    // ===== 容器文件：列举 =====
    http.post('/api/v1/hosts/:hostId/containers/files/list', async ({ request }) => {
      await delay(80)

      const body = (await request.json().catch(() => ({}))) as { containerId?: string; path?: string }
      const path = normalizePath(body.path)
      const result = listingOf(path, undefined, body.containerId)

      if (!result) {
        return HttpResponse.json(fail(ErrorCode.FilePathNotFound), {
          status: httpStatusOf(ErrorCode.FilePathNotFound)
        })
      }

      return HttpResponse.json(ok(result))
    }),

    // ===== 容器文件：下载 =====
    http.post('/api/v1/hosts/:hostId/containers/files/download', async ({ request }) => {
      await delay(80)

      const body = (await request.json().catch(() => ({}))) as { path?: string }
      const path = normalizePath(body.path)

      if (!TREE[path]?.length && path !== '/') {
        return HttpResponse.json(fail(ErrorCode.FilePathNotFound), {
          status: httpStatusOf(ErrorCode.FilePathNotFound)
        })
      }

      return new HttpResponse(new Blob(['mock file content\n'], { type: 'application/octet-stream' }), {
        status: 200,
        headers: { 'Content-Type': 'application/octet-stream' }
      })
    }),

    // ===== 容器文件：写入（multipart）=====
    http.post('/api/v1/hosts/:hostId/containers/files/write', async ({ request }) => {
      await delay(150)

      const form = await request.formData().catch(() => null)
      const directory = normalizePath(form?.get('directoryPath'))
      const file = form?.get('file')

      if (!(file instanceof File) || file.size === 0) {
        return HttpResponse.json(fail(ErrorCode.ValidationFailed), {
          status: httpStatusOf(ErrorCode.ValidationFailed)
        })
      }

      if (directory !== '/' && !TREE[directory]) {
        return HttpResponse.json(fail(ErrorCode.FilePathNotFound), {
          status: httpStatusOf(ErrorCode.FilePathNotFound)
        })
      }

      writtenInSession.set(`${directory === '/' ? '' : directory}/${file.name}`, file.size)

      return HttpResponse.json(ok(file.size))
    }),

    // ===== 卷文件：列举 / 下载 / 写入 =====
    http.post('/api/v1/hosts/:hostId/volumes/files/list', async ({ request }) => {
      await delay(120)

      const body = (await request.json().catch(() => ({}))) as { volumeName?: string; path?: string }
      const result = listingOf(normalizePath(body.path), body.volumeName)

      if (!result) {
        return HttpResponse.json(fail(ErrorCode.FilePathNotFound), {
          status: httpStatusOf(ErrorCode.FilePathNotFound)
        })
      }

      return HttpResponse.json(ok(result))
    }),

    http.post('/api/v1/hosts/:hostId/volumes/files/download', async ({ request }) => {
      await delay(120)

      const body = (await request.json().catch(() => ({}))) as { path?: string }

      if (!body.path) {
        return HttpResponse.json(fail(ErrorCode.FilePathNotFound), {
          status: httpStatusOf(ErrorCode.FilePathNotFound)
        })
      }

      return new HttpResponse(new Blob(['mock volume content\n'], { type: 'application/octet-stream' }), {
        status: 200,
        headers: { 'Content-Type': 'application/octet-stream' }
      })
    }),

    http.post('/api/v1/hosts/:hostId/volumes/files/write', async ({ request }) => {
      await delay(180)

      const form = await request.formData().catch(() => null)
      const file = form?.get('file')

      if (!(file instanceof File)) {
        return HttpResponse.json(fail(ErrorCode.ValidationFailed), {
          status: httpStatusOf(ErrorCode.ValidationFailed)
        })
      }

      return HttpResponse.json(ok(file.size))
    }),

    // ===== 容器定义反向生成 =====
    http.post('/api/v1/hosts/:hostId/containers/run-command', async ({ request }) => {
      await delay(100)

      const body = (await request.json().catch(() => ({}))) as { containerId?: string; format?: string }

      if (!body.containerId) {
        return HttpResponse.json(fail(ErrorCode.ValidationFailed), {
          status: httpStatusOf(ErrorCode.ValidationFailed)
        })
      }

      const spec: ContainerRunSpec =
        body.format === 'composeYaml'
          ? {
              format: 'composeYaml',
              content: [
                'services:',
                '  mock:',
                '    image: nginx:1.27',
                '    container_name: mock',
                '    ports:',
                '      - "8080:80"',
                ''
              ].join('\n')
            }
          : {
              format: 'dockerRun',
              content: 'docker run --name mock --publish 8080:80 nginx:1.27'
            }

      return HttpResponse.json(ok(spec))
    }),

    // ===== 编排原文导出 =====
    http.post('/api/v1/hosts/:hostId/composes/export', async ({ request }) => {
      await delay(100)

      const body = (await request.json().catch(() => ({}))) as { name?: string }

      if (!body.name) {
        return HttpResponse.json(fail(ErrorCode.ValidationFailed), {
          status: httpStatusOf(ErrorCode.ValidationFailed)
        })
      }

      // 与真实后端同口径：只有平台部署过的项目才有原文。
      // mock 里把 `unmanaged` 这个名字当成「守护进程上有、库里没有」的样本。
      if (body.name === 'unmanaged') {
        return HttpResponse.json(fail(ErrorCode.ComposeNotManaged), {
          status: httpStatusOf(ErrorCode.ComposeNotManaged)
        })
      }

      const result: ComposeExportResult = {
        name: body.name,
        content: 'services:\n  web:\n    image: nginx:1.27\n'
      }

      return HttpResponse.json(ok(result))
    })
  ]
}
