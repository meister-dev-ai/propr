// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import createClient from 'openapi-fetch'
import type { paths } from '@/types'
import { useSession } from '@/composables/useSession'
import { API_BASE_URL } from '@/services/apiBase'
import { getActiveRuntime } from '@/app/runtime/runtimeContext'

export class UnauthorizedError extends Error {
  constructor() {
    super('Unauthorized')
    this.name = 'UnauthorizedError'
  }
}

function readStringField(value: unknown): string | null {
  return typeof value === 'string' && value ? value : null
}

function readFirstFieldError(errors: unknown): string | null {
  if (!errors || typeof errors !== 'object') {
    return null
  }

  return Object.values(errors as Record<string, string[]>).flat()[0] ?? null
}

export function getApiErrorMessage(error: unknown, fallback: string): string {
  if (!error || typeof error !== 'object') {
    return fallback
  }

  const apiError = error as {
    message?: string
    error?: string
    detail?: string
    title?: string
    errors?: Record<string, string[]>
  }

  return (
    readStringField(apiError.message) ??
    readStringField(apiError.error) ??
    readStringField(apiError.detail) ??
    readStringField(apiError.title) ??
    readFirstFieldError(apiError.errors) ??
    fallback
  )
}

/**
 * Attempt a silent token refresh using the httpOnly refresh cookie (sent via
 * credentials: 'include'); no token is held in JS. Returns the new access token or null.
 */
export async function refreshAccessToken(baseUrl?: string): Promise<string | null> {
  try {
    const res = await fetch((baseUrl ?? resolveAdminClientBaseUrl()) + '/auth/refresh', {
      method: 'POST',
      credentials: 'include',
    })
    if (!res.ok) return null
    const data = (await res.json()) as { accessToken: string }
    return data.accessToken
  } catch {
    return null
  }
}

/**
 * Registered by the app shell to react to a definitively-ended session (refresh failed):
 * clear state and route to login. Kept as a callback to avoid a router import cycle here.
 */
let onSessionExpired: (() => void) | null = null
export function setOnSessionExpired(handler: (() => void) | null): void {
  onSessionExpired = handler
}

export interface CreateAdminClientOptions {
  overrideKey?: string
  baseUrl?: string
}

function resolveAdminClientBaseUrl(explicitBaseUrl?: string): string {
  const baseUrl = explicitBaseUrl && explicitBaseUrl.length > 0
    ? explicitBaseUrl
    : getActiveRuntime().apiBaseUrl || API_BASE_URL

  if (baseUrl.startsWith('/') && typeof window !== 'undefined' && window.location?.origin) {
    return new URL(baseUrl, window.location.origin).toString()
  }

  return baseUrl
}

export function createAdminClient(opts?: CreateAdminClientOptions) {
  const { getAccessToken, setAccessToken, clearTokens, accessTokenExpiresIn, loadClientRoles } = useSession()
  const baseUrl = resolveAdminClientBaseUrl(opts?.baseUrl)

  const client = createClient<paths>({
    baseUrl,
  })

  // Refresh fails terminally → the session is over: clear state and let the app route to login.
  function endSession() {
    clearTokens()
    onSessionExpired?.()
  }

  // Request middleware: inject Authorization header (Admin JWT) if present
  // Note: `opts.overrideKey` is accepted for backward compatibility but ignored —
  // the runtime no longer supports the legacy `X-Admin-Key` header.
  client.use({
    async onRequest({ request }) {
      let token = getAccessToken()

      // Proactively refresh (via the httpOnly cookie) if within 60 s of expiry.
      if (token && accessTokenExpiresIn() < 60) {
        const newToken = await refreshAccessToken(baseUrl)
        if (newToken) {
          setAccessToken(newToken)
          await loadClientRoles()
          token = newToken
        }
      }

      if (token) {
        request.headers.set('Authorization', `Bearer ${token}`)
      }
      return request
    },
    async onResponse({ request, response, options }) {
      if (response.status === 401) {
        // Try one silent cookie refresh before giving up.
        const newToken = await refreshAccessToken(baseUrl)
        if (newToken) {
          setAccessToken(newToken)
          await loadClientRoles()
          const retryRequest = new Request(request)
          retryRequest.headers.set('Authorization', `Bearer ${newToken}`)
          return options.fetch(retryRequest)
        }
        endSession()
        throw new UnauthorizedError()
      }
      return response
    },
  })

  return client
}

/**
 * Refresh-aware `fetch` for the hand-written service wrappers whose endpoints are not in
 * the generated OpenAPI schema (jobs, thread-memory, tenant admin). It mirrors the auth
 * middleware in `createAdminClient`: proactively refreshes the access token via the httpOnly
 * cookie when it is within 60 s of expiry, attaches the bearer token, and on a 401 tries one
 * silent refresh + retry before ending the session.
 *
 * Without this, a tab left open past the access-token lifetime fails its next in-app request
 * (e.g. "Failed to load review history") until a full page reload re-bootstraps the token.
 *
 * `input` is the fully-resolved request URL; callers build it as they already do.
 */
export async function authedFetch(input: string, init: RequestInit = {}): Promise<Response> {
  const { getAccessToken, setAccessToken, clearTokens, accessTokenExpiresIn, loadClientRoles } = useSession()
  const refreshBaseUrl = resolveAdminClientBaseUrl()

  let token = getAccessToken()

  // Proactively refresh (via the httpOnly cookie) if within 60 s of expiry.
  if (token && accessTokenExpiresIn() < 60) {
    const refreshed = await refreshAccessToken(refreshBaseUrl)
    if (refreshed) {
      setAccessToken(refreshed)
      await loadClientRoles()
      token = refreshed
    }
  }

  const send = (bearer: string | null): Promise<Response> => {
    const headers = new Headers(init.headers)
    if (bearer) {
      headers.set('Authorization', `Bearer ${bearer}`)
    }
    return fetch(input, { ...init, headers })
  }

  const response = await send(token)
  if (response.status !== 401) {
    return response
  }

  // A 401 despite the proactive check (token expired mid-flight, or clock skew): try one
  // silent cookie refresh, then retry the request once with the fresh token.
  const refreshed = await refreshAccessToken(refreshBaseUrl)
  if (refreshed) {
    setAccessToken(refreshed)
    await loadClientRoles()
    return send(refreshed)
  }

  // Refresh failed terminally → the session is over: clear state and let the app route to login.
  clearTokens()
  onSessionExpired?.()
  throw new UnauthorizedError()
}
