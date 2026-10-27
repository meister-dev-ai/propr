// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { beforeEach, describe, expect, it, vi } from 'vitest'

const get = vi.fn()

vi.mock('@/services/api', () => ({
  authedFetch: vi.fn(),
  createAdminClient: () => ({ GET: get }),
  getApiErrorMessage: (_error: unknown, fallback: string) => fallback,
}))

describe('listTenantClients', () => {
  beforeEach(() => {
    get.mockReset()
  })

  // For a platform administrator, GET /clients returns all clients in one list without pages, so one request without
  // parameters yields every client of the tenant.
  it('returns every client of the tenant and no client of another tenant from one request for all clients', async () => {
    get.mockResolvedValue({
      data: [
        { id: 'client-1', displayName: 'Payments', tenantId: 'tenant-1' },
        { id: 'client-2', displayName: 'Ledger', tenantId: 'tenant-2' },
        { id: 'client-3', displayName: 'Invoices', tenantId: 'tenant-1' },
      ],
    })
    const { listTenantClients } = await import('@/services/elevationsService')

    await expect(listTenantClients('tenant-1')).resolves.toEqual([
      { id: 'client-1', displayName: 'Payments' },
      { id: 'client-3', displayName: 'Invoices' },
    ])
    expect(get).toHaveBeenCalledTimes(1)
    expect(get).toHaveBeenCalledWith('/clients')
  })

  it('rejects a failed request, so the caller does not take it for a tenant without clients', async () => {
    get.mockResolvedValue({ error: { error: 'Service unavailable.' } })
    const { listTenantClients } = await import('@/services/elevationsService')

    await expect(listTenantClients('tenant-1')).rejects.toThrow('The clients of the tenant could not be loaded.')
  })
})
