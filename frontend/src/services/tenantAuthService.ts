import {
  buildTenantExternalCallbackPath,
  buildTenantExternalChallengePath,
  buildTenantLocalLoginPath,
  buildTenantProvidersPath,
  tenantApiRequest,
  TenantApiError,
} from '@/services/tenantApiClient'
import { getApiErrorMessage } from '@/services/api'
import { getActiveRuntime } from '@/app/runtime/runtimeContext'
import type { RuntimeMode } from '@/app/runtime/createRuntime'

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

async function getTenantLoginOptionsInternal(tenantSlug: string): Promise<TenantLoginOptions> {
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
  return resolveTenantAuthService().loginWithTenantCredentials(tenantSlug, request)
}

async function loginWithTenantCredentialsInternal(
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

export async function getTenantLoginOptions(tenantSlug: string): Promise<TenantLoginOptions> {
  return resolveTenantAuthService().getTenantLoginOptions(tenantSlug)
}

export async function loginTenantLocally(
  tenantSlug: string,
  request: TenantLocalLoginRequest,
): Promise<TenantSessionResponse> {
  return resolveTenantAuthService().loginTenantLocally(tenantSlug, request)
}

export function getTenantExternalChallengeUrl(tenantSlug: string, providerId: string, returnUrl?: string): string {
  return resolveTenantAuthService().getTenantExternalChallengeUrl(tenantSlug, providerId, returnUrl)
}

export function getTenantExternalCallbackUrl(tenantSlug: string): string {
  return resolveTenantAuthService().getTenantExternalCallbackUrl(tenantSlug)
}

export interface TenantAuthService {
  runtimeMode: RuntimeMode
  getTenantLoginOptions: (tenantSlug: string) => Promise<TenantLoginOptions>
  loginWithTenantCredentials: (tenantSlug: string, request: TenantLocalLoginRequest) => Promise<TenantSessionResponse>
  loginTenantLocally: (tenantSlug: string, request: TenantLocalLoginRequest) => Promise<TenantSessionResponse>
  getTenantExternalChallengeUrl: (tenantSlug: string, providerId: string, returnUrl?: string) => string
  getTenantExternalCallbackUrl: (tenantSlug: string) => string
}

function createTenantAuthService(runtimeMode: RuntimeMode): TenantAuthService {
  return {
    runtimeMode,
    getTenantLoginOptions: getTenantLoginOptionsInternal,
    loginWithTenantCredentials: loginWithTenantCredentialsInternal,
    loginTenantLocally: loginWithTenantCredentialsInternal,
    getTenantExternalChallengeUrl: (tenantSlug, providerId, returnUrl) =>
      buildTenantExternalChallengePath(tenantSlug, providerId, returnUrl),
    getTenantExternalCallbackUrl: (tenantSlug) => buildTenantExternalCallbackPath(tenantSlug),
  }
}

const liveTenantAuthService = createTenantAuthService('live')
const mockTenantAuthService = createTenantAuthService('mock')

export function resolveTenantAuthService(): TenantAuthService {
  return getActiveRuntime().mode === 'mock'
    ? mockTenantAuthService
    : liveTenantAuthService
}
