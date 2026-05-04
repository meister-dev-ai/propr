import { computed, ref } from 'vue'
import type { Router } from 'vue-router'
import { beforeEach, describe, expect, it, vi } from 'vitest'

const isAuthenticated = ref(false)
const isAdmin = ref(false)
const clientRoles = ref<Record<string, number>>({})
const tenantRoles = ref<Record<string, number>>({})
const edition = ref<'community' | 'commercial'>('commercial')
const hasClientRole = vi.fn(() => false)
const hasTenantRole = vi.fn(() => false)

vi.mock('@/composables/useSession', () => ({
  useSession: () => ({
    isAuthenticated: computed(() => isAuthenticated.value),
    isAdmin: computed(() => isAdmin.value),
    clientRoles,
    tenantRoles,
    edition: computed(() => edition.value),
    hasClientRole,
    hasTenantRole,
  }),
}))

async function importRouter(): Promise<Router> {
  vi.resetModules()
  const mod = await import('@/router/index')
  return mod.default
}

describe('tenant router scaffolding', () => {
  beforeEach(() => {
    isAuthenticated.value = false
    isAdmin.value = false
    clientRoles.value = {}
    tenantRoles.value = {}
    edition.value = 'commercial'
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
  })

  it('registers a tenant-members route for tenant membership management', async () => {
    const router = await importRouter()
    const route = router.getRoutes().find((candidate) => candidate.name === 'tenant-members')

    expect(route?.meta.requiresAuth).toBe(true)
    expect(route?.meta.requiresTenantAdmin).toBe(true)
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

  it('blocks tenant administration routes in community edition', async () => {
    isAuthenticated.value = true
    isAdmin.value = true
    edition.value = 'community'

    const router = await importRouter()
    await router.push({ name: 'tenant-directory' })

    expect(router.currentRoute.value.name).toBe('access-denied')
  })
})
