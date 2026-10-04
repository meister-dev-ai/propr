// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

import { onScopeDispose, ref, toValue, watch, type MaybeRefOrGetter } from 'vue'
import { fetchFindings, type CodeInsightFinding } from '@/services/codeInsightsAnalyticsService'
import type { RetainedPrIdentity } from './useRetainedPrData'

/** Load complete PR metadata on demand, retaining it across tab changes. */
export function usePrFindingMetadata(
    identity: MaybeRefOrGetter<RetainedPrIdentity | null>,
    enabled: MaybeRefOrGetter<boolean>,
    reviewDates: MaybeRefOrGetter<string[]>,
) {
    const findings = ref<CodeInsightFinding[]>([])
    const loading = ref(false)
    const error = ref<string | null>(null)
    let generation = 0
    let currentKey = ''
    let currentReviewDates = ''
    let loaded = false

    async function load(): Promise<void> {
        const pr = toValue(identity)
        if (!pr || !toValue(enabled) || loading.value) return
        const request = ++generation
        loading.value = true
        error.value = null
        try {
            const dates = toValue(reviewDates).filter(date => !Number.isNaN(Date.parse(date)))
                .map(date => new Date(date).toISOString().slice(0, 10)).sort()
            const today = new Date().toISOString().slice(0, 10)
            const scope = {
                clientId: pr.clientId, repositoryId: pr.repositoryId, pullRequestId: pr.pullRequestId,
                from: '1970-01-01', to: [today, ...dates].sort().at(-1)!,
            }
            const collected = new Map<string, CodeInsightFinding>()
            for (let offset = 0; ; offset += 200) {
                const page = await fetchFindings(scope, { limit: 200, offset })
                if (request !== generation) return
                const previousSize = collected.size
                for (const finding of page) collected.set(finding.id, finding)
                if (page.length < 200) break
                if (collected.size === previousSize) throw new Error('Finding metadata pagination did not advance.')
            }
            findings.value = [...collected.values()]
            loaded = true
        } catch (err) {
            if (request === generation) error.value = err instanceof Error ? err.message : 'Failed to load finding metadata.'
        } finally {
            if (request === generation) loading.value = false
        }
    }

    watch([
        () => toValue(identity),
        () => toValue(enabled),
        () => toValue(reviewDates).filter(date => !Number.isNaN(Date.parse(date)))
            .map(date => new Date(date).toISOString()).sort().join('|'),
    ], ([pr, active, dates]) => {
        const key = pr ? `${pr.clientId}|${pr.providerScopePath ?? ''}|${pr.repositoryId}|${pr.pullRequestId}` : ''
        if (key !== currentKey || dates !== currentReviewDates) {
            generation++
            if (key !== currentKey) findings.value = []
            currentKey = key
            currentReviewDates = dates
            loaded = false
            loading.value = false
            error.value = null
        }
        if (active && !loaded && !error.value) void load()
    }, { immediate: true })

    onScopeDispose(() => { generation++ })
    return { findings, loading, error, load }
}
