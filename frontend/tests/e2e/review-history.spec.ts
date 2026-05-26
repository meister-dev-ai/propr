// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { expect, test } from '@playwright/test'

test('opens PR review from review history', async ({ page }) => {
  const pageErrors: string[] = []
  page.on('pageerror', (error) => pageErrors.push(error.message))

  await page.goto('/login')

  await page.getByLabel('Username').fill('admin')
  await page.getByLabel('Password').fill('admin')
  await page.locator('form.login-form button[type="submit"]').click()

  await page.waitForURL('**/clients')
  await page.goto('/reviews')

  await expect(page.locator('h2.view-title')).toHaveText('Review History')
  await expect(page.getByRole('link', { name: 'PR View ↗' }).first()).toBeVisible()

  await page.getByRole('link', { name: 'PR View ↗' }).first().click()

  await page.waitForURL(/\/pr-review\?/)
  await expect(page.getByRole('heading', { name: 'PR Review View' })).toBeVisible()
  await expect(page.getByRole('heading', { name: 'Review Jobs' })).toBeVisible()
  expect(pageErrors).toEqual([])
})
