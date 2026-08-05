// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { computed, ref } from 'vue'
import type { Router } from 'vue-router'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { RoleLevel } from '@/composables/roles'

const isAuthenticated = ref(true)
const isAdmin = ref(false)
const clientRoles = ref<Record<string, number>>({})
const tenantRoles = ref<Record<string, number>>({})
const edition = ref<'community' | 'commercial'>('commercial')

/** The real implementation, so what the guard asks about is what decides the answer. */
const hasClientRole = vi.fn((clientId: string, minRole: number) => {
  if (isAdmin.value) return true
  const role = clientRoles.value[clientId]
  return role !== undefined && role >= minRole
})

vi.mock('@/composables/useSession', () => ({
  useSession: () => ({
    isAuthenticated: computed(() => isAuthenticated.value),
    isAdmin: computed(() => isAdmin.value),
    clientRoles,
    tenantRoles,
    edition: computed(() => edition.value),
    hasClientRole,
    hasTenantRole: () => false,
    isCapabilityAvailable: () => true,
  }),
}))

async function importRouter(): Promise<Router> {
  vi.resetModules()
  const mod = await import('@/app/router')
  return mod.default
}

const JOB_ID = '4439698f-797b-4fe6-b086-cdac8d9a4630'
const CLIENT_ID = '7e2456e5-f799-4aea-b749-9bf543308780'

describe('job protocol route access', () => {
  beforeEach(() => {
    isAuthenticated.value = true
    isAdmin.value = false
    clientRoles.value = { [CLIENT_ID]: RoleLevel.User }
    tenantRoles.value = {}
    hasClientRole.mockClear()
  })

  it('does not read the job id in the path as a client id', async () => {
    const router = await importRouter()

    await router.push(`/jobs/${JOB_ID}/protocol`)

    // A link that names no client, which is what the browser extension's Trace control opens, is answered
    // by the caller holding client access somewhere, not by asking whether a job id is a client they see.
    expect(router.currentRoute.value.name).toBe('job-protocol')
    expect(hasClientRole).not.toHaveBeenCalledWith(JOB_ID, expect.anything())
  })

  it('lets a client user through to a protocol of a client they hold a role for', async () => {
    const router = await importRouter()

    await router.push(`/jobs/${JOB_ID}/protocol?clientId=${CLIENT_ID}`)

    expect(router.currentRoute.value.name).toBe('job-protocol')
  })

  it('denies a protocol of a client the caller holds no role for', async () => {
    const router = await importRouter()

    await router.push(`/jobs/${JOB_ID}/protocol?clientId=00000000-0000-0000-0000-000000000001`)

    expect(router.currentRoute.value.name).toBe('access-denied')
  })

  it('denies a caller with no client role at all', async () => {
    clientRoles.value = {}
    const router = await importRouter()

    await router.push(`/jobs/${JOB_ID}/protocol`)

    expect(router.currentRoute.value.name).toBe('access-denied')
  })

  it('still reads a client detail route id as the client it is', async () => {
    const router = await importRouter()

    await router.push(`/${CLIENT_ID}`)

    expect(router.currentRoute.value.name).toBe('client-detail')
    expect(hasClientRole).toHaveBeenCalledWith(CLIENT_ID, RoleLevel.User)
  })
})
