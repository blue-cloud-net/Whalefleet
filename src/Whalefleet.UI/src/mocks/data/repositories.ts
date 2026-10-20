import type { RepositorySummary } from '@/types/api'

/**
 * mock 用的仓库配置夹具（0.4.0）。
 *
 * ## 覆盖的两种形态（各对应一个界面分支）
 * 1. **带凭据**（`harbor-prod`）：账号列要显示用户名，推送时应「已认证」
 * 2. **匿名**（`public-mirror`）：账号列必须显式写「匿名」而不是留空——
 *    空单元格会被读成「后端没返回」，而实际含义是「推送时不发送任何凭据」
 *
 * ## 为什么 `harbor.local:5000` 是对的夹具地址
 * 它带端口，能顺带验证「端口不是标签」这条判据（`repositoryAddressOf` 若把
 * `:5000` 当标签，这里会自动匹配不上，界面显示「未登记凭据」）。
 * 用 `harbor.example.com` 这类无端口的地址就测不到这一条。
 *
 * ⚠️ 口令**不存在夹具里**：后端的响应本来就不含口令，mock 也不该有一份——
 * 一旦有了，「界面不回显口令」这条就永远测不出来（因为 mock 压根没给过）。
 */
export const MOCK_REPOSITORIES_DEFAULT: RepositorySummary[] = [
  {
    id: '00000000-0000-0000-0000-0000000000b1',
    name: 'harbor-prod',
    serverAddress: 'harbor.local:5000',
    username: 'deployer',
    hasCredentials: true,
    description: '内网 Harbor，CI 推送用',
    createdAt: '2026-09-20T02:11:00Z',
    lastModifiedAt: null
  },
  {
    id: '00000000-0000-0000-0000-0000000000b2',
    name: 'public-mirror',
    serverAddress: 'registry.example.com',
    username: '',
    hasCredentials: false,
    description: '公开镜像仓库，匿名可推',
    createdAt: '2026-09-25T09:40:00Z',
    lastModifiedAt: null
  }
]
