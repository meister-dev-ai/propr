// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { expect, test } from '@playwright/test'

test('opens client detail when clicking a client row', async ({ page }) => {
  const pageErrors: string[] = []
  page.on('pageerror', (error) => pageErrors.push(error.message))

  await page.goto('/login')

  await page.getByLabel('Username').fill('admin')
  await page.getByLabel('Password').fill('admin')
  await page.getByRole('button', { name: 'Sign in' }).click()

  await page.waitForURL('**/clients')
  await expect(page.locator('h2.view-title')).toHaveText('Clients')

  await page.locator('tbody tr').first().click()

  await page.waitForURL(/\/1(?:\?.*)?$/)
  await expect(page.getByText('Mocked Client 1')).toBeVisible()
  expect(pageErrors).toEqual([])
})
