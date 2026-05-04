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
const TENANT_ROLES_KEY = 'meisterpropr_tenant_roles'
const LOCAL_PASSWORD_KEY = 'meisterpropr_has_local_password'

type InstallationEdition = components['schemas']['InstallationEdition']
type PremiumCapabilityDto = components['schemas']['PremiumCapabilityDto']
type SessionSnapshot = {
  globalRole?: string
  clientRoles?: Record<string, number>
  tenantRoles?: Record<string, number>
  hasLocalPassword?: boolean
  edition?: InstallationEdition
  capabilities?: PremiumCapabilityDto[] | null
}

type JwtPayload = {
  exp?: number
  global_role?: string
  unique_name?: string
}

function decodeBase64UrlSegment(segment: string): string | null {
  if (!segment) return null

  const normalized = segment.replace(/-/g, '+').replace(/_/g, '/')
  const paddingLength = (4 - (normalized.length % 4)) % 4
  const base64 = normalized.padEnd(normalized.length + paddingLength, '=')

  try {
    const binary = atob(base64)
    const bytes = Uint8Array.from(binary, (character) => character.charCodeAt(0))
    return new TextDecoder().decode(bytes)
  } catch {
    return null
  }
}

function decodeJwtPayload(token: string | null): JwtPayload | null {
  const payloadSegment = token?.split('.')[1]
  if (!payloadSegment) return null

  const payloadJson = decodeBase64UrlSegment(payloadSegment)
  if (!payloadJson) return null

  try {
    const payload = JSON.parse(payloadJson) as unknown
    return payload !== null && typeof payload === 'object' ? (payload as JwtPayload) : null
  } catch {
    return null
  }
}

function readStoredJsonObject(key: string): Record<string, number> {
  const rawValue = sessionStorage.getItem(key)
  if (!rawValue) return {}

  try {
    const parsed = JSON.parse(rawValue) as unknown
    if (parsed === null || typeof parsed !== 'object' || Array.isArray(parsed)) {
      return {}
    }

    return Object.entries(parsed).every(([, value]) => typeof value === 'number')
      ? (parsed as Record<string, number>)
      : {}
  } catch {
    return {}
  }
}

function readStoredJsonArray(key: string): PremiumCapabilityDto[] {
  const rawValue = sessionStorage.getItem(key)
  if (!rawValue) return []

  try {
    const parsed = JSON.parse(rawValue) as unknown
    return Array.isArray(parsed) ? (parsed as PremiumCapabilityDto[]) : []
  } catch {
    return []
  }
}

// Initialize shared reactive state FROM storage
const accessToken = ref<string | null>(sessionStorage.getItem(ACCESS_TOKEN_KEY))
const refreshToken = ref<string | null>(localStorage.getItem(REFRESH_TOKEN_KEY))

/** clientRoles: clientId → 0 (ClientUser) | 1 (ClientAdministrator) */
const clientRoles = ref<Record<string, number>>(readStoredJsonObject(CLIENT_ROLES_KEY))

const edition = ref<InstallationEdition>((sessionStorage.getItem(EDITION_KEY) as InstallationEdition | null) ?? 'community')
const capabilities = ref<PremiumCapabilityDto[]>(readStoredJsonArray(CAPABILITIES_KEY))

/** tenantRoles: tenantId -> 0 (TenantUser) | 1 (TenantAdministrator) */
const tenantRoles = ref<Record<string, number>>(readStoredJsonObject(TENANT_ROLES_KEY))

const hasLocalPassword = ref(sessionStorage.getItem(LOCAL_PASSWORD_KEY) === 'true')

export function useSession() {
  function setTokens(at: string, rt: string): void {
    setClientRoles({})
    setTenantRoles({})
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
    sessionStorage.removeItem(TENANT_ROLES_KEY)
    sessionStorage.removeItem(LOCAL_PASSWORD_KEY)
    accessToken.value = null
    refreshToken.value = null
    clientRoles.value = {}
    tenantRoles.value = {}
    hasLocalPassword.value = false
    edition.value = 'community'
    capabilities.value = []
  }

  function setAccessToken(token: string): void {
    sessionStorage.setItem(ACCESS_TOKEN_KEY, token)
    accessToken.value = token
  }

  async function establishSession(session: { accessToken: string; refreshToken: string }): Promise<void> {
    setTokens(session.accessToken, session.refreshToken)
    await loadClientRoles()
  }

  /** Returns true when an access token is present in this session. */
  const isAuthenticated = computed(() => accessToken.value !== null)

  /**
   * Returns seconds until the access token expires, or 0 if expired/missing.
   * Reads the `exp` claim from the JWT payload (no signature verification needed client-side).
   */
  function accessTokenExpiresIn(): number {
    const payload = decodeJwtPayload(getAccessToken())
    if (typeof payload?.exp !== 'number') {
      return 0
    }

    const expiresAt = payload.exp * 1000
    return Math.max(0, Math.floor((expiresAt - Date.now()) / 1000))
  }

  /**
   * Returns the user's global role from the JWT payload ('Admin' or 'User'),
   * or null if no token is present or the claim is absent.
   */
  function getGlobalRole(): string | null {
    return decodeJwtPayload(getAccessToken())?.global_role ?? null
  }

  /** Returns true when the current user has the Admin global role. */
  const isAdmin = computed(() => getGlobalRole() === 'Admin')

  /** Returns the username from the JWT `unique_name` claim, or null when unavailable. */
  function getUsername(): string | null {
    return decodeJwtPayload(getAccessToken())?.unique_name ?? null
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

  function setTenantRoles(roles: Record<string, number>): void {
    sessionStorage.setItem(TENANT_ROLES_KEY, JSON.stringify(roles))
    tenantRoles.value = roles
  }

  function setHasLocalPassword(value: boolean): void {
    sessionStorage.setItem(LOCAL_PASSWORD_KEY, String(value))
    hasLocalPassword.value = value
  }

  /**
   * Returns true if the current user has at least `minRole` for `clientId`,
   * or if they are a global admin.
   */
  function hasClientRole(clientId: string, minRole: 0 | 1): boolean {
    if (isAdmin.value) return true
    const role = clientRoles.value[clientId]
    return role !== undefined && role >= minRole
  }

  function hasTenantRole(tenantId: string, minRole: 0 | 1): boolean {
    if (isAdmin.value) return true
    const role = tenantRoles.value[tenantId]
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
      setTenantRoles({})
      setHasLocalPassword(false)
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
        setTenantRoles(data.tenantRoles ?? {})
        setHasLocalPassword(data.hasLocalPassword === true)
        setLicensingState(data.edition ?? 'community', data.capabilities ?? [])
      } else {
        setClientRoles({})
        setTenantRoles({})
        setHasLocalPassword(false)
        setLicensingState('community', [])
      }
    } catch {
      setClientRoles({})
      setTenantRoles({})
      setHasLocalPassword(false)
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
    establishSession,
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
    tenantRoles,
    hasLocalPassword,
    setClientRoles,
    setLicensingState,
    getCapability,
    isCapabilityAvailable,
    setTenantRoles,
    setHasLocalPassword,
    hasClientRole,
    hasTenantRole,
    loadClientRoles,
    // Legacy aliases
    setAdminKey,
    getAdminKey,
    clearAdminKey,
  }
}
