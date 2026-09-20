// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { beforeEach, describe, expect, it, vi } from 'vitest'

const getMock = vi.fn()

vi.mock('@/services/api', () => ({
  createAdminClient: () => ({ GET: getMock }),
  getApiErrorMessage: (_error: unknown, fallback: string) => fallback,
}))

function ok(data: unknown) {
  return { data, error: undefined, response: { ok: true } }
}

function failed() {
  return { data: undefined, error: { error: 'nope' }, response: { ok: false } }
}

describe('providerAddInsService', () => {
  beforeEach(() => {
    vi.clearAllMocks()
  })

  it('reads both lists from the inventory endpoint', async () => {
    const { getProviderAddInInventory } = await import('@/services/providerAddInsService')
    getMock.mockResolvedValue(
      ok({
        loaded: [{ key: 'meisterdev/example', label: 'Example', origin: 'built-in' }],
        rejected: [{ category: 'mis-packaged', reason: 'It ships the contract.', origin: 'external' }],
      }),
    )

    const inventory = await getProviderAddInInventory()

    expect(getMock).toHaveBeenCalledWith('/admin/ai-provider-add-ins', {})
    expect(inventory.loaded).toHaveLength(1)
    expect(inventory.rejected[0].category).toBe('mis-packaged')
  })

  // A host that loaded nothing and skipped nothing answers with two empty lists, which is not an error and
  // must not render as one.
  it('answers with two empty lists when the host loaded no add-in', async () => {
    const { getProviderAddInInventory } = await import('@/services/providerAddInsService')
    getMock.mockResolvedValue(ok({ loaded: [], rejected: [] }))

    const inventory = await getProviderAddInInventory()

    expect(inventory.loaded).toEqual([])
    expect(inventory.rejected).toEqual([])
  })

  it('throws when the endpoint refuses', async () => {
    const { getProviderAddInInventory } = await import('@/services/providerAddInsService')
    getMock.mockResolvedValue(failed())

    await expect(getProviderAddInInventory()).rejects.toThrow(
      'Failed to load the provider add-in inventory.',
    )
  })
})
