// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { computed, ref } from 'vue'
import type { Router } from 'vue-router'
import { beforeEach, describe, expect, it, vi } from 'vitest'

const isAuthenticated = ref(true)
const isAdmin = ref(false)
const clientRoles = ref<Record<string, number>>({})
const tenantRoles = ref<Record<string, number>>({})
const edition = ref<'community' | 'commercial'>('commercial')
const capabilityAvailable = ref(true)
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
    isCapabilityAvailable: (key: string) => key === 'distributed-execution' && capabilityAvailable.value,
  }),
}))

async function importRouter(): Promise<Router> {
  vi.resetModules()
  const { default: router } = await import('@/app/router')
  return router
}

describe('runner routes', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    isAuthenticated.value = true
    isAdmin.value = false
    clientRoles.value = {}
    tenantRoles.value = {}
    edition.value = 'commercial'
    capabilityAvailable.value = true
    hasTenantRole.mockReturnValue(false)
  })

  it('the installation-wide fleet takes platform administration and the licence', async () => {
    const router = await importRouter()
    const route = router.getRoutes().find((candidate) => candidate.name === 'runners-all')

    expect(route?.meta.requiresAuth).toBe(true)
    expect(route?.meta.requiresAdmin).toBe(true)
    expect(route?.meta.requiresCapability).toBe('distributed-execution')
  })

  it("a tenant's fleet takes administration of that tenant, not of the platform", async () => {
    const router = await importRouter()
    const route = router.getRoutes().find((candidate) => candidate.name === 'runners')

    expect(route?.meta.requiresAuth).toBe(true)
    expect(route?.meta.requiresTenantAdmin).toBe(true)
    expect(route?.meta.requiresCapability).toBe('distributed-execution')
    // requiresAdmin here denied every tenant administrator the nav had just offered the link to.
    expect(route?.meta.requiresAdmin).toBeUndefined()
  })

  it('a platform administrator reaches the installation-wide fleet', async () => {
    isAdmin.value = true

    const router = await importRouter()
    await router.push({ name: 'runners-all' })

    expect(router.currentRoute.value.name).toBe('runners-all')
  })

  it('a tenant administrator reaches their own tenant fleet', async () => {
    hasTenantRole.mockReturnValue(true)
    tenantRoles.value = { 'tenant-1': 1 }

    const router = await importRouter()
    await router.push({ name: 'runners', params: { tenantId: 'tenant-1' } })

    expect(router.currentRoute.value.name).toBe('runners')
  })

  it('a tenant administrator is turned away from another tenant fleet', async () => {
    hasTenantRole.mockReturnValue(false)
    tenantRoles.value = { 'tenant-1': 1 }

    const router = await importRouter()
    await router.push({ name: 'runners', params: { tenantId: 'tenant-2' } })

    expect(router.currentRoute.value.name).not.toBe('runners')
  })

  it('a tenant administrator is turned away from the installation-wide fleet', async () => {
    hasTenantRole.mockReturnValue(true)
    tenantRoles.value = { 'tenant-1': 1 }

    const router = await importRouter()
    await router.push({ name: 'runners-all' })

    expect(router.currentRoute.value.name).not.toBe('runners-all')
  })

  it('an unlicensed installation admits nobody to either fleet view', async () => {
    isAdmin.value = true
    capabilityAvailable.value = false

    const router = await importRouter()
    await router.push({ name: 'runners-all' })

    expect(router.currentRoute.value.name).not.toBe('runners-all')
  })
})
