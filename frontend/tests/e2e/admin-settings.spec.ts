// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { expect, test } from '@playwright/test'

test('opens account settings and shows password management controls', async ({ page }) => {
  const pageErrors: string[] = []
  page.on('pageerror', (error) => pageErrors.push(error.message))

  await page.goto('/login')

  await page.getByLabel('Username').fill('admin')
  await page.getByLabel('Password').fill('admin')
  await page.locator('form.login-form button[type="submit"]').click()

  await page.waitForURL('**/clients')
  await page.goto('/settings')

  await expect(page.getByRole('heading', { name: 'Profile & Password' })).toBeVisible()
  await expect(page.getByLabel('Current password')).toBeVisible()
  await expect(page.getByRole('button', { name: 'Update password' })).toBeVisible()
  await expect(page.getByRole('button', { name: 'Personal Access Tokens' })).toBeVisible()
  expect(pageErrors).toEqual([])
})
