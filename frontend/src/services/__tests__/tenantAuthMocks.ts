import { http, HttpResponse } from 'msw'
import { API_BASE_URL } from '@/services/apiBase'

export type TenantProviderKind = 'entraId' | 'google' | 'github'
export type TenantProtocolKind = 'oidc' | 'oauth2'

export interface TenantLoginProviderFixture {
  providerId: string
  displayName: string
  providerKind: TenantProviderKind
}

export interface TenantLoginOptionsFixture {
  tenantSlug: string
  localLoginEnabled: boolean
  providers: TenantLoginProviderFixture[]
}

export interface TenantAuthHandlersOptions {
  tenantSlug?: string
  localLoginEnabled?: boolean
  providers?: TenantLoginProviderFixture[]
  accessToken?: string
  refreshToken?: string
}

const defaultProviders: TenantLoginProviderFixture[] = [
  {
    providerId: 'provider-entra-default',
    displayName: 'Contoso Entra ID',
    providerKind: 'entraId',
  },
  {
    providerId: 'provider-github-default',
    displayName: 'Contoso GitHub',
    providerKind: 'github',
  },
]

function encodeBase64Url(value: string): string {
  return btoa(value)
    .replace(/\+/g, '-')
    .replace(/\//g, '_')
    .replace(/=+$/g, '')
}

export function createTenantAccessToken(username = 'tenant.user'): string {
  const header = encodeBase64Url(JSON.stringify({ alg: 'none', typ: 'JWT' }))
  const payload = encodeBase64Url(JSON.stringify({
    global_role: 'User',
    unique_name: username,
    exp: Math.floor(Date.now() / 1000) + 3600,
    probe: 'a~',
  }))

  return `${header}.${payload}.signature`
}

export function createTenantLoginOptionsFixture(
  overrides: Partial<TenantLoginOptionsFixture> = {},
): TenantLoginOptionsFixture {
  return {
    tenantSlug: overrides.tenantSlug ?? 'acme',
    localLoginEnabled: overrides.localLoginEnabled ?? true,
    providers: overrides.providers ?? defaultProviders,
  }
}

export function createTenantAuthHandlers(
  options: TenantAuthHandlersOptions = {},
) {
  const tenantSlug = options.tenantSlug ?? 'acme'
  const accessToken = options.accessToken ?? createTenantAccessToken()
  const refreshToken = options.refreshToken ?? 'tenant-refresh-token'
  const loginOptions = createTenantLoginOptionsFixture({
    tenantSlug,
    localLoginEnabled: options.localLoginEnabled,
    providers: options.providers,
  })

  return [
    http.get(`${API_BASE_URL}/auth/tenants/:tenantSlug/providers`, ({ params }) => {
      if (params.tenantSlug !== tenantSlug) {
        return HttpResponse.json({ error: 'Tenant not found.' }, { status: 404 })
      }

      return HttpResponse.json(loginOptions)
    }),

    http.post(`${API_BASE_URL}/auth/tenants/:tenantSlug/local-login`, ({ params }) => {
      if (params.tenantSlug !== tenantSlug || !loginOptions.localLoginEnabled) {
        return HttpResponse.json({ error: 'Local sign-in is disabled for this tenant.' }, { status: 401 })
      }

      return HttpResponse.json({ accessToken, refreshToken })
    }),

    http.get(`${API_BASE_URL}/auth/external/challenge/:tenantSlug/:providerId`, ({ params }) => {
      if (params.tenantSlug !== tenantSlug) {
        return HttpResponse.json({ error: 'Tenant not found.' }, { status: 404 })
      }

      const provider = loginOptions.providers.find((candidate) => candidate.providerId === params.providerId)
      if (!provider) {
        return HttpResponse.json({ error: 'Provider not found.' }, { status: 404 })
      }

      return HttpResponse.redirect(`https://identity.example.test/${tenantSlug}/${provider.providerId}`)
    }),

    http.get(`${API_BASE_URL}/auth/external/callback/:tenantSlug`, ({ params }) => {
      if (params.tenantSlug !== tenantSlug) {
        return HttpResponse.json({ error: 'Tenant not found.' }, { status: 404 })
      }

      const provider = loginOptions.providers[0]
      if (!provider) {
        return HttpResponse.json({ error: 'Provider not found.' }, { status: 404 })
      }

      return HttpResponse.json({ accessToken, refreshToken })
    }),
  ]
}
