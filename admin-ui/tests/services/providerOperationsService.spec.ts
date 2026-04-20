// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { beforeEach, describe, expect, it, vi } from 'vitest'

const getMock = vi.fn()

vi.mock('@/services/api', () => ({
  createAdminClient: () => ({
    GET: getMock,
  }),
  getApiErrorMessage: (_error: unknown, fallback: string) => fallback,
}))

describe('providerOperationsService', () => {
  beforeEach(() => {
    vi.resetModules()
    getMock.mockReset()
  })

  it('returns server-provided readiness fields for provider connection status', async () => {
    getMock.mockResolvedValue({
      data: {
        connections: [
          {
            connectionId: 'provider-conn-1',
            providerFamily: 'github',
            displayName: 'GitHub Cloud',
            hostBaseUrl: 'https://github.com',
            hostVariant: 'hosted',
            isActive: true,
            verificationStatus: 'verified',
            readinessLevel: 'onboardingReady',
            readinessReason: 'Connection is verified for onboarding, but workflow-complete readiness criteria are still missing.',
            missingReadinessCriteria: ['Configured reviewer identity is required for workflow-complete readiness.'],
            health: 'degraded',
            lastCheckedAt: '2026-04-17T10:00:00Z',
            failureCategory: null,
            statusReason: 'Connection is verified for onboarding, but workflow-complete readiness criteria are still missing.',
          },
        ],
        providerFamilies: [],
      },
      error: null,
      response: { ok: true },
    })

    const { listProviderOperationalStatus } = await import('@/services/providerOperationsService')
    const result = await listProviderOperationalStatus('client-1')

    expect(getMock).toHaveBeenCalledWith('/clients/{clientId}/provider-operations/status', {
      params: { path: { clientId: 'client-1' } },
    })
    expect(result[0].readinessLevel).toBe('onboardingReady')
    expect(result[0].missingReadinessCriteria).toEqual([
      'Configured reviewer identity is required for workflow-complete readiness.',
    ])
  })
})