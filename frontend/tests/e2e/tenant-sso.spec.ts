// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { expect, test } from '@playwright/test'
import { installLiveRuntimeApiStubs } from './runtimeParity'

async function setMockSsoAvailability(page: import('@playwright/test').Page, testInfo: import('@playwright/test').TestInfo, ssoAvailable: boolean) {
  await installLiveRuntimeApiStubs(page, testInfo)
  await page.goto('/login')
  await page.waitForLoadState('networkidle')

  const ok = await page.evaluate(async ({ ssoAvailable }) => {
    const response = await fetch(`${window.location.origin}/api/admin/licensing/mock`, {
      method: 'PATCH',
      headers: {
        'Content-Type': 'application/json',
      },
      body: JSON.stringify({
        // Keep the installation commercial so tenant-admin routes stay accessible
        // while exercising capability-specific SSO gating.
        edition: 'commercial',
        ssoAvailable,
      }),
    })

    return response.ok
  }, { ssoAvailable })

  expect(ok).toBeTruthy()
}

test('mock authentication options follow the effective licensing capability state', async ({ page }, testInfo) => {
  await installLiveRuntimeApiStubs(page, testInfo)
  await page.goto('/login')
  await page.waitForLoadState('networkidle')

  const options = await page.evaluate(async () => {
    await fetch(`${window.location.origin}/api/admin/licensing/mock`, {
      method: 'PATCH',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ edition: 'community', ssoAvailable: true }),
    })

    const response = await fetch(`${window.location.origin}/api/auth/options`)
    return response.json() as Promise<{
      availableSignInMethods: string[]
      capabilities: Array<{ key: string; isAvailable: boolean }>
    }>
  })

  expect(options.availableSignInMethods).toEqual(['password'])
  expect(options.capabilities.find((capability) => capability.key === 'sso-authentication')?.isAvailable).toBe(false)
  expect(options.capabilities.some((capability) => capability.key === 'parallel-review-execution')).toBe(true)
})

test('mock SSO routes apply a disable override even when the raw SSO switch is on', async ({ page }, testInfo) => {
  await installLiveRuntimeApiStubs(page, testInfo)
  await page.goto('/login')
  await page.waitForLoadState('networkidle')

  const responses = await page.evaluate(async () => {
    await fetch(`${window.location.origin}/api/admin/licensing/mock`, {
      method: 'PATCH',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ edition: 'commercial', ssoAvailable: true }),
    })
    await fetch(`${window.location.origin}/api/admin/licensing/overrides`, {
      method: 'PATCH',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        capabilityOverrides: [{ key: 'sso-authentication', overrideState: 'disabled' }],
      }),
    })

    const [providers, challenge, callback, administration, session] = await Promise.all([
      fetch(`${window.location.origin}/api/auth/tenants/acme/providers`),
      fetch(`${window.location.origin}/api/auth/external/challenge/acme/provider-1`, { redirect: 'manual' }),
      fetch(`${window.location.origin}/api/auth/external/callback/acme`, { redirect: 'manual' }),
      fetch(`${window.location.origin}/api/admin/tenants/tenant-1/sso-providers`),
      fetch(`${window.location.origin}/api/auth/me`),
    ])

    return {
      providers: await providers.json() as { providers: unknown[] },
      challenge: {
        status: challenge.status,
        body: challenge.status === 409 ? await challenge.json() as { reason?: string } : {},
      },
      callback: {
        status: callback.status,
        body: callback.status === 409 ? await callback.json() as { reason?: string } : {},
      },
      administration: {
        status: administration.status,
        body: administration.status === 409 ? await administration.json() as { reason?: string } : {},
      },
      session: await session.json() as {
        capabilities: Array<{ key: string, isAvailable: boolean, reason?: string }>,
      },
    }
  })

  expect(responses.providers.providers).toEqual([])
  expect(responses.challenge).toMatchObject({ status: 409, body: { reason: 'disabledByOverride' } })
  expect(responses.callback).toMatchObject({ status: 409, body: { reason: 'disabledByOverride' } })
  expect(responses.administration).toMatchObject({ status: 409, body: { reason: 'disabledByOverride' } })
  expect(responses.session.capabilities).toContainEqual(expect.objectContaining({
    key: 'sso-authentication',
    isAvailable: false,
    reason: 'disabledByOverride',
  }))

  await page.evaluate(async () => {
    await fetch(`${window.location.origin}/api/admin/licensing/overrides`, {
      method: 'PATCH',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        capabilityOverrides: [{ key: 'sso-authentication', overrideState: 'default' }],
      }),
    })
  })
})

test('tenant login stays SSO-only even when installation SSO capability is unavailable', async ({ page }, testInfo) => {
  await installLiveRuntimeApiStubs(page, testInfo)
  await setMockSsoAvailability(page, testInfo, false)

  await page.goto('/tenants/acme/login')

  await expect(page.getByText('No external providers are enabled for this tenant.')).toBeVisible()
  await expect(page.getByTestId('tenant-provider-link-provider-1')).toHaveCount(0)
  await expect(page.getByText('Back to platform sign-in')).toBeVisible()
})

test('tenant settings hide SSO provider management when SSO capability is unavailable', async ({ page }, testInfo) => {
  await installLiveRuntimeApiStubs(page, testInfo)
  await setMockSsoAvailability(page, testInfo, false)

  await page.getByLabel('Username').fill('admin')
  await page.getByLabel('Password').fill('admin')
  await page.locator('form.login-form button[type="submit"]').click()

  await page.waitForURL('**/clients')
  await page.goto('/tenants/tenant-1/settings')

  await expect(page.getByText('Tenant memberships are created when someone signs in through an enabled provider')).toBeVisible()
  await expect(page.getByTestId('provider-submit')).toHaveCount(0)
})

test('tenant login exposes external SSO provider links when capability is available', async ({ page }, testInfo) => {
  await installLiveRuntimeApiStubs(page, testInfo)
  await setMockSsoAvailability(page, testInfo, true)

  await page.goto('/tenants/acme/login')

  await expect(page.getByTestId('tenant-provider-link-provider-1')).toBeVisible()
  await expect(page.getByText('No external providers are enabled for this tenant.')).toHaveCount(0)
})
