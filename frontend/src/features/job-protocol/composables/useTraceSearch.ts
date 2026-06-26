// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { computed, ref } from 'vue'
import type { Ref } from 'vue'
import type { ProtocolEventDto, ReviewProtocolPass, TraceSearchableRow } from '../types'

/**
 * Owns the trace-tab search/filter state and the row-matching logic. The
 * orchestrator reuses `matchesTraceFilters` / `buildTraceSearchableRow` when it
 * builds event-display rows and the sidebar tree, so those are returned as
 * functions closing over the filter state held here.
 */
export function useTraceSearch(protocols: Ref<ReviewProtocolPass[]>) {
    const isTraceSearchCollapsed = ref(true)
    const traceFindingsOnly = ref(false)
    const traceFilters = ref({
        queryText: '',
        filePath: '',
        modelId: '',
    })

    type TraceFilterKey = keyof typeof traceFilters.value

    const normalizedTraceFilters = computed(() => ({
        queryText: traceFilters.value.queryText.trim().toLowerCase(),
        filePath: traceFilters.value.filePath.trim().toLowerCase(),
        modelId: traceFilters.value.modelId.trim().toLowerCase(),
    }))

    const hasActiveTraceFilters = computed(() =>
        Object.values(normalizedTraceFilters.value).some(value => value.length > 0),
    )

    const traceSearchToggleLabel = computed(() => (isTraceSearchCollapsed.value ? 'Show filters' : 'Hide filters'))
    const traceSearchToggleIcon = computed(() => (isTraceSearchCollapsed.value ? 'mdi-chevron-down' : 'mdi-chevron-up'))

    function normalizeTraceFilterValue(value: unknown): string {
        if (typeof value === 'string') {
            return value
        }

        if (typeof value === 'number') {
            return String(value)
        }

        if (Array.isArray(value)) {
            return normalizeTraceFilterValue(value[0])
        }

        return ''
    }

    function traceAutocompleteValue(value: string): string | null {
        return value.trim().length > 0 ? value : null
    }

    function setTraceFilterValue(key: TraceFilterKey, value: unknown): void {
        traceFilters.value[key] = normalizeTraceFilterValue(value)
    }

    function normalizeTraceCategory(kind: string | null | undefined, name: string | null | undefined, eventCategory?: string | null): string {
        const normalizedCategory = (eventCategory ?? '').trim().toLowerCase()
        if (normalizedCategory) return normalizedCategory

        const normalizedKind = (kind ?? '').trim().toLowerCase()
        const normalizedName = (name ?? '').trim().toLowerCase()

        if (normalizedKind === 'memoryoperation') return 'memory'
        if (normalizedName.startsWith('dedup_')) return 'duplicate-suppression'
        if (normalizedName.includes('comment_relevance')) return 'comment-relevance'
        if (normalizedName.includes('verification')) return 'verification'
        if (normalizedName.includes('review_finding_gate') || normalizedName.includes('summary_reconciliation') || normalizedName.includes('repeated_judgment')) return 'review-finding-gate'
        if (normalizedName.includes('prorv')) return 'prorv-prefilter'
        if (normalizedName.includes('pr_wide')) return 'pr-wide-review'
        if (normalizedName.includes('review_strategy') || normalizedName.includes('agentic_file') || normalizedName.includes('review_agent_session') || normalizedName.includes('prompt_stage_evidence') || normalizedName.includes('review_step_skipped')) return 'review-strategy'
        if (normalizedKind === 'aicall') return 'ai-call'
        if (normalizedKind === 'toolcall') return 'tool-call'
        return 'operational'
    }

    function buildTraceSnippet(value: string | null | undefined, queryText: string): string | null {
        const normalized = value?.trim()
        if (!normalized) {
            return null
        }

        const maxLength = 220
        if (!queryText) {
            return normalized.length <= maxLength ? normalized : `${normalized.slice(0, maxLength).trim()}...`
        }

        const index = normalized.toLowerCase().indexOf(queryText)
        if (index < 0) {
            return null
        }

        const start = Math.max(0, index - 80)
        const end = Math.min(normalized.length, start + maxLength)
        let snippet = normalized.slice(start, end).trim()
        if (start > 0) snippet = `...${snippet}`
        if (end < normalized.length) snippet = `${snippet}...`
        return snippet
    }

    function firstTraceValue(...values: Array<string | null | undefined>): string | null {
        return values.find(value => !!value && value.trim().length > 0)?.trim() ?? null
    }

    function detectTraceRedaction(...values: Array<string | null | undefined>): boolean {
        const markers = ['[REDACTED]', '***REDACTED***', '<redacted>']
        return values.some(value => !!value && markers.some(marker => value.includes(marker)))
    }

    // Memoize per event so the trace-tab computeds (which walk every event of every
    // pass and call this multiple times per row) don't re-derive identical rows on each
    // reactive tick. Keyed by the event OBJECT, not its id: the same event id exists as
    // two distinct objects — the stripped `includeEvents=false` overview row and the full
    // detail row — and a re-fetched in-progress pass yields fresh objects. Object identity
    // distinguishes those (an id key would serve the stale stripped row after detail loads)
    // and lets entries GC when protocols are replaced. The row depends only on the event,
    // stable pass metadata, and the query text; file/model filtering lives in
    // matchesTraceFilters, so dropping the cache on a query change is sufficient.
    let traceRowCache = new WeakMap<ProtocolEventDto, TraceSearchableRow>()
    let traceRowCacheQuery: string | null = null

    function buildTraceSearchableRow(protocol: ReviewProtocolPass, event: ProtocolEventDto): TraceSearchableRow {
        const filters = normalizedTraceFilters.value
        if (filters.queryText !== traceRowCacheQuery) {
            traceRowCache = new WeakMap()
            traceRowCacheQuery = filters.queryText
        }

        const cached = traceRowCache.get(event)
        if (cached) {
            return cached
        }

        const candidates: Array<{ field: string; value: string | null | undefined }> = [
            { field: 'eventName', value: event.name },
            { field: 'inputTextSample', value: event.inputTextSample },
            { field: 'systemPrompt', value: event.systemPrompt },
            { field: 'outputSummary', value: event.outputSummary },
            { field: 'error', value: event.error },
        ]

        const firstVisibleField = candidates.find(candidate => !!candidate.value && candidate.value.trim().length > 0) ?? null
        const matchingField = filters.queryText
            ? candidates.find(candidate => candidate.value?.toLowerCase().includes(filters.queryText)) ?? null
            : firstVisibleField

        const matchedField = matchingField?.field ?? null
        const matchSnippet = buildTraceSnippet(matchingField?.value, filters.queryText)
        const contextSource = matchedField === 'inputTextSample'
            ? firstTraceValue(event.outputSummary, event.systemPrompt, event.error)
            : matchedField === 'systemPrompt'
                ? firstTraceValue(event.inputTextSample, event.outputSummary, event.error)
                : matchedField === 'outputSummary'
                    ? firstTraceValue(event.inputTextSample, event.systemPrompt, event.error)
                    : matchedField === 'error'
                        ? firstTraceValue(event.outputSummary, event.inputTextSample, event.systemPrompt)
                        : firstTraceValue(event.outputSummary, event.inputTextSample, event.systemPrompt, event.error)

        const row: TraceSearchableRow = {
            filePath: protocol.fileOutcome?.filePath ?? protocol.label ?? null,
            protocolLabel: protocol.label ?? null,
            eventKind: String(event.kind ?? 'unknown'),
            eventCategory: normalizeTraceCategory(String(event.kind ?? ''), event.name, event.eventCategory ?? null),
            eventName: event.name ?? 'Unknown',
            modelId: protocol.modelId ?? null,
            matchedField,
            matchSnippet,
            contextSnippet: buildTraceSnippet(contextSource, ''),
            hasLimitedMetadata: !protocol.label || !protocol.modelId,
            isRedacted: detectTraceRedaction(matchSnippet, contextSource, event.inputTextSample, event.systemPrompt, event.outputSummary, event.error),
        }

        traceRowCache.set(event, row)

        return row
    }

    function matchesTraceFilters(protocol: ReviewProtocolPass, event: ProtocolEventDto): boolean {
        const filters = normalizedTraceFilters.value
        const row = buildTraceSearchableRow(protocol, event)

        if (filters.queryText && !row.matchSnippet) return false
        if (filters.filePath && !(row.filePath ?? '').toLowerCase().includes(filters.filePath)) return false
        if (filters.modelId && !(row.modelId ?? '').toLowerCase().includes(filters.modelId)) return false
        return true
    }

    // Autocomplete options come from pass-level metadata, not loaded event bodies, so the
    // file/model filters populate immediately without forcing every pass's full trace to be
    // fetched (the trace tab loads pass detail lazily/in the background).
    const traceSuggestions = computed(() => {
        const collect = (values: Array<string | null | undefined>) => Array.from(new Set(values.filter((value): value is string => !!value && value.trim().length > 0))).sort((left, right) => left.localeCompare(right))

        return {
            filePaths: collect(protocols.value.map(protocol => protocol.fileOutcome?.filePath ?? protocol.label ?? null)),
            modelIds: collect(protocols.value.map(protocol => protocol.modelId ?? null)),
        }
    })

    function protocolHasVisibleTraceRows(protocolId: string | null | undefined): boolean {
        if (!protocolId) {
            return false
        }

        return protocols.value.some(protocol =>
            protocol.id === protocolId && (protocol.events ?? []).some(event => matchesTraceFilters(protocol, event)),
        )
    }

    function clearTraceFilters() {
        traceFilters.value = {
            queryText: '',
            filePath: '',
            modelId: '',
        }
    }

    return {
        isTraceSearchCollapsed,
        traceFindingsOnly,
        traceFilters,
        normalizedTraceFilters,
        hasActiveTraceFilters,
        traceSearchToggleLabel,
        traceSearchToggleIcon,
        traceSuggestions,
        traceAutocompleteValue,
        setTraceFilterValue,
        buildTraceSearchableRow,
        matchesTraceFilters,
        protocolHasVisibleTraceRows,
        clearTraceFilters,
    }
}
