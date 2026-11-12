import { request } from '@/api'

export type ContainerPermissionMode = 'inheritHost' | 'requireAssignment'
export interface AuthorizationSettings {
  containerPermissionMode: ContainerPermissionMode
}
export function getAuthorizationSettings(): Promise<AuthorizationSettings> {
  return request({
    url: '/api/v1/admin/settings/authorization/get',
    method: 'post'
  })
}
export function updateAuthorizationSettings(
  data: AuthorizationSettings
): Promise<AuthorizationSettings> {
  return request({
    url: '/api/v1/admin/settings/authorization/update',
    method: 'post',
    data
  })
}
