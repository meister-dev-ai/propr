// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { beforeEach, describe, expect, it, vi } from 'vitest'
import { ref } from 'vue'
import { listPerformanceClients } from '../reviewerPerformanceService'

const get = vi.fn()
const isAdmin = ref(false)
const tenantRoles = ref<Record<string, number>>({})

vi.mock('@/services/api', () => ({
  createAdminClient: () => ({ GET: (...args: unknown[]) => get(...args) }),
  getApiErrorMessage: (_error: unknown, fallback: string) => fallback,
}))
vi.mock('@/composables/useSession', () => ({
  useSession: () => ({ isAdmin, tenantRoles }),
}))

const visibleClients = [
  { id: 'managed', displayName: 'Managed tenant', tenantId: 'tenant-admin' },
  { id: 'member', displayName: 'Client membership only', tenantId: 'tenant-member' },
  { id: 'unowned', displayName: 'No tenant', tenantId: null },
]

describe('reviewer performance client catalogue', () => {
  beforeEach(() => {
    isAdmin.value = false
    tenantRoles.value = {}
    get.mockReset()
    get.mockResolvedValue({ data: visibleClients })
  })

  it('includes clients without metrics for a platform administrator', async () => {
    isAdmin.value = true

    expect(await listPerformanceClients()).toEqual([
      { id: 'member', label: 'Client membership only' },
      { id: 'managed', label: 'Managed tenant' },
      { id: 'unowned', label: 'No tenant' },
    ])
    expect(get).toHaveBeenCalledWith('/clients', {})
  })

  it('limits choices to tenants the caller administers', async () => {
    tenantRoles.value = { 'tenant-admin': 1, 'tenant-member': 0 }

    expect(await listPerformanceClients()).toEqual([
      { id: 'managed', label: 'Managed tenant' },
    ])
  })

  it('does not request a catalogue for ordinary client or tenant members', async () => {
    tenantRoles.value = { 'tenant-member': 0 }

    expect(await listPerformanceClients()).toEqual([])
    expect(get).not.toHaveBeenCalled()
  })

  it('uses current tenant permissions when a pending catalogue request completes', async () => {
    tenantRoles.value = { 'tenant-admin': 1 }
    let respond!: (value: { data: typeof visibleClients }) => void
    get.mockReturnValueOnce(new Promise((resolve) => { respond = resolve }))

    const request = listPerformanceClients()
    tenantRoles.value = { 'tenant-member': 1 }
    respond({ data: visibleClients })

    expect(await request).toEqual([
      { id: 'member', label: 'Client membership only' },
    ])
  })

  it('excludes clients when tenant administration is removed during the request', async () => {
    tenantRoles.value = { 'tenant-admin': 1 }
    let respond!: (value: { data: typeof visibleClients }) => void
    get.mockReturnValueOnce(new Promise((resolve) => { respond = resolve }))

    const request = listPerformanceClients()
    tenantRoles.value = { 'tenant-admin': 0 }
    respond({ data: visibleClients })

    expect(await request).toEqual([])
  })

  it('surfaces a failed catalogue read without inventing client choices', async () => {
    isAdmin.value = true
    get.mockResolvedValue({ error: { error: 'Failed' } })

    await expect(listPerformanceClients()).rejects.toThrow('Clients could not be loaded.')
  })
})
