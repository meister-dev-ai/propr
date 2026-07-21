// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

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
  return `/admin/tenants/${encodeURIComponent(tenantId)}/memberships`
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
