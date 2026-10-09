import { describe, expect, it, vi } from 'vitest'
import { listConnectionScopes } from '@/services/providerDiscoveryService'

const request = vi.hoisted(() => vi.fn(async () => ({ data: [], response: { ok: true } })))
vi.mock('@/services/api', () => ({
  createAdminClient: () => ({ GET: request }),
  getApiErrorMessage: () => 'Discovery failed.',
}))

describe('connection discovery', () => {
  it('addresses the selected connection and supplies the required purpose', async () => {
    await listConnectionScopes('client', 'selected', 'mention')

    expect(request).toHaveBeenCalledWith(
      '/admin/clients/{clientId}/connections/{connectionId}/discovery/scopes',
      expect.objectContaining({ params: expect.objectContaining({
        path: { clientId: 'client', connectionId: 'selected' },
        query: { purpose: 'mention' },
      }) }),
    )
  })
})
