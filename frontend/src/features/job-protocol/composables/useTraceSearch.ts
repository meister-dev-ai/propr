// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { computed, ref } from 'vue'
import type { Ref } from 'vue'
import type { ProtocolEventDto, ReviewProtocolPass, TraceSearchableRow } from '../types'
import {
    TRACE_CHIP_COUNT_CAP,
    formatTraceChipCount,
    rowMatchesActiveChips,
    traceChipDefinitions,
    traceChipGroups,
} from './traceQuickFilters'
import type { TraceChipGroup, TraceChipId } from './traceQuickFilters'

/** View-model entry for one quick-filter chip rendered in the toolbar. */
export interface TraceChipViewModel {
    id: TraceChipId
    label: string
    group: TraceChipGroup
    isActive: boolean
    /** Number of rows this chip would match against the current non-chip filter state (uncapped). */
    count: number
    /** Display label for the count badge, capped at "1000+". */
    countLabel: string
    /** A chip with zero matches against the non-chip filter state is disabled. */
    isDisabled: boolean
}

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

/**
 * Per-chip match counting for one event, shared by the `traceChipCounts`
 * computed below. Extracted so the computed itself stays a flat two-level
 * loop instead of nesting a third conditional loop inline.
 */
function accumulateChipCounts(
    protocol: ReviewProtocolPass,
    event: ProtocolEventDto,
    counts: Record<TraceChipId, number>,
    remaining: Set<TraceChipId>,
): void {
    for (const definition of traceChipDefinitions) {
        if (!remaining.has(definition.id)) {
            continue
        }

        if (definition.matches(protocol, event)) {
            counts[definition.id] += 1
            if (counts[definition.id] >= TRACE_CHIP_COUNT_CAP) {
                remaining.delete(definition.id)
            }
        }
    }
}

/**
 * Owns the trace-tab search/filter state and the row-matching logic. The
 * orchestrator reuses `matchesTraceFilters` / `buildTraceSearchableRow` when it
 * builds event-display rows and the sidebar tree, so those are returned as
 * functions closing over the filter state held here.
 */
export function useTraceSearch(protocols: Ref<ReviewProtocolPass[]>) {
    const isTraceSearchCollapsed = ref(true)
    const traceFindingsOnly = ref(false)
    const activeTraceChipIds = ref<Set<TraceChipId>>(new Set())
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

    const hasActiveTextTraceFilters = computed(() =>
        Object.values(normalizedTraceFilters.value).some(value => value.length > 0),
    )

    const hasActiveTraceChips = computed(() => activeTraceChipIds.value.size > 0)

    const hasActiveTraceFilters = computed(() =>
        hasActiveTextTraceFilters.value || hasActiveTraceChips.value,
    )

    const traceSearchToggleLabel = computed(() => (isTraceSearchCollapsed.value ? 'Show filters' : 'Hide filters'))
    const traceSearchToggleIcon = computed(() => (isTraceSearchCollapsed.value ? 'mdi-chevron-down' : 'mdi-chevron-up'))

    function setTraceFilterValue(key: TraceFilterKey, value: unknown): void {
        traceFilters.value[key] = normalizeTraceFilterValue(value)
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

        let contextSource: string | null
        if (matchedField === 'inputTextSample') {
            contextSource = firstTraceValue(event.outputSummary, event.systemPrompt, event.error)
        } else if (matchedField === 'systemPrompt') {
            contextSource = firstTraceValue(event.inputTextSample, event.outputSummary, event.error)
        } else if (matchedField === 'outputSummary') {
            contextSource = firstTraceValue(event.inputTextSample, event.systemPrompt, event.error)
        } else if (matchedField === 'error') {
            contextSource = firstTraceValue(event.outputSummary, event.inputTextSample, event.systemPrompt)
        } else {
            contextSource = firstTraceValue(event.outputSummary, event.inputTextSample, event.systemPrompt, event.error)
        }

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

    // Row-level free-text/file-path/model matching, independent of the quick
    // filter chips. The chip count badges are computed against this baseline so
    // each badge reflects "how many rows this chip alone would add".
    function matchesNonChipTraceFilters(protocol: ReviewProtocolPass, event: ProtocolEventDto): boolean {
        const filters = normalizedTraceFilters.value
        const row = buildTraceSearchableRow(protocol, event)

        if (filters.queryText && !row.matchSnippet) return false
        if (filters.filePath && !(row.filePath ?? '').toLowerCase().includes(filters.filePath)) return false
        if (filters.modelId && !(row.modelId ?? '').toLowerCase().includes(filters.modelId)) return false
        return true
    }

    function matchesTraceFilters(protocol: ReviewProtocolPass, event: ProtocolEventDto): boolean {
        if (!matchesNonChipTraceFilters(protocol, event)) return false
        if (!rowMatchesActiveChips(protocol, event, activeTraceChipIds.value)) return false
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

    // Per-chip match counts against the non-chip filter baseline. Recomputed
    // only when the loaded event data or the non-chip filters change; counting
    // stops at the display cap so very large reviews stay cheap.
    const traceChipCounts = computed<Record<TraceChipId, number>>(() => {
        const counts = Object.fromEntries(
            traceChipDefinitions.map(definition => [definition.id, 0]),
        ) as Record<TraceChipId, number>

        const remaining = new Set(traceChipDefinitions.map(definition => definition.id))

        for (const protocol of protocols.value) {
            for (const event of protocol.events ?? []) {
                if (!matchesNonChipTraceFilters(protocol, event)) {
                    continue
                }

                accumulateChipCounts(protocol, event, counts, remaining)

                if (remaining.size === 0) {
                    return counts
                }
            }
        }

        return counts
    })

    const traceChips = computed<TraceChipViewModel[]>(() =>
        traceChipDefinitions.map(definition => {
            const count = traceChipCounts.value[definition.id]
            const isActive = activeTraceChipIds.value.has(definition.id)
            return {
                id: definition.id,
                label: definition.label,
                group: definition.group,
                isActive,
                count,
                countLabel: formatTraceChipCount(count),
                // A chip with no matches against the non-chip filters is disabled,
                // unless it is already active (so it can still be toggled off).
                isDisabled: count === 0 && !isActive,
            }
        }),
    )

    function toggleTraceChip(chipId: TraceChipId): void {
        const next = new Set(activeTraceChipIds.value)
        if (next.has(chipId)) {
            next.delete(chipId)
        } else {
            const count = traceChipCounts.value[chipId] ?? 0
            // Clicking a disabled chip is a no-op.
            if (count === 0) {
                return
            }

            next.add(chipId)
        }

        activeTraceChipIds.value = next
    }

    function setActiveTraceChips(chipIds: Iterable<TraceChipId>): void {
        activeTraceChipIds.value = new Set(chipIds)
    }

    function clearTraceChips(): void {
        if (activeTraceChipIds.value.size > 0) {
            activeTraceChipIds.value = new Set()
        }
    }

    function clearTraceFilters() {
        traceFilters.value = {
            queryText: '',
            filePath: '',
            modelId: '',
        }
        clearTraceChips()
    }

    return {
        isTraceSearchCollapsed,
        traceFindingsOnly,
        activeTraceChipIds,
        traceFilters,
        normalizedTraceFilters,
        hasActiveTextTraceFilters,
        hasActiveTraceChips,
        hasActiveTraceFilters,
        traceSearchToggleLabel,
        traceSearchToggleIcon,
        traceSuggestions,
        traceAutocompleteValue,
        traceChips,
        traceChipGroups,
        setTraceFilterValue,
        buildTraceSearchableRow,
        matchesTraceFilters,
        protocolHasVisibleTraceRows,
        toggleTraceChip,
        setActiveTraceChips,
        clearTraceChips,
        clearTraceFilters,
    }
}
