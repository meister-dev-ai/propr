// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { describe, expect, it } from 'vitest'
import { ref } from 'vue'
import type { JobDetail, ReviewProtocolPass } from '../../types'
import { useTokenTotals } from '../useTokenTotals'

function jobDetail(tokenBreakdown: JobDetail['tokenBreakdown'], breakdownConsistent: boolean | null = true): JobDetail {
    return {
        aiModel: 'review-model',
        reviewTemperature: null,
        tokenBreakdown,
        breakdownConsistent,
    }
}

function pass(overrides: Partial<ReviewProtocolPass>): ReviewProtocolPass {
    return { events: [], ...overrides } as ReviewProtocolPass
}

describe('useTokenTotals', () => {
    // One tier can run several logical models on the same physical model. Each of them is its own row with its own
    // cost, so the tokens of a row and the cost next to it describe the same calls.
    it('keeps one row per tier, logical model and model with the cost of that row', () => {
        const detail = ref<JobDetail | null>(jobDetail([
            { connectionCategory: 'highEffort', modelId: 'sol', logicalModelName: 'Low Budget', totalInputTokens: 300, totalOutputTokens: 30, estimatedCostUsd: 3 },
            { connectionCategory: 'highEffort', modelId: 'sol', logicalModelName: 'High Budget', totalInputTokens: 100, totalOutputTokens: 10, estimatedCostUsd: 1 },
        ]))
        const { tokenBreakdown } = useTokenTotals(ref([]), detail)

        expect(tokenBreakdown.value.map(row => [row.logicalModelName, row.totalInputTokens, row.estimatedCostUsd])).toEqual([
            ['Low Budget', 300, 3],
            ['High Budget', 100, 1],
        ])
    })

    // Triage and verification calls are recorded under their own tier. Grouping the passes by the pass tier would
    // move those tokens into the row of the pass.
    it('shows the tiers the job recorded, not the tiers of the passes', () => {
        const protocols = ref<ReviewProtocolPass[]>([
            pass({ aiConnectionCategory: 'lowEffort', modelId: 'luna', totalInputTokens: 1_100, totalOutputTokens: 11 }),
        ])
        const detail = ref<JobDetail | null>(jobDetail([
            { connectionCategory: 'lowEffort', modelId: 'luna', totalInputTokens: 1_000, totalOutputTokens: 10, estimatedCostUsd: 0.1 },
            { connectionCategory: 'default', modelId: 'luna', totalInputTokens: 100, totalOutputTokens: 1, estimatedCostUsd: 0.01 },
        ]))
        const { tokenBreakdown } = useTokenTotals(protocols, detail)

        expect(tokenBreakdown.value.map(row => [row.connectionCategory, row.totalInputTokens])).toEqual([
            ['lowEffort', 1_000],
            ['default', 100],
        ])
    })

    it('leaves out rows without tokens', () => {
        const detail = ref<JobDetail | null>(jobDetail([
            { connectionCategory: 'default', modelId: 'luna', totalInputTokens: 0, totalOutputTokens: 0, estimatedCostUsd: 0 },
            { connectionCategory: 'lowEffort', modelId: 'luna', totalInputTokens: 10, totalOutputTokens: 1, estimatedCostUsd: 0.1 },
        ]))
        const { tokenBreakdown } = useTokenTotals(ref([]), detail)

        expect(tokenBreakdown.value).toHaveLength(1)
    })

    it('reports the consistency the server computed for the job breakdown', () => {
        const detail = ref<JobDetail | null>(jobDetail(
            [{ connectionCategory: 'lowEffort', modelId: 'luna', totalInputTokens: 10, totalOutputTokens: 1 }],
            false,
        ))
        const { breakdownConsistent } = useTokenTotals(ref([]), detail)

        expect(breakdownConsistent.value).toBe(false)
    })

    it('groups the protocol passes when the job recorded no breakdown', () => {
        const protocols = ref<ReviewProtocolPass[]>([
            pass({ aiConnectionCategory: 'lowEffort', modelId: 'luna', totalInputTokens: 10, totalOutputTokens: 1 }),
            pass({ aiConnectionCategory: 'lowEffort', modelId: 'luna', totalInputTokens: 20, totalOutputTokens: 2 }),
        ])
        const { tokenBreakdown, breakdownConsistent } = useTokenTotals(protocols, ref(jobDetail([], null)))

        expect(tokenBreakdown.value).toEqual([
            expect.objectContaining({ connectionCategory: 'lowEffort', modelId: 'luna', totalInputTokens: 30, totalOutputTokens: 3 }),
        ])
        expect(breakdownConsistent.value).toBe(true)
    })
})
