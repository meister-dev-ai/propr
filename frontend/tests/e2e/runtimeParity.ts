// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import type { Page, TestInfo } from '@playwright/test'

type RuntimeKind = 'mock' | 'live'

export function getRuntimeKind(testInfo: TestInfo): RuntimeKind {
  return testInfo.project.name === 'live' ? 'live' : 'mock'
}

export async function installLiveRuntimeApiStubs(page: Page, testInfo: TestInfo): Promise<void> {
  if (getRuntimeKind(testInfo) !== 'live') {
    return
  }

  let licensingState = {
    edition: 'commercial',
    ssoAvailable: true,
  }

  await page.route('**/api/auth/options', async (route) => {
    await route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        edition: licensingState.edition,
        availableSignInMethods: licensingState.ssoAvailable ? ['password', 'sso'] : ['password'],
        capabilities: [
          {
            key: 'sso-authentication',
            displayName: 'Single sign-on authentication',
            requiresCommercial: true,
            defaultWhenCommercial: true,
            overrideState: 'default',
            isAvailable: licensingState.ssoAvailable,
            message: licensingState.ssoAvailable
              ? null
              : 'A commercial license is required to use single sign-on, including in self-hosted deployments.',
          },
        ],
        publicBaseUrl: 'http://127.0.0.1:4173/',
      }),
    })
  })

  await page.route('**/api/admin/licensing/mock', async (route) => {
    const body = route.request().postDataJSON() as { edition?: string; ssoAvailable?: boolean }
    licensingState = {
      edition: body.edition === 'community' ? 'community' : 'commercial',
      ssoAvailable: body.ssoAvailable !== false,
    }

    await route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        edition: licensingState.edition,
        capabilities: [
          {
            key: 'sso-authentication',
            isAvailable: licensingState.ssoAvailable,
          },
        ],
      }),
    })
  })

  await page.route('**/api/auth/login', async (route) => {
    await route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        accessToken: createJwt({ global_role: 'Admin', unique_name: 'mock.admin' }),
        refreshToken: 'mock-refresh',
      }),
    })
  })

  await page.route('**/api/auth/refresh', async (route) => {
    await route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ accessToken: createJwt({ global_role: 'Admin', unique_name: 'mock.admin' }) }),
    })
  })

  await page.route('**/api/auth/me', async (route) => {
    await route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        globalRole: 'Admin',
        clientRoles: { '1': 1, '2': 1 },
        tenantRoles: { 'tenant-1': 1 },
        hasLocalPassword: true,
        edition: licensingState.edition,
        capabilities: [
          {
            key: 'sso-authentication',
            isAvailable: licensingState.ssoAvailable,
            message: licensingState.ssoAvailable
              ? null
              : 'A commercial license is required to use single sign-on, including in self-hosted deployments.',
          },
          {
            key: 'multiple-scm-providers',
            isAvailable: true,
            message: null,
          },
          {
            key: 'crawl-configs',
            isAvailable: true,
            message: null,
          },
        ],
      }),
    })
  })

  await page.route('**/api/clients', async (route) => {
    await route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify([
        {
          id: '1',
          displayName: 'Acme Corp',
          isActive: true,
          createdAt: new Date('2026-05-01T10:00:00Z').toISOString(),
          recentUsageTokens: 14520,
        },
      ]),
    })
  })

  await page.route('**/api/clients/1', async (route) => {
    if (route.request().method() === 'PATCH') {
      const body = route.request().postDataJSON() as Record<string, unknown>
      await route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({
          id: '1',
          displayName: String(body.displayName ?? 'Mocked Client 1'),
          isActive: body.isActive ?? true,
          createdAt: new Date('2026-05-01T10:00:00Z').toISOString(),
          scmCommentPostingEnabled: body.scmCommentPostingEnabled ?? true,
          defaultReviewStrategy: body.defaultReviewStrategy ?? 'fileByFile',
        }),
      })
      return
    }

    await route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        id: '1',
        displayName: 'Mocked Client 1',
        isActive: true,
        createdAt: new Date('2026-05-01T10:00:00Z').toISOString(),
        scmCommentPostingEnabled: true,
        defaultReviewStrategy: 'fileByFile',
      }),
    })
  })

  await page.route('**/api/auth/tenants/acme/providers', async (route) => {
    await route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        tenantSlug: 'acme',
        localLoginEnabled: true,
        providers: licensingState.ssoAvailable
          ? [{ providerId: 'provider-1', displayName: 'Acme Entra', providerKind: 'EntraId', providerLabel: 'EntraID' }]
          : [],
      }),
    })
  })

  await page.route('**/api/auth/external/challenge/acme/provider-1**', async (route) => {
    const url = new URL(route.request().url())
    const returnUrl = url.searchParams.get('returnUrl')
    await route.fulfill({
      status: 302,
      headers: {
        location: returnUrl ?? 'http://127.0.0.1:4173/tenants/acme/login/callback',
      },
      body: '',
    })
  })

  await page.route('**/api/admin/tenants/tenant-1', async (route) => {
    await route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        id: 'tenant-1',
        slug: 'acme',
        displayName: 'Acme Corp',
        isActive: true,
        isEditable: true,
        localLoginEnabled: true,
        createdAt: new Date('2026-05-01T10:00:00Z').toISOString(),
        updatedAt: new Date('2026-05-01T10:00:00Z').toISOString(),
      }),
    })
  })

  await page.route('**/api/admin/tenants/tenant-1/sso-providers', async (route) => {
    if (route.request().method() === 'POST') {
      const body = route.request().postDataJSON() as Record<string, unknown>
      await route.fulfill({
        status: 201,
        contentType: 'application/json',
        body: JSON.stringify({
          id: 'provider-2',
          tenantId: 'tenant-1',
          displayName: String(body.displayName ?? 'New provider'),
          providerKind: String(body.providerKind ?? 'EntraId'),
          protocolKind: String(body.protocolKind ?? 'Oidc'),
          issuerOrAuthorityUrl: body.issuerOrAuthorityUrl ?? null,
          clientId: String(body.clientId ?? 'generated-client-id'),
          secretConfigured: Boolean(body.clientSecret),
          scopes: Array.isArray(body.scopes) ? body.scopes : [],
          allowedEmailDomains: Array.isArray(body.allowedEmailDomains) ? body.allowedEmailDomains : [],
          isEnabled: body.isEnabled ?? true,
          autoCreateUsers: body.autoCreateUsers ?? true,
          createdAt: new Date('2026-05-01T10:00:00Z').toISOString(),
          updatedAt: new Date('2026-05-01T10:00:00Z').toISOString(),
        }),
      })
      return
    }

    await route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify(
        licensingState.ssoAvailable
          ? [
              {
                id: 'provider-1',
                tenantId: 'tenant-1',
                displayName: 'Acme Entra',
                providerKind: 'EntraId',
                protocolKind: 'Oidc',
                issuerOrAuthorityUrl: 'https://login.microsoftonline.com/common/v2.0',
                clientId: 'acme-client-id',
                secretConfigured: true,
                scopes: ['openid', 'profile', 'email'],
                allowedEmailDomains: ['acme.test'],
                isEnabled: true,
                autoCreateUsers: true,
                createdAt: new Date('2026-05-01T10:00:00Z').toISOString(),
                updatedAt: new Date('2026-05-01T10:00:00Z').toISOString(),
              },
            ]
          : [],
      ),
    })
  })

  await page.route('**/api/users/me/password', async (route) => {
    await route.fulfill({ status: 204, body: '' })
  })

  await page.route('**/api/users/me/pats', async (route) => {
    if (route.request().method() === 'POST') {
      await route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({ token: 'mock-pat-token' }),
      })
      return
    }

    await route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify([
        {
          id: 'p1',
          label: 'CI Pipeline',
          createdAt: new Date('2026-05-01T10:00:00Z').toISOString(),
          lastUsedAt: new Date('2026-05-02T10:00:00Z').toISOString(),
          expiresAt: null,
          isRevoked: false,
        },
      ]),
    })
  })

  await page.route('**/api/users/me/pats/*', async (route) => {
    await route.fulfill({ status: 204, body: '' })
  })
}

function createJwt(payload: { global_role: string; unique_name: string }): string {
  const header = toBase64Url(JSON.stringify({ alg: 'none', typ: 'JWT' }))
  const body = toBase64Url(JSON.stringify({ ...payload, exp: Math.floor(Date.now() / 1000) + 3600 }))
  return `${header}.${body}.signature`
}

function toBase64Url(value: string): string {
  return Buffer.from(value, 'utf8').toString('base64url')
}
