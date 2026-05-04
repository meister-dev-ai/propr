import { API_BASE_URL } from '@/services/apiBase'
import { useSession } from '@/composables/useSession'

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
  return `${API_BASE_URL}/auth/tenants/${encodeURIComponent(tenantSlug)}/providers`
}

export function buildTenantLocalLoginPath(tenantSlug: string): string {
  return `${API_BASE_URL}/auth/tenants/${encodeURIComponent(tenantSlug)}/local-login`
}

export function buildTenantExternalChallengePath(tenantSlug: string, providerId: string, returnUrl?: string): string {
  const path = `${API_BASE_URL}/auth/external/challenge/${encodeURIComponent(tenantSlug)}/${encodeURIComponent(providerId)}`
  if (!returnUrl) {
    return path
  }

  const query = new URLSearchParams({ returnUrl })
  return `${path}?${query.toString()}`
}

export function buildTenantExternalCallbackPath(tenantSlug: string): string {
  return `${API_BASE_URL}/auth/external/callback/${encodeURIComponent(tenantSlug)}`
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

  if (requireAuth) {
    const token = useSession().getAccessToken()
    if (token) {
      requestHeaders.set('Authorization', `Bearer ${token}`)
    }
  }

  const response = await fetch(path, {
    ...init,
    headers: requestHeaders,
  })

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
