// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { beforeEach, describe, expect, it, vi } from 'vitest'
import type {
  ProCursorKnowledgeSourceRequest,
  ProCursorRefreshResponse,
} from '@/services/proCursorService'
import {
  ProCursorNotConfiguredError,
  createProCursorSource,
  getProCursorTopSources,
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
  getApiErrorMessage: (error: unknown, fallback: string) => {
    if (error && typeof error === 'object') {
      const apiError = error as { message?: string; error?: string; detail?: string; title?: string }
      return apiError.message || apiError.error || apiError.detail || apiError.title || fallback
    }

    return fallback
  },
}))

describe('proCursorService', () => {
  beforeEach(() => {
    vi.clearAllMocks()
  })

  it('creates a ProCursor source with the guided-selection payload', async () => {
    const request = {
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

    const result = await createProCursorSource('client-1', request as Parameters<typeof createProCursorSource>[1])

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

  it('reports the answer of an installation without ProCursor as its own error', async () => {
    getMock.mockResolvedValue({
      data: undefined,
      error: {
        type: 'https://tools.ietf.org/html/rfc9110#section-15.5.5',
        title: 'ProCursor is not configured in this installation.',
        status: 404,
        code: 'procursor_not_configured',
      },
      response: { ok: false, status: 404 },
    })

    await expect(listProCursorSources('client-1')).rejects.toBeInstanceOf(ProCursorNotConfiguredError)
    await expect(getProCursorTopSources('client-1', '30d', 5)).rejects.toBeInstanceOf(ProCursorNotConfiguredError)
  })

  it('reports a 404 without the code as a failed request', async () => {
    getMock.mockResolvedValue({
      data: undefined,
      error: { title: 'Not Found', status: 404 },
      response: { ok: false, status: 404 },
    })

    const failure = getProCursorTopSources('client-1', '30d', 5)

    await expect(failure).rejects.not.toBeInstanceOf(ProCursorNotConfiguredError)
    await expect(failure).rejects.toThrow('Not Found')
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
    const refreshResponse = {
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
