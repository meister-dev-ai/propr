// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { computed, ref, type ComputedRef, type Ref } from 'vue'
import { createAdminClient, getApiErrorMessage } from '@/services/api'
import { getActiveRuntime } from '@/app/runtime/runtimeContext'
import type { components } from '@/types'

export type RetainedThread = components['schemas']['RetainedThreadDto']
export type RetainedComment = components['schemas']['RetainedCommentDto']
export type RetainedFile = components['schemas']['RetainedFileDto']
export type RetainedFileDiff = components['schemas']['RetainedFileDiffDto']

/**
 * The diff renderer (JobProtocolDiffViewer) consumes the richer FileDiffDto shape that
 * carries an `availability` discriminator. The retained-archive endpoint returns the
 * leaner RetainedFileDiffDto, so the composable adapts one to the other rather than
 * forcing the viewer to learn a second contract.
 */
export type ViewerFileDiff = components['schemas']['FileDiffDto']

/** Identifies a single retained pull request. The owning connection is resolved server-side. */
export interface RetainedPrIdentity {
  clientId: string
  /**
   * Provider scope path as carried on the PR-review route. Retained reads no longer use it (the
   * server resolves the owning connection from the retained data itself); kept only because the
   * view still threads it through.
   */
  providerScopePath?: string
  repositoryId: string
  pullRequestId: number
}

export interface UseRetainedPrData {
  /** True while any retained-data request is in flight. */
  loading: Ref<boolean>
  /** Non-null when a request failed unexpectedly (never set for "no data" / disabled retention). */
  error: Ref<string | null>
  /** Retained discussion threads (human + AI comments), in provider order. */
  threads: Ref<RetainedThread[]>
  /** Retained changed files (newest revision per path, no diff text). */
  files: Ref<RetainedFile[]>
  /**
   * True when retention yielded nothing to show: retention is disabled or the archive is empty.
   * The view renders a calm empty state.
   */
  empty: ComputedRef<boolean>
  /** Count of comments anchored to a given file path (PR-level threads are excluded). */
  commentCountForFile: (filePath: string) => number
  /** Count of threads anchored to a given file path. */
  threadCountForFile: (filePath: string) => number
  /** Threads not anchored to any file (pull-request-level discussion). */
  prLevelThreads: ComputedRef<RetainedThread[]>
  /** Loads threads + file list for the identity. Safe to call repeatedly. */
  load: () => Promise<void>
  /** Lazily fetches and adapts a single file's stored diff. Returns null when none is retained. */
  loadFileDiff: (filePath: string, revisionKey?: string | null) => Promise<ViewerFileDiff | null>
}

function getClient() {
  return createAdminClient({ baseUrl: getActiveRuntime().apiBaseUrl })
}

/**
 * Builds the link to a review run's execution trace (the job-protocol view), mirroring the form
 * used elsewhere in the PR review surface: `/jobs/{jobId}/protocol?clientId={clientId}`. The
 * clientId carries the tenant context the protocol view needs to resolve the run.
 */
export function buildProtocolHref(jobId: string, clientId: string): string {
  return `/jobs/${jobId}/protocol?clientId=${clientId}`
}

/** Maps a retained file diff onto the shape the shared diff viewer expects. */
function toViewerDiff(diff: RetainedFileDiff): ViewerFileDiff {
  return {
    filePath: diff.filePath,
    unifiedDiff: diff.unifiedDiff,
    changeType: diff.changeType,
    isBinary: diff.isBinary,
    originalPath: null,
    availability: diff.isBinary ? 'Binary' : 'Available',
    availabilityMessage: null,
  }
}

export function useRetainedPrData(identity: RetainedPrIdentity): UseRetainedPrData {
  const loading = ref(false)
  const error = ref<string | null>(null)
  const threads = ref<RetainedThread[]>([])
  const files = ref<RetainedFile[]>([])

  const empty = computed(() => threads.value.length === 0 && files.value.length === 0)

  const prLevelThreads = computed(() => threads.value.filter(thread => !thread.filePath))

  function threadsForFile(filePath: string): RetainedThread[] {
    return threads.value.filter(thread => thread.filePath === filePath)
  }

  function threadCountForFile(filePath: string): number {
    return threadsForFile(filePath).length
  }

  function commentCountForFile(filePath: string): number {
    return threadsForFile(filePath).reduce((total, thread) => total + (thread.comments?.length ?? 0), 0)
  }

  async function load(): Promise<void> {
    loading.value = true
    error.value = null
    threads.value = []
    files.value = []

    try {
      const query = {
        repositoryId: identity.repositoryId,
        pullRequestId: identity.pullRequestId,
      }
      const client = getClient()

      const [threadsResult, filesResult] = await Promise.all([
        client.GET('/clients/{clientId}/review-archive/pull-requests/threads', {
          params: { path: { clientId: identity.clientId }, query },
        }),
        client.GET('/clients/{clientId}/review-archive/pull-requests/files', {
          params: { path: { clientId: identity.clientId }, query },
        }),
      ])

      if (!threadsResult.response.ok) {
        throw new Error(getApiErrorMessage(threadsResult.error, 'Failed to load retained threads.'))
      }
      if (!filesResult.response.ok) {
        throw new Error(getApiErrorMessage(filesResult.error, 'Failed to load retained files.'))
      }

      threads.value = threadsResult.data ?? []
      files.value = filesResult.data ?? []
    } catch (err) {
      error.value = getApiErrorMessage(err, 'Failed to load retained pull request data.')
    } finally {
      loading.value = false
    }
  }

  async function loadFileDiff(filePath: string, revisionKey?: string | null): Promise<ViewerFileDiff | null> {
    const result = await getClient().GET('/clients/{clientId}/review-archive/pull-requests/file-diff', {
      params: {
        path: { clientId: identity.clientId },
        query: {
          repositoryId: identity.repositoryId,
          pullRequestId: identity.pullRequestId,
          filePath,
          revisionKey: revisionKey ?? undefined,
        },
      },
    })

    // A 404 here is the documented "no stored diff" case, not an error to surface.
    if (result.response.status === 404) {
      return null
    }
    if (!result.response.ok) {
      throw new Error(getApiErrorMessage(result.error, 'Failed to load the retained file diff.'))
    }
    if (!result.data) {
      return null
    }

    return toViewerDiff(result.data)
  }

  return {
    loading,
    error,
    threads,
    files,
    empty,
    commentCountForFile,
    threadCountForFile,
    prLevelThreads,
    load,
    loadFileDiff,
  }
}
