// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { createRuntime } from '@/app/runtime/createRuntime'
import { resetActiveRuntime, setActiveRuntime } from '@/app/runtime/runtimeContext'
import { getAuthOptions, getPlatformAdminSignInUrl, supportsTenantSignIn } from '@/services/authOptionsService'

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

  // The sign-in screen renders what the edition allows and keeps no edition of its own, so both editions have
  // to be read off the payload rather than one of them being what the service falls back to.
  it.each([['community'], ['commercial']])('carries the %s edition off the fetched payload', async (edition) => {
    vi.mocked(global.fetch).mockResolvedValueOnce(
      new Response(
        JSON.stringify({
          edition,
          availableSignInMethods: ['password'],
          capabilities: [],
          publicBaseUrl: null,
        }),
        {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        },
      ),
    )

    const options = await getAuthOptions()

    expect(options.edition).toBe(edition)
  })

  // A payload that carries no edition reads as the edition an installation has without a license, so nothing
  // commercial is offered on the strength of a field the backend did not send.
  it('reads a payload with no edition as community', async () => {
    vi.mocked(global.fetch).mockResolvedValueOnce(
      new Response(JSON.stringify({ availableSignInMethods: ['password', 'sso'] }), {
        status: 200,
        headers: { 'Content-Type': 'application/json' },
      }),
    )

    const options = await getAuthOptions()

    expect(options.edition).toBe('community')
    expect(supportsTenantSignIn(options)).toBe(false)
  })

  it('carries the identity provider for platform administrators when the installation offers one', async () => {
    vi.mocked(global.fetch).mockResolvedValueOnce(
      new Response(
        JSON.stringify({
          edition: 'commercial',
          availableSignInMethods: ['password', 'sso'],
          capabilities: [],
          platformAdminSignIn: { displayName: 'Meister DEV staff' },
        }),
        {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        },
      ),
    )

    const options = await getAuthOptions()

    expect(options.platformAdminSignIn).toEqual({ displayName: 'Meister DEV staff' })
  })

  it('reads a payload without the identity provider for platform administrators as none', async () => {
    vi.mocked(global.fetch).mockResolvedValueOnce(
      new Response(JSON.stringify({ edition: 'commercial', platformAdminSignIn: null }), {
        status: 200,
        headers: { 'Content-Type': 'application/json' },
      }),
    )

    const options = await getAuthOptions()

    expect(options.platformAdminSignIn).toBeNull()
  })

  it('builds the address that starts the platform administrator sign-in on the API base URL', () => {
    setActiveRuntime(createRuntime({ mode: 'live', apiBaseUrl: '/custom-api/' }))

    const url = getPlatformAdminSignInUrl('https://propr.example.test/login/callback')

    expect(url).toBe(
      '/custom-api/auth/platform-admin/challenge?returnUrl=https%3A%2F%2Fpropr.example.test%2Flogin%2Fcallback',
    )
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
