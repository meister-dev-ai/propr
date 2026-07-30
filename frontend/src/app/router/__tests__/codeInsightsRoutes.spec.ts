// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { computed, ref } from 'vue'
import type { Router } from 'vue-router'
import { beforeEach, describe, expect, it, vi } from 'vitest'

const isAuthenticated = ref(true)
const isAdmin = ref(false)
const clientRoles = ref<Record<string, number>>({ 'client-a': 0 })
const tenantRoles = ref<Record<string, number>>({})
const edition = ref<'community' | 'commercial'>('commercial')
const capabilityAvailable = ref(true)
const hasClientRole = vi.fn(() => true)
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
    isCapabilityAvailable: (key: string) => key === 'code-insights' && capabilityAvailable.value,
  }),
}))

async function importRouter(): Promise<Router> {
  vi.resetModules()
  const mod = await import('@/app/router')
  return mod.default
}

describe('code insights routes', () => {
  beforeEach(() => {
    isAuthenticated.value = true
    isAdmin.value = false
    clientRoles.value = { 'client-a': 0 }
    tenantRoles.value = {}
    capabilityAvailable.value = true
    hasClientRole.mockReset()
    hasClientRole.mockReturnValue(true)
  })

  it('code quality takes client access and the licence', async () => {
    const router = await importRouter()
    const route = router.getRoutes().find((candidate) => candidate.name === 'code-quality')

    expect(route?.meta.requiresAuth).toBe(true)
    expect(route?.meta.requiresClientAccess).toBe(true)
    expect(route?.meta.requiresCapability).toBe('code-insights')
    // No operator gate: this is what a developer needs from the collected findings.
    expect(route?.meta.requiresAnyTenantAdmin).toBeUndefined()
  })

  it('reviewer performance takes tenant administration and the licence', async () => {
    const router = await importRouter()
    const route = router.getRoutes().find((candidate) => candidate.name === 'reviewer-performance')

    expect(route?.meta.requiresAuth).toBe(true)
    expect(route?.meta.requiresAnyTenantAdmin).toBe(true)
    expect(route?.meta.requiresCapability).toBe('code-insights')
  })

  it('a client user reaches code quality', async () => {
    const router = await importRouter()

    await router.push({ name: 'code-quality' })

    expect(router.currentRoute.value.name).toBe('code-quality')
  })

  it('a client user is refused reviewer performance', async () => {
    // The split is an authorisation boundary, not a presentation choice: client access is not enough.
    const router = await importRouter()

    await router.push('/reviewer-performance')

    expect(router.currentRoute.value.name).toBe('access-denied')
  })

  it('a tenant administrator reaches reviewer performance', async () => {
    tenantRoles.value = { 'tenant-1': 1 }
    const router = await importRouter()

    await router.push({ name: 'reviewer-performance' })

    expect(router.currentRoute.value.name).toBe('reviewer-performance')
  })

  it('a platform administrator reaches reviewer performance', async () => {
    isAdmin.value = true
    const router = await importRouter()

    await router.push({ name: 'reviewer-performance' })

    expect(router.currentRoute.value.name).toBe('reviewer-performance')
  })

  it('refuses either surface when the capability is not licensed', async () => {
    // The nav entries are absent on Community, but a bookmark does not care about the nav, so the guard repeats
    // the check, and the server repeats it again.
    capabilityAvailable.value = false
    isAdmin.value = true
    const router = await importRouter()

    await router.push('/code-quality')
    expect(router.currentRoute.value.name).toBe('access-denied')

    await router.push('/reviewer-performance')
    expect(router.currentRoute.value.name).toBe('access-denied')
  })

  it('the old single-page path lands on code quality rather than a 404', async () => {
    const router = await importRouter()

    await router.push('/code-insights')

    expect(router.currentRoute.value.name).toBe('code-quality')
  })
})
