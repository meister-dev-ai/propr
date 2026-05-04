// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { expect, test } from '@playwright/test'

async function setMockSsoAvailability(page: import('@playwright/test').Page, ssoAvailable: boolean) {
  await page.goto('/login')
  await page.waitForLoadState('networkidle')

  const ok = await page.evaluate(async ({ ssoAvailable }) => {
    const response = await fetch(`${window.location.origin}/api/admin/licensing/mock`, {
      method: 'PATCH',
      headers: {
        'Content-Type': 'application/json',
      },
      body: JSON.stringify({
        edition: ssoAvailable ? 'commercial' : 'community',
        ssoAvailable,
      }),
    })

    return response.ok
  }, { ssoAvailable })

  expect(ok).toBeTruthy()
}

test('tenant login stays SSO-only even when installation SSO capability is unavailable', async ({ page }) => {
  await setMockSsoAvailability(page, false)

  await page.goto('/tenants/acme/login')

  await expect(page.getByText('No external providers are enabled for this tenant.')).toBeVisible()
  await expect(page.getByTestId('tenant-provider-link-provider-1')).toHaveCount(0)
  await expect(page.getByText('Back to platform sign-in')).toBeVisible()
})

test('tenant settings hide SSO provider management when SSO capability is unavailable', async ({ page }) => {
  await setMockSsoAvailability(page, false)

  await page.getByLabel('Username').fill('admin')
  await page.getByLabel('Password').fill('admin')
  await page.locator('form.login-form button[type="submit"]').click()

  await page.waitForURL('**/clients')
  await page.goto('/tenants/tenant-1/settings')

  await expect(page.getByText('Tenant memberships are created when someone signs in through an enabled provider')).toBeVisible()
  await expect(page.getByTestId('provider-submit')).toHaveCount(0)
})

test('tenant login exposes external SSO provider links when capability is available', async ({ page }) => {
  await setMockSsoAvailability(page, true)

  await page.goto('/tenants/acme/login')

  await expect(page.getByTestId('tenant-provider-link-provider-1')).toBeVisible()
  await expect(page.getByText('No external providers are enabled for this tenant.')).toHaveCount(0)
})
