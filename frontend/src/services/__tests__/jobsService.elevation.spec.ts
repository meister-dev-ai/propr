// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { beforeEach, describe, expect, it, vi } from 'vitest'
import { ElevationRequiredError } from '@/services/elevationRequirement'

const authedFetch = vi.fn()

vi.mock('@/services/api', () => ({
  authedFetch: (...args: unknown[]) => authedFetch(...args),
  createAdminClient: vi.fn(),
  getApiErrorMessage: (_error: unknown, fallback: string) => fallback,
}))

const identity = { providerScopePath: 'https://dev.azure.com/org', providerProjectKey: 'proj', repositoryId: 'repo', pullRequestId: 7 }

describe('getPrView', () => {
  beforeEach(() => {
    authedFetch.mockReset()
  })

  it('keeps the refusal of a pull request that needs an elevation, so the view can offer one', async () => {
    authedFetch.mockResolvedValue(
      new Response(JSON.stringify({ code: 'elevation_required', tenantId: 'tenant-1', clientId: 'client-1' }), { status: 403 }),
    )
    const { getPrView } = await import('@/services/jobsService')

    const error = await getPrView('client-1', identity).catch((caught: unknown) => caught)

    expect(error).toBeInstanceOf(ElevationRequiredError)
    expect((error as ElevationRequiredError).requirement).toEqual({ tenantId: 'tenant-1', clientId: 'client-1' })
  })

  it('reports any other failure as a failed load', async () => {
    authedFetch.mockResolvedValue(new Response(JSON.stringify({ error: 'Access denied.' }), { status: 403 }))
    const { getPrView } = await import('@/services/jobsService')

    const error = await getPrView('client-1', identity).catch((caught: unknown) => caught)

    expect(error).not.toBeInstanceOf(ElevationRequiredError)
    expect(error).toBeInstanceOf(Error)
    expect((error as Error).message).toBe('GET /clients/client-1/pr-view: 403')
  })
})
