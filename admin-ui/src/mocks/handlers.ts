// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { http, HttpResponse, delay } from 'msw'
import protocolMockData from '../../mock/data/protocol_response_1.json'
import { API_BASE_URL } from '@/services/apiBase'

const base = API_BASE_URL

let jobTick = 0

let crawlConfigs = [
  {
    id: 'config-1',
    clientId: '1',
    organizationScopeId: 'scope-1',
    organizationUrl: 'https://dev.azure.com/meister-propr',
    projectId: 'Meister-ProPR',
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
    organizationUrl: 'https://dev.azure.com/cloud-native',
    projectId: 'Infrastructure',
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
    organizationUrl: 'https://dev.azure.com/meister-propr',
    projectId: 'Sandbox',
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
      organizationUrl: 'https://dev.azure.com/meister-propr',
      projectId: 'Meister-ProPR',
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
      organizationUrl: 'https://dev.azure.com/meister-propr',
      projectId: 'Meister-ProPR',
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
      organizationUrl: 'https://dev.azure.com/meister-propr',
      projectId: 'Sandbox',
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
  http.post(`${base}/auth/login`, async () => {
    await delay(500)
    // The dummy token contains a base64 payload with both role and username claims.
    return HttpResponse.json({ 
      accessToken: 'dummyHeader.eyJnbG9iYWxfcm9sZSI6IkFkbWluIiwidW5pcXVlX25hbWUiOiJtb2NrLmFkbWluIn0=.dummySignature',
      refreshToken: 'mock-refresh'
    })
  }),

  http.post(`${base}/auth/refresh`, async () => {
    return HttpResponse.json({ accessToken: 'dummyHeader.eyJnbG9iYWxfcm9sZSI6IkFkbWluIiwidW5pcXVlX25hbWUiOiJtb2NrLmFkbWluIn0=.dummySignature' })
  }),

  http.get(`${base}/auth/me`, async () => {
    return HttpResponse.json({ globalRole: 'Admin', clientRoles: { '1': 1, '2': 1 } })
  }),

  http.get(`${base}/clients`, async () => {
    await delay(300)
    return HttpResponse.json([
      { id: '1', displayName: 'Acme Corp', isActive: true, hasAdoCredentials: true, createdAt: new Date().toISOString(), recentUsageTokens: 14520 },
      { id: '2', displayName: 'Globex Inc', isActive: false, hasAdoCredentials: false, createdAt: new Date().toISOString(), recentUsageTokens: 0 },
      { id: '3', displayName: 'Umbrella Corp', isActive: true, hasAdoCredentials: true, createdAt: new Date().toISOString(), recentUsageTokens: 89300 }
    ])
  }),

  http.get(`${base}/clients/:id`, async ({ params }) => {
    await delay(300)
    return HttpResponse.json({
        id: params.id, 
        displayName: 'Mocked Client ' + params.id, 
        isActive: true, 
        hasAdoCredentials: true, 
        createdAt: new Date().toISOString(), 
        recentUsageTokens: 14520,
        reviewerId: '0000-1111-2222-3333'
    })
  }),
  
  http.patch(`${base}/clients/:id`, async ({ request }) => {
    await delay(300)
    const body = await request.json() as any
    return HttpResponse.json({
        id: '1', displayName: body.displayName ?? 'Mocked Client', isActive: body.isActive ?? true, hasAdoCredentials: true, createdAt: new Date().toISOString(), recentUsageTokens: 14520
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
      organizationUrl: scope?.organizationUrl ?? null,
      projectId: body.projectId ?? null,
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
          projectId: 'proj-x',
          repositoryId: 'backend-service',
          pullRequestId: 42,
          organizationUrl: 'https://dev.azure.com/acme',
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
          projectId: 'proj-y',
          repositoryId: 'frontend-app',
          pullRequestId: 89,
          organizationUrl: 'https://dev.azure.com/acme',
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
          projectId: 'proj-z',
          repositoryId: 'infrastructure',
          pullRequestId: 12,
          organizationUrl: 'https://dev.azure.com/acme',
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

    const availableFilters = getCrawlFilters(body.organizationScopeId, body.projectId)
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
      organizationUrl: scope?.organizationUrl ?? body.organizationUrl,
      projectId: body.projectId,
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
    const availableFilters = getCrawlFilters(existingConfig.organizationScopeId, existingConfig.projectId)
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
    const orgUrl = url.searchParams.get('orgUrl')
    const project = url.searchParams.get('project')
    const repositoryId = url.searchParams.get('repositoryId')
    const pullRequestId = Number(url.searchParams.get('pullRequestId'))
    const clientId = params.clientId as string

    // Mock data for PR #81 (as seen in user screenshot) or default
    const isSpecialPR = pullRequestId === 81 || pullRequestId === 42

    return HttpResponse.json({
        clientId,
        organizationUrl: orgUrl || 'https://dev.azure.com/meister-propr',
        projectId: project || 'Meister-ProPR',
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
