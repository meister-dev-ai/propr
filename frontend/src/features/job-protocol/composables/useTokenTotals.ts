// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { computed } from 'vue'
import type { Ref } from 'vue'
import type { JobDetail, ReviewProtocolPass, TokenBreakdownEntry } from '../types'

/**
 * Derives the aggregate token totals and the per-connection/model breakdown
 * shown on the Tokens tab from the loaded protocol passes and job detail.
 * Pure computeds over the two input refs — no internal state.
 */
export function useTokenTotals(
    protocols: Ref<ReviewProtocolPass[]>,
    jobDetail: Ref<JobDetail | null>,
) {
    const totalInputTokens = computed(() =>
        protocols.value.reduce((sum, protocol) => sum + (protocol.totalInputTokens ?? 0), 0),
    )
    const totalOutputTokens = computed(() =>
        protocols.value.reduce((sum, protocol) => sum + (protocol.totalOutputTokens ?? 0), 0),
    )
    const totalCachedInputTokens = computed(() =>
        protocols.value.reduce((sum, protocol) => sum + (protocol.totalCachedInputTokens ?? 0), 0),
    )
    const totalEffectiveInputTokens = computed(() =>
        Math.max(0, totalInputTokens.value - totalCachedInputTokens.value),
    )

    const protocolTokenBreakdown = computed<TokenBreakdownEntry[]>(() => {
        const grouped = new Map<string, TokenBreakdownEntry>()

        protocols.value.forEach(protocol => {
            const input = protocol.totalInputTokens ?? 0
            const output = protocol.totalOutputTokens ?? 0
            const cached = protocol.totalCachedInputTokens ?? 0
            if (input === 0 && output === 0) {
                return
            }

            const connectionCategory = protocol.aiConnectionCategory ?? null
            const modelId = protocol.modelId ?? null
            const key = `${String(connectionCategory ?? 'unknown')}|${modelId ?? '(default)'}`
            const existing = grouped.get(key)
            if (existing) {
                existing.totalInputTokens += input
                existing.totalOutputTokens += output
                existing.totalCachedInputTokens = (existing.totalCachedInputTokens ?? 0) + cached
                return
            }

            grouped.set(key, {
                connectionCategory,
                modelId,
                totalInputTokens: input,
                totalOutputTokens: output,
                totalCachedInputTokens: cached,
            })
        })

        // The protocol passes carry no cost; enrich the recomputed per-tier rows with the
        // USD cost persisted on ReviewJob.TokenBreakdown (computed once at review time).
        const persistedCost = new Map<string, { cost: number | null; approximate: boolean }>()
        for (const entry of jobDetail.value?.tokenBreakdown ?? []) {
            const category = entry.connectionCategory ?? null
            const model = entry.modelId ?? null
            const key = `${String(category ?? 'unknown')}|${model ?? '(default)'}`
            persistedCost.set(key, {
                cost: entry.estimatedCostUsd ?? null,
                approximate: Boolean(entry.costIsApproximate),
            })
        }
        grouped.forEach((entry, key) => {
            const match = persistedCost.get(key)
            if (match) {
                entry.estimatedCostUsd = match.cost
                entry.costIsApproximate = match.approximate
            }
        })

        return Array.from(grouped.values()).sort((left, right) => {
            const leftTotal = left.totalInputTokens + left.totalOutputTokens
            const rightTotal = right.totalInputTokens + right.totalOutputTokens
            if (leftTotal !== rightTotal) {
                return rightTotal - leftTotal
            }

            return `${left.connectionCategory ?? ''}|${left.modelId ?? ''}`.localeCompare(`${right.connectionCategory ?? ''}|${right.modelId ?? ''}`)
        })
    })

    const protocolBreakdownConsistent = computed(() => {
        if (protocolTokenBreakdown.value.length === 0) {
            return jobDetail.value?.breakdownConsistent ?? null
        }

        const breakdownInput = protocolTokenBreakdown.value.reduce((sum, entry) => sum + entry.totalInputTokens, 0)
        const breakdownOutput = protocolTokenBreakdown.value.reduce((sum, entry) => sum + entry.totalOutputTokens, 0)
        return breakdownInput === totalInputTokens.value && breakdownOutput === totalOutputTokens.value
    })

    return {
        totalInputTokens,
        totalOutputTokens,
        totalCachedInputTokens,
        totalEffectiveInputTokens,
        protocolTokenBreakdown,
        protocolBreakdownConsistent,
    }
}
