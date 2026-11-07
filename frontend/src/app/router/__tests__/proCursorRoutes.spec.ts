// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { computed, ref } from 'vue'
import type { Router } from 'vue-router'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { RoleLevel } from '@/composables/roles'

const CLIENT_ID = '7e2456e5-f799-4aea-b749-9bf543308780'
const proCursorConfigured = ref(true)
const clientRoles = ref<Record<string, number>>({ [CLIENT_ID]: RoleLevel.Administrator })

vi.mock('@/composables/useSession', () => ({
  useSession: () => ({
    isAuthenticated: computed(() => true),
    isAdmin: computed(() => false),
    clientRoles,
    tenantRoles: ref({}),
    hasClientRole: (clientId: string, minRole: number) => (clientRoles.value[clientId] ?? -1) >= minRole,
    hasTenantRole: () => false,
    isCapabilityAvailable: () => true,
    isProCursorConfigured: computed(() => proCursorConfigured.value),
  }),
}))

async function importRouter(): Promise<Router> {
  vi.resetModules()
  const mod = await import('@/app/router')
  return mod.default
}

describe('ProCursor source events route', () => {
  beforeEach(() => {
    proCursorConfigured.value = true
  })

  it('opens the source events while the installation runs with ProCursor', async () => {
    const router = await importRouter()

    await router.push(`/${CLIENT_ID}/procursor/sources/source-1/events`)

    expect(router.currentRoute.value.name).toBe('client-procursor-source-events')
  })

  it('opens the client page for a link to the source events while the installation runs without ProCursor', async () => {
    proCursorConfigured.value = false
    const router = await importRouter()

    await router.push(`/${CLIENT_ID}/procursor/sources/source-1/events`)

    expect(router.currentRoute.value.name).toBe('client-detail')
    expect(router.currentRoute.value.params.id).toBe(CLIENT_ID)
  })
})
