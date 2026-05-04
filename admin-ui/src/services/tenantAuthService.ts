import {
  buildTenantExternalCallbackPath,
  buildTenantExternalChallengePath,
  buildTenantLocalLoginPath,
  buildTenantProvidersPath,
  tenantApiRequest,
  TenantApiError,
} from '@/services/tenantApiClient'
import { getApiErrorMessage } from '@/services/api'

export interface TenantLoginProvider {
  providerId: string
  displayName: string
  providerKind: string
  providerLabel: string
}

export type TenantLoginProviderDto = TenantLoginProvider

export interface TenantLoginOptions {
  tenantSlug: string
  localLoginEnabled: boolean
  providers: TenantLoginProvider[]
}

export type TenantLoginOptionsDto = TenantLoginOptions

export interface TenantSessionResponse {
  accessToken: string
  refreshToken: string
  expiresIn?: number
  tokenType?: string
}

export type TenantAuthSessionDto = TenantSessionResponse

export interface TenantLocalLoginRequest {
  username: string
  password: string
}

export class TenantPremiumFeatureUnavailableError extends Error {
  feature: string | null

  constructor(message: string, feature?: string | null) {
    super(message)
    this.name = 'TenantPremiumFeatureUnavailableError'
    this.feature = feature ?? null
  }
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

export async function getTenantLoginOptions(tenantSlug: string): Promise<TenantLoginOptions> {
  try {
    return await tenantApiRequest<TenantLoginOptions>(buildTenantProvidersPath(tenantSlug))
  } catch (error) {
    if (error instanceof TenantApiError) {
      throw toTenantPremiumFeatureUnavailableError(error) ?? error
    }

    throw error instanceof Error ? error : new Error('Failed to load tenant sign-in options.')
  }
}

export async function loginWithTenantCredentials(
  tenantSlug: string,
  request: TenantLocalLoginRequest,
): Promise<TenantSessionResponse> {
  try {
    return await tenantApiRequest<TenantSessionResponse>(buildTenantLocalLoginPath(tenantSlug), {
      method: 'POST',
      body: JSON.stringify(request),
    })
  } catch (error) {
    if (error instanceof TenantApiError) {
      throw toTenantPremiumFeatureUnavailableError(error) ?? error
    }

    throw error instanceof Error ? error : new Error('Tenant login failed. Please try again.')
  }
}

export async function loginTenantLocally(
  tenantSlug: string,
  request: TenantLocalLoginRequest,
): Promise<TenantSessionResponse> {
  return loginWithTenantCredentials(tenantSlug, request)
}

export function getTenantExternalChallengeUrl(tenantSlug: string, providerId: string, returnUrl?: string): string {
  return buildTenantExternalChallengePath(tenantSlug, providerId, returnUrl)
}

export function getTenantExternalCallbackUrl(tenantSlug: string): string {
  return buildTenantExternalCallbackPath(tenantSlug)
}
