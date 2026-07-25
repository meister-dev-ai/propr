// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { describe, expect, it } from 'vitest'
import {
  buildReviewChartData,
  getReviewSeriesKey,
  getReviewSeriesLabel,
} from '@/components/usageDashboardFormatters'
import type { ClientTokenUsageResponse, ClientTokenUsageSample } from '@/types/clientTokenUsage'

function sample(overrides: Partial<ClientTokenUsageSample>): ClientTokenUsageSample {
  return {
    modelId: 'gpt-4o',
    date: '2026-07-25',
    inputTokens: 100,
    outputTokens: 50,
    ...overrides,
  }
}

function response(samples: ClientTokenUsageSample[]): ClientTokenUsageResponse {
  return {
    clientId: 'c1',
    from: '2026-07-25',
    to: '2026-07-25',
    totalInputTokens: samples.reduce((sum, s) => sum + s.inputTokens, 0),
    totalOutputTokens: samples.reduce((sum, s) => sum + s.outputTokens, 0),
    samples,
  }
}

describe('review usage grouped by provider', () => {
  it('separates the same model reached through two providers', () => {
    const data = buildReviewChartData(
      response([
        sample({ providerKind: 'OpenAi', inputTokens: 100, outputTokens: 0 }),
        sample({ providerKind: 'LiteLlm', inputTokens: 300, outputTokens: 0 }),
      ]),
      true,
      'provider',
    )

    expect(data.datasets.map((set) => set.label).sort()).toEqual(['LiteLlm', 'OpenAi'])
    expect(data.datasets.find((set) => set.label === 'OpenAi')?.data).toEqual([100])
    expect(data.datasets.find((set) => set.label === 'LiteLlm')?.data).toEqual([300])
  })

  it('merges the same provider across models into one curve', () => {
    const data = buildReviewChartData(
      response([
        sample({ providerKind: 'OpenAi', modelId: 'gpt-4o', inputTokens: 100, outputTokens: 0 }),
        sample({ providerKind: 'OpenAi', modelId: 'gpt-4o-mini', inputTokens: 25, outputTokens: 0 }),
      ]),
      true,
      'provider',
    )

    expect(data.datasets).toHaveLength(1)
    expect(data.datasets[0].data).toEqual([125])
  })

  // Usage recorded before the provider was captured keeps its own curve rather than being attributed to a
  // provider it may not have come from.
  it('keeps unattributed usage in its own series', () => {
    expect(getReviewSeriesKey(sample({}), 'provider')).toBe('(unattributed)')
    expect(getReviewSeriesLabel(sample({}), 'provider')).toBe('Unattributed')
    expect(getReviewSeriesKey(sample({ providerKind: '  ' }), 'provider')).toBe('(unattributed)')
  })

  it('leaves the other groupings alone', () => {
    const byLogicalModel = sample({ logicalModelName: 'reviewer', providerKind: 'OpenAi' })

    expect(getReviewSeriesLabel(byLogicalModel, 'logicalModel')).toBe('reviewer')
    expect(getReviewSeriesKey(byLogicalModel, 'model')).toContain('gpt-4o')
  })
})
