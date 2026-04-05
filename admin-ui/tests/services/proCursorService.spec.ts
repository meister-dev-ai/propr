// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { beforeEach, describe, expect, it, vi } from 'vitest'
import type {
  ProCursorKnowledgeSourceRequest,
  ProCursorRefreshResponse,
} from '@/services/proCursorService'
import {
  createProCursorSource,
  listProCursorSources,
  queueProCursorRefresh,
} from '@/services/proCursorService'

const getMock = vi.fn()
const postMock = vi.fn()
const putMock = vi.fn()
const deleteMock = vi.fn()

vi.mock('@/services/api', () => ({
  createAdminClient: () => ({
    GET: getMock,
    POST: postMock,
    PUT: putMock,
    DELETE: deleteMock,
  }),
}))

describe('proCursorService', () => {
  beforeEach(() => {
    vi.clearAllMocks()
  })

  it('creates a ProCursor source with the guided-selection payload', async () => {
    const request: ProCursorKnowledgeSourceRequest = {
      displayName: 'Platform Docs',
      sourceKind: 'repository',
      organizationUrl: 'https://dev.azure.com/example',
      projectId: 'project-a',
      repositoryId: 'repo-a',
      defaultBranch: 'main',
      rootPath: '/docs',
      symbolMode: 'auto',
      trackedBranches: [
        {
          branchName: 'main',
          refreshTriggerMode: 'branchUpdate',
          miniIndexEnabled: true,
        },
      ],
      organizationScopeId: 'scope-1',
      canonicalSourceRef: {
        provider: 'azureDevOps',
        value: 'repo-a',
      },
      sourceDisplayName: 'Repo A',
    }

    const expectedSource = {
      sourceId: 'source-1',
      displayName: 'Platform Docs',
      sourceKind: 'repository',
      organizationUrl: 'https://dev.azure.com/example',
      projectId: 'project-a',
      repositoryId: 'repo-a',
      defaultBranch: 'main',
      rootPath: '/docs',
      symbolMode: 'auto',
      status: 'enabled',
      latestSnapshot: null,
      organizationScopeId: 'scope-1',
      canonicalSourceRef: {
        provider: 'azureDevOps',
        value: 'repo-a',
      },
      sourceDisplayName: 'Repo A',
    }

    postMock.mockResolvedValue({
      data: expectedSource,
      error: undefined,
      response: { ok: true },
    })

    const result = await createProCursorSource('client-1', request)

    expect(postMock).toHaveBeenCalledWith('/admin/clients/{clientId}/procursor/sources', {
      params: { path: { clientId: 'client-1' } },
      body: request,
    })
    expect(result).toEqual(expectedSource)
  })

  it('lists ProCursor sources through the admin client', async () => {
    const expectedSources = [
      {
        sourceId: 'source-1',
        displayName: 'Platform Docs',
        sourceKind: 'repository',
        organizationUrl: 'https://dev.azure.com/example',
        projectId: 'project-a',
        repositoryId: 'repo-a',
        defaultBranch: 'main',
        rootPath: '/docs',
        symbolMode: 'auto',
        status: 'enabled',
        latestSnapshot: null,
      },
    ]

    getMock.mockResolvedValue({
      data: expectedSources,
      error: undefined,
      response: { ok: true },
    })

    const result = await listProCursorSources('client-1')

    expect(getMock).toHaveBeenCalledWith('/admin/clients/{clientId}/procursor/sources', {
      params: { path: { clientId: 'client-1' } },
    })
    expect(result).toEqual(expectedSources)
  })

  it('surfaces API error details when creating a source fails', async () => {
    postMock.mockResolvedValue({
      data: undefined,
      error: {
        detail: 'Unsupported non-git knowledge sources are rejected.',
      },
      response: { ok: false },
    })

    await expect(
      createProCursorSource('client-1', {
        displayName: 'Guided Wiki',
        sourceKind: 'adoWiki',
        organizationUrl: 'https://dev.azure.com/example',
        projectId: 'project-a',
        repositoryId: 'wiki-a',
        defaultBranch: 'main',
        symbolMode: 'text_only',
        trackedBranches: [],
        organizationScopeId: 'scope-1',
        canonicalSourceRef: {
          provider: 'azureDevOps',
          value: 'wiki-a',
        },
        sourceDisplayName: 'Wiki A',
      } as ProCursorKnowledgeSourceRequest),
    ).rejects.toThrow('Unsupported non-git knowledge sources are rejected.')
  })

  it('queues a ProCursor refresh with the default refresh payload', async () => {
    const refreshResponse: ProCursorRefreshResponse = {
      jobId: 'job-1',
      sourceId: 'source-1',
      trackedBranchId: 'branch-1',
      branchName: 'main',
      requestedCommitSha: null,
      jobKind: 'refresh',
      status: 'pending',
      queuedAt: '2026-04-03T10:00:00Z',
      startedAt: null,
      completedAt: null,
      failureReason: null,
    }

    postMock.mockResolvedValue({
      data: refreshResponse,
      error: undefined,
      response: { ok: true },
    })

    const result = await queueProCursorRefresh('client-1', 'source-1')

    expect(postMock).toHaveBeenCalledWith(
      '/admin/clients/{clientId}/procursor/sources/{sourceId}/refresh',
      {
        params: { path: { clientId: 'client-1', sourceId: 'source-1' } },
        body: { jobKind: 'refresh' },
      },
    )
    expect(result).toEqual(refreshResponse)
  })
})
