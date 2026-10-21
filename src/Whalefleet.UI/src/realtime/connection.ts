import { HubConnectionBuilder, HubConnection, LogLevel } from '@microsoft/signalr'
import { useAuthStore } from '@/stores/auth'

/**
 * SignalR 连接工厂（0.5.0）。
 *
 * ## 为什么令牌要走查询参数
 * 浏览器发起的 WebSocket 握手**不能自定义请求头**，而 SignalR 的默认握手是
 * `POST /hubs/xxx/negotiate` + `GET /hubs/xxx?id=…`（长轮询时后续请求也带 id）。
 * 因此令牌只能放在查询串里（`access_token=…`），
 * 服务端只在 `/hubs` 路径上接受它——见后端 `HubTokenQueryReader`。
 *
 * ## 为什么 `accessTokenFactory` 而不是把令牌拼进 URL
 * 令牌会变（刷新后会换新），而 URL 在连接建立时只算一次。
 * 工厂函数在**每次需要令牌时**被调用，重连时拿到的就是新令牌。
 */

/** 实时通道的路径前缀。 */
const HUB_BASE = '/hubs'

/**
 * 拼接 Hub 的完整地址。
 *
 * @param path 相对 `/hubs` 的路径（如 `hosts/local/container-log`）。
 * @returns 完整 URL。
 */
export function hubUrl(path: string): string {
  const base = import.meta.env.VITE_API_BASE_URL || ''

  return `${base}${HUB_BASE}/${path.replace(/^\/+/, '')}`
}

/**
 * 创建一个 Hub 连接。
 *
 * @param path 相对 `/hubs` 的路径。
 * @returns 未启动的连接。
 */
export function createHubConnection(path: string): HubConnection {
  return new HubConnectionBuilder()
    .withUrl(hubUrl(path), {
      // 令牌走查询参数（WebSocket 握手不能带自定义头）。
      accessTokenFactory: () => useAuthStore().accessToken
    })
    // 重连策略：立即 → 1s → 3s → 10s，然后放弃。
    // 放弃之后由调用方决定要不要手工重连（界面会显示「已断开」）。
    .withAutomaticReconnect([0, 1000, 3000, 10000])
    // 默认的日志级别会把每条消息都打出来，噪音极大；
    // 只在 SignalR 自己的内部出错时记录（连接失败、重连失败等）。
    .configureLogging(LogLevel.Warning)
    .build()
}

/**
 * 把一个 Hub 的路径按主机作用域拼出来。
 *
 * @param hostId 主机标识（主机名）。
 * @param kind Hub 种类：`container-log` / `container-stats`。
 * @returns 相对 `/hubs` 的路径。
 */
export function hostHubPath(hostId: string, kind: 'container-log' | 'container-stats'): string {
  return `hosts/${encodeURIComponent(hostId)}/${kind}`
}
