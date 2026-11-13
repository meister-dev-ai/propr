// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { mount } from '@vue/test-utils'
import { describe, expect, it } from 'vitest'
import TokenBreakdownTable from '@/components/usage/TokenBreakdownTable.vue'

describe('TokenBreakdownTable estimated cost column', () => {
  it('renders per-tier cost, marks approximate rows, and totals cost null-aware', () => {
    const wrapper = mount(TokenBreakdownTable, {
      props: {
        breakdown: [
          {
            connectionCategory: 2,
            modelId: 'gpt-4o',
            totalInputTokens: 1_000_000,
            totalOutputTokens: 500_000,
            totalCachedInputTokens: 0,
            estimatedCostUsd: 7,
            costIsApproximate: false,
          },
          {
            connectionCategory: 0,
            modelId: 'gpt-4o-mini',
            totalInputTokens: 100,
            totalOutputTokens: 50,
            totalCachedInputTokens: 0,
            estimatedCostUsd: 0.25,
            costIsApproximate: true,
          },
        ],
      },
    })

    const text = wrapper.text()
    expect(text).toContain('Est. Cost')
    expect(text).toContain('$7.00')
    // Approximate row is prefixed.
    expect(text).toContain('≈$0.25')
    // Total sums both tiers; approximate because one tier is approximate.
    expect(text).toContain('≈$7.25')
  })

  it('renders a dash for unpriced tiers and marks a mixed total approximate', () => {
    const wrapper = mount(TokenBreakdownTable, {
      props: {
        breakdown: [
          {
            connectionCategory: 2,
            modelId: 'gpt-4o',
            totalInputTokens: 1000,
            totalOutputTokens: 500,
            estimatedCostUsd: 0.5,
            costIsApproximate: false,
          },
          {
            connectionCategory: 0,
            modelId: 'gpt-4o-mini',
            totalInputTokens: 100,
            totalOutputTokens: 50,
            estimatedCostUsd: null,
            costIsApproximate: true,
          },
        ],
      },
    })

    const text = wrapper.text()
    // One priced tier and one unpriced tier: total is the priced sum, flagged approximate.
    expect(text).toContain('≈$0.50')
    // The unpriced tier shows an em dash for its cost.
    expect(text).toContain('—')
  })

  // From GPT-5.6 on, OpenAI bills the input a call writes to the prompt cache at its own rate. The uncached input is
  // what remains after the cache reads and the cache writes.
  it('shows cache writes in their own column and leaves them out of the uncached input', () => {
    const wrapper = mount(TokenBreakdownTable, {
      props: {
        breakdown: [
          {
            connectionCategory: 2,
            modelId: 'gpt-6.1-terra',
            totalInputTokens: 12_000,
            totalOutputTokens: 300,
            totalCachedInputTokens: 4_000,
            totalCacheWriteTokens: 7_000,
          },
        ],
      },
    })

    const headers = wrapper.findAll('thead th').map(cell => cell.text())
    const cells = wrapper.findAll('tbody tr')[0].findAll('td').map(cell => cell.text())
    expect(cells[headers.indexOf('Cache Write')]).toBe((7_000).toLocaleString())
    expect(cells[headers.indexOf('Uncached Input')]).toBe((1_000).toLocaleString())
  })
})
