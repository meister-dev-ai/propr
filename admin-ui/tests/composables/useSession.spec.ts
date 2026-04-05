// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { beforeEach, describe, expect, it, vi } from 'vitest'
import { useSession } from '@/composables/useSession'

const ACCESS_TOKEN_KEY = 'meisterpropr_access_token'
const REFRESH_TOKEN_KEY = 'meisterpropr_refresh_token'
const CLIENT_ROLES_KEY = 'meisterpropr_client_roles'

function createJwt(globalRole: 'Admin' | 'User', expSecondsFromNow = 3600): string {
  const header = btoa(JSON.stringify({ alg: 'none', typ: 'JWT' }))
  const payload = btoa(JSON.stringify({
    global_role: globalRole,
    exp: Math.floor(Date.now() / 1000) + expSecondsFromNow,
  }))

  return `${header}.${payload}.signature`
}

describe('useSession', () => {
  beforeEach(() => {
    localStorage.clear()
    sessionStorage.clear()
    useSession().clearTokens()
  })

  it('setTokens stores access and refresh tokens', () => {
    const { setTokens, getAccessToken, getRefreshToken } = useSession()

    setTokens('access-token', 'refresh-token')

    expect(sessionStorage.setItem).toHaveBeenCalledWith(ACCESS_TOKEN_KEY, 'access-token')
    expect(localStorage.setItem).toHaveBeenCalledWith(REFRESH_TOKEN_KEY, 'refresh-token')
    expect(getAccessToken()).toBe('access-token')
    expect(getRefreshToken()).toBe('refresh-token')
  })

  it('clearTokens removes tokens and client roles', () => {
    const { setTokens, setClientRoles, clearTokens, getAccessToken, getRefreshToken, clientRoles } = useSession()

    setTokens('access-token', 'refresh-token')
    setClientRoles({ 'client-1': 1 })
    clearTokens()

    expect(sessionStorage.removeItem).toHaveBeenCalledWith(ACCESS_TOKEN_KEY)
    expect(localStorage.removeItem).toHaveBeenCalledWith(REFRESH_TOKEN_KEY)
    expect(sessionStorage.removeItem).toHaveBeenCalledWith(CLIENT_ROLES_KEY)
    expect(getAccessToken()).toBeNull()
    expect(getRefreshToken()).toBeNull()
    expect(clientRoles.value).toEqual({})
  })

  it('hasClientRole returns true for an assigned client user role', () => {
    const { setClientRoles, hasClientRole } = useSession()

    setClientRoles({ 'client-1': 0 })

    expect(hasClientRole('client-1', 0)).toBe(true)
    expect(hasClientRole('client-1', 1)).toBe(false)
    expect(hasClientRole('client-2', 0)).toBe(false)
  })

  it('hasClientRole returns true for global admins without client assignments', () => {
    const { setAccessToken, hasClientRole, isAdmin } = useSession()

    setAccessToken(createJwt('Admin'))

    expect(isAdmin.value).toBe(true)
    expect(hasClientRole('any-client', 1)).toBe(true)
  })

  it('isAuthenticated is true after setting an access token', () => {
    const { setAccessToken, isAuthenticated } = useSession()

    setAccessToken(createJwt('User'))

    expect(isAuthenticated.value).toBe(true)
  })

  it('loadClientRoles stores the fetched roles', async () => {
    const { setAccessToken, loadClientRoles, clientRoles } = useSession()
    vi.mocked(fetch).mockResolvedValue(new Response(JSON.stringify({
      globalRole: 'User',
      clientRoles: { 'client-1': 1 },
    }), { status: 200 }))

    setAccessToken(createJwt('User'))
    await loadClientRoles()

    expect(fetch).toHaveBeenCalledWith('http://localhost/api/auth/me', {
      headers: { Authorization: `Bearer ${useSession().getAccessToken()}` },
    })
    expect(clientRoles.value).toEqual({ 'client-1': 1 })
    expect(sessionStorage.getItem(CLIENT_ROLES_KEY)).toBe(JSON.stringify({ 'client-1': 1 }))
  })

  it('loadClientRoles clears stale roles when auth/me fails', async () => {
    const { setAccessToken, setClientRoles, loadClientRoles, clientRoles } = useSession()
    vi.mocked(fetch).mockResolvedValue(new Response(null, { status: 401 }))

    setAccessToken(createJwt('User'))
    setClientRoles({ 'client-1': 1 })
    await loadClientRoles()

    expect(clientRoles.value).toEqual({})
  })

  it('isAuthenticated is false after clearTokens', () => {
    const { setAccessToken, clearTokens, isAuthenticated } = useSession()

    setAccessToken(createJwt('User'))
    clearTokens()

    expect(isAuthenticated.value).toBe(false)
  })
})
