// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

import { tenantApiRequest } from '@/services/tenantApiClient'

export interface TenantDto {
  id: string
  slug: string
  displayName: string
  isActive: boolean
  localLoginEnabled: boolean
  isEditable: boolean
  createdAt: string
  updatedAt: string
}

export interface CreateTenantRequest {
  slug: string
  displayName: string
}

export interface UpdateTenantRequest {
  displayName?: string
  isActive?: boolean
  localLoginEnabled?: boolean
}

function buildTenantsPath(): string {
  return '/admin/tenants'
}

function buildTenantPath(tenantId: string): string {
  return `${buildTenantsPath()}/${encodeURIComponent(tenantId)}`
}

export async function listTenants(): Promise<TenantDto[]> {
  return tenantApiRequest<TenantDto[]>(buildTenantsPath(), {
    requireAuth: true,
  })
}

export async function createTenant(request: CreateTenantRequest): Promise<TenantDto> {
  return tenantApiRequest<TenantDto>(buildTenantsPath(), {
    method: 'POST',
    requireAuth: true,
    body: JSON.stringify(request),
  })
}

export async function getTenant(tenantId: string): Promise<TenantDto> {
  return tenantApiRequest<TenantDto>(buildTenantPath(tenantId), {
    requireAuth: true,
  })
}

export async function updateTenant(tenantId: string, request: UpdateTenantRequest): Promise<TenantDto> {
  return tenantApiRequest<TenantDto>(buildTenantPath(tenantId), {
    method: 'PATCH',
    requireAuth: true,
    body: JSON.stringify(request),
  })
}
