import type { CreateContainerPayload } from '@/api/container'
import type { ContainerDetail } from '@/types/api'

/**
 * 容器详情 → 「编辑并重建」表单的初值。
 *
 * ## 为什么需要这个转换
 *
 * 后端详情是**给人看的投影**（精选字段 + 已翻译的枚举），而表单要的是
 * **创建请求体的形状**。两者字段名不完全一致（`protocol` / `type`、
 * `mounts` / `volumes` …），直接塞给表单会让表单拿到一堆它不认识的键。
 *
 * ## ⚠️ 两处刻意的「少给」
 *
 * 1. **端口按 (容器端口, 协议) 去重**：`-p 8080:80` 在 Docker 里是**两条**记录
 *    （`0.0.0.0` 与 `::`），而表单的一条端口映射只能表达**一个**绑定地址。
 *    不去重的话，用户一改端口就提交 → 后端收到重复的容器端口 → `1000`。
 *    取第一条是因为后端已按「IPv4 优先」排序过。
 *
 * 2. **tmpfs / npipe 挂载被跳过**：它们没有宿主来源，而表单的挂载表达的是
 *    `宿主路径:容器内路径`。硬拼会得到「把宿主根目录挂进容器」这种请求。
 *    它们也不在表单的初值里 → 不会被发出去 → 重建时后端沿用原容器的值。
 */
export function detailToConfig(detail: ContainerDetail): CreateContainerPayload {
  const seen = new Set<string>()

  const ports = detail.ports
    .filter((port) => {
      const key = `${port.privatePort}/${port.type}`

      if (seen.has(key)) return false

      seen.add(key)
      return true
    })
    .map((port) => ({
      containerPort: port.privatePort,
      ...(port.publicPort === null ? {} : { hostPort: port.publicPort }),
      ...(port.ip ? { hostIp: port.ip } : {}),
      ...(port.type === 'udp' ? { type: 'udp' } : {})
    }))

  const volumes = detail.mounts
    .filter((mount) => mount.type !== 'tmpfs' && mount.type !== 'npipe' && mount.source.length > 0)
    .map((mount) => ({
      source: mount.source,
      destination: mount.destination,
      readOnly: !mount.readWrite
    }))

  return {
    image: detail.config.image,
    cmd: [...detail.config.cmd],
    entrypoint: [...detail.config.entrypoint],
    env: [...detail.config.env],
    ports,
    volumes,
    restartPolicy: detail.hostConfig.restartPolicy,
    autoRemove: detail.hostConfig.autoRemove,
    network: detail.hostConfig.networkMode,
    hostname: detail.config.hostname,
    workingDir: detail.config.workingDir,
    user: detail.config.user,
    tty: detail.config.tty,
    ...(detail.hostConfig.memoryLimitBytes ? { memoryLimitBytes: detail.hostConfig.memoryLimitBytes } : {}),
    ...(detail.hostConfig.nanoCpus ? { nanoCpus: detail.hostConfig.nanoCpus } : {})
  }
}
