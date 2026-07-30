// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

/**
 * State behind the Reviewer Performance area: whether ProPR is right and improving, whether humans want what it
 * says, and what it failed to raise.
 *
 * An operator surface. It aggregates across everything the caller administers by default, because the question
 * is about the reviewer rather than about one codebase, and narrows by client or repository on request.
 */

import { computed, ref, shallowRef } from 'vue'
import {
  fetchCoverage,
  fetchMisses,
  fetchQuality,
  fetchRejectionReasons,
  fetchReviewerFindings,
  fetchReviewerPerformanceByGrain,
  type CodeInsightBucket,
  type CodeInsightCoverage,
  type CodeInsightDisposition,
  type CodeInsightFinding,
  type CodeInsightMiss,
  type CodeInsightQuality,
  type CodeInsightRejectionReason,
  type CodeInsightRejectionReasons,
  type CodeInsightScope,
  type CodeInsightScopedMetric,
} from '@/services/codeInsightsAnalyticsService'
import type { ReviewerPerformanceGrain } from '@/features/code-insights/components/ReviewerPerformanceByScopePanel.vue'

/** Which question is on screen. */
export type ReviewerPerformanceSection = 'correctness' | 'byScope' | 'acceptance' | 'misses' | 'coverage'

const DEFAULT_WINDOW_DAYS = 90

const EMPTY_REASONS: CodeInsightRejectionReasons = {
  reasons: [],
  unclassified: 0,
  rejections: 0,
  byConcernClass: [],
}

const EMPTY_COVERAGE: CodeInsightCoverage = {
  reviewJobs: 0,
  jobsCollected: 0,
  producedFindings: 0,
  collectedFindings: 0,
  pullRequests: 0,
  pullRequestsRetained: 0,
  clientsWithCollectionOff: 0,
  rows: [],
}

function isoDate(date: Date): string {
  return date.toISOString().slice(0, 10)
}

export function useReviewerPerformanceViewModel() {
  const section = ref<ReviewerPerformanceSection>('correctness')

  const to = ref(isoDate(new Date()))
  // A wider default window than the code-quality views use: correctness only moves when pull requests close, and
  // thirty days of closes is rarely enough to clear the sample floor.
  const from = ref(isoDate(new Date(Date.now() - DEFAULT_WINDOW_DAYS * 24 * 60 * 60 * 1000)))
  const bucket = ref<CodeInsightBucket>('week')
  const clientId = ref<string | null>(null)
  const repositoryId = ref<string | null>(null)

  const loading = ref(false)
  const error = ref<string | null>(null)

  const quality = shallowRef<CodeInsightQuality | null>(null)
  const misses = shallowRef<CodeInsightMiss[]>([])
  const byScope = shallowRef<CodeInsightScopedMetric[]>([])
  const coverage = shallowRef<CodeInsightCoverage>(EMPTY_COVERAGE)
  const coverageError = ref<string | null>(null)
  const scopeGrain = ref<ReviewerPerformanceGrain>('repository')

  const rejectionReasons = shallowRef<CodeInsightRejectionReasons>(EMPTY_REASONS)
  const rejectionReasonsError = ref<string | null>(null)

  const drill = ref<{ title: string; disposition: CodeInsightDisposition | null } | null>(null)
  const drillFindings = shallowRef<CodeInsightFinding[]>([])
  const drillLoading = ref(false)

  const scope = computed<CodeInsightScope>(() => ({
    from: from.value,
    to: to.value,
    clientId: clientId.value,
    repositoryId: repositoryId.value,
  }))

  /**
   * Whether correctness rests on enough closed pull requests to be presented as a number rather than as an
   * annotation. The single place the threshold is applied, and the threshold itself comes from the server so it
   * can be recalibrated without a release.
   */
  const hasEnoughCorrectnessSample = computed(() => {
    const current = quality.value
    if (!current) return false
    return current.correctnessTotal.sampleSize >= current.minimumSampleSize
  })

  async function load(): Promise<void> {
    loading.value = true
    error.value = null
    try {
      // Coverage loads beside these rather than with them. It describes the measurement apparatus rather than
      // the measurements, and one failed read of it must not cost the page every number on it: an installation
      // whose backend predates the endpoint would otherwise see nothing at all.
      void loadCoverage()

      // Same reasoning for the reason distribution: a backend that predates the endpoint answers 404, and a
      // shared load would take every metric on the page down with it.
      void loadRejectionReasons()

      const [loadedQuality, loadedMisses, loadedByScope] = await Promise.all([
        fetchQuality(scope.value, bucket.value),
        fetchMisses(scope.value, 50),
        fetchReviewerPerformanceByGrain(scope.value, scopeGrain.value),
      ])

      quality.value = loadedQuality
      misses.value = loadedMisses
      byScope.value = loadedByScope
    } catch (err) {
      error.value = err instanceof Error ? err.message : 'Failed to load reviewer performance.'
    } finally {
      loading.value = false
    }
  }

  /** Loads the coverage comparison, keeping its own failure inside its own section. */
  async function loadCoverage(): Promise<void> {
    coverageError.value = null
    try {
      coverage.value = await fetchCoverage(scope.value)
    } catch (err) {
      coverage.value = EMPTY_COVERAGE
      coverageError.value = err instanceof Error ? err.message : 'Failed to load the collection coverage.'
    }
  }

  /** Loads the rejection-reason distribution, keeping its own failure inside its own section. */
  async function loadRejectionReasons(): Promise<void> {
    rejectionReasonsError.value = null
    try {
      rejectionReasons.value = await fetchRejectionReasons(scope.value)
    } catch (err) {
      rejectionReasons.value = EMPTY_REASONS
      rejectionReasonsError.value =
        err instanceof Error ? err.message : 'Failed to load the rejection reasons.'
    }
  }

  /** Reloads only the grouped table, for when the grain changes without the window having moved. */
  async function loadByScope(): Promise<void> {
    try {
      byScope.value = await fetchReviewerPerformanceByGrain(scope.value, scopeGrain.value)
    } catch (err) {
      error.value = err instanceof Error ? err.message : 'Failed to load reviewer performance by scope.'
    }
  }

  async function openDrill(title: string, disposition: CodeInsightDisposition): Promise<void> {
    drill.value = { title, disposition }
    drillLoading.value = true
    drillFindings.value = []
    try {
      drillFindings.value = await fetchReviewerFindings(scope.value, { disposition, limit: 50 })
    } catch (err) {
      error.value = err instanceof Error ? err.message : 'Failed to load the findings behind this metric.'
    } finally {
      drillLoading.value = false
    }
  }

  /** Opens the findings behind one rejection reason. The reason implies its outcome, so none is sent. */
  async function openReasonDrill(title: string, rejectionReason: CodeInsightRejectionReason): Promise<void> {
    drill.value = { title, disposition: null }
    drillLoading.value = true
    drillFindings.value = []
    try {
      drillFindings.value = await fetchReviewerFindings(scope.value, { rejectionReason, limit: 50 })
    } catch (err) {
      error.value = err instanceof Error ? err.message : 'Failed to load the findings behind this metric.'
    } finally {
      drillLoading.value = false
    }
  }

  function closeDrill(): void {
    drill.value = null
    drillFindings.value = []
  }

  return {
    section,
    from,
    to,
    bucket,
    clientId,
    repositoryId,
    loading,
    error,
    quality,
    misses,
    byScope,
    coverage,
    coverageError,
    loadCoverage,
    rejectionReasons,
    rejectionReasonsError,
    loadRejectionReasons,
    scopeGrain,
    hasEnoughCorrectnessSample,
    drill,
    drillFindings,
    drillLoading,
    load,
    loadByScope,
    openDrill,
    openReasonDrill,
    closeDrill,
  }
}
