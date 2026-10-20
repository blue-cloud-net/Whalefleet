import { request } from '@/api'
import type { RepositorySummary, RepositoryTestResult } from '@/types/api'

/**
 * 私有仓库配置端点（**全局作用域**，路径不含 `hostId`）。
 *
 * ## 为什么是全局而不是主机作用域
 * 同一个 Harbor 推到哪台机器都用它。挂到主机下会让同一份凭据在 N 台主机上
 * 重复登记 N 遍，而 N 份凭据迟早会漂移（改了其中一份，另外几台开始认不上）。
 *
 * ## 权限
 * `list` 要 `repository.read`；`add` / `remove` / `test` 要 `repository.write`。
 * ⚠️ `test` 用 write 而不是 read：测试传的是凭据，「能试」等于「能读出」——
 * 探测接口还能把口令送到任意地址上去（把地址改成攻击者服务器即可截走）。
 *
 * ## 失败错误码（界面要能区分，不要统一显示「操作失败」）
 * - `1800` 配置不存在（删除时被别人抢先删掉了）
 * - `1801` 别名已被占用
 * - `1802` 地址已被占用——**同址尤其危险**：推送时用哪条凭据会变成随机行为
 * - `1804` 凭据被拒（该重填口令）
 * - `1805` 不可达（该查网络 / 防火墙，与 1804 是完全不同的处置）
 */

/** 列出已登记的仓库配置（响应不含任何口令信息）。 */
export function listRepositories(
  params: { search?: string } = {}
): Promise<RepositorySummary[]> {
  return request<RepositorySummary[]>({
    url: '/api/v1/admin/repositories/list',
    method: 'post',
    data: params
  })
}

/**
 * 新增一条 仓库配置。
 *
 * `username` / `password` 必须**同时给或同时不给**（后端两个方向都校验）：
 * 只给账号等于「用空口令登录」，只给口令等于「没有账号」——两者都是笔误，
 * 放过去只会得到一条必然认证失败的配置。
 */
export function addRepository(payload: {
  name: string
  serverAddress: string
  username?: string
  password?: string
  description?: string
}): Promise<RepositorySummary> {
  return request<RepositorySummary>({
    url: '/api/v1/admin/repositories/add',
    method: 'post',
    data: payload
  })
}

/**
 * 删除一条 仓库配置（软删除）。
 *
 * ⚠️ 没有「更新」端点：口令密文不回显，因此「留空表示不修改」无法实现。
 * 改口令的正确做法是删掉重加。
 */
export function removeRepository(id: string): Promise<null> {
  return request<null>({
    url: '/api/v1/admin/repositories/remove',
    method: 'post',
    data: { id }
  })
}

/**
 * 测试连通性与凭据。
 *
 * 给 `id` 则测**已保存**的配置（口令自动解密）；不给则测请求体里的地址 + 凭据，
 * 用于「保存前先试一下」。
 *
 * 返回值不是布尔：`reachable` 与 `authenticated` 分开给，
 * 界面对「不通」与「通了但凭据无效」要给出不同的下一步。
 */
export function testRepository(
  payload:
    | { id: string }
    | { serverAddress: string; username?: string; password?: string }
): Promise<RepositoryTestResult> {
  return request<RepositoryTestResult>({
    url: '/api/v1/admin/repositories/test',
    method: 'post',
    data: payload
  })
}
