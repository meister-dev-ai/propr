// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { http, HttpResponse, delay } from 'msw'
import protocolMockData from '../../mock/data/protocol_response_1.json'
import { API_BASE_URL } from '@/services/apiBase'

const base = API_BASE_URL

const tenantSsoCapabilityKey = 'sso-authentication'
const mockLicensingStateKey = 'mock-licensing-state'

let mockEdition = 'commercial'
let mockSsoCapabilityAvailable = true

hydrateMockLicensingState()

let mockTenants = [
  {
    id: 'tenant-1',
    slug: 'acme',
    displayName: 'Acme Corp',
    isActive: true,
    localLoginEnabled: true,
    createdAt: '2026-04-24T12:00:00Z',
    updatedAt: '2026-04-24T12:00:00Z',
  },
]

let mockTenantSsoProviders: Record<string, any[]> = {
  'tenant-1': [
    {
      id: 'provider-1',
      tenantId: 'tenant-1',
      displayName: 'Acme Entra',
      providerKind: 'EntraId',
      protocolKind: 'Oidc',
      issuerOrAuthorityUrl: 'https://identity.example.test/acme',
      clientId: 'acme-client-id',
      secretConfigured: true,
      scopes: ['openid', 'profile', 'email'],
      allowedEmailDomains: ['acme.test'],
      isEnabled: true,
      autoCreateUsers: true,
      createdAt: '2026-04-24T12:00:00Z',
      updatedAt: '2026-04-24T12:00:00Z',
    },
  ],
}

function getMockSsoCapability() {
  return {
    key: tenantSsoCapabilityKey,
    displayName: 'Single sign-on authentication',
    requiresCommercial: true,
    defaultWhenCommercial: true,
    overrideState: 'default',
    isAvailable: mockSsoCapabilityAvailable,
    message: mockSsoCapabilityAvailable ? null : 'Commercial edition is required to use single sign-on.',
  }
}

function getMockTenantBySlug(tenantSlug: string) {
  return mockTenants.find((tenant) => tenant.slug === tenantSlug) ?? null
}

function getMockTenantById(tenantId: string) {
  return mockTenants.find((tenant) => tenant.id === tenantId) ?? null
}

function createPremiumFeatureUnavailableResponse() {
  return HttpResponse.json(
    {
      error: 'premium_feature_unavailable',
      feature: tenantSsoCapabilityKey,
      message: 'Commercial edition is required to use single sign-on.',
    },
    { status: 409 },
  )
}

function persistMockLicensingState() {
  if (typeof window === 'undefined') {
    return
  }

  window.localStorage.setItem(mockLicensingStateKey, JSON.stringify({
    edition: mockEdition,
    ssoAvailable: mockSsoCapabilityAvailable,
  }))
}

function hydrateMockLicensingState() {
  if (typeof window === 'undefined') {
    return
  }

  try {
    const rawValue = window.localStorage.getItem(mockLicensingStateKey)
    if (!rawValue) {
      return
    }

    const parsed = JSON.parse(rawValue) as {
      edition?: string
      ssoAvailable?: boolean
    }

    if (parsed.edition === 'community' || parsed.edition === 'commercial') {
      mockEdition = parsed.edition
    }

    if (typeof parsed.ssoAvailable === 'boolean') {
      mockSsoCapabilityAvailable = parsed.ssoAvailable
    }
  } catch {
    // Ignore invalid persisted mock state and keep defaults.
  }
}

function decodeBase64UrlSegment(segment: string): string | null {
  if (!segment) {
    return null
  }

  const normalized = segment.replace(/-/g, '+').replace(/_/g, '/')
  const paddingLength = (4 - (normalized.length % 4)) % 4
  const base64 = normalized.padEnd(normalized.length + paddingLength, '=')

  try {
    const binary = atob(base64)
    const bytes = Uint8Array.from(binary, (character) => character.charCodeAt(0))
    return new TextDecoder().decode(bytes)
  } catch {
    return null
  }
}

function encodeBase64Url(value: string): string {
  return btoa(value)
    .replace(/\+/g, '-')
    .replace(/\//g, '_')
    .replace(/=+$/g, '')
}

function createMockJwt(payload: { global_role: string; unique_name: string }): string {
  const header = encodeBase64Url(JSON.stringify({ alg: 'none', typ: 'JWT' }))
  const body = encodeBase64Url(JSON.stringify({
    ...payload,
    exp: Math.floor(Date.now() / 1000) + 3600,
    probe: 'a~',
  }))

  return `${header}.${body}.dummySignature`
}

const mockAdminAccessToken = createMockJwt({ global_role: 'Admin', unique_name: 'mock.admin' })
const mockTenantAccessToken = createMockJwt({ global_role: 'User', unique_name: 'tenant.user' })
const mockTenantSsoAccessToken = createMockJwt({ global_role: 'User', unique_name: 'tenant.sso.user' })

function parseJwtPayload(authorizationHeader: string | null) {
  if (!authorizationHeader?.startsWith('Bearer ')) {
    return null
  }

  const token = authorizationHeader.slice('Bearer '.length)
  const payloadJson = decodeBase64UrlSegment(token.split('.')[1] ?? '')
  if (!payloadJson) {
    return null
  }

  try {
    return JSON.parse(payloadJson) as {
      global_role?: string
      unique_name?: string
    }
  } catch {
    return null
  }
}

let jobTick = 0

let crawlConfigs = [
  {
    id: 'config-1',
    clientId: '1',
    organizationScopeId: 'scope-1',
    providerScopePath: 'https://dev.azure.com/meister-propr',
    providerProjectKey: 'Meister-ProPR',
    crawlIntervalSeconds: 60,
    isActive: true,
    repoFilters: [
      {
        id: 'filter-1',
        repositoryName: 'meister-propr',
        displayName: 'meister-propr',
        canonicalSourceRef: {
          provider: 'azureDevOps',
          value: 'repo-1',
        },
        targetBranchPatterns: ['main'],
      },
      {
        id: 'filter-2',
        repositoryName: 'propr-admin-ui',
        displayName: 'propr-admin-ui',
        canonicalSourceRef: {
          provider: 'azureDevOps',
          value: 'repo-2',
        },
        targetBranchPatterns: ['main', 'develop'],
      },
    ],
    proCursorSourceScopeMode: 'selectedSources',
    proCursorSourceIds: ['src-1', 'src-2'],
    invalidProCursorSourceIds: [],
    createdAt: '2024-03-27T10:00:00Z',
    updatedAt: '2024-03-27T10:00:00Z'
  },
  {
    id: 'config-2',
    clientId: '2',
    organizationScopeId: 'scope-3',
    providerScopePath: 'https://dev.azure.com/cloud-native',
    providerProjectKey: 'Infrastructure',
    crawlIntervalSeconds: 300,
    isActive: false,
    repoFilters: [],
    proCursorSourceScopeMode: 'allClientSources',
    proCursorSourceIds: [],
    invalidProCursorSourceIds: [],
    createdAt: '2024-03-27T11:00:00Z',
    updatedAt: '2024-03-27T11:30:00Z'
  },
  {
    id: 'config-3',
    clientId: '1',
    organizationScopeId: 'scope-1',
    providerScopePath: 'https://dev.azure.com/meister-propr',
    providerProjectKey: 'Sandbox',
    crawlIntervalSeconds: 120,
    isActive: true,
    repoFilters: [
      {
        id: 'filter-legacy-1',
        repositoryName: 'ai-dev-days-local-test',
        displayName: 'ai-dev-days-local-test',
        canonicalSourceRef: null,
        targetBranchPatterns: ['main'],
      },
      {
        id: 'filter-legacy-2',
        repositoryName: 'meister-propr',
        displayName: 'meister-propr',
        canonicalSourceRef: null,
        targetBranchPatterns: [],
      },
    ],
    proCursorSourceScopeMode: 'allClientSources',
    proCursorSourceIds: [],
    invalidProCursorSourceIds: ['src-stale-1'],
    createdAt: '2024-01-10T09:00:00Z',
    updatedAt: '2024-01-15T14:00:00Z'
  }
]

let webhookConfigs: any[] = [
  {
    id: 'webhook-config-1',
    clientId: '1',
    provider: 'azureDevOps',
    organizationScopeId: 'scope-1',
    providerScopePath: 'https://dev.azure.com/meister-propr',
    providerProjectKey: 'Meister-ProPR',
    isActive: true,
    enabledEvents: ['pullRequestCreated', 'pullRequestUpdated', 'pullRequestCommented'],
    repoFilters: [
      {
        id: 'webhook-filter-1',
        repositoryName: 'meister-propr',
        displayName: 'meister-propr',
        canonicalSourceRef: {
          provider: 'azureDevOps',
          value: 'repo-1',
        },
        targetBranchPatterns: ['main'],
      },
    ],
    listenerUrl: 'https://propr.example.com/webhooks/v1/providers/ado/mock-path-key-1',
    createdAt: '2024-03-27T10:15:00Z',
  },
]

const webhookDeliveryLogsByConfig: Record<string, any[]> = {
  'webhook-config-1': [
    {
      id: 'webhook-log-1',
      webhookConfigurationId: 'webhook-config-1',
      receivedAt: '2024-03-27T10:20:00Z',
      eventType: 'git.pullrequest.updated',
      deliveryOutcome: 'accepted',
      httpStatusCode: 200,
      repositoryId: 'repo-1',
      pullRequestId: 42,
      sourceBranch: 'refs/heads/feature/mock',
      targetBranch: 'refs/heads/main',
      actionSummaries: ['Submitted review intake refresh'],
      failureReason: null,
      failureCategory: null,
    },
  ],
}

const adoOrganizationScopesByClient: Record<string, any[]> = {
  '1': [
    {
      id: 'scope-1',
      clientId: '1',
      organizationUrl: 'https://dev.azure.com/meister-propr',
      displayName: 'Meister Org',
      isEnabled: true,
      verificationStatus: 'verified',
      createdAt: '2024-03-20T10:00:00Z',
      updatedAt: '2024-03-27T10:00:00Z',
    },
    {
      id: 'scope-2',
      clientId: '1',
      organizationUrl: 'https://dev.azure.com/meister-propr-legacy',
      displayName: 'Legacy Sandbox',
      isEnabled: false,
      verificationStatus: 'stale',
      createdAt: '2024-03-19T10:00:00Z',
      updatedAt: '2024-03-25T10:00:00Z',
    },
  ],
  '2': [
    {
      id: 'scope-3',
      clientId: '2',
      organizationUrl: 'https://dev.azure.com/cloud-native',
      displayName: 'Cloud Native',
      isEnabled: true,
      verificationStatus: 'verified',
      createdAt: '2024-03-20T10:00:00Z',
      updatedAt: '2024-03-27T10:00:00Z',
    },
  ],
}

const adoProjectsByScope: Record<string, any[]> = {
  'scope-1': [
    { organizationScopeId: 'scope-1', projectId: 'Meister-ProPR', projectName: 'Meister-ProPR' },
    { organizationScopeId: 'scope-1', projectId: 'Sandbox', projectName: 'Sandbox' },
  ],
  'scope-3': [
    { organizationScopeId: 'scope-3', projectId: 'Infrastructure', projectName: 'Infrastructure' },
  ],
}

const adoCrawlFiltersByProject: Record<string, any[]> = {
  'scope-1::Meister-ProPR': [
    {
      canonicalSourceRef: { provider: 'azureDevOps', value: 'repo-1' },
      displayName: 'meister-propr',
      branchSuggestions: [
        { branchName: 'main', isDefault: true },
        { branchName: 'release/*', isDefault: false },
      ],
    },
    {
      canonicalSourceRef: { provider: 'azureDevOps', value: 'repo-2' },
      displayName: 'propr-admin-ui',
      branchSuggestions: [
        { branchName: 'main', isDefault: true },
        { branchName: 'develop', isDefault: false },
      ],
    },
  ],
  'scope-1::Sandbox': [
    {
      canonicalSourceRef: { provider: 'azureDevOps', value: 'repo-3' },
      displayName: 'sandbox-service',
      branchSuggestions: [
        { branchName: 'main', isDefault: true },
      ],
    },
  ],
  'scope-3::Infrastructure': [
    {
      canonicalSourceRef: { provider: 'azureDevOps', value: 'repo-4' },
      displayName: 'terraform-live',
      branchSuggestions: [
        { branchName: 'main', isDefault: true },
      ],
    },
  ],
}

const adoSourcesByProject: Record<string, any[]> = {
  'scope-1::Meister-ProPR::repository': [
    { canonicalSourceRef: { provider: 'azureDevOps', value: 'repo-1' }, displayName: 'meister-propr' },
    { canonicalSourceRef: { provider: 'azureDevOps', value: 'repo-2' }, displayName: 'propr-admin-ui' },
  ],
  'scope-1::Meister-ProPR::adoWiki': [
    { canonicalSourceRef: { provider: 'azureDevOps', value: 'wiki-1' }, displayName: 'Meister-ProPR.wiki' },
  ],
  'scope-1::Sandbox::repository': [
    { canonicalSourceRef: { provider: 'azureDevOps', value: 'repo-3' }, displayName: 'sandbox-service' },
  ],
  'scope-1::Sandbox::adoWiki': [],
  'scope-3::Infrastructure::repository': [
    { canonicalSourceRef: { provider: 'azureDevOps', value: 'repo-4' }, displayName: 'terraform-live' },
  ],
  'scope-3::Infrastructure::adoWiki': [],
}

const adoBranchesBySource: Record<string, any[]> = {
  'repo-1': [
    { branchName: 'main', isDefault: true },
    { branchName: 'release/v2', isDefault: false },
    { branchName: 'develop', isDefault: false },
  ],
  'repo-2': [
    { branchName: 'main', isDefault: true },
    { branchName: 'develop', isDefault: false },
  ],
  'repo-3': [
    { branchName: 'main', isDefault: true },
  ],
  'repo-4': [
    { branchName: 'main', isDefault: true },
    { branchName: 'staging', isDefault: false },
  ],
  'wiki-1': [
    { branchName: 'wikiMaster', isDefault: true },
  ],
}

let proCursorSourcesByClient: Record<string, any[]> = {
  '1': [
    {
      sourceId: 'src-1',
      clientId: '1',
      organizationScopeId: 'scope-1',
      providerScopePath: 'https://dev.azure.com/meister-propr',
      providerProjectKey: 'Meister-ProPR',
      repositoryId: 'repo-1',
      sourceDisplayName: 'meister-propr',
      canonicalSourceRef: { provider: 'azureDevOps', value: 'repo-1' },
      displayName: 'Meister ProPR Docs',
      sourceKind: 'repository',
      defaultBranch: 'main',
      rootPath: '/docs',
      symbolMode: 'auto',
      isEnabled: true,
      status: 'ready',
      latestSnapshot: {
        branch: 'main',
        commitSha: 'a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2',
        freshnessStatus: 'fresh',
        supportsSymbolQueries: true,
        completedAt: new Date(Date.now() - 3600000 * 6).toISOString(),
      },
      createdAt: new Date(Date.now() - 86400000 * 14).toISOString(),
      updatedAt: new Date(Date.now() - 3600000 * 6).toISOString(),
    },
    {
      sourceId: 'src-2',
      clientId: '1',
      organizationScopeId: 'scope-1',
      providerScopePath: 'https://dev.azure.com/meister-propr',
      providerProjectKey: 'Meister-ProPR',
      repositoryId: 'wiki-1',
      sourceDisplayName: 'Meister-ProPR.wiki',
      canonicalSourceRef: { provider: 'azureDevOps', value: 'wiki-1' },
      displayName: 'Architecture Wiki',
      sourceKind: 'adoWiki',
      defaultBranch: 'wikiMaster',
      rootPath: null,
      symbolMode: 'text_only',
      isEnabled: true,
      status: 'ready',
      latestSnapshot: {
        branch: 'wikiMaster',
        commitSha: 'b2c3d4e5f6b2c3d4e5f6b2c3d4e5f6b2c3d4e5f6',
        freshnessStatus: 'stale',
        supportsSymbolQueries: false,
        completedAt: new Date(Date.now() - 86400000 * 3).toISOString(),
      },
      createdAt: new Date(Date.now() - 86400000 * 7).toISOString(),
      updatedAt: new Date(Date.now() - 86400000 * 3).toISOString(),
    },
    {
      sourceId: 'src-3',
      clientId: '1',
      organizationScopeId: 'scope-1',
      providerScopePath: 'https://dev.azure.com/meister-propr',
      providerProjectKey: 'Sandbox',
      repositoryId: 'repo-3',
      sourceDisplayName: 'sandbox-service',
      canonicalSourceRef: { provider: 'azureDevOps', value: 'repo-3' },
      displayName: 'Sandbox Service',
      sourceKind: 'repository',
      defaultBranch: 'main',
      rootPath: null,
      symbolMode: 'auto',
      isEnabled: false,
      status: 'disabled',
      latestSnapshot: null,
      createdAt: new Date(Date.now() - 86400000 * 30).toISOString(),
      updatedAt: new Date(Date.now() - 86400000 * 10).toISOString(),
    },
  ],
  '2': [],
}

  function hoursAgoIso(hours: number) {
    return new Date(Date.now() - hours * 60 * 60 * 1000).toISOString()
  }

  function daysAgoIso(days: number, hour = 8) {
    const date = new Date()
    date.setUTCDate(date.getUTCDate() - days)
    date.setUTCHours(hour, 0, 0, 0)
    return date.toISOString()
  }

  const proCursorTopSourcesByClient: Record<string, any[]> = {
    '1': [
      {
        rank: 1,
        sourceId: 'src-1',
        sourceDisplayName: 'Meister ProPR Docs',
        totalTokens: 10420,
        estimatedCostUsd: 0.84,
        estimatedEventCount: 2,
      },
      {
        rank: 2,
        sourceId: 'src-2',
        sourceDisplayName: 'Architecture Wiki',
        totalTokens: 4820,
        estimatedCostUsd: 0.31,
        estimatedEventCount: 1,
      },
    ],
    '2': [],
  }

  const proCursorClientUsageByClient: Record<string, any> = {
    '1': {
      clientId: '1',
      from: daysAgoIso(29),
      to: daysAgoIso(0),
      granularity: 'daily',
      groupBy: 'source',
      totals: {
        promptTokens: 11640,
        completionTokens: 3600,
        totalTokens: 15240,
        estimatedCostUsd: 1.15,
        eventCount: 41,
        estimatedEventCount: 5,
      },
      includesEstimatedUsage: true,
      includesGapFilledEvents: true,
      lastRollupCompletedAtUtc: hoursAgoIso(2),
      topSources: [],
      series: [
        {
          bucketStart: daysAgoIso(5),
          promptTokens: 1320,
          completionTokens: 360,
          totalTokens: 1680,
          estimatedCostUsd: 0.13,
          breakdown: [
            { sourceId: 'src-1', sourceDisplayName: 'Meister ProPR Docs', modelName: 'text-embedding-3-large', totalTokens: 980 },
            { sourceId: 'src-2', sourceDisplayName: 'Architecture Wiki', modelName: 'gpt-4o-mini', totalTokens: 700 },
          ],
        },
        {
          bucketStart: daysAgoIso(4),
          promptTokens: 1760,
          completionTokens: 520,
          totalTokens: 2280,
          estimatedCostUsd: 0.18,
          breakdown: [
            { sourceId: 'src-1', sourceDisplayName: 'Meister ProPR Docs', modelName: 'text-embedding-3-large', totalTokens: 1460 },
            { sourceId: 'src-2', sourceDisplayName: 'Architecture Wiki', modelName: 'gpt-4o-mini', totalTokens: 820 },
          ],
        },
        {
          bucketStart: daysAgoIso(3),
          promptTokens: 2050,
          completionTokens: 710,
          totalTokens: 2760,
          estimatedCostUsd: 0.21,
          breakdown: [
            { sourceId: 'src-1', sourceDisplayName: 'Meister ProPR Docs', modelName: 'text-embedding-3-large', totalTokens: 1880 },
            { sourceId: 'src-2', sourceDisplayName: 'Architecture Wiki', modelName: 'gpt-4o-mini', totalTokens: 880 },
          ],
        },
        {
          bucketStart: daysAgoIso(2),
          promptTokens: 2410,
          completionTokens: 760,
          totalTokens: 3170,
          estimatedCostUsd: 0.24,
          breakdown: [
            { sourceId: 'src-1', sourceDisplayName: 'Meister ProPR Docs', modelName: 'text-embedding-3-large', totalTokens: 2190 },
            { sourceId: 'src-2', sourceDisplayName: 'Architecture Wiki', modelName: 'gpt-4o-mini', totalTokens: 980 },
          ],
        },
        {
          bucketStart: daysAgoIso(1),
          promptTokens: 2360,
          completionTokens: 740,
          totalTokens: 3100,
          estimatedCostUsd: 0.23,
          breakdown: [
            { sourceId: 'src-1', sourceDisplayName: 'Meister ProPR Docs', modelName: 'text-embedding-3-large', totalTokens: 2450 },
            { sourceId: 'src-2', sourceDisplayName: 'Architecture Wiki', modelName: 'gpt-4o-mini', totalTokens: 650 },
          ],
        },
        {
          bucketStart: daysAgoIso(0),
          promptTokens: 1740,
          completionTokens: 510,
          totalTokens: 2250,
          estimatedCostUsd: 0.16,
          breakdown: [
            { sourceId: 'src-1', sourceDisplayName: 'Meister ProPR Docs', modelName: 'gpt-4o-mini', totalTokens: 1460 },
            { sourceId: 'src-2', sourceDisplayName: 'Architecture Wiki', modelName: 'text-embedding-3-large', totalTokens: 790 },
          ],
        },
      ],
    },
    '2': {
      clientId: '2',
      from: daysAgoIso(29),
      to: daysAgoIso(0),
      granularity: 'daily',
      groupBy: 'source',
      totals: {
        promptTokens: 0,
        completionTokens: 0,
        totalTokens: 0,
        estimatedCostUsd: 0,
        eventCount: 0,
        estimatedEventCount: 0,
      },
      includesEstimatedUsage: false,
      includesGapFilledEvents: false,
      lastRollupCompletedAtUtc: null,
      topSources: [],
      series: [],
    },
  }

  const proCursorSourceUsageBySource: Record<string, any> = {
    'src-1': {
      sourceId: 'src-1',
      period: '30d',
      totals: {
        promptTokens: 7820,
        completionTokens: 2600,
        totalTokens: 10420,
        estimatedCostUsd: 0.84,
        eventCount: 28,
        estimatedEventCount: 2,
      },
      includesEstimatedUsage: true,
      includesGapFilledEvents: true,
      lastRollupCompletedAtUtc: hoursAgoIso(2),
      byModel: [
        { modelName: 'text-embedding-3-large', totalTokens: 6120, estimatedCostUsd: 0.34, eventCount: 17 },
        { modelName: 'gpt-4o-mini', totalTokens: 4300, estimatedCostUsd: 0.5, eventCount: 11 },
      ],
      series: [
        { bucketStart: daysAgoIso(5), promptTokens: 880, completionTokens: 280, totalTokens: 1160, estimatedCostUsd: 0.08 },
        { bucketStart: daysAgoIso(4), promptTokens: 1260, completionTokens: 390, totalTokens: 1650, estimatedCostUsd: 0.12 },
        { bucketStart: daysAgoIso(3), promptTokens: 1520, completionTokens: 520, totalTokens: 2040, estimatedCostUsd: 0.16 },
        { bucketStart: daysAgoIso(2), promptTokens: 1700, completionTokens: 610, totalTokens: 2310, estimatedCostUsd: 0.18 },
        { bucketStart: daysAgoIso(1), promptTokens: 1580, completionTokens: 510, totalTokens: 2090, estimatedCostUsd: 0.17 },
        { bucketStart: daysAgoIso(0), promptTokens: 880, completionTokens: 290, totalTokens: 1170, estimatedCostUsd: 0.13 },
      ],
    },
    'src-2': {
      sourceId: 'src-2',
      period: '30d',
      totals: {
        promptTokens: 3820,
        completionTokens: 1000,
        totalTokens: 4820,
        estimatedCostUsd: 0.31,
        eventCount: 13,
        estimatedEventCount: 1,
      },
      includesEstimatedUsage: true,
      includesGapFilledEvents: false,
      lastRollupCompletedAtUtc: hoursAgoIso(5),
      byModel: [
        { modelName: 'text-embedding-3-large', totalTokens: 2780, estimatedCostUsd: 0.14, eventCount: 8 },
        { modelName: 'gpt-4o-mini', totalTokens: 2040, estimatedCostUsd: 0.17, eventCount: 5 },
      ],
      series: [
        { bucketStart: daysAgoIso(5), promptTokens: 440, completionTokens: 140, totalTokens: 580, estimatedCostUsd: 0.04 },
        { bucketStart: daysAgoIso(4), promptTokens: 500, completionTokens: 170, totalTokens: 670, estimatedCostUsd: 0.05 },
        { bucketStart: daysAgoIso(3), promptTokens: 530, completionTokens: 190, totalTokens: 720, estimatedCostUsd: 0.05 },
        { bucketStart: daysAgoIso(2), promptTokens: 710, completionTokens: 150, totalTokens: 860, estimatedCostUsd: 0.06 },
        { bucketStart: daysAgoIso(1), promptTokens: 840, completionTokens: 110, totalTokens: 950, estimatedCostUsd: 0.06 },
        { bucketStart: daysAgoIso(0), promptTokens: 800, completionTokens: 240, totalTokens: 1040, estimatedCostUsd: 0.05 },
      ],
    },
    'src-3': {
      sourceId: 'src-3',
      period: '30d',
      totals: {
        promptTokens: 0,
        completionTokens: 0,
        totalTokens: 0,
        estimatedCostUsd: 0,
        eventCount: 0,
        estimatedEventCount: 0,
      },
      includesEstimatedUsage: false,
      includesGapFilledEvents: false,
      lastRollupCompletedAtUtc: null,
      byModel: [],
      series: [],
    },
  }

  const proCursorRecentEventsBySource: Record<string, any[]> = {
    'src-1': [
      {
        occurredAtUtc: hoursAgoIso(1),
        callType: 'semantic_search',
        modelName: 'gpt-4o-mini',
        deploymentName: 'knowledge-reasoning',
        totalTokens: 420,
        promptTokens: 320,
        completionTokens: 100,
        estimatedCostUsd: 0.05,
        sourcePath: '/docs/architecture/review-memory.md',
        resourceId: 'docs-review-memory',
        requestId: 'req-pc-1a2b3c4d5e6f',
        tokensEstimated: false,
        costEstimated: false,
      },
      {
        occurredAtUtc: hoursAgoIso(3),
        callType: 'embedding_index',
        modelName: 'text-embedding-3-large',
        deploymentName: 'knowledge-embeddings',
        totalTokens: 1180,
        promptTokens: 1180,
        completionTokens: 0,
        estimatedCostUsd: 0.06,
        sourcePath: '/docs/admin/token-governance.md',
        resourceId: 'docs-token-governance',
        requestId: 'req-pc-7f8e9d0c1b2a',
        tokensEstimated: false,
        costEstimated: false,
      },
      {
        occurredAtUtc: hoursAgoIso(6),
        callType: 'symbol_lookup',
        modelName: 'gpt-4o-mini',
        deploymentName: 'knowledge-reasoning',
        totalTokens: 290,
        promptTokens: 210,
        completionTokens: 80,
        estimatedCostUsd: 0.03,
        sourcePath: '/docs/runtime/procursor-sources.md',
        resourceId: 'docs-procursor-sources',
        requestId: 'req-pc-9a8b7c6d5e4f',
        tokensEstimated: true,
        costEstimated: true,
      },
      {
        occurredAtUtc: hoursAgoIso(10),
        callType: 'semantic_search',
        modelName: 'gpt-4o-mini',
        deploymentName: 'knowledge-reasoning',
        totalTokens: 360,
        promptTokens: 260,
        completionTokens: 100,
        estimatedCostUsd: 0.04,
        sourcePath: '/docs/runbooks/source-refresh.md',
        resourceId: 'docs-source-refresh',
        requestId: 'req-pc-112233445566',
        tokensEstimated: false,
        costEstimated: false,
      },
      {
        occurredAtUtc: hoursAgoIso(15),
        callType: 'embedding_index',
        modelName: 'text-embedding-3-large',
        deploymentName: 'knowledge-embeddings',
        totalTokens: 940,
        promptTokens: 940,
        completionTokens: 0,
        estimatedCostUsd: 0.04,
        sourcePath: '/docs/security/secret-storage.md',
        resourceId: 'docs-secret-storage',
        requestId: 'req-pc-abcdef123456',
        tokensEstimated: false,
        costEstimated: false,
      },
    ],
    'src-2': [
      {
        occurredAtUtc: hoursAgoIso(8),
        callType: 'semantic_search',
        modelName: 'gpt-4o-mini',
        deploymentName: 'knowledge-reasoning',
        totalTokens: 240,
        promptTokens: 180,
        completionTokens: 60,
        estimatedCostUsd: 0.02,
        sourcePath: '/wiki/architecture/review-pipeline',
        resourceId: 'wiki-review-pipeline',
        requestId: 'req-pc-fedcba654321',
        tokensEstimated: false,
        costEstimated: false,
      },
      {
        occurredAtUtc: hoursAgoIso(20),
        callType: 'embedding_index',
        modelName: 'text-embedding-3-large',
        deploymentName: 'knowledge-embeddings',
        totalTokens: 720,
        promptTokens: 720,
        completionTokens: 0,
        estimatedCostUsd: 0.03,
        sourcePath: '/wiki/admin/protocol-audit',
        resourceId: 'wiki-protocol-audit',
        requestId: 'req-pc-334455667788',
        tokensEstimated: true,
        costEstimated: true,
      },
    ],
    'src-3': [],
  }

function getScope(clientId: string, scopeId: string | null | undefined) {
  if (!scopeId) {
    return null
  }

  return (adoOrganizationScopesByClient[clientId] ?? []).find((scope) => scope.id === scopeId) ?? null
}

function getCrawlFilters(scopeId: string | null | undefined, projectId: string | null | undefined) {
  if (!scopeId || !projectId) {
    return []
  }

  return adoCrawlFiltersByProject[`${scopeId}::${projectId}`] ?? []
}

function getProviderConnection(clientId: string, connectionId: string | null | undefined) {
  if (!connectionId) {
    return null
  }

  return (providerConnectionsByClient[clientId] ?? [])
    .filter((connection) => isProviderEnabled(connection.providerFamily))
    .find((connection) => connection.id === connectionId) ?? null
}

function isProviderEnabled(providerFamily: string | null | undefined) {
  return providerActivationStatuses.find((status) => status.providerFamily === providerFamily)?.isEnabled ?? true
}

function buildProviderAuditTrail(clientId: string) {
  return (providerConnectionsByClient[clientId] ?? [])
    .filter((connection) => isProviderEnabled(connection.providerFamily))
    .flatMap((connection) => {
      const entries = [
        {
          id: `${connection.id}:created`,
          clientId,
          connectionId: connection.id,
          providerFamily: connection.providerFamily,
          displayName: connection.displayName,
          hostBaseUrl: connection.hostBaseUrl,
          eventType: 'connectionCreated',
          summary: `Connection created for ${connection.displayName}.`,
          occurredAt: connection.createdAt,
          status: 'info',
          failureCategory: null,
          detail: null,
        },
      ]

      if (connection.updatedAt && connection.updatedAt !== connection.createdAt) {
        entries.push({
          id: `${connection.id}:updated`,
          clientId,
          connectionId: connection.id,
          providerFamily: connection.providerFamily,
          displayName: connection.displayName,
          hostBaseUrl: connection.hostBaseUrl,
          eventType: connection.isActive ? 'connectionUpdated' : 'connectionDisabled',
          summary: connection.isActive
            ? `Connection updated for ${connection.displayName}.`
            : `Connection disabled for ${connection.displayName}.`,
          occurredAt: connection.updatedAt,
          status: connection.isActive ? 'info' : 'warning',
          failureCategory: null,
          detail: null,
        })
      }

      if (connection.lastVerifiedAt) {
        const isFailed = connection.verificationStatus?.toLowerCase() === 'failed'
        entries.push({
          id: `${connection.id}:verified`,
          clientId,
          connectionId: connection.id,
          providerFamily: connection.providerFamily,
          displayName: connection.displayName,
          hostBaseUrl: connection.hostBaseUrl,
          eventType: isFailed ? 'connectionVerificationFailed' : 'connectionVerified',
          summary: isFailed
            ? `Verification failed for ${connection.displayName}.`
            : `Connection verified for ${connection.displayName}.`,
          occurredAt: connection.lastVerifiedAt,
          status: isFailed ? 'error' : 'success',
          failureCategory: connection.lastVerificationFailureCategory ?? null,
          detail: connection.lastVerificationError ?? null,
        })
      }

      return entries
    })
    .sort((left, right) => Date.parse(right.occurredAt) - Date.parse(left.occurredAt))
}

let dismissedFindings = [
  {
    id: 'd1',
    clientId: '1',
    patternText: 'postgres uses hardcoded credentials postgrespassword devpass ensure this compose file is strictly for development/test',
    label: 'False positive: dev credentials',
    createdAt: new Date(Date.now() - 86400000).toISOString()
  },
  {
    id: 'd2',
    clientId: '1',
    patternText: 'Potential use of insecure industrial protocol (Modbus/TCP) without TLS encryption layer in the communication stack.',
    label: 'Intentional: Legacy support',
    createdAt: new Date(Date.now() - 172800000).toISOString()
  }
]

let promptOverrides = [
  {
    id: 'o1',
    clientId: '1',
    scope: 'clientScope',
    promptKey: 'SystemPrompt',
    overrideText: 'You are an expert code reviewer specialising in .NET/C# and general cloud-native architecture. Prioritize security and naming consistency.',
    createdAt: new Date(Date.now() - 86400000 * 2).toISOString(),
    updatedAt: new Date(Date.now() - 86400000 * 2).toISOString()
  },
  {
    id: 'o2',
    clientId: '1',
    scope: 'clientScope',
    promptKey: 'AgenticLoopGuidance',
    overrideText: 'When reviewing Bicep files, always check for resource naming best practices and ensure identity-based access is used over connection strings.',
    createdAt: new Date(Date.now() - 86400000 * 3).toISOString(),
    updatedAt: new Date(Date.now() - 86400000 * 3).toISOString()
  }
]

let aiConnectionsByClient: Record<string, any[]> = {
  '1': [
    {
      id: 'ai-1',
      clientId: '1',
      displayName: 'Azure OpenAI Prod',
      endpointUrl: 'https://acme-prod.openai.azure.com/',
      models: ['gpt-4o', 'gpt-4o-mini'],
      isActive: true,
      activeModel: 'gpt-4o',
      modelCategory: null,
      createdAt: new Date(Date.now() - 86400000 * 7).toISOString(),
      updatedAt: new Date(Date.now() - 3600000).toISOString(),
    },
    {
      id: 'ai-2',
      clientId: '1',
      displayName: 'Embedding Pool',
      endpointUrl: 'https://acme-embeddings.openai.azure.com/',
      models: ['text-embedding-3-large'],
      isActive: false,
      activeModel: null,
      modelCategory: 'embedding',
      modelCapabilities: [
        {
          modelName: 'text-embedding-3-large',
          tokenizerName: 'cl100k_base',
          maxInputTokens: 8192,
          embeddingDimensions: 3072,
        },
      ],
      createdAt: new Date(Date.now() - 86400000 * 4).toISOString(),
      updatedAt: new Date(Date.now() - 86400000).toISOString(),
    },
  ],
}

let providerActivationStatuses = [
  {
    providerFamily: 'azureDevOps',
    isEnabled: true,
    baselineAdapterSetRegistered: true,
    registeredCapabilities: ['repositoryDiscovery'],
    supportClaimReadiness: 'workflowComplete',
    supportClaimReason: 'Azure DevOps is fully supported.',
    updatedAt: new Date(Date.now() - 2 * 3600000).toISOString(),
  },
  {
    providerFamily: 'github',
    isEnabled: false,
    baselineAdapterSetRegistered: true,
    registeredCapabilities: ['repositoryDiscovery'],
    supportClaimReadiness: 'onboardingReady',
    supportClaimReason: 'GitHub remains onboarding ready when enabled.',
    updatedAt: new Date(Date.now() - 2 * 3600000).toISOString(),
  },
  {
    providerFamily: 'gitLab',
    isEnabled: true,
    baselineAdapterSetRegistered: true,
    registeredCapabilities: ['repositoryDiscovery'],
    supportClaimReadiness: 'onboardingReady',
    supportClaimReason: 'GitLab remains onboarding ready when enabled.',
    updatedAt: new Date(Date.now() - 2 * 3600000).toISOString(),
  },
  {
    providerFamily: 'forgejo',
    isEnabled: false,
    baselineAdapterSetRegistered: true,
    registeredCapabilities: ['repositoryDiscovery'],
    supportClaimReadiness: 'onboardingReady',
    supportClaimReason: 'Forgejo remains onboarding ready when enabled.',
    updatedAt: new Date(Date.now() - 2 * 3600000).toISOString(),
  },
]

let providerConnectionsByClient: Record<string, any[]> = {
  '1': [
    {
      id: 'provider-conn-ado-1',
      clientId: '1',
      providerFamily: 'azureDevOps',
      hostBaseUrl: 'https://dev.azure.com',
      authenticationKind: 'oauthClientCredentials',
      displayName: 'Meister Azure DevOps',
      isActive: true,
      verificationStatus: 'verified',
      lastVerifiedAt: new Date(Date.now() - 4 * 3600000).toISOString(),
      lastVerificationError: null,
      lastVerificationFailureCategory: null,
      createdAt: new Date(Date.now() - 86400000 * 7).toISOString(),
      updatedAt: new Date(Date.now() - 4 * 3600000).toISOString(),
    },
    {
      id: 'provider-conn-github-1',
      clientId: '1',
      providerFamily: 'github',
      hostBaseUrl: 'https://github.com',
      authenticationKind: 'personalAccessToken',
      displayName: 'Acme GitHub',
      isActive: true,
      verificationStatus: 'verified',
      lastVerifiedAt: new Date(Date.now() - 2 * 3600000).toISOString(),
      lastVerificationError: null,
      lastVerificationFailureCategory: null,
      createdAt: new Date(Date.now() - 86400000 * 3).toISOString(),
      updatedAt: new Date(Date.now() - 2 * 3600000).toISOString(),
    },
  ],
  '2': [
    {
      id: 'provider-conn-gitlab-1',
      clientId: '2',
      providerFamily: 'gitLab',
      hostBaseUrl: 'https://gitlab.example.com',
      authenticationKind: 'personalAccessToken',
      displayName: 'Platform GitLab',
      isActive: false,
      verificationStatus: 'stale',
      lastVerifiedAt: new Date(Date.now() - 36 * 3600000).toISOString(),
      lastVerificationError: 'Token missing read_api scope.',
      lastVerificationFailureCategory: 'authentication',
      createdAt: new Date(Date.now() - 86400000 * 10).toISOString(),
      updatedAt: new Date(Date.now() - 36 * 3600000).toISOString(),
    },
  ],
}

let providerScopesByConnection: Record<string, any[]> = {
  'provider-conn-ado-1': [
    {
      id: 'provider-scope-ado-1',
      clientId: '1',
      connectionId: 'provider-conn-ado-1',
      scopeType: 'organization',
      externalScopeId: 'meister-propr',
      scopePath: 'meister-propr',
      displayName: 'Meister Org',
      verificationStatus: 'verified',
      isEnabled: true,
      lastVerifiedAt: new Date(Date.now() - 4 * 3600000).toISOString(),
      lastVerificationError: null,
      createdAt: new Date(Date.now() - 86400000 * 7).toISOString(),
      updatedAt: new Date(Date.now() - 4 * 3600000).toISOString(),
    },
  ],
  'provider-conn-github-1': [
    {
      id: 'provider-scope-github-1',
      clientId: '1',
      connectionId: 'provider-conn-github-1',
      scopeType: 'organization',
      externalScopeId: 'acme',
      scopePath: 'acme',
      displayName: 'Acme',
      verificationStatus: 'verified',
      isEnabled: true,
      lastVerifiedAt: new Date(Date.now() - 2 * 3600000).toISOString(),
      lastVerificationError: null,
      createdAt: new Date(Date.now() - 86400000 * 3).toISOString(),
      updatedAt: new Date(Date.now() - 2 * 3600000).toISOString(),
    },
  ],
  'provider-conn-gitlab-1': [
    {
      id: 'provider-scope-gitlab-1',
      clientId: '2',
      connectionId: 'provider-conn-gitlab-1',
      scopeType: 'group',
      externalScopeId: 'acme/platform',
      scopePath: 'acme/platform',
      displayName: 'acme/platform',
      verificationStatus: 'stale',
      isEnabled: false,
      lastVerifiedAt: new Date(Date.now() - 36 * 3600000).toISOString(),
      lastVerificationError: 'The scope is no longer reachable with the stored token.',
      createdAt: new Date(Date.now() - 86400000 * 10).toISOString(),
      updatedAt: new Date(Date.now() - 36 * 3600000).toISOString(),
    },
  ],
}

let providerReviewerIdentitiesByConnection: Record<string, any | null> = {
  'provider-conn-ado-1': {
    id: 'provider-reviewer-ado-1',
    clientId: '1',
    connectionId: 'provider-conn-ado-1',
    providerFamily: 'azureDevOps',
    externalUserId: 'ado-reviewer-1',
    login: 'meister-bot',
    displayName: 'Meister Bot',
    isBot: true,
    updatedAt: new Date(Date.now() - 4 * 3600000).toISOString(),
  },
  'provider-conn-github-1': {
    id: 'provider-reviewer-github-1',
    clientId: '1',
    connectionId: 'provider-conn-github-1',
    providerFamily: 'github',
    externalUserId: 'github-reviewer-1',
    login: 'meister-dev-bot',
    displayName: 'Meister Dev Bot',
    isBot: true,
    updatedAt: new Date(Date.now() - 2 * 3600000).toISOString(),
  },
  'provider-conn-gitlab-1': null,
}

const providerReviewerIdentityCandidatesByConnection: Record<string, any[]> = {
  'provider-conn-ado-1': [
    {
      id: 'provider-reviewer-ado-1',
      clientId: '1',
      connectionId: 'provider-conn-ado-1',
      providerFamily: 'azureDevOps',
      externalUserId: 'ado-reviewer-1',
      login: 'meister-bot',
      displayName: 'Meister Bot',
      isBot: true,
      updatedAt: new Date(Date.now() - 4 * 3600000).toISOString(),
    },
  ],
  'provider-conn-github-1': [
    {
      id: 'provider-reviewer-github-1',
      clientId: '1',
      connectionId: 'provider-conn-github-1',
      providerFamily: 'github',
      externalUserId: 'github-reviewer-1',
      login: 'meister-dev-bot',
      displayName: 'Meister Dev Bot',
      isBot: true,
      updatedAt: new Date(Date.now() - 2 * 3600000).toISOString(),
    },
    {
      id: 'provider-reviewer-github-2',
      clientId: '1',
      connectionId: 'provider-conn-github-1',
      providerFamily: 'github',
      externalUserId: 'github-reviewer-2',
      login: 'meister-maintainer',
      displayName: 'Meister Maintainer',
      isBot: false,
      updatedAt: new Date(Date.now() - 6 * 3600000).toISOString(),
    },
  ],
  'provider-conn-gitlab-1': [
    {
      id: 'provider-reviewer-gitlab-1',
      clientId: '2',
      connectionId: 'provider-conn-gitlab-1',
      providerFamily: 'gitLab',
      externalUserId: 'gitlab-reviewer-1',
      login: 'meister-reviewer',
      displayName: 'Meister Reviewer',
      isBot: true,
      updatedAt: new Date(Date.now() - 36 * 3600000).toISOString(),
    },
  ],
}

let threadMemoryRecords = [
  {
    id: 'tm-1',
    clientId: '1',
    threadId: 1024,
    repositoryId: 'meister-propr',
    pullRequestId: 450,
    filePath: 'src/MeisterProPR.Api/Features/Reviewing/Diagnostics/Controllers/JobsController.cs',
    resolutionSummary: 'The user requested to add a new endpoint for fetching job protocols. The developer implemented it by adding the `GetProtocol` method to the `JobsController`.',
    createdAt: new Date(Date.now() - 86400000 * 5).toISOString(),
    updatedAt: new Date(Date.now() - 86400000 * 5).toISOString()
  },
  {
    id: 'tm-2',
    clientId: '1',
    threadId: 1025,
    repositoryId: 'meister-propr',
    pullRequestId: 450,
    filePath: 'src/MeisterProPR.Core/Services/JobService.cs',
    resolutionSummary: 'Fixed a race condition in the job status update logic by implementing a distributed lock using Redis.',
    createdAt: new Date(Date.now() - 86400000 * 4).toISOString(),
    updatedAt: new Date(Date.now() - 86400000 * 4).toISOString()
  },
  {
    id: 'tm-3',
    clientId: '2',
    threadId: 501,
    repositoryId: 'infrastructure',
    pullRequestId: 12,
    filePath: 'terraform/main.tf',
    resolutionSummary: 'Updated the CIDR block for the production VNET to avoid overlap with the management network.',
    createdAt: new Date(Date.now() - 86400000 * 10).toISOString(),
    updatedAt: new Date(Date.now() - 86400000 * 10).toISOString()
  }
]

let memoryActivityLog = [
  {
    id: 'log-1',
    clientId: '1',
    threadId: 1024,
    repositoryId: 'meister-propr',
    pullRequestId: 450,
    action: 0,
    previousStatus: null,
    currentStatus: 'resolved',
    reason: 'Thread resolution summary generated and stored.',
    occurredAt: new Date(Date.now() - 86400000 * 5).toISOString()
  },
  {
    id: 'log-2',
    clientId: '1',
    threadId: 1025,
    repositoryId: 'meister-propr',
    pullRequestId: 450,
    action: 0,
    previousStatus: 'active',
    currentStatus: 'resolved',
    reason: 'Thread resolved by developer, summary updated.',
    occurredAt: new Date(Date.now() - 86400000 * 4).toISOString()
  },
  {
    id: 'log-3',
    clientId: '1',
    threadId: 1026,
    repositoryId: 'meister-propr',
    pullRequestId: 451,
    action: 2,
    previousStatus: 'active',
    currentStatus: 'active',
    reason: 'Thread still active, no summary generated.',
    occurredAt: new Date(Date.now() - 86400000 * 2).toISOString()
  }
]

export const handlers = [
  http.get(`${base}/auth/options`, async () => {
    return HttpResponse.json({
      edition: mockEdition,
      availableSignInMethods: mockSsoCapabilityAvailable ? ['password', 'sso'] : ['password'],
      capabilities: [getMockSsoCapability()],
    })
  }),

  http.patch(`${base}/admin/licensing/mock`, async ({ request }) => {
    const body = await request.json() as {
      edition?: string
      ssoAvailable?: boolean
    }

    if (body.edition === 'community' || body.edition === 'commercial') {
      mockEdition = body.edition
    }

    if (typeof body.ssoAvailable === 'boolean') {
      mockSsoCapabilityAvailable = body.ssoAvailable
    }

    persistMockLicensingState()

    return HttpResponse.json({
      edition: mockEdition,
      capabilities: [getMockSsoCapability()],
    })
  }),

  http.post(`${base}/auth/login`, async () => {
    await delay(500)
    // The dummy token contains a base64url payload with both role and username claims.
    return HttpResponse.json({
      accessToken: mockAdminAccessToken,
      refreshToken: 'mock-refresh'
    })
  }),

  http.post(`${base}/auth/refresh`, async () => {
    return HttpResponse.json({ accessToken: mockAdminAccessToken })
  }),

  http.get(`${base}/auth/me`, async ({ request }) => {
    const payload = parseJwtPayload(request.headers.get('Authorization'))
    const isAdmin = payload?.global_role === 'Admin'
    const username = payload?.unique_name ?? ''

    return HttpResponse.json({
      globalRole: isAdmin ? 'Admin' : 'User',
      clientRoles: isAdmin ? { '1': 1, '2': 1 } : {},
      tenantRoles: isAdmin ? { 'tenant-1': 1 } : { 'tenant-1': 0 },
      hasLocalPassword: isAdmin || !username.includes('sso'),
      edition: mockEdition,
      capabilities: [getMockSsoCapability()],
    })
  }),

  http.get(`${base}/auth/tenants/:tenantSlug/providers`, async ({ params }) => {
    await delay(180)
    const tenantSlug = String(params.tenantSlug)
    const tenant = getMockTenantBySlug(tenantSlug)
    if (!tenant || tenant.isActive === false) {
      return HttpResponse.json({ error: 'Tenant sign-in is not available.' }, { status: 404 })
    }

    const providers = mockSsoCapabilityAvailable
      ? (mockTenantSsoProviders[tenant.id] ?? [])
        .filter((provider) => provider.isEnabled)
        .map((provider) => ({
          providerId: provider.id,
          displayName: provider.displayName,
          providerKind: provider.providerKind,
        }))
      : []

    return HttpResponse.json({
      tenantSlug: tenant.slug,
      localLoginEnabled: tenant.localLoginEnabled,
      providers,
    })
  }),

  http.post(`${base}/auth/tenants/:tenantSlug/local-login`, async ({ params }) => {
    await delay(220)
    const tenantSlug = String(params.tenantSlug)
    const tenant = getMockTenantBySlug(tenantSlug)
    if (!tenant || !tenant.localLoginEnabled) {
      return HttpResponse.json({ error: 'Local sign-in is disabled for this tenant.' }, { status: 401 })
    }

    return HttpResponse.json({
      accessToken: mockTenantAccessToken,
      refreshToken: 'tenant-refresh-token',
      expiresIn: 900,
      tokenType: 'Bearer',
    })
  }),

  http.get(`${base}/auth/external/challenge/:tenantSlug/:providerId`, async ({ params, request }) => {
    await delay(160)

    if (!mockSsoCapabilityAvailable) {
      return createPremiumFeatureUnavailableResponse()
    }

    const tenantSlug = String(params.tenantSlug)
    const providerId = String(params.providerId)
    const tenant = getMockTenantBySlug(tenantSlug)
    const provider = tenant ? (mockTenantSsoProviders[tenant.id] ?? []).find((candidate) => candidate.id === providerId && candidate.isEnabled) : null

    if (!tenant || !provider) {
      return HttpResponse.json({ error: 'Provider not found.' }, { status: 404 })
    }

    const returnUrl = new URL(request.url).searchParams.get('returnUrl')
    if (returnUrl) {
      return HttpResponse.redirect(`${returnUrl}#accessToken=${mockTenantSsoAccessToken}&refreshToken=tenant-sso-refresh-token&expiresIn=900&tokenType=Bearer`)
    }

    return HttpResponse.redirect(`${base}/auth/external/callback/${tenant.slug}`)
  }),

  http.get(`${base}/auth/external/callback/:tenantSlug`, async ({ params }) => {
    await delay(180)

    if (!mockSsoCapabilityAvailable) {
      return createPremiumFeatureUnavailableResponse()
    }

    const tenantSlug = String(params.tenantSlug)
    const tenant = getMockTenantBySlug(tenantSlug)
    const provider = tenant ? (mockTenantSsoProviders[tenant.id] ?? []).find((candidate) => candidate.isEnabled) : null

    if (!tenant || !provider) {
      return HttpResponse.json({ error: 'Provider not found.' }, { status: 404 })
    }

    return HttpResponse.json({
      accessToken: mockTenantSsoAccessToken,
      refreshToken: 'tenant-sso-refresh-token',
      expiresIn: 900,
      tokenType: 'Bearer',
    })
  }),

  http.get(`${base}/api/admin/tenants/:tenantId`, async ({ params }) => {
    await delay(180)
    const tenantId = String(params.tenantId)
    const tenant = getMockTenantById(tenantId)

    return tenant
      ? HttpResponse.json(tenant)
      : new HttpResponse(null, { status: 404 })
  }),

  http.patch(`${base}/api/admin/tenants/:tenantId`, async ({ params, request }) => {
    await delay(220)
    const tenantId = String(params.tenantId)
    const tenant = getMockTenantById(tenantId)
    if (!tenant) {
      return new HttpResponse(null, { status: 404 })
    }

    const body = await request.json() as any
    const updatedTenant = {
      ...tenant,
      displayName: body.displayName ?? tenant.displayName,
      isActive: body.isActive ?? tenant.isActive,
      localLoginEnabled: body.localLoginEnabled ?? tenant.localLoginEnabled,
      updatedAt: new Date().toISOString(),
    }

    mockTenants = mockTenants.map((candidate) => candidate.id === tenantId ? updatedTenant : candidate)
    return HttpResponse.json(updatedTenant)
  }),

  http.get(`${base}/api/admin/tenants`, async () => {
    await delay(180)
    return HttpResponse.json(mockTenants)
  }),

  http.post(`${base}/api/admin/tenants`, async ({ request }) => {
    await delay(220)
    const body = await request.json() as any
    const created = {
      id: `tenant-${Math.random().toString(36).slice(2, 10)}`,
      slug: body.slug ?? 'new-tenant',
      displayName: body.displayName ?? 'New Tenant',
      isActive: true,
      localLoginEnabled: true,
      createdAt: new Date().toISOString(),
      updatedAt: new Date().toISOString(),
    }

    mockTenants = [...mockTenants, created]
    mockTenantSsoProviders[created.id] = []
    return HttpResponse.json(created, { status: 201 })
  }),

  http.get(`${base}/api/admin/tenants/:tenantId/sso-providers`, async ({ params }) => {
    await delay(180)
    const tenantId = String(params.tenantId)
    const tenant = getMockTenantById(tenantId)
    if (!tenant) {
      return new HttpResponse(null, { status: 404 })
    }

    if (!mockSsoCapabilityAvailable) {
      return createPremiumFeatureUnavailableResponse()
    }

    return HttpResponse.json(mockTenantSsoProviders[tenantId] ?? [])
  }),

  http.post(`${base}/api/admin/tenants/:tenantId/sso-providers`, async ({ params, request }) => {
    await delay(240)
    const tenantId = String(params.tenantId)
    const tenant = getMockTenantById(tenantId)
    if (!tenant) {
      return new HttpResponse(null, { status: 404 })
    }

    if (!mockSsoCapabilityAvailable) {
      return createPremiumFeatureUnavailableResponse()
    }

    const body = await request.json() as any
    const created = {
      id: `provider-${Math.random().toString(36).slice(2, 10)}`,
      tenantId,
      displayName: body.displayName ?? 'New provider',
      providerKind: body.providerKind ?? 'EntraId',
      protocolKind: body.protocolKind ?? 'Oidc',
      issuerOrAuthorityUrl: body.issuerOrAuthorityUrl ?? null,
      clientId: body.clientId ?? 'generated-client-id',
      secretConfigured: Boolean(body.clientSecret),
      scopes: body.scopes ?? [],
      allowedEmailDomains: body.allowedEmailDomains ?? [],
      isEnabled: body.isEnabled ?? true,
      autoCreateUsers: body.autoCreateUsers ?? true,
      createdAt: new Date().toISOString(),
      updatedAt: new Date().toISOString(),
    }

    mockTenantSsoProviders[tenantId] = [...(mockTenantSsoProviders[tenantId] ?? []), created]
    return HttpResponse.json(created, { status: 201 })
  }),

  http.delete(`${base}/api/admin/tenants/:tenantId/sso-providers/:providerId`, async ({ params }) => {
    await delay(200)
    const tenantId = String(params.tenantId)
    const providerId = String(params.providerId)

    if (!getMockTenantById(tenantId)) {
      return new HttpResponse(null, { status: 404 })
    }

    if (!mockSsoCapabilityAvailable) {
      return createPremiumFeatureUnavailableResponse()
    }

    mockTenantSsoProviders[tenantId] = (mockTenantSsoProviders[tenantId] ?? []).filter((provider) => provider.id !== providerId)
    return new HttpResponse(null, { status: 204 })
  }),

  http.get(`${base}/clients`, async () => {
    await delay(300)
    return HttpResponse.json([
      { id: '1', displayName: 'Acme Corp', isActive: true, createdAt: new Date().toISOString(), recentUsageTokens: 14520 },
      { id: '2', displayName: 'Globex Inc', isActive: false, createdAt: new Date().toISOString(), recentUsageTokens: 0 },
      { id: '3', displayName: 'Umbrella Corp', isActive: true, createdAt: new Date().toISOString(), recentUsageTokens: 89300 }
    ])
  }),

  http.get(`${base}/clients/:id`, async ({ params }) => {
    await delay(300)
    return HttpResponse.json({
        id: params.id,
        displayName: 'Mocked Client ' + params.id,
        isActive: true,
        createdAt: new Date().toISOString(),
        recentUsageTokens: 14520,
        reviewerId: '0000-1111-2222-3333'
    })
  }),

  http.patch(`${base}/clients/:id`, async ({ request }) => {
    await delay(300)
    const body = await request.json() as any
    return HttpResponse.json({
      id: '1', displayName: body.displayName ?? 'Mocked Client', isActive: body.isActive ?? true, createdAt: new Date().toISOString(), recentUsageTokens: 14520
    })
  }),

  http.get(`${base}/clients/:clientId/ado-organization-scopes`, async ({ params }) => {
    await delay(250)
    const clientId = String(params.clientId)
    return HttpResponse.json(adoOrganizationScopesByClient[clientId] ?? [])
  }),

  http.post(`${base}/clients/:clientId/ado-organization-scopes`, async ({ params, request }) => {
    await delay(400)
    const clientId = String(params.clientId)
    const body = await request.json() as any
    const newScope = {
      id: `scope-${Math.random().toString(36).slice(2, 10)}`,
      clientId,
      organizationUrl: body.organizationUrl ?? '',
      displayName: body.displayName ?? null,
      isEnabled: body.isEnabled ?? true,
      verificationStatus: 'pending',
      createdAt: new Date().toISOString(),
      updatedAt: new Date().toISOString(),
    }
    adoOrganizationScopesByClient[clientId] = [...(adoOrganizationScopesByClient[clientId] ?? []), newScope]
    return HttpResponse.json(newScope, { status: 201 })
  }),

  http.patch(`${base}/clients/:clientId/ado-organization-scopes/:scopeId`, async ({ params, request }) => {
    await delay(300)
    const clientId = String(params.clientId)
    const scopeId = String(params.scopeId)
    const body = await request.json() as any
    const scopes = adoOrganizationScopesByClient[clientId] ?? []
    const idx = scopes.findIndex(s => s.id === scopeId)
    if (idx === -1) return new HttpResponse(null, { status: 404 })
    scopes[idx] = { ...scopes[idx], ...body, updatedAt: new Date().toISOString() }
    return HttpResponse.json(scopes[idx])
  }),

  http.delete(`${base}/clients/:clientId/ado-organization-scopes/:scopeId`, async ({ params }) => {
    await delay(300)
    const clientId = String(params.clientId)
    const scopeId = String(params.scopeId)
    adoOrganizationScopesByClient[clientId] = (adoOrganizationScopesByClient[clientId] ?? []).filter(s => s.id !== scopeId)
    return new HttpResponse(null, { status: 204 })
  }),

  http.get(`${base}/admin/clients/:clientId/ado/discovery/projects`, async ({ params, request }) => {
    await delay(250)
    const clientId = String(params.clientId)
    const url = new URL(request.url)
    const organizationScopeId = url.searchParams.get('organizationScopeId')
    const scope = getScope(clientId, organizationScopeId)

    if (!scope || scope.isEnabled === false) {
      return HttpResponse.json({ error: 'The selected Azure DevOps organization is no longer available for this client.' }, { status: 409 })
    }

    return HttpResponse.json(adoProjectsByScope[scope.id] ?? [])
  }),

  http.get(`${base}/admin/clients/:clientId/ado/discovery/crawl-filters`, async ({ params, request }) => {
    await delay(250)
    const clientId = String(params.clientId)
    const url = new URL(request.url)
    const organizationScopeId = url.searchParams.get('organizationScopeId')
    const projectId = url.searchParams.get('projectId')
    const scope = getScope(clientId, organizationScopeId)

    if (!scope || scope.isEnabled === false) {
      return HttpResponse.json({ error: 'The selected Azure DevOps organization is no longer available for this client.' }, { status: 409 })
    }

    return HttpResponse.json(getCrawlFilters(scope.id, projectId))
  }),

  http.get(`${base}/admin/clients/:clientId/ado/discovery/sources`, async ({ params, request }) => {
    await delay(250)
    const clientId = String(params.clientId)
    const url = new URL(request.url)
    const organizationScopeId = url.searchParams.get('organizationScopeId')
    const projectId = url.searchParams.get('projectId')
    const sourceKind = url.searchParams.get('sourceKind') ?? 'repository'
    const scope = getScope(clientId, organizationScopeId)

    if (!scope || scope.isEnabled === false) {
      return HttpResponse.json({ error: 'The selected Azure DevOps organization is no longer available for this client.' }, { status: 409 })
    }

    const key = `${scope.id}::${projectId}::${sourceKind}`
    return HttpResponse.json(adoSourcesByProject[key] ?? [])
  }),

  http.get(`${base}/admin/clients/:clientId/ado/discovery/branches`, async ({ params, request }) => {
    await delay(250)
    const clientId = String(params.clientId)
    const url = new URL(request.url)
    const organizationScopeId = url.searchParams.get('organizationScopeId')
    const canonicalSourceValue = url.searchParams.get('canonicalSourceValue')
    const scope = getScope(clientId, organizationScopeId)

    if (!scope || scope.isEnabled === false) {
      return HttpResponse.json({ error: 'The selected Azure DevOps organization is no longer available for this client.' }, { status: 409 })
    }

    return HttpResponse.json(adoBranchesBySource[canonicalSourceValue ?? ''] ?? [])
  }),

  http.get(`${base}/admin/clients/:clientId/procursor/sources`, async ({ params }) => {
    await delay(300)
    const clientId = String(params.clientId)
    return HttpResponse.json(proCursorSourcesByClient[clientId] ?? [])
  }),

  http.post(`${base}/admin/clients/:clientId/procursor/sources`, async ({ params, request }) => {
    await delay(500)
    const clientId = String(params.clientId)
    const body = await request.json() as any
    const scope = getScope(clientId, body.organizationScopeId)

    if (body.organizationScopeId && (!scope || scope.isEnabled === false)) {
      return HttpResponse.json({ error: 'The selected Azure DevOps organization is no longer available for this client.' }, { status: 409 })
    }

    const newSource = {
      sourceId: `src-${Math.random().toString(36).slice(2, 10)}`,
      clientId,
      organizationScopeId: body.organizationScopeId ?? null,
      providerScopePath: scope?.organizationUrl ?? null,
      providerProjectKey: body.providerProjectKey ?? null,
      repositoryId: body.canonicalSourceRef?.value ?? null,
      sourceDisplayName: body.sourceDisplayName ?? null,
      canonicalSourceRef: body.canonicalSourceRef ?? null,
      displayName: body.displayName ?? 'New Source',
      sourceKind: body.sourceKind ?? 'repository',
      defaultBranch: body.defaultBranch ?? 'main',
      rootPath: body.rootPath ?? null,
      symbolMode: body.symbolMode ?? 'auto',
      isEnabled: true,
      status: 'pending',
      latestSnapshot: null,
      createdAt: new Date().toISOString(),
      updatedAt: new Date().toISOString(),
    }

    proCursorSourcesByClient[clientId] = [newSource, ...(proCursorSourcesByClient[clientId] ?? [])]
    return HttpResponse.json(newSource, { status: 201 })
  }),

  http.post(`${base}/admin/clients/:clientId/procursor/sources/:sourceId/refresh`, async ({ params }) => {
    await delay(300)
    const clientId = String(params.clientId)
    const sourceId = String(params.sourceId)
    const sources = proCursorSourcesByClient[clientId] ?? []
    const source = sources.find(s => s.sourceId === sourceId)

    if (!source) {
      return new HttpResponse(null, { status: 404 })
    }

    return HttpResponse.json({ sourceId, status: 'queued', queuedAt: new Date().toISOString() })
  }),

  http.get(`${base}/admin/clients/:clientId/procursor/sources/:sourceId/branches`, async ({ params }) => {
    await delay(250)
    const clientId = String(params.clientId)
    const sourceId = String(params.sourceId)
    const sources = proCursorSourcesByClient[clientId] ?? []
    const source = sources.find(s => s.sourceId === sourceId)

    if (!source) return new HttpResponse(null, { status: 404 })

    const branches = adoBranchesBySource[source.repositoryId] ?? []
    return HttpResponse.json(
      branches.map((b: any, i: number) => ({
        branchId: `branch-${sourceId}-${i}`,
        sourceId,
        branchName: b.branchName,
        isDefault: b.isDefault ?? false,
        autoRefreshEnabled: b.isDefault ?? false,
        createdAt: new Date(Date.now() - 86400000 * (i + 1)).toISOString(),
        updatedAt: new Date(Date.now() - 86400000 * (i + 1)).toISOString(),
      }))
    )
  }),

  http.post(`${base}/admin/clients/:clientId/procursor/sources/:sourceId/branches`, async ({ params, request }) => {
    await delay(300)
    const body = await request.json() as any
    const sourceId = String(params.sourceId)
    const newBranch = {
      branchId: `branch-${Math.random().toString(36).slice(2, 10)}`,
      sourceId,
      branchName: body.branchName ?? 'main',
      isDefault: body.isDefault ?? false,
      autoRefreshEnabled: body.autoRefreshEnabled ?? false,
      createdAt: new Date().toISOString(),
      updatedAt: new Date().toISOString(),
    }
    return HttpResponse.json(newBranch, { status: 201 })
  }),

  http.patch(`${base}/admin/clients/:clientId/procursor/sources/:sourceId/branches/:branchId`, async ({ params, request }) => {
    await delay(250)
    const body = await request.json() as any
    return HttpResponse.json({
      branchId: String(params.branchId),
      sourceId: String(params.sourceId),
      branchName: body.branchName ?? 'main',
      isDefault: body.isDefault ?? false,
      autoRefreshEnabled: body.autoRefreshEnabled ?? false,
      updatedAt: new Date().toISOString(),
    })
  }),

  http.delete(`${base}/admin/clients/:clientId/procursor/sources/:sourceId/branches/:branchId`, async () => {
    await delay(250)
    return new HttpResponse(null, { status: 204 })
  }),

  http.get(`${base}/admin/clients/:clientId/procursor/token-usage`, async ({ params }) => {
    await delay(250)
    const clientId = String(params.clientId)
    const usage = proCursorClientUsageByClient[clientId]

    if (!usage) {
      return HttpResponse.json({ error: 'Failed to load ProCursor usage.' }, { status: 404 })
    }

    return HttpResponse.json({
      ...usage,
      topSources: proCursorTopSourcesByClient[clientId] ?? usage.topSources ?? [],
    })
  }),

  http.get(`${base}/admin/clients/:clientId/procursor/token-usage/top-sources`, async ({ params }) => {
    await delay(180)
    const clientId = String(params.clientId)
    return HttpResponse.json({ items: proCursorTopSourcesByClient[clientId] ?? [] })
  }),

  http.get(`${base}/admin/clients/:clientId/procursor/sources/:sourceId/token-usage`, async ({ params }) => {
    await delay(220)
    const sourceId = String(params.sourceId)
    const usage = proCursorSourceUsageBySource[sourceId]

    if (!usage) {
      return HttpResponse.json({ error: 'Failed to load source-level ProCursor usage.' }, { status: 404 })
    }

    return HttpResponse.json(usage)
  }),

  http.get(`${base}/admin/clients/:clientId/procursor/sources/:sourceId/token-usage/events`, async ({ params, request }) => {
    await delay(220)
    const sourceId = String(params.sourceId)
    const url = new URL(request.url)
    const limit = Number(url.searchParams.get('limit') ?? '10')
    const items = proCursorRecentEventsBySource[sourceId] ?? []
    return HttpResponse.json({ items: items.slice(0, limit) })
  }),

  http.get(`${base}/clients/:clientId/ai-connections`, async ({ params }) => {
    await delay(250)
    const clientId = String(params.clientId)
    return HttpResponse.json(aiConnectionsByClient[clientId] ?? [])
  }),

  http.post(`${base}/clients/:clientId/ai-connections`, async ({ params, request }) => {
    await delay(300)
    const clientId = String(params.clientId)
    const body = await request.json() as any
    const newConnection = {
      id: `ai-${Math.random().toString(36).slice(2, 10)}`,
      clientId,
      displayName: body.displayName ?? 'New connection',
      endpointUrl: body.endpointUrl ?? '',
      models: body.models ?? [],
      isActive: false,
      activeModel: null,
      modelCategory: body.modelCategory ?? null,
      modelCapabilities: body.modelCapabilities ?? [],
      createdAt: new Date().toISOString(),
      updatedAt: new Date().toISOString(),
    }

    aiConnectionsByClient[clientId] = [newConnection, ...(aiConnectionsByClient[clientId] ?? [])]
    return HttpResponse.json(newConnection, { status: 201 })
  }),

  http.patch(`${base}/clients/:clientId/ai-connections/:connectionId`, async ({ params, request }) => {
    await delay(300)
    const clientId = String(params.clientId)
    const connectionId = String(params.connectionId)
    const body = await request.json() as any
    const connections = aiConnectionsByClient[clientId] ?? []
    const idx = connections.findIndex((connection) => connection.id === connectionId)

    if (idx === -1) {
      return new HttpResponse(null, { status: 404 })
    }

    connections[idx] = {
      ...connections[idx],
      displayName: body.displayName ?? connections[idx].displayName,
      endpointUrl: body.endpointUrl ?? connections[idx].endpointUrl,
      models: body.models ?? connections[idx].models,
      modelCapabilities: body.modelCapabilities ?? connections[idx].modelCapabilities,
      updatedAt: new Date().toISOString(),
    }

    return HttpResponse.json(connections[idx])
  }),

  http.post(`${base}/clients/:clientId/ai-connections/:connectionId/activate`, async ({ params, request }) => {
    await delay(250)
    const clientId = String(params.clientId)
    const connectionId = String(params.connectionId)
    const body = await request.json() as any
    const connections = aiConnectionsByClient[clientId] ?? []
    const idx = connections.findIndex((connection) => connection.id === connectionId)

    if (idx === -1) {
      return new HttpResponse(null, { status: 404 })
    }

    connections[idx] = {
      ...connections[idx],
      isActive: connections[idx].modelCategory ? connections[idx].isActive : true,
      activeModel: body.model,
      updatedAt: new Date().toISOString(),
    }

    return HttpResponse.json(connections[idx])
  }),

  http.post(`${base}/clients/:clientId/ai-connections/:connectionId/deactivate`, async ({ params }) => {
    await delay(250)
    const clientId = String(params.clientId)
    const connectionId = String(params.connectionId)
    const connections = aiConnectionsByClient[clientId] ?? []
    const idx = connections.findIndex((connection) => connection.id === connectionId)

    if (idx === -1) {
      return new HttpResponse(null, { status: 404 })
    }

    connections[idx] = {
      ...connections[idx],
      isActive: false,
      activeModel: null,
      updatedAt: new Date().toISOString(),
    }

    return HttpResponse.json(connections[idx])
  }),

  http.delete(`${base}/clients/:clientId/ai-connections/:connectionId`, async ({ params }) => {
    await delay(250)
    const clientId = String(params.clientId)
    const connectionId = String(params.connectionId)
    const connections = aiConnectionsByClient[clientId] ?? []
    aiConnectionsByClient[clientId] = connections.filter((connection) => connection.id !== connectionId)
    return new HttpResponse(null, { status: 204 })
  }),

  http.get(`${base}/admin/providers`, async () => {
    await delay(180)
    return HttpResponse.json(providerActivationStatuses)
  }),

  http.patch(`${base}/admin/providers/:provider`, async ({ params, request }) => {
    await delay(220)
    const providerFamily = String(params.provider)
    const body = await request.json() as any
    const index = providerActivationStatuses.findIndex((status) => status.providerFamily === providerFamily)

    if (index === -1) {
      return new HttpResponse(null, { status: 404 })
    }

    providerActivationStatuses[index] = {
      ...providerActivationStatuses[index],
      isEnabled: body.isEnabled !== false,
      updatedAt: new Date().toISOString(),
    }

    return HttpResponse.json(providerActivationStatuses[index])
  }),

  http.get(`${base}/clients/:clientId/provider-connections`, async ({ params }) => {
    await delay(220)
    const clientId = String(params.clientId)
    return HttpResponse.json((providerConnectionsByClient[clientId] ?? []).filter((connection) => isProviderEnabled(connection.providerFamily)))
  }),

  http.post(`${base}/clients/:clientId/provider-connections`, async ({ params, request }) => {
    await delay(280)
    const clientId = String(params.clientId)
    const body = await request.json() as any

    if (!isProviderEnabled(body.providerFamily)) {
      return HttpResponse.json({ error: 'The selected provider family is currently disabled by system administration.' }, { status: 409 })
    }

    const connection = {
      id: `provider-conn-${Math.random().toString(36).slice(2, 10)}`,
      clientId,
      providerFamily: body.providerFamily ?? 'github',
      hostBaseUrl: body.hostBaseUrl ?? 'https://github.com',
      authenticationKind: body.authenticationKind ?? 'personalAccessToken',
      displayName: body.displayName ?? 'New provider connection',
      isActive: body.isActive ?? true,
      verificationStatus: 'verified',
      lastVerifiedAt: new Date().toISOString(),
      lastVerificationError: null,
      lastVerificationFailureCategory: null,
      createdAt: new Date().toISOString(),
      updatedAt: new Date().toISOString(),
    }

    providerConnectionsByClient[clientId] = [connection, ...(providerConnectionsByClient[clientId] ?? [])]
    providerScopesByConnection[connection.id] = []
    providerReviewerIdentitiesByConnection[connection.id] = null
    providerReviewerIdentityCandidatesByConnection[connection.id] = []

    return HttpResponse.json(connection, { status: 201 })
  }),

  http.get(`${base}/clients/:clientId/provider-operations/audit-trail`, async ({ params, request }) => {
    await delay(180)
    const clientId = String(params.clientId)
    const url = new URL(request.url)
    const take = Math.max(1, Number(url.searchParams.get('take') ?? '20'))

    return HttpResponse.json(buildProviderAuditTrail(clientId).slice(0, take))
  }),

  http.patch(`${base}/clients/:clientId/provider-connections/:connectionId`, async ({ params, request }) => {
    await delay(260)
    const clientId = String(params.clientId)
    const connectionId = String(params.connectionId)
    const body = await request.json() as any
    const connections = providerConnectionsByClient[clientId] ?? []
    const index = connections.findIndex((connection) => connection.id === connectionId)

    if (index === -1) {
      return new HttpResponse(null, { status: 404 })
    }

    connections[index] = {
      ...connections[index],
      displayName: body.displayName ?? connections[index].displayName,
      hostBaseUrl: body.hostBaseUrl ?? connections[index].hostBaseUrl,
      authenticationKind: body.authenticationKind ?? connections[index].authenticationKind,
      isActive: body.isActive ?? connections[index].isActive,
      updatedAt: new Date().toISOString(),
    }

    return HttpResponse.json(connections[index])
  }),

  http.delete(`${base}/clients/:clientId/provider-connections/:connectionId`, async ({ params }) => {
    await delay(220)
    const clientId = String(params.clientId)
    const connectionId = String(params.connectionId)

    providerConnectionsByClient[clientId] = (providerConnectionsByClient[clientId] ?? [])
      .filter((connection) => connection.id !== connectionId)
    delete providerScopesByConnection[connectionId]
    delete providerReviewerIdentitiesByConnection[connectionId]
    delete providerReviewerIdentityCandidatesByConnection[connectionId]

    return new HttpResponse(null, { status: 204 })
  }),

  http.post(`${base}/clients/:clientId/provider-connections/:connectionId/verify`, async ({ params }) => {
    await delay(220)
    const clientId = String(params.clientId)
    const connectionId = String(params.connectionId)
    const connection = getProviderConnection(clientId, connectionId)

    if (!connection) {
      return new HttpResponse(null, { status: 404 })
    }

    connection.verificationStatus = 'verified'
    connection.lastVerifiedAt = new Date().toISOString()
    connection.lastVerificationError = null
    connection.lastVerificationFailureCategory = null
    connection.updatedAt = new Date().toISOString()

    return HttpResponse.json(connection)
  }),

  http.get(`${base}/clients/:clientId/provider-connections/:connectionId/scopes`, async ({ params }) => {
    await delay(220)
    const clientId = String(params.clientId)
    const connectionId = String(params.connectionId)

    if (!getProviderConnection(clientId, connectionId)) {
      return new HttpResponse(null, { status: 404 })
    }

    return HttpResponse.json(providerScopesByConnection[connectionId] ?? [])
  }),

  http.post(`${base}/clients/:clientId/provider-connections/:connectionId/scopes`, async ({ params, request }) => {
    await delay(260)
    const clientId = String(params.clientId)
    const connectionId = String(params.connectionId)
    const connection = getProviderConnection(clientId, connectionId)

    if (!connection) {
      return new HttpResponse(null, { status: 404 })
    }

    const body = await request.json() as any
    const scope = {
      id: `provider-scope-${Math.random().toString(36).slice(2, 10)}`,
      clientId,
      connectionId,
      scopeType: body.scopeType ?? 'organization',
      externalScopeId: body.externalScopeId ?? body.scopePath ?? 'generated-scope',
      scopePath: body.scopePath ?? body.externalScopeId ?? 'generated-scope',
      displayName: body.displayName ?? 'Generated Scope',
      verificationStatus: 'verified',
      isEnabled: body.isEnabled ?? true,
      lastVerifiedAt: new Date().toISOString(),
      lastVerificationError: null,
      createdAt: new Date().toISOString(),
      updatedAt: new Date().toISOString(),
    }

    providerScopesByConnection[connectionId] = [scope, ...(providerScopesByConnection[connectionId] ?? [])]

    return HttpResponse.json(scope, { status: 201 })
  }),

  http.patch(`${base}/clients/:clientId/provider-connections/:connectionId/scopes/:scopeId`, async ({ params, request }) => {
    await delay(240)
    const clientId = String(params.clientId)
    const connectionId = String(params.connectionId)
    const scopeId = String(params.scopeId)

    if (!getProviderConnection(clientId, connectionId)) {
      return new HttpResponse(null, { status: 404 })
    }

    const scopes = providerScopesByConnection[connectionId] ?? []
    const index = scopes.findIndex((scope) => scope.id === scopeId)

    if (index === -1) {
      return new HttpResponse(null, { status: 404 })
    }

    const body = await request.json() as any
    scopes[index] = {
      ...scopes[index],
      displayName: body.displayName ?? scopes[index].displayName,
      isEnabled: body.isEnabled ?? scopes[index].isEnabled,
      verificationStatus: body.verificationStatus ?? scopes[index].verificationStatus,
      lastVerificationError: body.lastVerificationError ?? scopes[index].lastVerificationError,
      updatedAt: new Date().toISOString(),
    }

    return HttpResponse.json(scopes[index])
  }),

  http.get(`${base}/clients/:clientId/provider-connections/:connectionId/reviewer-identities/resolve`, async ({ params, request }) => {
    await delay(220)
    const clientId = String(params.clientId)
    const connectionId = String(params.connectionId)

    if (!getProviderConnection(clientId, connectionId)) {
      return new HttpResponse(null, { status: 404 })
    }

    const url = new URL(request.url)
    const search = url.searchParams.get('search')?.trim().toLowerCase()
    const identities = providerReviewerIdentityCandidatesByConnection[connectionId] ?? []

    if (!search) {
      return HttpResponse.json(identities)
    }

    return HttpResponse.json(
      identities.filter((identity) =>
        identity.login.toLowerCase().includes(search) || identity.displayName.toLowerCase().includes(search)))
  }),

  http.get(`${base}/clients/:clientId/provider-connections/:connectionId/reviewer-identity`, async ({ params }) => {
    await delay(180)
    const clientId = String(params.clientId)
    const connectionId = String(params.connectionId)

    if (!getProviderConnection(clientId, connectionId)) {
      return new HttpResponse(null, { status: 404 })
    }

    const identity = providerReviewerIdentitiesByConnection[connectionId]
    if (!identity) {
      return new HttpResponse(null, { status: 404 })
    }

    return HttpResponse.json(identity)
  }),

  http.put(`${base}/clients/:clientId/provider-connections/:connectionId/reviewer-identity`, async ({ params, request }) => {
    await delay(240)
    const clientId = String(params.clientId)
    const connectionId = String(params.connectionId)
    const connection = getProviderConnection(clientId, connectionId)

    if (!connection) {
      return new HttpResponse(null, { status: 404 })
    }

    const body = await request.json() as any
    const selectedIdentity =
      (providerReviewerIdentityCandidatesByConnection[connectionId] ?? []).find((identity) =>
        identity.externalUserId === body.externalUserId || identity.id === body.id) ?? {
        id: `provider-reviewer-${Math.random().toString(36).slice(2, 10)}`,
        clientId,
        connectionId,
        providerFamily: connection.providerFamily,
        externalUserId: body.externalUserId ?? 'provider-reviewer',
        login: body.login ?? 'provider-reviewer',
        displayName: body.displayName ?? 'Provider Reviewer',
        isBot: body.isBot ?? true,
      }

    providerReviewerIdentitiesByConnection[connectionId] = {
      ...selectedIdentity,
      updatedAt: new Date().toISOString(),
    }

    return HttpResponse.json(providerReviewerIdentitiesByConnection[connectionId])
  }),

  http.delete(`${base}/clients/:clientId/provider-connections/:connectionId/reviewer-identity`, async ({ params }) => {
    await delay(180)
    const clientId = String(params.clientId)
    const connectionId = String(params.connectionId)

    if (!getProviderConnection(clientId, connectionId)) {
      return new HttpResponse(null, { status: 404 })
    }

    providerReviewerIdentitiesByConnection[connectionId] = null
    return new HttpResponse(null, { status: 204 })
  }),

  http.get(`${base}/admin/users`, async () => {
    await delay(400)
    return HttpResponse.json([
      { id: 'u1', username: 'admin', globalRole: 'Admin', isActive: true, createdAt: new Date().toISOString() },
      { id: 'u2', username: 'jsmith', globalRole: 'User', isActive: true, createdAt: new Date().toISOString() }
    ])
  }),

  http.get(`${base}/admin/users/:id`, async () => {
    return HttpResponse.json({
       assignments: [
         { assignmentId: 'a1', clientId: '1', role: 'ClientAdministrator', assignedAt: new Date().toISOString() },
         { assignmentId: 'a2', clientId: '2', role: 'ClientUser', assignedAt: new Date().toISOString() }
       ]
    })
  }),

  http.get(`${base}/users/me/pats`, async () => {
    await delay(200)
    return HttpResponse.json([
      { id: 'p1', label: 'CI Pipeline', createdAt: new Date().toISOString(), lastUsedAt: new Date().toISOString(), expiresAt: null, isRevoked: false },
      { id: 'p2', label: 'Local Dev Proxy', createdAt: new Date().toISOString(), lastUsedAt: null, expiresAt: new Date(Date.now() + 86400000).toISOString(), isRevoked: false }
    ])
  }),

  http.post(`${base}/users/me/pats`, async () => {
    await delay(300)
    return HttpResponse.json({ token: 'mock-pat-' + Math.random().toString(36).substring(7) })
  }),

  http.post(`${base}/users/me/password`, async () => {
    await delay(250)
    return new HttpResponse(null, { status: 204 })
  }),

  http.get(`${base}/jobs`, async () => {
    await delay(400)
    jobTick++

    const isCompleted = false

    return HttpResponse.json({
      items: [
        {
          id: 'job-123',
          providerProjectKey: 'proj-x',
          repositoryId: 'backend-service',
          pullRequestId: 42,
          providerScopePath: 'https://dev.azure.com/acme',
          status: 'completed',
          iterationId: 2,
          submittedAt: new Date(Date.now() - 86400000).toISOString(),
          processingStartedAt: new Date(Date.now() - 86000000).toISOString(),
          completedAt: new Date(Date.now() - 85000000).toISOString(),
          totalInputTokens: 12000,
          totalOutputTokens: 850,
          resultSummary: 'Found 3 minor issues. Suggested improvements.',
          prTitle: 'feat: Add authentication middleware',
          prRepositoryName: 'backend-service',
          prSourceBranch: 'feature/auth-middleware',
          prTargetBranch: 'main',
          aiModel: 'claude-opus-4-5',
          clientId: '1'
        },
        {
          id: 'job-124',
          providerProjectKey: 'proj-y',
          repositoryId: 'frontend-app',
          pullRequestId: 89,
          providerScopePath: 'https://dev.azure.com/acme',
          status: isCompleted ? 'completed' : 'processing',
          iterationId: Math.ceil(jobTick / 2) || 1,
          submittedAt: new Date(Date.now() - 1000000).toISOString(),
          processingStartedAt: new Date(Date.now() - 500000).toISOString(),
          completedAt: isCompleted ? new Date().toISOString() : null,
          totalInputTokens: 5000 + (jobTick * 200),
          totalOutputTokens: jobTick * 100,
          resultSummary: isCompleted
            ? 'Automated review finished. LGTM!'
            : `Evaluating subjob ${jobTick}: src/components/Component${Math.ceil(jobTick/2)}.vue`,
          prTitle: 'refactor: Migrate to Composition API',
          prRepositoryName: 'frontend-app',
          prSourceBranch: 'refactor/composition-api',
          prTargetBranch: 'develop',
          aiModel: 'gpt-4o',
          clientId: '1'
        },
        {
          id: 'job-125',
          providerProjectKey: 'proj-z',
          repositoryId: 'infrastructure',
          pullRequestId: 12,
          providerScopePath: 'https://dev.azure.com/acme',
          status: 'failed',
          iterationId: 1,
          submittedAt: new Date(Date.now() - 200000000).toISOString(),
          processingStartedAt: new Date(Date.now() - 190000000).toISOString(),
          completedAt: new Date(Date.now() - 180000000).toISOString(),
          errorMessage: 'Failed to access ADO repository due to expired token.',
          prTitle: 'chore: Update Terraform modules',
          prRepositoryName: 'infrastructure',
          prSourceBranch: 'chore/terraform-update',
          prTargetBranch: 'main',
          aiModel: 'gemini-2.5-pro',
          clientId: '1'
        }
      ]
    })
  }),

  http.get(`${base}/Reviews/:jobId`, async ({ params }) => {
    await delay(300)

    const jobId = params.jobId as string

    // Simulating "No Synthesis" for a failed job
    if (jobId === 'job-125') {
        return HttpResponse.json({
            jobId,
            status: 'failed',
            result: null
        })
    }

    // Simulating "In Progress" for a processing job
    if (jobId === 'job-124') {
        const isSynthesizing = jobTick > 8 // Lets say it synthesizes after 4 file reviews
        return HttpResponse.json({
            jobId,
            status: 'processing',
            result: isSynthesizing ? {
                summary: "Partial summary: The review is ongoing...",
                comments: []
            } : null
        })
    }

    // Provide a mocked synthesis review result for others (completed)
    return HttpResponse.json({
        jobId,
        status: 'completed',
        result: {
            summary: "**AI Review Summary**\n\nThe PR delivers a comprehensive Azure deployment example with supporting documentation, diagrams, and Bicep modules, but a few implementation issues need addressing before it can be considered ready. The README is thorough and matches the template wiring, and the new deployment diagram and Dockerfiles are largely informational. The PowerShell deployment script is well organized but could be tightened around secret handling and credentials exposure.\n\nIn the infrastructure modules, the main Bicep file has a resource-naming bug derived from `projectName` that can break deployments; containerApps.bicep omits the `shareName` for the AzureFile volume (so the mount cannot succeed) and also has opportunities to harden ingress/security settings and avoid hardcoded IDs. Overall the architecture is well laid out but tightening these areas will improve security, reliability, and usability.",
            comments: [
                {
                    filePath: "/.azure/modules/network.bicep",
                    lineNumber: 7,
                    severity: "suggestion",
                    message: "Consider parameterizing the address space prefixes instead of hardcoding '10.0.0.0/16' to improve reuse and flexibility."
                },
                {
                    filePath: "/.azure/modules/containerApps.bicep",
                    lineNumber: 101,
                    severity: "error",
                    message: "Role assignment depends on `db.identity.principalId` but has no explicit `dependsOn` on `db`. Managed identity service principals can be eventually consistent; this can cause intermittent `PrincipalNotFound` during deployment. Add `dependsOn: [db]` (or a deterministic dependency path) to ensure identity exists before assignment."
                },
                {
                    filePath: "/.azure/modules/containerEnvironment.bicep",
                    lineNumber: 10,
                    severity: "warning",
                    message: "Using `Microsoft.App/managedEnvironments@2025-10-02-preview` introduces preview API risk (breaking changes/region support drift). For production IaC, prefer the latest stable API version unless a required feature is preview-only."
                }
            ]
        }
    })
  }),

  // Admin-authenticated result endpoint (used by management UI instead of /Reviews/:jobId)
  http.get(`${base}/jobs/:id/result`, async ({ params }) => {
    await delay(300)

    const id = params.id as string

    if (id === 'job-125') {
      return new HttpResponse(null, { status: 404 })
    }

    if (id === 'job-124') {
      const isSynthesizing = jobTick > 8
      if (!isSynthesizing) return new HttpResponse(null, { status: 404 })
      return HttpResponse.json({
        jobId: id,
        status: 'processing',
        submittedAt: new Date(Date.now() - 1000000).toISOString(),
        completedAt: null,
        result: {
          summary: "Partial summary: The review is ongoing...",
          comments: []
        }
      })
    }

    return HttpResponse.json({
      jobId: id,
      status: 'completed',
      submittedAt: new Date(Date.now() - 86400000).toISOString(),
      completedAt: new Date(Date.now() - 85000000).toISOString(),
      result: {
        summary: "**AI Review Summary**\n\nThe PR delivers a comprehensive Azure deployment example with supporting documentation, diagrams, and Bicep modules, but a few implementation issues need addressing before it can be considered ready. The README is thorough and matches the template wiring, and the new deployment diagram and Dockerfiles are largely informational. The PowerShell deployment script is well organized but could be tightened around secret handling and credentials exposure.\n\nIn the infrastructure modules, the main Bicep file has a resource-naming bug derived from `projectName` that can break deployments; containerApps.bicep omits the `shareName` for the AzureFile volume (so the mount cannot succeed) and also has opportunities to harden ingress/security settings and avoid hardcoded IDs. Overall the architecture is well laid out but tightening these areas will improve security, reliability, and usability.",
        comments: [
          {
            filePath: "/.azure/modules/network.bicep",
            lineNumber: 7,
            severity: "suggestion",
            message: "Consider parameterizing the address space prefixes instead of hardcoding '10.0.0.0/16' to improve reuse and flexibility."
          },
          {
            filePath: "/.azure/modules/containerApps.bicep",
            lineNumber: 101,
            severity: "error",
            message: "Role assignment depends on `db.identity.principalId` but has no explicit `dependsOn` on `db`. Managed identity service principals can be eventually consistent; this can cause intermittent `PrincipalNotFound` during deployment. Add `dependsOn: [db]` (or a deterministic dependency path) to ensure identity exists before assignment."
          },
          {
            filePath: "/.azure/modules/containerEnvironment.bicep",
            lineNumber: 10,
            severity: "warning",
            message: "Using `Microsoft.App/managedEnvironments@2025-10-02-preview` introduces preview API risk (breaking changes/region support drift). For production IaC, prefer the latest stable API version unless a required feature is preview-only."
          }
        ]
      }
    })
  }),

  http.get(`${base}/jobs/:id/protocol`, async ({ params }) => {
    await delay(600)

    if (params.id === 'job-124') {
        const events = []
        const currentTick = Math.min(jobTick, 8)

        for (let i = 1; i <= currentTick; i++) {
            // Odd ticks generate a fresh ToolCall
            events.push({
                id: `e${i}_call`,
                occurredAt: new Date(Date.now() - 500000 + i * 1500).toISOString(),
                kind: 'ToolCall',
                name: 'AnalyzeCodeChunk',
                inputTokens: 500, outputTokens: 0,
                inputTextSample: `function execute() {\n  return "processing chunk ${Math.ceil(i/2)}";\n}`,
                outputSummary: null
            })
            // Even ticks "answer" the previous call with a ToolResult
            if (i % 2 === 0) {
                events.push({
                    id: `e${i}_result`,
                    occurredAt: new Date(Date.now() - 500000 + i * 1500 + 800).toISOString(),
                    kind: 'ToolResult',
                    name: 'AnalyzeCodeChunk',
                    inputTokens: 0, outputTokens: 100,
                    inputTextSample: null,
                    outputSummary: `Analysis complete. Chunk ${i/2} is clean and optimal.`
                })
            }
        }

        const isCompleted = false
        return HttpResponse.json([
          {
            id: 'pass124',
            jobId: 'job-124',
            label: `src/components/Component${Math.ceil(currentTick/2)}.vue`,
            startedAt: new Date(Date.now() - 500000).toISOString(),
            completedAt: isCompleted ? new Date().toISOString() : null,
            outcome: isCompleted ? 'Success' : 'Processing',
            iterationCount: Math.ceil(currentTick / 2),
            toolCallCount: Math.floor(currentTick / 2),
            finalConfidence: isCompleted ? 99 : null,
            totalInputTokens: 5000 + (currentTick * 200),
            totalOutputTokens: currentTick * 100,
            events
          }
        ])
    }

    return HttpResponse.json(protocolMockData)
  }),

  // Crawl Configurations
  http.get(`${base}/admin/crawl-configurations`, async () => {
    await delay(400)
    return HttpResponse.json(crawlConfigs)
  }),

  http.post(`${base}/admin/crawl-configurations`, async ({ request }) => {
    await delay(600)
    const body = await request.json() as any
    const scope = getScope(String(body.clientId), body.organizationScopeId)

    if (body.organizationScopeId && (!scope || scope.isEnabled === false)) {
      return HttpResponse.json({ error: 'The selected Azure DevOps organization is no longer available for this client.' }, { status: 409 })
    }

    const availableFilters = getCrawlFilters(body.organizationScopeId, body.providerProjectKey)
    const staleFilter = (body.repoFilters ?? []).find((filter: any) => {
      if (!filter?.canonicalSourceRef?.provider || !filter?.canonicalSourceRef?.value) {
        return false
      }

      return !availableFilters.some((option: any) =>
        option.canonicalSourceRef?.provider === filter.canonicalSourceRef.provider &&
        option.canonicalSourceRef?.value === filter.canonicalSourceRef.value,
      )
    })

    if (staleFilter) {
      return HttpResponse.json({ error: 'The selected crawl filter is no longer available in Azure DevOps.' }, { status: 409 })
    }

    const newConfig = {
      id: `config-${Math.random().toString(36).substr(2, 9)}`,
      clientId: body.clientId,
      organizationScopeId: body.organizationScopeId ?? null,
      providerScopePath: scope?.organizationUrl ?? body.providerScopePath,
      providerProjectKey: body.providerProjectKey,
      crawlIntervalSeconds: body.crawlIntervalSeconds ?? 60,
      isActive: true,
      repoFilters: body.repoFilters ?? [],
      proCursorSourceScopeMode: body.proCursorSourceScopeMode ?? 'allClientSources',
      proCursorSourceIds: body.proCursorSourceIds ?? [],
      invalidProCursorSourceIds: [],
      createdAt: new Date().toISOString(),
      updatedAt: new Date().toISOString()
    }
    crawlConfigs.unshift(newConfig)
    return HttpResponse.json(newConfig, { status: 201 })
  }),

  http.patch(`${base}/admin/crawl-configurations/:configId`, async ({ params, request }) => {
    await delay(500)
    const { configId } = params
    const body = await request.json() as any
    const idx = crawlConfigs.findIndex(c => c.id === configId)
    if (idx === -1) return new HttpResponse(null, { status: 404 })

    const existingConfig = crawlConfigs[idx]
    const availableFilters = getCrawlFilters(existingConfig.organizationScopeId, existingConfig.providerProjectKey)
    const staleFilter = (body.repoFilters ?? []).find((filter: any) => {
      if (!filter?.canonicalSourceRef?.provider || !filter?.canonicalSourceRef?.value) {
        return false
      }

      return !availableFilters.some((option: any) =>
        option.canonicalSourceRef?.provider === filter.canonicalSourceRef.provider &&
        option.canonicalSourceRef?.value === filter.canonicalSourceRef.value,
      )
    })

    if (staleFilter) {
      return HttpResponse.json({ error: 'The selected crawl filter is no longer available in Azure DevOps.' }, { status: 409 })
    }

    crawlConfigs[idx] = {
      ...existingConfig,
      ...body,
      repoFilters: body.repoFilters ?? existingConfig.repoFilters,
      proCursorSourceScopeMode: body.proCursorSourceScopeMode ?? existingConfig.proCursorSourceScopeMode,
      proCursorSourceIds: body.proCursorSourceIds ?? existingConfig.proCursorSourceIds,
      updatedAt: new Date().toISOString()
    }
    return HttpResponse.json(crawlConfigs[idx])
  }),

  http.delete(`${base}/admin/crawl-configurations/:configId`, async ({ params }) => {
    await delay(400)
    const { configId } = params
    const idx = crawlConfigs.findIndex(c => c.id === configId)
    if (idx === -1) return new HttpResponse(null, { status: 404 })

    crawlConfigs.splice(idx, 1)
    return new HttpResponse(null, { status: 204 })
  }),

  // Webhook Configurations
  http.get(`${base}/admin/webhook-configurations`, async () => {
    await delay(300)
    return HttpResponse.json(webhookConfigs.filter((config) => isProviderEnabled(config.provider)))
  }),

  http.get(`${base}/admin/webhook-configurations/:configId/deliveries`, async ({ params }) => {
    await delay(250)
    const configId = String(params.configId)
    return HttpResponse.json({
      items: webhookDeliveryLogsByConfig[configId] ?? [],
    })
  }),

  http.post(`${base}/admin/webhook-configurations`, async ({ request }) => {
    await delay(500)
    const body = await request.json() as any
    const scope = getScope(String(body.clientId), body.organizationScopeId)
    const providerSegment = body.provider === 'azureDevOps'
      ? 'ado'
      : body.provider === 'gitLab'
        ? 'gitlab'
        : body.provider === 'forgejo'
          ? 'forgejo'
          : 'github'

    if (!Array.isArray(body.enabledEvents) || body.enabledEvents.length === 0) {
      return HttpResponse.json({ error: 'At least one enabled event is required.' }, { status: 400 })
    }

    if (!isProviderEnabled(body.provider)) {
      return HttpResponse.json({ error: 'The selected provider family is currently disabled by system administration.' }, { status: 409 })
    }

    if (body.organizationScopeId && (!scope || scope.isEnabled === false)) {
      return HttpResponse.json({ error: 'The selected Azure DevOps organization is no longer available for this client.' }, { status: 409 })
    }

    const availableFilters = getCrawlFilters(body.organizationScopeId, body.providerProjectKey)
    const staleFilter = (body.repoFilters ?? []).find((filter: any) => {
      if (!filter?.canonicalSourceRef?.provider || !filter?.canonicalSourceRef?.value) {
        return false
      }

      return !availableFilters.some((option: any) =>
        option.canonicalSourceRef?.provider === filter.canonicalSourceRef.provider &&
        option.canonicalSourceRef?.value === filter.canonicalSourceRef.value,
      )
    })

    if (staleFilter) {
      return HttpResponse.json({ error: 'The selected webhook filter is no longer available in Azure DevOps.' }, { status: 409 })
    }

    const created = {
      id: `webhook-config-${Math.random().toString(36).slice(2, 9)}`,
      clientId: body.clientId,
      provider: body.provider ?? 'azureDevOps',
      organizationScopeId: body.organizationScopeId ?? null,
      providerScopePath: scope?.organizationUrl ?? body.providerScopePath,
      providerProjectKey: body.providerProjectKey,
      isActive: true,
      enabledEvents: body.enabledEvents ?? [],
      repoFilters: (body.repoFilters ?? []).map((filter: any, index: number) => ({
        id: `webhook-filter-${index + 1}`,
        repositoryName: filter.repositoryName ?? filter.displayName ?? filter.canonicalSourceRef?.value,
        displayName: filter.displayName ?? filter.repositoryName ?? filter.canonicalSourceRef?.value,
        canonicalSourceRef: filter.canonicalSourceRef ?? null,
        targetBranchPatterns: filter.targetBranchPatterns ?? [],
      })),
      listenerUrl: `https://propr.example.com/webhooks/v1/providers/${providerSegment}/${Math.random().toString(16).slice(2, 18)}`,
      generatedSecret: 'generated-secret',
      createdAt: new Date().toISOString(),
    }

    webhookConfigs.unshift(created)
    webhookDeliveryLogsByConfig[created.id] = []
    return HttpResponse.json(created, { status: 201 })
  }),

  http.patch(`${base}/admin/webhook-configurations/:configId`, async ({ params, request }) => {
    await delay(450)
    const configId = String(params.configId)
    const idx = webhookConfigs.findIndex(config => config.id === configId)
    if (idx === -1) {
      return new HttpResponse(null, { status: 404 })
    }

    const body = await request.json() as any
    const existingConfig = webhookConfigs[idx]
    if (body.enabledEvents !== undefined && (!Array.isArray(body.enabledEvents) || body.enabledEvents.length === 0)) {
      return HttpResponse.json({ error: 'At least one enabled event is required.' }, { status: 400 })
    }

    const availableFilters = getCrawlFilters(existingConfig.organizationScopeId, existingConfig.providerProjectKey)
    const staleFilter = (body.repoFilters ?? []).find((filter: any) => {
      if (!filter?.canonicalSourceRef?.provider || !filter?.canonicalSourceRef?.value) {
        return false
      }

      return !availableFilters.some((option: any) =>
        option.canonicalSourceRef?.provider === filter.canonicalSourceRef.provider &&
        option.canonicalSourceRef?.value === filter.canonicalSourceRef.value,
      )
    })

    if (staleFilter) {
      return HttpResponse.json({ error: 'The selected webhook filter is no longer available in Azure DevOps.' }, { status: 409 })
    }

    webhookConfigs[idx] = {
      ...existingConfig,
      ...body,
      repoFilters: body.repoFilters ?? existingConfig.repoFilters,
      generatedSecret: null,
    }

    return HttpResponse.json(webhookConfigs[idx])
  }),

  http.delete(`${base}/admin/webhook-configurations/:configId`, async ({ params }) => {
    await delay(300)
    const configId = String(params.configId)
    const idx = webhookConfigs.findIndex(config => config.id === configId)
    if (idx === -1) {
      return new HttpResponse(null, { status: 404 })
    }

    webhookConfigs.splice(idx, 1)
    delete webhookDeliveryLogsByConfig[configId]
    return new HttpResponse(null, { status: 204 })
  }),

  // Dismissals
  http.get(`${base}/clients/:clientId/dismissals`, async () => {
    await delay(300)
    return HttpResponse.json(dismissedFindings)
  }),

  http.post(`${base}/clients/:clientId/dismissals`, async ({ request }) => {
    await delay(500)
    const body = await request.json() as any
    const newItem = {
      id: `d-${Math.random().toString(36).substr(2, 9)}`,
      clientId: '1',
      patternText: body.originalMessage,
      label: body.label,
      createdAt: new Date().toISOString()
    }
    dismissedFindings.unshift(newItem)
    return HttpResponse.json(newItem, { status: 201 })
  }),

  http.delete(`${base}/clients/:clientId/dismissals/:id`, async ({ params }) => {
    await delay(300)
    const { id } = params
    dismissedFindings = dismissedFindings.filter(d => d.id !== id)
    return new HttpResponse(null, { status: 204 })
  }),

  // Prompt Overrides
  http.get(`${base}/clients/:clientId/prompt-overrides`, async () => {
    await delay(300)
    return HttpResponse.json(promptOverrides)
  }),

  http.post(`${base}/clients/:clientId/prompt-overrides`, async ({ request }) => {
    await delay(500)
    const body = await request.json() as any
    const newItem = {
      id: `o-${Math.random().toString(36).substr(2, 9)}`,
      clientId: '1',
      scope: body.scope,
      crawlConfigId: body.crawlConfigId,
      promptKey: body.promptKey,
      overrideText: body.overrideText,
      createdAt: new Date().toISOString(),
      updatedAt: new Date().toISOString()
    }
    promptOverrides.unshift(newItem)
    return HttpResponse.json(newItem, { status: 201 })
  }),

  http.delete(`${base}/clients/:clientId/prompt-overrides/:id`, async ({ params }) => {
    await delay(300)
    const { id } = params
    promptOverrides = promptOverrides.filter(o => o.id !== id)
    return new HttpResponse(null, { status: 204 })
  }),

  // Thread Memory
  http.get(`${base}/admin/thread-memory`, async ({ request }) => {
    await delay(400)
    const url = new URL(request.url)
    const clientId = url.searchParams.get('clientId')
    const search = url.searchParams.get('search')?.toLowerCase()
    const page = Number(url.searchParams.get('page') || '1')
    const pageSize = Number(url.searchParams.get('pageSize') || '50')

    let items = threadMemoryRecords.filter(r => r.clientId === clientId)
    if (search) {
      items = items.filter(r =>
        r.repositoryId.toLowerCase().includes(search) ||
        r.filePath?.toLowerCase().includes(search) ||
        r.resolutionSummary.toLowerCase().includes(search)
      )
    }

    const totalCount = items.length
    const paginatedItems = items.slice((page - 1) * pageSize, page * pageSize)

    return HttpResponse.json({
      items: paginatedItems,
      totalCount,
      page,
      pageSize
    })
  }),

  http.delete(`${base}/admin/thread-memory/:id`, async ({ params }) => {
    await delay(300)
    const { id } = params
    threadMemoryRecords = threadMemoryRecords.filter(r => r.id !== id)
    return new HttpResponse(null, { status: 204 })
  }),

  http.get(`${base}/admin/thread-memory/activity-log`, async ({ request }) => {
    await delay(500)
    const url = new URL(request.url)
    const clientId = url.searchParams.get('clientId')
    const action = url.searchParams.get('action')
    const page = Number(url.searchParams.get('page') || '1')
    const pageSize = Number(url.searchParams.get('pageSize') || '50')

    let items = memoryActivityLog.filter(l => l.clientId === clientId)
    if (action != null && action !== '') {
      items = items.filter(l => l.action === Number(action))
    }

    const totalCount = items.length
    const paginatedItems = items.slice((page - 1) * pageSize, page * pageSize)

    return HttpResponse.json({
      items: paginatedItems,
      totalCount,
      page,
      pageSize
    })
  }),

  // Client Token Usage
  http.get(`${base}/admin/clients/:clientId/token-usage`, async ({ params }) => {
    const clientId = params.clientId as string
    const today = new Date()
    const samples = Array.from({ length: 14 }, (_, i) => {
      const d = new Date(today)
      d.setDate(d.getDate() - (13 - i))
      const date = d.toISOString().slice(0, 10)
      return [
        { connectionCategory: 1, modelId: 'gpt-5.4-mini', date, inputTokens: 1200 + i * 80, outputTokens: 300 + i * 20 },
        { connectionCategory: 5, modelId: 'gpt-5.4-nano', date, inputTokens: 600 + i * 40, outputTokens: 150 + i * 10 },
      ]
    }).flat()
    const totalInputTokens = samples.reduce((sum, s) => sum + s.inputTokens, 0)
    const totalOutputTokens = samples.reduce((sum, s) => sum + s.outputTokens, 0)
    return HttpResponse.json({
      clientId,
      from: samples[0].date,
      to: samples[samples.length - 1].date,
      totalInputTokens,
      totalOutputTokens,
      samples,
    })
  }),

  // PR Review View Aggregated Data
  http.get(`${base}/clients/:clientId/pr-view`, async ({ request, params }) => {
    await delay(500)
    const url = new URL(request.url)
    const providerScopePath = url.searchParams.get('providerScopePath')
    const providerProjectKey = url.searchParams.get('providerProjectKey')
    const repositoryId = url.searchParams.get('repositoryId')
    const pullRequestId = Number(url.searchParams.get('pullRequestId'))
    const clientId = params.clientId as string

    // Mock data for PR #81 (as seen in user screenshot) or default
    const isSpecialPR = pullRequestId === 81 || pullRequestId === 42

    return HttpResponse.json({
        clientId,
      providerScopePath: providerScopePath || 'https://dev.azure.com/meister-propr',
      providerProjectKey: providerProjectKey || 'Meister-ProPR',
        repositoryId: repositoryId || 'ai-dev-days-local-test',
        pullRequestId: pullRequestId || 81,
        totalJobs: isSpecialPR ? 1 : 0,
        totalInputTokens: isSpecialPR ? 51355 : 0,
        totalOutputTokens: isSpecialPR ? 4658 : 0,
        originatedMemoryCount: isSpecialPR ? 0 : 0,
        contributedMemoryCount: isSpecialPR ? 2 : 0,
        breakdownConsistent: true,
        aggregatedTokenBreakdown: isSpecialPR ? [
          { connectionCategory: 1, modelId: 'gpt-5.4-mini', totalInputTokens: 28775, totalOutputTokens: 1616 },
          { connectionCategory: 5, modelId: 'gpt-5.4-nano', totalInputTokens: 21317, totalOutputTokens: 2373 },
          { connectionCategory: 4, modelId: 'gpt-5.4-nano', totalInputTokens: 1263, totalOutputTokens: 669 },
          { connectionCategory: 2, modelId: 'gpt-5.3-codex', totalInputTokens: 0, totalOutputTokens: 0 }
        ] : [],
        jobs: isSpecialPR ? [
            {
                jobId: pullRequestId === 42 ? 'job-123' : '72bc4447-4fa5-4dc2-b869-bb80e4e980a7',
                status: 'completed',
                submittedAt: new Date(Date.now() - 3600000).toISOString(),
            totalInputTokens: 51355,
            totalOutputTokens: 4658,
                tokenBreakdown: [
              { connectionCategory: 1, modelId: 'gpt-5.4-mini', totalInputTokens: 28775, totalOutputTokens: 1616 },
              { connectionCategory: 5, modelId: 'gpt-5.4-nano', totalInputTokens: 21317, totalOutputTokens: 2373 },
              { connectionCategory: 4, modelId: 'gpt-5.4-nano', totalInputTokens: 1263, totalOutputTokens: 669 },
              { connectionCategory: 2, modelId: 'gpt-5.3-codex', totalInputTokens: 0, totalOutputTokens: 0 }
                ]
            }
        ] : []
    })
  })
]
