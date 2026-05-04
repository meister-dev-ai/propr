import { API_BASE_URL } from '@/services/apiBase'
import { getApiErrorMessage } from '@/services/api'
import { tenantApiRequest, TenantApiError } from '@/services/tenantApiClient'
import { TenantPremiumFeatureUnavailableError } from '@/services/tenantAuthService'

export interface TenantSsoProviderDto {
  id: string
  tenantId: string
  displayName: string
  providerKind: string
  protocolKind: string
  issuerOrAuthorityUrl?: string | null
  clientId: string
  secretConfigured: boolean
  scopes: string[]
  allowedEmailDomains: string[]
  isEnabled: boolean
  autoCreateUsers: boolean
  createdAt: string
  updatedAt: string
}

export interface TenantSsoProviderInput {
  displayName: string
  providerKind: string
  protocolKind: string
  issuerOrAuthorityUrl: string
  clientId: string
  clientSecret: string
  scopes: string[]
  allowedEmailDomains: string[]
  isEnabled: boolean
  autoCreateUsers: boolean
}

function buildProvidersPath(tenantId: string): string {
  return `${API_BASE_URL}/api/admin/tenants/${encodeURIComponent(tenantId)}/sso-providers`
}

function buildProviderPath(tenantId: string, providerId: string): string {
  return `${buildProvidersPath(tenantId)}/${encodeURIComponent(providerId)}`
}

function toTenantPremiumFeatureUnavailableError(error: unknown): TenantPremiumFeatureUnavailableError | null {
  if (!(error instanceof TenantApiError) || error.error !== 'premium_feature_unavailable') {
    return null
  }

  return new TenantPremiumFeatureUnavailableError(
    getApiErrorMessage(error, 'This premium feature is unavailable for the current installation.'),
    error.feature,
  )
}

export async function listTenantSsoProviders(tenantId: string): Promise<TenantSsoProviderDto[]> {
  try {
    return await tenantApiRequest<TenantSsoProviderDto[]>(buildProvidersPath(tenantId), {
      requireAuth: true,
    })
  } catch (error) {
    if (error instanceof TenantApiError) {
      throw toTenantPremiumFeatureUnavailableError(error) ?? error
    }

    throw error instanceof Error ? error : new Error('Failed to load tenant providers.')
  }
}

export async function createTenantSsoProvider(
  tenantId: string,
  request: TenantSsoProviderInput,
): Promise<TenantSsoProviderDto> {
  try {
    return await tenantApiRequest<TenantSsoProviderDto>(buildProvidersPath(tenantId), {
      method: 'POST',
      requireAuth: true,
      body: JSON.stringify(request),
    })
  } catch (error) {
    if (error instanceof TenantApiError) {
      throw toTenantPremiumFeatureUnavailableError(error) ?? error
    }

    throw error instanceof Error ? error : new Error('Failed to create tenant provider.')
  }
}

export async function updateTenantSsoProvider(
  tenantId: string,
  providerId: string,
  request: TenantSsoProviderInput,
): Promise<TenantSsoProviderDto> {
  try {
    return await tenantApiRequest<TenantSsoProviderDto>(buildProviderPath(tenantId, providerId), {
      method: 'PUT',
      requireAuth: true,
      body: JSON.stringify(request),
    })
  } catch (error) {
    if (error instanceof TenantApiError) {
      throw toTenantPremiumFeatureUnavailableError(error) ?? error
    }

    throw error instanceof Error ? error : new Error('Failed to update tenant provider.')
  }
}

export async function deleteTenantSsoProvider(tenantId: string, providerId: string): Promise<void> {
  try {
    await tenantApiRequest<void>(buildProviderPath(tenantId, providerId), {
      method: 'DELETE',
      requireAuth: true,
    })
  } catch (error) {
    if (error instanceof TenantApiError) {
      throw toTenantPremiumFeatureUnavailableError(error) ?? error
    }

    throw error instanceof Error ? error : new Error('Failed to remove tenant provider.')
  }
}
