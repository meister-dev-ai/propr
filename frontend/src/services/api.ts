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

export function getApiErrorMessage(error: unknown, fallback: string): string {
  if (error && typeof error === 'object') {
    const apiError = error as {
      message?: string
      error?: string
      detail?: string
      title?: string
      errors?: Record<string, string[]>
    }

    if (typeof apiError.message === 'string' && apiError.message) {
      return apiError.message
    }

    if (typeof apiError.error === 'string' && apiError.error) {
      return apiError.error
    }

    if (typeof apiError.detail === 'string' && apiError.detail) {
      return apiError.detail
    }

    if (typeof apiError.title === 'string' && apiError.title) {
      return apiError.title
    }

    if (apiError.errors && typeof apiError.errors === 'object') {
      const firstError = Object.values(apiError.errors).flat()[0]
      if (firstError) {
        return firstError
      }
    }
  }

  return fallback
}

/** Attempt a silent token refresh. Returns the new access token or null on failure. */
async function tryRefreshToken(refreshToken: string, baseUrl: string): Promise<string | null> {
  try {
    const res = await fetch(baseUrl + '/auth/refresh', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ refreshToken }),
    })
    if (!res.ok) return null
    const data = (await res.json()) as { accessToken: string }
    return data.accessToken
  } catch {
    return null
  }
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
  const { getAccessToken, getRefreshToken, setAccessToken, clearTokens, accessTokenExpiresIn, loadClientRoles } =
    useSession()
  const baseUrl = resolveAdminClientBaseUrl(opts?.baseUrl)

  const client = createClient<paths>({
    baseUrl,
  })

  // Request middleware: inject Authorization header (Admin JWT) if present
  // Note: `opts.overrideKey` is accepted for backward compatibility but ignored —
  // the runtime no longer supports the legacy `X-Admin-Key` header.
  client.use({
    async onRequest({ request }) {
      let token = getAccessToken()

      // Proactively refresh if within 60 s of expiry
      if (token && accessTokenExpiresIn() < 60) {
        const refreshToken = getRefreshToken()
        if (refreshToken) {
          const newToken = await tryRefreshToken(refreshToken, baseUrl)
          if (newToken) {
            setAccessToken(newToken)
            await loadClientRoles()
            token = newToken
          }
        }
      }

      if (token) {
        request.headers.set('Authorization', `Bearer ${token}`)
      }
      return request
    },
    async onResponse({ request, response, options }) {
      if (response.status === 401) {
        // Try one silent refresh before giving up
        const refreshToken = getRefreshToken()
        if (refreshToken) {
          const newToken = await tryRefreshToken(refreshToken, baseUrl)
          if (newToken) {
            setAccessToken(newToken)
            await loadClientRoles()
            const retryRequest = new Request(request)
            retryRequest.headers.set('Authorization', `Bearer ${newToken}`)
            return options.fetch(retryRequest)
          } else {
            clearTokens()
          }
        } else {
          clearTokens()
        }
        throw new UnauthorizedError()
      }
      return response
    },
  })

  return client
}
