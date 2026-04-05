// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import createClient from 'openapi-fetch'
import type { paths } from './generated/openapi'
import { useSession } from '@/composables/useSession'
import { API_BASE_URL } from '@/services/apiBase'

export class UnauthorizedError extends Error {
  constructor() {
    super('Unauthorized')
    this.name = 'UnauthorizedError'
  }
}

export function getApiErrorMessage(error: unknown, fallback: string): string {
  if (error && typeof error === 'object') {
    const apiError = error as {
      error?: string
      detail?: string
      title?: string
      errors?: Record<string, string[]>
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
async function tryRefreshToken(refreshToken: string): Promise<string | null> {
  try {
    const res = await fetch(API_BASE_URL + '/auth/refresh', {
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

export function createAdminClient(opts?: { overrideKey?: string }) {
  const { getAccessToken, getRefreshToken, setAccessToken, clearTokens, accessTokenExpiresIn, loadClientRoles } =
    useSession()

  const client = createClient<paths>({
    baseUrl: API_BASE_URL,
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
          const newToken = await tryRefreshToken(refreshToken)
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
    async onResponse({ response }) {
      if (response.status === 401) {
        // Try one silent refresh before giving up
        const refreshToken = getRefreshToken()
        if (refreshToken) {
          const newToken = await tryRefreshToken(refreshToken)
          if (newToken) {
            setAccessToken(newToken)
            await loadClientRoles()
            // Caller must retry; we throw so the UI can decide
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

