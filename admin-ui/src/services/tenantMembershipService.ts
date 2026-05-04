import { API_BASE_URL } from '@/services/apiBase'
import { tenantApiRequest } from '@/services/tenantApiClient'

export type TenantRole = 'tenantUser' | 'tenantAdministrator'

export interface TenantMembershipDto {
  id: string
  tenantId: string
  userId: string
  username: string
  email?: string | null
  userIsActive: boolean
  role: TenantRole
  assignedAt: string
  updatedAt: string
}

export interface UpdateTenantMembershipRequest {
  role: TenantRole
}

function buildMembershipsPath(tenantId: string): string {
  return `${API_BASE_URL}/api/admin/tenants/${encodeURIComponent(tenantId)}/memberships`
}

function buildMembershipPath(tenantId: string, membershipId: string): string {
  return `${buildMembershipsPath(tenantId)}/${encodeURIComponent(membershipId)}`
}

export async function listTenantMemberships(tenantId: string): Promise<TenantMembershipDto[]> {
  return tenantApiRequest<TenantMembershipDto[]>(buildMembershipsPath(tenantId), {
    requireAuth: true,
  })
}

export async function updateTenantMembership(
  tenantId: string,
  membershipId: string,
  request: UpdateTenantMembershipRequest,
): Promise<TenantMembershipDto> {
  return tenantApiRequest<TenantMembershipDto>(buildMembershipPath(tenantId, membershipId), {
    method: 'PATCH',
    requireAuth: true,
    body: JSON.stringify(request),
  })
}

export async function deleteTenantMembership(tenantId: string, membershipId: string): Promise<void> {
  await tenantApiRequest<void>(buildMembershipPath(tenantId, membershipId), {
    method: 'DELETE',
    requireAuth: true,
  })
}
