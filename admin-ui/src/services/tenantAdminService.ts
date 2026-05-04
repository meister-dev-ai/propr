import { API_BASE_URL } from '@/services/apiBase'
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
  return `${API_BASE_URL}/api/admin/tenants`
}

function buildTenantPath(tenantId: string): string {
  return `${API_BASE_URL}/api/admin/tenants/${encodeURIComponent(tenantId)}`
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
