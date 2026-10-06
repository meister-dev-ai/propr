// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

import { computed, ref, shallowRef } from 'vue'
import type {
  PerformanceResponse,
  PerformanceSavedReport,
  PerformanceScope,
} from '@/services/reviewerPerformanceService'

type DisplayedEvidence =
  | { kind: 'current'; response: PerformanceResponse }
  | { kind: 'snapshot'; response: PerformanceResponse; report: PerformanceSavedReport }
type ReportDeletion = 'pending' | 'confirmed'

export function useReviewerPerformanceEvidence(initialScopes: PerformanceScope[]) {
  const submittedScopes = ref<PerformanceScope[]>(cloneScopes(initialScopes))
  const displayed = shallowRef<DisplayedEvidence>()
  const reportDeletions = ref(new Map<string, ReportDeletion>())
  const response = computed(() => displayed.value?.response)
  const saved = computed(() =>
    displayed.value?.kind === 'snapshot' ? displayed.value.report : undefined,
  )
  const frozen = computed(() => displayed.value?.kind === 'snapshot')
  const selectedDeletion = computed(() => reportDeletions.value.get(saved.value?.report?.id ?? ''))
  const deletingReport = computed(() => selectedDeletion.value === 'pending')
  const deletedReport = computed(() => selectedDeletion.value === 'confirmed')

  function submitScopes(scopes: PerformanceScope[]): void {
    for (const [index, scope] of cloneScopes(scopes).entries()) {
      submittedScopes.value[index] = scope
    }
  }

  function showCurrent(result: PerformanceResponse): void {
    displayed.value = { kind: 'current', response: result }
  }

  function showSnapshot(report: PerformanceSavedReport, result: PerformanceResponse): void {
    displayed.value = { kind: 'snapshot', report, response: result }
    submitScopes(result.query?.views ?? result.views?.map((view) => view.scope ?? {}) ?? [])
  }

  function replaceResponse(result: PerformanceResponse): void {
    displayed.value =
      displayed.value?.kind === 'snapshot'
        ? { ...displayed.value, response: result }
        : { kind: 'current', response: result }
  }

  function beginDeletion(id: string): boolean {
    if (reportDeletions.value.has(id)) return false
    reportDeletions.value.set(id, 'pending')
    return true
  }

  function confirmDeletion(id: string): void {
    reportDeletions.value.set(id, 'confirmed')
  }

  function finishDeletion(id: string): void {
    if (reportDeletions.value.get(id) === 'pending') reportDeletions.value.delete(id)
  }

  return {
    submittedScopes,
    response,
    saved,
    frozen,
    deletingReport,
    deletedReport,
    submitScopes,
    showCurrent,
    showSnapshot,
    replaceResponse,
    beginDeletion,
    confirmDeletion,
    finishDeletion,
  }
}

function cloneScopes(scopes: PerformanceScope[]): PerformanceScope[] {
  return JSON.parse(JSON.stringify(scopes)) as PerformanceScope[]
}
