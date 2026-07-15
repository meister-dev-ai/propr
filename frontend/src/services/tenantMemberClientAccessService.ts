import { tenantApiRequest } from '@/services/tenantApiClient'

export type ClientRole = 'clientUser' | 'clientAdministrator'

export interface TenantClientSummaryDto {
  id: string
  displayName: string
  isActive: boolean
}

export interface TenantMemberClientAccessDto {
  clientId: string
  clientDisplayName: string
  role: ClientRole
  assignedAt: string
}

export interface AssignMemberClientAccessRequest {
  clientId: string
  role: ClientRole
}

function buildTenantClientsPath(tenantId: string): string {
  return `/admin/tenants/${encodeURIComponent(tenantId)}/clients`
}

function buildMemberClientsPath(tenantId: string, membershipId: string): string {
  return `/admin/tenants/${encodeURIComponent(tenantId)}/memberships/${encodeURIComponent(membershipId)}/clients`
}

function buildMemberClientPath(tenantId: string, membershipId: string, clientId: string): string {
  return `${buildMemberClientsPath(tenantId, membershipId)}/${encodeURIComponent(clientId)}`
}

export async function listTenantClients(tenantId: string): Promise<TenantClientSummaryDto[]> {
  return tenantApiRequest<TenantClientSummaryDto[]>(buildTenantClientsPath(tenantId), {
    requireAuth: true,
  })
}

export async function listMemberClientAccess(
  tenantId: string,
  membershipId: string,
): Promise<TenantMemberClientAccessDto[]> {
  return tenantApiRequest<TenantMemberClientAccessDto[]>(buildMemberClientsPath(tenantId, membershipId), {
    requireAuth: true,
  })
}

export async function assignMemberClientAccess(
  tenantId: string,
  membershipId: string,
  request: AssignMemberClientAccessRequest,
): Promise<TenantMemberClientAccessDto> {
  return tenantApiRequest<TenantMemberClientAccessDto>(buildMemberClientsPath(tenantId, membershipId), {
    method: 'POST',
    requireAuth: true,
    body: JSON.stringify(request),
  })
}

export async function removeMemberClientAccess(
  tenantId: string,
  membershipId: string,
  clientId: string,
): Promise<void> {
  await tenantApiRequest<void>(buildMemberClientPath(tenantId, membershipId, clientId), {
    method: 'DELETE',
    requireAuth: true,
  })
}
