// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { describe, expect, it } from 'vitest'
import { ElevationRequiredError, readElevationRequired, throwForFailedResponse } from '@/services/elevationRequirement'

describe('elevationRequirement', () => {
  it('recognizes the refusal of content that needs an elevation', () => {
    expect(readElevationRequired({ code: 'elevation_required', tenantId: 't', clientId: 'c', error: 'x' }))
      .toEqual({ tenantId: 't', clientId: 'c' })
    expect(readElevationRequired({ error: 'Access denied.' })).toBeNull()
    expect(readElevationRequired(null)).toBeNull()
  })

  it('throws an elevation error for a refused content read and a plain error otherwise', async () => {
    const refused = new Response(JSON.stringify({ code: 'elevation_required', tenantId: 't', clientId: 'c' }), { status: 403 })
    const error = await throwForFailedResponse(refused, 'GET /clients/c/pr-view').catch((caught: unknown) => caught)
    expect(error).toBeInstanceOf(ElevationRequiredError)
    expect((error as InstanceType<typeof ElevationRequiredError>).requirement).toEqual({ tenantId: 't', clientId: 'c' })

    const forbidden = new Response(JSON.stringify({ error: 'Access denied.' }), { status: 403 })
    await expect(throwForFailedResponse(forbidden, 'GET /clients/c/pr-view')).rejects.toThrow('GET /clients/c/pr-view: 403')
  })
})
