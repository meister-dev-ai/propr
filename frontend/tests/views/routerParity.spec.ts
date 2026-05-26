// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { describe, expect, it, vi } from 'vitest'
import { computed, ref } from 'vue'

vi.mock('@/composables/useSession', () => ({
  useSession: () => ({
    isAuthenticated: computed(() => false),
    isAdmin: computed(() => false),
    clientRoles: ref<Record<string, number>>({}),
    tenantRoles: ref<Record<string, number>>({}),
    edition: computed(() => 'commercial' as const),
    hasClientRole: vi.fn(() => false),
    hasTenantRole: vi.fn(() => false),
  }),
}))

import router from '@/app/router'
import { routeParity } from '@/app/routeParity'

describe('route parity (FR-003, frontend-ui-parity contract)', () => {
  it.each(routeParity.map((item) => [item.id, item.workflowName] as const))(
    'registers route %s (%s) per the parity contract',
    (id) => {
      expect(router.hasRoute(id)).toBe(true)
    },
  )

  it('keeps the legacy /pats redirect to /settings', () => {
    const route = router.getRoutes().find((candidate) => candidate.path === '/pats')
    expect(route).toBeDefined()
    expect(route?.redirect).toBe('/settings')
  })

  it('client-detail-providers redirects to the client-detail providers tab', () => {
    const route = router.getRoutes().find((candidate) => candidate.name === 'client-detail-providers')
    expect(route).toBeDefined()
    expect(typeof route?.redirect).toBe('function')
    const redirect = route?.redirect as (to: { params: Record<string, unknown>; query: Record<string, unknown> }) => unknown
    expect(redirect({ params: { id: 'abc' }, query: {} })).toEqual({
      name: 'client-detail',
      params: { id: 'abc' },
      query: { tab: 'providers' },
    })
  })

  it('protects every authenticated route with requiresAuth meta', () => {
    const authProtected = ['clients', 'reviews', 'job-protocol', 'settings', 'users', 'thread-memory', 'provider-settings', 'licensing', 'pr-review', 'client-detail', 'client-procursor-source-events', 'tenant-directory', 'tenant-settings', 'tenant-members']
    for (const name of authProtected) {
      const route = router.getRoutes().find((candidate) => candidate.name === name)
      expect(route, `route ${name} should exist`).toBeDefined()
      expect(route?.meta.requiresAuth, `route ${name} should require auth`).toBe(true)
    }
  })

  it('marks platform-admin-only routes with requiresAdmin meta', () => {
    const adminOnly = ['users', 'thread-memory', 'provider-settings', 'licensing']
    for (const name of adminOnly) {
      const route = router.getRoutes().find((candidate) => candidate.name === name)
      expect(route?.meta.requiresAdmin, `route ${name} should require admin`).toBe(true)
    }
  })

  it('marks client-access routes with requiresClientAccess meta', () => {
    const clientAccess = ['clients', 'job-protocol', 'pr-review', 'client-detail']
    for (const name of clientAccess) {
      const route = router.getRoutes().find((candidate) => candidate.name === name)
      expect(route?.meta.requiresClientAccess, `route ${name} should require client access`).toBe(true)
    }
  })

  it('marks tenant-admin routes with requiresTenantAdmin meta', () => {
    const tenantAdminOnly = ['tenant-settings', 'tenant-members']
    for (const name of tenantAdminOnly) {
      const route = router.getRoutes().find((candidate) => candidate.name === name)
      expect(route?.meta.requiresTenantAdmin, `route ${name} should require tenant admin`).toBe(true)
    }
  })
})
