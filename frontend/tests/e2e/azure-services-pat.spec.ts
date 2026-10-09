// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { expect, test } from '@playwright/test'

for (const host of ['https://dev.azure.com', 'https://organization.visualstudio.com']) {
  test(`Azure Services editor keeps OAuth default while typing ${host} and saves an explicit PAT choice`, async ({ page }, testInfo) => {
    test.skip(testInfo.project.name !== 'mock', 'Uses the mock runtime account and client.')
    await page.goto('/login')
    await page.getByLabel('Username').fill('admin')
    await page.getByLabel('Password').fill('admin')
    await page.locator('form.login-form button[type="submit"]').click()
    await page.waitForURL('**/clients')
    await page.locator('tbody tr').first().click()
    await page.getByRole('complementary').getByRole('button', { name: /SCM Providers/ }).click()
    await page.getByRole('button', { name: /Add Connection/ }).click()
    await page.getByRole('combobox', { name: 'Provider', exact: true }).selectOption('azureDevOps')
    const authentication = page.getByRole('combobox', { name: 'Authentication', exact: true })
    await expect(authentication).toHaveValue('oauthClientCredentials')
    const tenantId = page.getByRole('textbox', { name: 'OAuth Tenant ID', exact: true })
    const clientId = page.getByRole('textbox', { name: 'OAuth Client ID', exact: true })
    await tenantId.fill('synthetic-tenant')
    await clientId.fill('synthetic-client')
    const hostInput = page.getByRole('textbox', { name: 'Host Base URL', exact: true })
    await hostInput.fill('')
    await hostInput.pressSequentially(host, { delay: 10 })
    await expect(hostInput).toHaveValue(host)
    await expect(hostInput).toBeFocused()
    await expect(authentication).toHaveValue('oauthClientCredentials')
    await expect(tenantId).toHaveValue('synthetic-tenant')
    await expect(clientId).toHaveValue('synthetic-client')
    await authentication.selectOption('personalAccessToken')
    await expect(authentication).toHaveValue('personalAccessToken')
    await expect(page.getByRole('textbox', { name: 'OAuth Client ID', exact: true })).toHaveCount(0)
    await expect(page.getByRole('textbox', { name: 'OAuth Tenant ID', exact: true })).toHaveCount(0)
    const secretInput = page.getByLabel('Secret', { exact: true })
    await expect(secretInput).toBeVisible()
    await expect(secretInput).toBeEditable()
    await expect(secretInput).toHaveAttribute('type', 'password')
    const displayName = 'Synthetic Azure Services PAT'
    const secret = 'synthetic-browser-pat'
    await page.getByRole('textbox', { name: 'Display Name', exact: true }).fill(displayName)
    await secretInput.fill(secret)
    await expect(secretInput).toHaveValue(secret)
    await hostInput.fill('')
    await hostInput.pressSequentially(host, { delay: 10 })
    await expect(hostInput).toHaveValue(host)
    await expect(hostInput).toBeFocused()
    await expect(authentication).toHaveValue('personalAccessToken')
    await expect(secretInput).toHaveValue(secret)

    const savedResponse = page.waitForResponse(response =>
      response.request().method() === 'POST'
      && /\/clients\/[^/]+\/provider-connections$/.test(new URL(response.url()).pathname),
    )
    await page.getByRole('button', { name: 'Save Connection', exact: true }).click()
    const response = await savedResponse
    expect(response.status()).toBe(201)
    expect(response.request().postDataJSON()).toMatchObject({
      providerFamily: 'azureDevOps',
      hostBaseUrl: host,
      authenticationKind: 'personalAccessToken',
      displayName,
      secret,
      oAuthTenantId: null,
      oAuthClientId: null,
    })
    await expect(page.getByRole('heading', { name: displayName, exact: true })).toBeVisible()
    await page.getByRole('complementary').getByRole('button', { name: /Back to list/ }).click()
    const savedConnection = page.locator('article.provider-connection-item').filter({ hasText: displayName })
    await expect(savedConnection).toBeVisible()
    await expect(savedConnection).toContainText(host)
    await savedConnection.click()
    await expect(hostInput).toHaveValue(host)
    await expect(page.getByRole('textbox', { name: 'Display Name', exact: true })).toHaveValue(displayName)
    await expect(authentication).toHaveValue('personalAccessToken')
    await expect(page.getByLabel('Secret (leave blank to keep current)', { exact: true })).toHaveValue('')
  })
}
