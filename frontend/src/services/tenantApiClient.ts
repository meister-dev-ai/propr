// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

import { authedFetch } from '@/services/api'
import { getActiveRuntime } from '@/app/runtime/runtimeContext'

function getTenantApiBaseUrl(): string {
  return getActiveRuntime().apiBaseUrl
}

function resolveTenantApiUrl(path: string): string {
  if (/^https?:\/\//.test(path)) {
    return path
  }

  const baseUrl = getTenantApiBaseUrl()
  if (!baseUrl) {
    return path
  }

  const normalizedBase = baseUrl.replace(/\/+$/, '')
  const normalizedPath = path.startsWith('/') ? path : `/${path}`
  return `${normalizedBase}${normalizedPath}`
}

export class TenantApiError extends Error {
  status: number
  error: string | null
  feature: string | null

  constructor(message: string, status: number, error?: string | null, feature?: string | null) {
    super(message)
    this.name = 'TenantApiError'
    this.status = status
    this.error = error ?? null
    this.feature = feature ?? null
  }
}

export interface TenantApiRequestOptions extends RequestInit {
  requireAuth?: boolean
}

export function buildTenantProvidersPath(tenantSlug: string): string {
  return `/auth/tenants/${encodeURIComponent(tenantSlug)}/providers`
}

export function buildTenantLocalLoginPath(tenantSlug: string): string {
  return `/auth/tenants/${encodeURIComponent(tenantSlug)}/local-login`
}

export function buildTenantExternalChallengePath(tenantSlug: string, providerId: string, returnUrl?: string): string {
  const path = `/auth/external/challenge/${encodeURIComponent(tenantSlug)}/${encodeURIComponent(providerId)}`
  if (!returnUrl) {
    return resolveTenantApiUrl(path)
  }

  const query = new URLSearchParams({ returnUrl })
  return resolveTenantApiUrl(`${path}?${query.toString()}`)
}

export function buildTenantExternalCallbackPath(tenantSlug: string): string {
  return resolveTenantApiUrl(`/auth/external/callback/${encodeURIComponent(tenantSlug)}`)
}

export async function tenantApiRequest<TResponse>(
  path: string,
  options: TenantApiRequestOptions = {},
): Promise<TResponse> {
  const { requireAuth = false, headers, ...init } = options
  const requestHeaders = new Headers(headers ?? {})

  if (!requestHeaders.has('Content-Type') && init.body) {
    requestHeaders.set('Content-Type', 'application/json')
  }

  // Authenticated calls go through the refresh-aware fetch so a token that expired while the
  // tab sat idle is silently refreshed (via the httpOnly cookie) instead of failing the request.
  // Unauthenticated calls (login, provider discovery) must not attach or refresh a token.
  const url = resolveTenantApiUrl(path)
  const requestInit: RequestInit = { ...init, headers: requestHeaders }
  const response = requireAuth
    ? await authedFetch(url, requestInit)
    : await fetch(url, requestInit)

  if (!response.ok) {
    let message = 'Tenant API request failed.'
    let errorCode: string | null = null
    let feature: string | null = null
    try {
      const body = await response.json() as { message?: string; error?: string; feature?: string | null; detail?: string; title?: string }
      message = body.message ?? body.error ?? body.detail ?? body.title ?? message
      errorCode = body.error ?? null
      feature = body.feature ?? null
    } catch {
      // Ignore JSON parse errors and fall back to the default message.
    }

    throw new TenantApiError(message, response.status, errorCode, feature)
  }

  if (response.status === 204) {
    return undefined as TResponse
  }

  return await response.json() as TResponse
}
