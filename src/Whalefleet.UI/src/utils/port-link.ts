/**
 * 端口链接的拼装（纯函数，无 vue-i18n 依赖，可脱离组件单测）。
 *
 * ## 为什么单独一个文件
 * 「怎么把一个绑定地址变成能点开的 URL」看起来只有一行，但它有两个**容易错**的点：
 * IPv6 字面量必须加方括号，而 `0.0.0.0` 这类通配地址**不能**被当成「没绑定」。
 * 把它们收在这里，组件只管「这个端口能不能点」。
 */

/**
 * 把绑定地址与宿主端口拼成可打开的 URL。
 *
 * @param ip 绑定地址（如 `0.0.0.0` / `192.168.1.10` / `::1`）
 * @param port 宿主端口
 * @returns URL 文本
 *
 * @remarks
 * **IPv6 必须加方括号**：`http://::1:8080` 会被解析成「主机 `::1`、端口 `8080`」之外的东西，
 * 正确写法是 `http://[::1]:8080`。
 *
 * **固定 `http` 而不猜协议**：容器里的服务是不是 HTTPS、路径是什么，
 * 守护进程都不知道（端口映射不含任何协议信息）。猜错的话链接点开是「无法连接」，
 * 而用户会以为是服务挂了——那比没有链接更糟。
 */
export function buildPortUrl(ip: string, port: number): string {
  const host = ip.includes(':') ? `[${ip}]` : ip

  return `http://${host}:${port}`
}

/**
 * 绑定地址的排序等级（越小越优先）。
 *
 * @param ip 绑定地址；`null` / 空串表示未指定
 * @returns 等级值
 *
 * @remarks
 * 顺序与后端 `ContainerService.IpRank` **保持一致**：具体 IPv4 → IPv4 通配 →
 * 具体 IPv6 → IPv6 通配 → 未指定。
 *
 * **IPv4 家族在前**的原因：`-p 8080:80` 会被 Docker 记成 `0.0.0.0` 与 `::` 两条，
 * 两条都指「所有网卡」，但 IPv4 是绝大多数场景真正能访问到的那一个。
 *
 * 这个等级同时决定两件事：链接用哪个地址、展示文本里显示哪个地址。
 */
export function ipRank(ip: string | null | undefined): number {
  const value = (ip ?? '').trim()

  if (value.length === 0) return 4
  if (value === '0.0.0.0') return 1
  if (value === '::') return 3

  return value.includes(':') ? 2 : 0
}

/**
 * 判断一个绑定地址是不是「通配」（即「所有网卡」）。
 *
 * @param ip 绑定地址
 * @returns 是通配返回 `true`
 *
 * @remarks
 * 通配地址**不是可访问的地址**——它只表达「监听所有网卡」。
 * 因此它不进展示文本（`0.0.0.0:8080→80/tcp` 里的 `0.0.0.0:` 是噪声，
 * 还会让人以为「访问 `0.0.0.0` 就能连上」），但它**仍然是链接目标**
 * （用户明确要求「用指定 IP，即使 0.0.0.0」）。
 */
export function isWildcardIp(ip: string | null | undefined): boolean {
  const rank = ipRank(ip)

  return rank === 1 || rank === 3 || rank === 4
}
