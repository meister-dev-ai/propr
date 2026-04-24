// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { computed, ref } from 'vue'
import { API_BASE_URL } from '@/services/apiBase'
import type { components } from '@/services/generated/openapi'

const ACCESS_TOKEN_KEY = 'meisterpropr_access_token'
const REFRESH_TOKEN_KEY = 'meisterpropr_refresh_token'
const CLIENT_ROLES_KEY = 'meisterpropr_client_roles'
const EDITION_KEY = 'meisterpropr_installation_edition'
const CAPABILITIES_KEY = 'meisterpropr_capabilities'

type InstallationEdition = components['schemas']['InstallationEdition']
type PremiumCapabilityDto = components['schemas']['PremiumCapabilityDto']
type SessionSnapshot = {
  globalRole?: string
  clientRoles?: Record<string, number>
  edition?: InstallationEdition
  capabilities?: PremiumCapabilityDto[] | null
}

// Initialize shared reactive state FROM storage
const accessToken = ref<string | null>(sessionStorage.getItem(ACCESS_TOKEN_KEY))
const refreshToken = ref<string | null>(localStorage.getItem(REFRESH_TOKEN_KEY))

/** clientRoles: clientId → 0 (ClientUser) | 1 (ClientAdministrator) */
const clientRoles = ref<Record<string, number>>(
  JSON.parse(sessionStorage.getItem(CLIENT_ROLES_KEY) ?? '{}') as Record<string, number>,
)

const edition = ref<InstallationEdition>((sessionStorage.getItem(EDITION_KEY) as InstallationEdition | null) ?? 'community')
const capabilities = ref<PremiumCapabilityDto[]>(
  JSON.parse(sessionStorage.getItem(CAPABILITIES_KEY) ?? '[]') as PremiumCapabilityDto[],
)

export function useSession() {
  function setTokens(at: string, rt: string): void {
    setClientRoles({})
    setLicensingState('community', [])
    sessionStorage.setItem(ACCESS_TOKEN_KEY, at)
    localStorage.setItem(REFRESH_TOKEN_KEY, rt)
    accessToken.value = at
    refreshToken.value = rt
  }

  function getAccessToken(): string | null {
    return accessToken.value
  }

  function getRefreshToken(): string | null {
    return refreshToken.value
  }

  function clearTokens(): void {
    sessionStorage.removeItem(ACCESS_TOKEN_KEY)
    localStorage.removeItem(REFRESH_TOKEN_KEY)
    sessionStorage.removeItem(CLIENT_ROLES_KEY)
    sessionStorage.removeItem(EDITION_KEY)
    sessionStorage.removeItem(CAPABILITIES_KEY)
    accessToken.value = null
    refreshToken.value = null
    clientRoles.value = {}
    edition.value = 'community'
    capabilities.value = []
  }

  function setAccessToken(token: string): void {
    sessionStorage.setItem(ACCESS_TOKEN_KEY, token)
    accessToken.value = token
  }

  /** Returns true when an access token is present in this session. */
  const isAuthenticated = computed(() => accessToken.value !== null)

  /**
   * Returns seconds until the access token expires, or 0 if expired/missing.
   * Reads the `exp` claim from the JWT payload (no signature verification needed client-side).
   */
  function accessTokenExpiresIn(): number {
    const token = getAccessToken()
    if (!token) return 0
    try {
      const payload = JSON.parse(atob(token.split('.')[1]))
      const expiresAt = (payload.exp as number) * 1000
      return Math.max(0, Math.floor((expiresAt - Date.now()) / 1000))
    } catch {
      return 0
    }
  }

  /**
   * Returns the user's global role from the JWT payload ('Admin' or 'User'),
   * or null if no token is present or the claim is absent.
   */
  function getGlobalRole(): string | null {
    const token = getAccessToken()
    if (!token) return null
    try {
      const payload = JSON.parse(atob(token.split('.')[1]))
      return (payload.global_role as string) ?? null
    } catch {
      return null
    }
  }

  /** Returns true when the current user has the Admin global role. */
  const isAdmin = computed(() => getGlobalRole() === 'Admin')

  /** Returns the username from the JWT `unique_name` claim, or null when unavailable. */
  function getUsername(): string | null {
    const token = getAccessToken()
    if (!token) return null
    try {
      const payload = JSON.parse(atob(token.split('.')[1]))
      return (payload.unique_name as string) ?? null
    } catch {
      return null
    }
  }

  const username = computed(() => getUsername())

  /**
   * Stores per-client roles received from `/auth/me` into sessionStorage and reactive state.
   * Values: 0 = ClientUser, 1 = ClientAdministrator.
   */
  function setClientRoles(roles: Record<string, number>): void {
    sessionStorage.setItem(CLIENT_ROLES_KEY, JSON.stringify(roles))
    clientRoles.value = roles
  }

  function setLicensingState(nextEdition: InstallationEdition, nextCapabilities: PremiumCapabilityDto[]): void {
    sessionStorage.setItem(EDITION_KEY, nextEdition)
    sessionStorage.setItem(CAPABILITIES_KEY, JSON.stringify(nextCapabilities))
    edition.value = nextEdition
    capabilities.value = nextCapabilities
  }

  function getCapability(key: string): PremiumCapabilityDto | null {
    const normalizedKey = key.trim().toLowerCase()
    return capabilities.value.find((capability) => capability.key?.toLowerCase() === normalizedKey) ?? null
  }

  function isCapabilityAvailable(key: string): boolean {
    return getCapability(key)?.isAvailable === true
  }

  const isCommercialEdition = computed(() => edition.value === 'commercial')

  /**
   * Returns true if the current user has at least `minRole` for `clientId`,
   * or if they are a global admin.
   */
  function hasClientRole(clientId: string, minRole: 0 | 1): boolean {
    if (isAdmin.value) return true
    const role = clientRoles.value[clientId]
    return role !== undefined && role >= minRole
  }

  /**
   * Fetches `/auth/me` and stores the returned `clientRoles` in sessionStorage.
   * Call this once after login (or token refresh) to prime the roles cache.
   */
  async function loadClientRoles(): Promise<void> {
    const token = getAccessToken()
    if (!token) {
      setClientRoles({})
      setLicensingState('community', [])
      return
    }

    try {
      const res = await fetch(API_BASE_URL + '/auth/me', {
        headers: { Authorization: `Bearer ${token}` },
      })
      if (res.ok) {
        const data = (await res.json()) as SessionSnapshot
        setClientRoles(data.clientRoles ?? {})
        setLicensingState(data.edition ?? 'community', data.capabilities ?? [])
      } else {
        setClientRoles({})
        setLicensingState('community', [])
      }
    } catch {
      setClientRoles({})
      setLicensingState('community', [])
    }
  }

  // Legacy — kept for backward compatibility during migration
  function setAdminKey(key: string): void {
    setAccessToken(key)
  }
  function getAdminKey(): string | null {
    return getAccessToken()
  }
  function clearAdminKey(): void {
    clearTokens()
  }

  return {
    setTokens,
    getAccessToken,
    getRefreshToken,
    clearTokens,
    setAccessToken,
    accessTokenExpiresIn,
    isAuthenticated,
    isAdmin,
    getGlobalRole,
    getUsername,
    username,
    clientRoles,
    edition,
    capabilities,
    isCommercialEdition,
    setClientRoles,
    setLicensingState,
    getCapability,
    isCapabilityAvailable,
    hasClientRole,
    loadClientRoles,
    // Legacy aliases
    setAdminKey,
    getAdminKey,
    clearAdminKey,
  }
}


