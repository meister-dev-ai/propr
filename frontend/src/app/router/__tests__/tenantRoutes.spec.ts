import { computed, ref } from 'vue'
import type { Router } from 'vue-router'
import { beforeEach, describe, expect, it, vi } from 'vitest'

const isAuthenticated = ref(false)
const isAdmin = ref(false)
const clientRoles = ref<Record<string, number>>({})
const tenantRoles = ref<Record<string, number>>({})
const availableCapabilities = ref<string[]>(['multi-tenancy'])
const hasClientRole = vi.fn(() => false)
const hasTenantRole = vi.fn(() => false)

vi.mock('@/composables/useSession', () => ({
  useSession: () => ({
    isAuthenticated: computed(() => isAuthenticated.value),
    isAdmin: computed(() => isAdmin.value),
    clientRoles,
    tenantRoles,
    hasClientRole,
    hasTenantRole,
    // Tenant administration is licensed, so the guard chain asks about the capability rather than the edition.
    isCapabilityAvailable: (key: string) => availableCapabilities.value.includes(key),
  }),
}))

async function importRouter(): Promise<Router> {
  vi.resetModules()
  const mod = await import('@/app/router')
  return mod.default
}

describe('tenant router scaffolding', () => {
  beforeEach(() => {
    isAuthenticated.value = false
    isAdmin.value = false
    clientRoles.value = {}
    tenantRoles.value = {}
    availableCapabilities.value = ['multi-tenancy']
    hasClientRole.mockReset()
    hasClientRole.mockReturnValue(false)
    hasTenantRole.mockReset()
    hasTenantRole.mockReturnValue(false)
  })

  it('registers a tenant-login route for tenant-scoped sign-in', async () => {
    const router = await importRouter()

    expect(router.hasRoute('tenant-login')).toBe(true)
  })

  it('registers a tenant-login-callback route for external sign-in handoff', async () => {
    const router = await importRouter()

    expect(router.hasRoute('tenant-login-callback')).toBe(true)
  })

  it('registers a tenant-directory route for tenant administration discovery', async () => {
    const router = await importRouter()
    const route = router.getRoutes().find((candidate) => candidate.name === 'tenant-directory')

    expect(route?.meta.requiresAuth).toBe(true)
    expect(route?.meta.requiresTenantDirectoryAccess).toBe(true)
    expect(route?.meta.requiresCapability).toBe('multi-tenancy')
  })

  it('registers a tenant-settings route for tenant administration', async () => {
    const router = await importRouter()

    expect(router.hasRoute('tenant-settings')).toBe(true)
  })

  it('marks tenant-settings as both authenticated and tenant-admin protected', async () => {
    const router = await importRouter()
    const route = router.getRoutes().find((candidate) => candidate.name === 'tenant-settings')

    expect(route?.meta.requiresAuth).toBe(true)
    expect(route?.meta.requiresTenantAdmin).toBe(true)
    expect(route?.meta.requiresCapability).toBe('multi-tenancy')
  })

  it('registers a tenant-members route for tenant membership management', async () => {
    const router = await importRouter()
    const route = router.getRoutes().find((candidate) => candidate.name === 'tenant-members')

    expect(route?.meta.requiresAuth).toBe(true)
    expect(route?.meta.requiresTenantAdmin).toBe(true)
    expect(route?.meta.requiresCapability).toBe('multi-tenancy')
  })

  it('registers the tenant workspace the sections live in', async () => {
    const router = await importRouter()
    const route = router.getRoutes().find((candidate) => candidate.name === 'tenant-detail')

    expect(route?.path).toBe('/tenants/:tenantId')
    expect(route?.meta.requiresAuth).toBe(true)
    expect(route?.meta.requiresTenantAdmin).toBe(true)
    expect(route?.meta.requiresCapability).toBe('multi-tenancy')
  })

  // The four tenant pages became sections of that workspace. Their paths still resolve, so a bookmark or an
  // older link lands on the section it meant instead of a dead end.
  // resolve() reports the matched record rather than following a redirect, so the record's own redirect is what
  // says where the old path lands.
  it.each([
    ['tenant-settings', undefined],
    ['tenant-members', 'members'],
    ['tenant-budget-overview', 'budget'],
    ['tenant-spend', 'spend'],
  ])('sends the old %s path into the workspace', async (name, section) => {
    const router = await importRouter()
    const record = router.getRoutes().find((candidate) => candidate.name === name)
    const redirect = record?.redirect as
      | ((to: { params: Record<string, unknown> }) => { name: string; params: Record<string, unknown>; query: Record<string, unknown> })
      | undefined

    expect(typeof redirect).toBe('function')

    const target = redirect!({ params: { tenantId: 'tenant-1' } })
    expect(target.name).toBe('tenant-detail')
    expect(target.params.tenantId).toBe('tenant-1')
    expect(target.query.section).toBe(section)
  })

  it('allows the clients directory for any authenticated client access role', async () => {
    const router = await importRouter()
    const route = router.getRoutes().find((candidate) => candidate.name === 'clients')

    expect(route?.meta.requiresAuth).toBe(true)
    expect(route?.meta.requiresClientAccess).toBe(true)
    expect(route?.meta.requiresClientAdmin).toBeUndefined()
  })

  it('allows the client detail route for any authenticated client access role', async () => {
    const router = await importRouter()
    const route = router.getRoutes().find((candidate) => candidate.name === 'client-detail')

    expect(route?.meta.requiresAuth).toBe(true)
    expect(route?.meta.requiresClientAccess).toBe(true)
    expect(route?.meta.requiresClientAdmin).toBeUndefined()
  })

  it('redirects tenant-only administrators to the tenant directory instead of a guessed tenant id', async () => {
    isAuthenticated.value = true
    tenantRoles.value = {
      'tenant-1': 1,
      'tenant-2': 1,
    }

    const router = await importRouter()
    const homeRoute = router.getRoutes().find((candidate) => candidate.name === 'home')
    const redirect = homeRoute?.redirect as (() => unknown) | undefined

    expect(redirect?.()).toEqual({ name: 'tenant-directory' })
  })

  it('sends a tenant-only administrator to the reviews page when multi-tenancy is not licensed', async () => {
    isAuthenticated.value = true
    tenantRoles.value = { 'tenant-1': 1 }
    availableCapabilities.value = []

    const router = await importRouter()
    const homeRoute = router.getRoutes().find((candidate) => candidate.name === 'home')
    const redirect = homeRoute?.redirect as (() => unknown) | undefined

    expect(redirect?.()).toEqual({ name: 'reviews' })
  })

  it('allows tenant administration routes when multi-tenancy is licensed', async () => {
    isAuthenticated.value = true
    isAdmin.value = true

    const router = await importRouter()
    await router.push({ name: 'tenant-directory' })

    expect(router.currentRoute.value.name).toBe('tenant-directory')
  })

  it('blocks tenant administration routes when multi-tenancy is not licensed', async () => {
    isAuthenticated.value = true
    isAdmin.value = true
    availableCapabilities.value = []

    const router = await importRouter()
    await router.push({ name: 'tenant-directory' })

    expect(router.currentRoute.value.name).toBe('access-denied')
  })

  it('blocks the tenant workspace when multi-tenancy is not licensed', async () => {
    isAuthenticated.value = true
    isAdmin.value = true
    availableCapabilities.value = []

    const router = await importRouter()
    await router.push({ name: 'tenant-detail', params: { tenantId: 'tenant-1' } })

    expect(router.currentRoute.value.name).toBe('access-denied')
  })
})
