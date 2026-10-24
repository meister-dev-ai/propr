// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

import type { PerformancePoint } from '@/services/reviewerPerformanceService'
import { metricLabel, metricRange, percent, type PerformanceMetric } from './reviewerPerformanceRanges'

interface EvidenceMetric {
  id: PerformanceMetric
  label: string
  range: string
}

export interface EvidencePresentation {
  windowFrom: string | undefined
  windowTo: string | undefined
  metrics: EvidenceMetric[]
  unavailableReasons: { id: string; label: string }[]
}

const METRICS: readonly PerformanceMetric[] = ['precision', 'recall', 'f1']

const UNAVAILABLE_REASON_LABELS: Record<string, string> = {
  'miss-risk-assessment-unavailable':
    'Observed recall / F1 unavailable: assessment filters and groups require compatible independent human concern impact, likelihood and confidence assessments. Precision uses the selected finding evidence.',
  'sealed-risk-filter-unavailable':
    'Correctness is unavailable for this risk scope: historical whole pull request scores do not retain risk-level measurements.',
  'miss-collection-coverage-unavailable':
    'Observed recall / F1 unavailable: a complete human-thread enumeration has not been retained for the population.',
  'miss-observation-provisional':
    'Open human threads are provisional and excluded from the settled-miss premises.',
  'miss-judgement-failed':
    'Observed recall / F1 unavailable: at least one human-thread judgement failed.',
  'miss-model-attribution-unavailable':
    'Model observed recall / F1 requires compatible human-miss model attribution.',
  'no-series-observation-in-period':
    'No observations are retained for this series in the selected period.',
  'miss-type-attribution-unavailable':
    'Observed recall / F1 unavailable: settled human misses have incomplete finding-type attribution.',
  'miss-qualifier-attribution-unavailable':
    'Observed recall / F1 unavailable: settled human misses have incomplete kind attribution.',
  'miss-provider-scope-unavailable':
    'Observed recall / F1 unavailable: human misses lack compatible provider scope.',
  'publication-identity-unavailable':
    'Findings without proven publication identity receive no publication score credit.',
  'duplicate-verification-incomplete':
    'Unknown or suspected duplication retains the recorded outcome contribution. Verification coverage is incomplete.',
  'finding-classification-incomplete': 'Finding-type or kind classification is incomplete.',
  'insufficient-covered-pull-requests': 'Observed recall / F1 unavailable: too few covered PRs meet the captured minimum.',
  'coverage-below-minimum': 'Observed recall / F1 unavailable: coverage is below the captured minimum.',
}

const EXCLUSION_REASON_LABELS: Record<string, string> = {
  'model-proof-unavailable': 'Complete historical model execution proof is missing.',
  'model-proof-revision-unavailable': 'Retained candidates belong to a different or unproven original revision.',
  'model-proof-read-limit-exceeded': 'The retained model evidence exceeds the measurement read limit.',
  'model-proof-identity-unavailable': 'The retained model proof lacks a compatible source identity.',
  'model-proof-limit-exceeded': 'The retained model proof exceeds the measurement limit.',
  'model-generation-incomplete': 'This model generation did not retain a complete valid candidate set.',
  'model-input-scope-unavailable': 'The assigned model input does not prove a complete compatible file and line scope.',
  'model-execution-cutoff-unavailable': 'A model execution before the original human concern is not proven.',
  'model-reuse-context-unavailable': 'Reused review input and surrounding context are not proven unchanged.',
  'model-concern-collection-unavailable': 'Complete independent human-concern collection is missing.',
  'model-concern-source-unavailable': 'The human concern lacks compatible provider and connection identity.',
  'model-concern-anchor-unavailable': 'The original human-concern revision, file, line or time is unknown.',
  'model-concern-judgement-failed': 'Independent human-concern classification is incomplete.',
  'model-concern-resolution-unavailable': 'The native human-concern resolution is unknown.',
  'model-concern-input-limit-exceeded': 'The retained concern or model candidate set exceeds the bounded classification input.',
  'model-concern-equivalence-limit-exceeded': 'The source exceeds the bounded concern-equivalence evaluation.',
  'model-concern-ambiguous': 'The discussion does not establish one independently verified human concern.',
  'model-concern-match-unavailable': 'Matching this concern against the model candidate set is incomplete.',
  'model-candidate-identity-unavailable': 'Retained model candidate identities are ambiguous.',
  'miss-collection-coverage-unavailable': 'Complete human-thread collection is missing.',
  'miss-judgement-failed': 'A human-thread judgement failed.',
  'miss-type-attribution-unavailable': 'Human misses have incomplete finding-type attribution.',
  'miss-qualifier-attribution-unavailable': 'Human misses have incomplete kind attribution.',
  'miss-provider-scope-unavailable': 'Human misses lack compatible provider scope.',
}

export function exclusionReasonLabel(reason: string): string {
  return EXCLUSION_REASON_LABELS[reason] ?? UNAVAILABLE_REASON_LABELS[reason] ?? reason
}

export function prepareEvidence(
  point: PerformancePoint | undefined,
): EvidencePresentation | undefined {
  if (!point) return undefined

  return {
    windowFrom: point.windowFrom ?? point.date,
    windowTo: point.windowTo ?? point.date,
    metrics: METRICS.map((metric) => {
      const range = metricRange(point, metric)
      return {
        id: metric,
        label: metricLabel(metric),
        range: range ? `${percent(range.minimum)}–${percent(range.maximum)}` : 'Unavailable',
      }
    }),
    unavailableReasons: (point.unavailableReasons ?? []).map((reason) => ({
      id: reason,
      label: point.coverage && ['coverage-below-minimum', 'insufficient-covered-pull-requests'].includes(reason)
        ? reason === 'coverage-below-minimum' ? 'Coverage is below the captured percentage minimum.' : 'The covered PR count is below the captured sample minimum.'
        : point.coverage && EXCLUSION_REASON_LABELS[reason]
        ? `Excluded PRs: ${EXCLUSION_REASON_LABELS[reason]}`
        : UNAVAILABLE_REASON_LABELS[reason] ?? reason,
    })),
  }
}
