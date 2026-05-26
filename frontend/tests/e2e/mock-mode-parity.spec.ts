// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { expect, test } from '@playwright/test'
import { installLiveRuntimeApiStubs } from './runtimeParity'

test('login and settings workflow behave consistently across runtime modes', async ({ page }, testInfo) => {
  await installLiveRuntimeApiStubs(page, testInfo)

  await page.goto('/login')

  await expect(page.getByText('Password and single sign-on are available for this installation.')).toBeVisible()

  await page.getByLabel('Username').fill('admin')
  await page.getByLabel('Password').fill('admin')
  await page.locator('form.login-form button[type="submit"]').click()

  await page.waitForURL('**/clients')
  await expect(page.locator('h2.view-title')).toHaveText('Clients')

  await page.goto('/settings')
  await expect(page.getByRole('heading', { name: 'Profile & Password' })).toBeVisible()
  await expect(page.getByLabel('Current password')).toBeVisible()
  await expect(page.getByRole('button', { name: 'Personal Access Tokens' })).toBeVisible()

  await page.getByRole('button', { name: 'Personal Access Tokens' }).click()
  await expect(page.getByRole('heading', { name: 'Personal Access Tokens' })).toBeVisible()

  await expect(page.getByText('CI Pipeline')).toBeVisible()
})
