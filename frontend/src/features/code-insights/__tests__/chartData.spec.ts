import { describe, expect, it } from 'vitest'

import { buildMetricChartData } from '@/features/code-insights/chartData'
import type { CodeInsightMetric, CodeInsightMetricPoint } from '@/services/codeInsightsAnalyticsService'

function metric(overrides: Partial<CodeInsightMetric> = {}): CodeInsightMetric {
  return {
    precision: 0.9,
    recall: 0.6,
    f1: 0.72,
    acceptanceRate: 0.8,
    addressed: 0,
    acknowledged: 0,
    dismissed: 0,
    falsePositive: 0,
    misses: 0,
    sampleSize: 20,
    discussed: 0,
    coveredSampleSize: 20,
    coveredTruePositives: 0,
    coveredMisses: 0,
    ...overrides,
  }
}

function point(bucketStart: string, overrides: Partial<CodeInsightMetric> = {}): CodeInsightMetricPoint {
  return { bucketStart, metric: metric(overrides) }
}

describe('buildMetricChartData', () => {
  it('leaves a gap for a bucket below the minimum sample', () => {
    const data = buildMetricChartData(
      [point('2026-06-01', { sampleSize: 2, coveredSampleSize: 2 }), point('2026-06-08')],
      (m) => m.f1,
      'F1',
      0,
      10,
    )

    expect(data.datasets[0].data).toEqual([null, 0.72])
  })

  it('measures the minimum against the sample the caller names', () => {
    // The eligibility count is supplied separately from the value, so a series can qualify on a different
    // sample than the default one. F1 uses that to qualify on the pull requests measured completely enough
    // for it; gating on the closed count would plot a value the card beside the chart withholds.
    const points = [point('2026-06-01', { sampleSize: 40, coveredSampleSize: 3 })]

    const byClosed = buildMetricChartData(points, (m) => m.f1, 'F1', 0, 10)
    const byCovered = buildMetricChartData(points, (m) => m.f1, 'F1', 0, 10, (m) => m.coveredSampleSize)

    expect(byClosed.datasets[0].data).toEqual([0.72])
    expect(byCovered.datasets[0].data).toEqual([null])
  })

  it('withholds a bucket whose coverage exceeds its closed sample', () => {
    // Coverage is a subset of the closed sample, so this payload describes nothing a read can produce. The
    // card withholds it by checking both counts; the chart takes the smaller for the same reason.
    const points = [point('2026-06-01', { sampleSize: 2, coveredSampleSize: 10 })]

    const data = buildMetricChartData(points, (m) => m.f1, 'F1', 0, 10, (m) =>
      Math.min(m.sampleSize, m.coveredSampleSize),
    )

    expect(data.datasets[0].data).toEqual([null])
  })
})
