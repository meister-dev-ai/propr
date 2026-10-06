// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

import type { PerformancePoint } from '@/services/reviewerPerformanceService'
import { metricLabel, percent, type PerformanceMetric } from './reviewerPerformanceRanges'

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
}

export function prepareEvidence(
  point: PerformancePoint | undefined,
): EvidencePresentation | undefined {
  if (!point) return undefined

  return {
    windowFrom: point.windowFrom ?? point.date,
    windowTo: point.windowTo ?? point.date,
    metrics: METRICS.map((metric) => {
      const range = point.score?.summary?.[metric]
      return {
        id: metric,
        label: metricLabel(metric),
        range: range ? `${percent(range.minimum)}–${percent(range.maximum)}` : 'Unavailable',
      }
    }),
    unavailableReasons: (point.unavailableReasons ?? []).map((reason) => ({
      id: reason,
      label: UNAVAILABLE_REASON_LABELS[reason] ?? reason,
    })),
  }
}
