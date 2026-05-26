// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { createRuntime } from '@/app/runtime/createRuntime'
import { resetActiveRuntime, setActiveRuntime } from '@/app/runtime/runtimeContext'
import { getAuthOptions, supportsTenantSignIn } from '@/services/authOptionsService'

describe('authOptionsService', () => {
  beforeEach(() => {
    vi.clearAllMocks()
  })

  afterEach(() => {
    resetActiveRuntime()
  })

  it('loads and normalizes auth options against the active runtime base URL', async () => {
    setActiveRuntime(createRuntime({ mode: 'live', apiBaseUrl: '/custom-api' }))
    vi.mocked(global.fetch).mockResolvedValueOnce(
      new Response(
        JSON.stringify({
          edition: 'commercial',
          availableSignInMethods: ['password', 'sso'],
          capabilities: [
            {
              key: 'sso-authentication',
              displayName: 'SSO',
              requiresCommercial: true,
              defaultWhenCommercial: true,
              overrideState: 'default',
              isAvailable: true,
              message: null,
            },
          ],
          publicBaseUrl: 'https://propr.example.test',
        }),
        {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        },
      ),
    )

    const options = await getAuthOptions()

    expect(global.fetch).toHaveBeenCalledWith('/custom-api/auth/options')
    expect(options.edition).toBe('commercial')
    expect(options.availableSignInMethods).toEqual(['password', 'sso'])
    expect(options.publicBaseUrl).toBe('https://propr.example.test')
    expect(supportsTenantSignIn(options)).toBe(true)
  })

  it('uses the mock adapter path when the active runtime is mock', async () => {
    setActiveRuntime(createRuntime({ mode: 'mock', apiBaseUrl: '/mock-api' }))
    vi.mocked(global.fetch).mockResolvedValueOnce(
      new Response(JSON.stringify({}), {
        status: 200,
        headers: { 'Content-Type': 'application/json' },
      }),
    )

    await getAuthOptions()

    expect(global.fetch).toHaveBeenCalledWith('/mock-api/auth/options')
  })
})
