import createClient from 'openapi-fetch'
import type { paths } from './generated/openapi'
import { useSession } from '@/composables/useSession'

export class UnauthorizedError extends Error {
  constructor() {
    super('Unauthorized')
    this.name = 'UnauthorizedError'
  }
}

/** Attempt a silent token refresh. Returns the new access token or null on failure. */
async function tryRefreshToken(refreshToken: string): Promise<string | null> {
  try {
    const res = await fetch(
      (import.meta.env.VITE_API_BASE_URL ?? '') + '/auth/refresh',
      {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ refreshToken }),
      },
    )
    if (!res.ok) return null
    const data = (await res.json()) as { accessToken: string }
    return data.accessToken
  } catch {
    return null
  }
}

export function createAdminClient(opts?: { overrideKey?: string }) {
  const { getAccessToken, getRefreshToken, setAccessToken, clearTokens, accessTokenExpiresIn } =
    useSession()

  const client = createClient<paths>({
    baseUrl: import.meta.env.VITE_API_BASE_URL ?? '',
  })

  // Request middleware: inject Authorization header or fall back to legacy X-Admin-Key
  client.use({
    async onRequest({ request }) {
      // opts.overrideKey is used during login to test the key before storing
      if (opts?.overrideKey) {
        request.headers.set('X-Admin-Key', opts.overrideKey)
        return request
      }

      let token = getAccessToken()

      // Proactively refresh if within 60 s of expiry
      if (token && accessTokenExpiresIn() < 60) {
        const refreshToken = getRefreshToken()
        if (refreshToken) {
          const newToken = await tryRefreshToken(refreshToken)
          if (newToken) {
            setAccessToken(newToken)
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

