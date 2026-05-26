// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { beforeEach, describe, expect, it, vi } from 'vitest'
import { computed, ref } from 'vue'

const isAuthenticatedSource = ref(false)
const isAdminSource = ref(false)
const usernameSource = ref<string | null>(null)
const editionSource = ref<'community' | 'commercial'>('commercial')
const clientRolesSource = ref<Record<string, number>>({})
const tenantRolesSource = ref<Record<string, number>>({})
const hasLocalPasswordSource = ref(false)

const establishSessionMock = vi.fn(async () => {})
const clearTokensMock = vi.fn()
const loadClientRolesMock = vi.fn(async () => {})
const hasClientRoleMock = vi.fn(() => false)
const hasTenantRoleMock = vi.fn(() => false)
const isCapabilityAvailableMock = vi.fn(() => false)

vi.mock('@/composables/useSession', () => ({
  useSession: () => ({
    isAuthenticated: computed(() => isAuthenticatedSource.value),
    isAdmin: computed(() => isAdminSource.value),
    username: computed(() => usernameSource.value),
    edition: editionSource,
    isCommercialEdition: computed(() => editionSource.value === 'commercial'),
    clientRoles: clientRolesSource,
    tenantRoles: tenantRolesSource,
    hasLocalPassword: hasLocalPasswordSource,
    establishSession: establishSessionMock,
    clearTokens: clearTokensMock,
    loadClientRoles: loadClientRolesMock,
    hasClientRole: hasClientRoleMock,
    hasTenantRole: hasTenantRoleMock,
    isCapabilityAvailable: isCapabilityAvailableMock,
  }),
}))

import { useSessionViewModel } from '@/features/auth/view-models/useSessionViewModel'

describe('useSessionViewModel (FR-007, FR-008)', () => {
  beforeEach(() => {
    isAuthenticatedSource.value = false
    isAdminSource.value = false
    usernameSource.value = null
    editionSource.value = 'commercial'
    clientRolesSource.value = {}
    tenantRolesSource.value = {}
    hasLocalPasswordSource.value = false
    establishSessionMock.mockClear()
    clearTokensMock.mockClear()
    loadClientRolesMock.mockClear()
    hasClientRoleMock.mockReset()
    hasClientRoleMock.mockReturnValue(false)
    hasTenantRoleMock.mockReset()
    hasTenantRoleMock.mockReturnValue(false)
    isCapabilityAvailableMock.mockReset()
    isCapabilityAvailableMock.mockReturnValue(false)
  })

  it('proxies isAuthenticated from the underlying session composable', () => {
    isAuthenticatedSource.value = true
    const vm = useSessionViewModel()
    expect(vm.isAuthenticated.value).toBe(true)
  })

  it('proxies username and isCommercialEdition', () => {
    usernameSource.value = 'admin@example.com'
    const vm = useSessionViewModel()
    expect(vm.username.value).toBe('admin@example.com')
    expect(vm.isCommercialEdition.value).toBe(true)
  })

  it('derives hasAnyAdminRole=true when the global admin flag is set', () => {
    isAdminSource.value = true
    const vm = useSessionViewModel()
    expect(vm.hasAnyAdminRole.value).toBe(true)
  })

  it('derives hasAnyAdminRole=true when at least one client role is admin (>=1)', () => {
    clientRolesSource.value = { 'client-a': 0, 'client-b': 1 }
    const vm = useSessionViewModel()
    expect(vm.hasAnyAdminRole.value).toBe(true)
  })

  it('derives hasAnyAdminRole=true when at least one tenant role is admin (>=1)', () => {
    tenantRolesSource.value = { 'tenant-x': 1 }
    const vm = useSessionViewModel()
    expect(vm.hasAnyAdminRole.value).toBe(true)
  })

  it('derives hasAnyAdminRole=false when no admin flag and no admin-level roles', () => {
    clientRolesSource.value = { 'client-a': 0 }
    tenantRolesSource.value = { 'tenant-x': 0 }
    const vm = useSessionViewModel()
    expect(vm.hasAnyAdminRole.value).toBe(false)
  })

  it('forwards establishSession to the underlying composable', async () => {
    const vm = useSessionViewModel()
    await vm.establishSession({ accessToken: 'a', refreshToken: 'r' })
    expect(establishSessionMock).toHaveBeenCalledWith({ accessToken: 'a', refreshToken: 'r' })
  })

  it('forwards clearTokens and loadClientRoles to the underlying composable', async () => {
    const vm = useSessionViewModel()
    vm.clearTokens()
    await vm.loadClientRoles()
    expect(clearTokensMock).toHaveBeenCalledTimes(1)
    expect(loadClientRolesMock).toHaveBeenCalledTimes(1)
  })

  it('forwards hasClientRole, hasTenantRole, and isCapabilityAvailable', () => {
    hasClientRoleMock.mockReturnValue(true)
    hasTenantRoleMock.mockReturnValue(true)
    isCapabilityAvailableMock.mockReturnValue(true)

    const vm = useSessionViewModel()
    expect(vm.hasClientRole('client-1', 1)).toBe(true)
    expect(vm.hasTenantRole('tenant-1', 1)).toBe(true)
    expect(vm.isCapabilityAvailable('procursor')).toBe(true)
    expect(hasClientRoleMock).toHaveBeenCalledWith('client-1', 1)
    expect(hasTenantRoleMock).toHaveBeenCalledWith('tenant-1', 1)
    expect(isCapabilityAvailableMock).toHaveBeenCalledWith('procursor')
  })
})
