import { computed } from 'vue'

const ACCESS_TOKEN_KEY = 'meisterpropr_access_token'
const REFRESH_TOKEN_KEY = 'meisterpropr_refresh_token'

export function useSession() {
  function setTokens(accessToken: string, refreshToken: string): void {
    sessionStorage.setItem(ACCESS_TOKEN_KEY, accessToken)
    localStorage.setItem(REFRESH_TOKEN_KEY, refreshToken)
  }

  function getAccessToken(): string | null {
    return sessionStorage.getItem(ACCESS_TOKEN_KEY)
  }

  function getRefreshToken(): string | null {
    return localStorage.getItem(REFRESH_TOKEN_KEY)
  }

  function clearTokens(): void {
    sessionStorage.removeItem(ACCESS_TOKEN_KEY)
    localStorage.removeItem(REFRESH_TOKEN_KEY)
  }

  function setAccessToken(token: string): void {
    sessionStorage.setItem(ACCESS_TOKEN_KEY, token)
  }

  /** Returns true when an access token is present in this session. */
  const isAuthenticated = computed(() => getAccessToken() !== null)

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

  // Legacy — kept for backward compatibility during migration
  function setAdminKey(key: string): void {
    sessionStorage.setItem(ACCESS_TOKEN_KEY, key)
  }
  function getAdminKey(): string | null {
    return sessionStorage.getItem(ACCESS_TOKEN_KEY)
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
    // Legacy aliases
    setAdminKey,
    getAdminKey,
    clearAdminKey,
  }
}

