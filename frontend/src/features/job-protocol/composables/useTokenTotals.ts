// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { computed } from 'vue'
import type { Ref } from 'vue'
import type { JobDetail, ReviewProtocolPass, TokenBreakdownEntry } from '../types'

/**
 * Derives the aggregate token totals from the loaded protocol passes and the per-tier breakdown shown on the
 * Tokens tab from the job detail. Pure computeds over the two input refs, without internal state.
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

    const tokenBreakdown = computed<TokenBreakdownEntry[]>(() => {
        // The job records every model call under the tier, logical model and model that answered it, with the cost
        // of that row. A protocol pass also carries calls of other tiers, for example triage and verification, so
        // grouping passes by their own tier puts those tokens under the wrong row.
        const persisted = (jobDetail.value?.tokenBreakdown ?? [])
            .filter(entry => (entry.totalInputTokens ?? 0) > 0 || (entry.totalOutputTokens ?? 0) > 0)
            .map<TokenBreakdownEntry>(entry => ({
                connectionCategory: entry.connectionCategory ?? null,
                modelId: entry.modelId ?? null,
                logicalModelName: entry.logicalModelName ?? null,
                totalInputTokens: entry.totalInputTokens ?? 0,
                totalOutputTokens: entry.totalOutputTokens ?? 0,
                totalCachedInputTokens: entry.totalCachedInputTokens ?? 0,
                estimatedCostUsd: entry.estimatedCostUsd ?? null,
                costIsApproximate: Boolean(entry.costIsApproximate),
            }))
        if (persisted.length > 0) {
            return sortByTokens(persisted)
        }

        // Jobs from before the job recorded a breakdown fall back to the protocol passes, which carry no cost.
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

        return sortByTokens(Array.from(grouped.values()))
    })

    const breakdownConsistent = computed(() => {
        if ((jobDetail.value?.tokenBreakdown ?? []).length > 0) {
            return jobDetail.value?.breakdownConsistent ?? null
        }

        if (tokenBreakdown.value.length === 0) {
            return null
        }

        const breakdownInput = tokenBreakdown.value.reduce((sum, entry) => sum + entry.totalInputTokens, 0)
        const breakdownOutput = tokenBreakdown.value.reduce((sum, entry) => sum + entry.totalOutputTokens, 0)
        return breakdownInput === totalInputTokens.value && breakdownOutput === totalOutputTokens.value
    })

    return {
        totalInputTokens,
        totalOutputTokens,
        totalCachedInputTokens,
        totalEffectiveInputTokens,
        tokenBreakdown,
        breakdownConsistent,
    }
}

function sortByTokens(entries: TokenBreakdownEntry[]): TokenBreakdownEntry[] {
    return entries.sort((left, right) => {
        const leftTotal = left.totalInputTokens + left.totalOutputTokens
        const rightTotal = right.totalInputTokens + right.totalOutputTokens
        if (leftTotal !== rightTotal) {
            return rightTotal - leftTotal
        }

        const leftKey = `${left.connectionCategory ?? ''}|${left.logicalModelName ?? ''}|${left.modelId ?? ''}`
        const rightKey = `${right.connectionCategory ?? ''}|${right.logicalModelName ?? ''}|${right.modelId ?? ''}`
        return leftKey.localeCompare(rightKey)
    })
}
