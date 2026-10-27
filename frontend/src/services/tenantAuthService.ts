// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

import {
  buildTenantExternalCallbackPath,
  buildTenantExternalChallengePath,
  buildTenantLocalLoginPath,
  buildTenantProvidersPath,
  tenantApiRequest,
  TenantApiError,
  tenantPendingLinkConfirmationPath,
  tenantPendingLinkNewAccountPath,
  tenantPendingLinkPath,
  tenantSignInLinksPath,
  type TenantExternalSignInMode,
} from '@/services/tenantApiClient'
import { getApiErrorMessage } from '@/services/api'
import { getActiveRuntime } from '@/app/runtime/runtimeContext'
import type { RuntimeMode } from '@/app/runtime/createRuntime'
import type { components } from '@/types'

/** A tenant sign-in that waits in this browser for the confirmation of its link, or for a new account. */
export type PendingTenantSignInLink = components['schemas']['TenantPendingLinkDto']

/** A sign-in through a provider of a tenant that is linked to the account that is signed in. */
export type TenantSignInLink = components['schemas']['TenantSignInLinkDto']

/** The tenant whose sign-in was linked to the account that is signed in. */
export type TenantSignInLinkConfirmation = components['schemas']['TenantSignInLinkConfirmationDto']

/** The access token of the session of a new account. The refresh token is set as an httpOnly cookie. */
export interface TenantExternalSession {
  accessToken: string
  expiresIn?: number
  tokenType?: string
}

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

export function getTenantExternalChallengeUrl(
  tenantSlug: string,
  providerId: string,
  returnUrl?: string,
  mode: TenantExternalSignInMode = 'signIn',
): string {
  return resolveTenantAuthService().getTenantExternalChallengeUrl(tenantSlug, providerId, returnUrl, mode)
}

/** Returns the tenant sign-in that waits in this browser, or null when none waits. */
export async function getPendingTenantSignInLink(): Promise<PendingTenantSignInLink | null> {
  return resolveTenantAuthService().getPendingTenantSignInLink()
}

/** Links the tenant sign-in that waits in this browser to the account that is signed in. */
export async function confirmPendingTenantSignInLink(): Promise<TenantSignInLinkConfirmation> {
  return resolveTenantAuthService().confirmPendingTenantSignInLink()
}

/** Creates a new account for the tenant sign-in that waits in this browser and returns its session. */
export async function createAccountForPendingTenantSignIn(): Promise<TenantExternalSession> {
  return resolveTenantAuthService().createAccountForPendingTenantSignIn()
}

/** Returns the sign-ins of tenants that are linked to the account that is signed in. */
export async function listTenantSignInLinks(): Promise<TenantSignInLink[]> {
  return resolveTenantAuthService().listTenantSignInLinks()
}

function toTenantAuthError(error: unknown, fallback: string): Error {
  if (error instanceof TenantApiError) {
    return toTenantPremiumFeatureUnavailableError(error) ?? error
  }

  return error instanceof Error ? error : new Error(fallback)
}

async function getPendingTenantSignInLinkInternal(): Promise<PendingTenantSignInLink | null> {
  try {
    // The waiting sign-in is an httpOnly cookie, which the request carries.
    return await tenantApiRequest<PendingTenantSignInLink>(tenantPendingLinkPath, { credentials: 'include' })
  } catch (error) {
    if (error instanceof TenantApiError && error.status === 404) {
      return null
    }

    throw toTenantAuthError(error, 'The waiting sign-in could not be loaded.')
  }
}

async function confirmPendingTenantSignInLinkInternal(): Promise<TenantSignInLinkConfirmation> {
  try {
    return await tenantApiRequest<TenantSignInLinkConfirmation>(tenantPendingLinkConfirmationPath, {
      method: 'POST',
      credentials: 'include',
      requireAuth: true,
    })
  } catch (error) {
    throw toTenantAuthError(error, 'The sign-in could not be linked to your account.')
  }
}

async function createAccountForPendingTenantSignInInternal(): Promise<TenantExternalSession> {
  let session: components['schemas']['TenantExternalSessionDto']
  try {
    session = await tenantApiRequest<components['schemas']['TenantExternalSessionDto']>(tenantPendingLinkNewAccountPath, {
      method: 'POST',
      credentials: 'include',
    })
  } catch (error) {
    throw toTenantAuthError(error, 'The new account could not be created.')
  }

  if (!session.accessToken) {
    throw new Error('The new account was created, but its session did not start. Sign in again.')
  }

  return { accessToken: session.accessToken, expiresIn: session.expiresIn, tokenType: session.tokenType ?? undefined }
}

async function listTenantSignInLinksInternal(): Promise<TenantSignInLink[]> {
  try {
    return await tenantApiRequest<TenantSignInLink[]>(tenantSignInLinksPath, { requireAuth: true })
  } catch (error) {
    throw toTenantAuthError(error, 'The linked sign-ins could not be loaded.')
  }
}

export function getTenantExternalCallbackUrl(tenantSlug: string): string {
  return resolveTenantAuthService().getTenantExternalCallbackUrl(tenantSlug)
}

export interface TenantAuthService {
  runtimeMode: RuntimeMode
  getTenantLoginOptions: (tenantSlug: string) => Promise<TenantLoginOptions>
  loginWithTenantCredentials: (tenantSlug: string, request: TenantLocalLoginRequest) => Promise<TenantSessionResponse>
  loginTenantLocally: (tenantSlug: string, request: TenantLocalLoginRequest) => Promise<TenantSessionResponse>
  getTenantExternalChallengeUrl: (
    tenantSlug: string,
    providerId: string,
    returnUrl?: string,
    mode?: TenantExternalSignInMode,
  ) => string
  getTenantExternalCallbackUrl: (tenantSlug: string) => string
  getPendingTenantSignInLink: () => Promise<PendingTenantSignInLink | null>
  confirmPendingTenantSignInLink: () => Promise<TenantSignInLinkConfirmation>
  createAccountForPendingTenantSignIn: () => Promise<TenantExternalSession>
  listTenantSignInLinks: () => Promise<TenantSignInLink[]>
}

function createTenantAuthService(runtimeMode: RuntimeMode): TenantAuthService {
  return {
    runtimeMode,
    getTenantLoginOptions: getTenantLoginOptionsInternal,
    loginWithTenantCredentials: loginWithTenantCredentialsInternal,
    loginTenantLocally: loginWithTenantCredentialsInternal,
    getTenantExternalChallengeUrl: (tenantSlug, providerId, returnUrl, mode) =>
      buildTenantExternalChallengePath(tenantSlug, providerId, returnUrl, mode),
    getTenantExternalCallbackUrl: (tenantSlug) => buildTenantExternalCallbackPath(tenantSlug),
    getPendingTenantSignInLink: getPendingTenantSignInLinkInternal,
    confirmPendingTenantSignInLink: confirmPendingTenantSignInLinkInternal,
    createAccountForPendingTenantSignIn: createAccountForPendingTenantSignInInternal,
    listTenantSignInLinks: listTenantSignInLinksInternal,
  }
}

const liveTenantAuthService = createTenantAuthService('live')
const mockTenantAuthService = createTenantAuthService('mock')

export function resolveTenantAuthService(): TenantAuthService {
  return getActiveRuntime().mode === 'mock'
    ? mockTenantAuthService
    : liveTenantAuthService
}
