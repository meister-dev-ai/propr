// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import type { ProtocolEventDto, ReviewProtocolPass } from '../types'

/**
 * Pure matching logic for the one-click quick-filter chips on the execution
 * trace toolbar. Each chip narrows the visible trace rows for a single job.
 * Chips in the same group OR together; chips from different groups AND; the
 * whole chip set ANDs with the free-text/file-path/model filters and the
 * "Final findings only" toggle. Keeping these predicates pure keeps them
 * unit-testable and reusable from the trace-search composable.
 */

/** Stable identifier for a quick-filter chip, also used as its URL token. */
export type TraceChipId =
    | 'droppedByGate'
    | 'highRiskReReview'
    | 'commentRelevanceDiscarded'
    | 'memoryReconsiderations'
    | 'errorsPresent'
    | 'toolCallFailed'

/** Visual grouping for chips. Same-group chips OR; cross-group chips AND. */
export type TraceChipGroup = 'gate-outcome' | 'risk-rereview' | 'pre-gate-override' | 'failure-cost'

export interface TraceChipDefinition {
    id: TraceChipId
    label: string
    group: TraceChipGroup
    /** Whether a row in the given pass matches this chip's intent. */
    matches: (pass: ReviewProtocolPass, event: ProtocolEventDto) => boolean
}

/** Display order and copy for each group divider. */
export const traceChipGroups: ReadonlyArray<{ group: TraceChipGroup; label: string }> = [
    { group: 'gate-outcome', label: 'Gate outcome' },
    { group: 'risk-rereview', label: 'Risk & re-review' },
    { group: 'pre-gate-override', label: 'Pre-gate & override' },
    { group: 'failure-cost', label: 'Failure & cost' },
]

const finalGateDecisionNames = new Set([
    'review_finding_gate_decision',
    'pr_wide_final_gate_decision',
])

const commentRelevanceDiscardNames = new Set([
    'comment_relevance_filter_output',
    'comment_relevance_filter_degraded',
    'comment_relevance_evaluator_degraded',
    'comment_relevance_filter_selection_fallback',
])

function normalize(value: string | null | undefined): string {
    return (value ?? '').trim().toLowerCase()
}

/**
 * Re-derives the event category the same way the trace-search composable does,
 * so chips match both new rows (with a persisted `eventCategory`) and legacy
 * rows (where it must be inferred from kind/name).
 */
export function deriveEventCategory(
    kind: string | null | undefined,
    name: string | null | undefined,
    eventCategory?: string | null,
): string {
    const normalizedCategory = normalize(eventCategory)
    if (normalizedCategory) return normalizedCategory

    const normalizedKind = normalize(kind)
    const normalizedName = normalize(name)

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

/**
 * Reads the `discardedCount` from a comment-relevance output summary. The
 * `OutputSummary` is capped at ~1000 chars upstream, so a truncated or
 * otherwise unparseable payload is treated as "no discards" rather than failing
 * the chip.
 */
export function parseCommentRelevanceDiscardedCount(outputSummary: string | null | undefined): number {
    if (!outputSummary) return 0

    try {
        const parsed = JSON.parse(outputSummary) as unknown
        if (!parsed || typeof parsed !== 'object' || Array.isArray(parsed)) {
            return 0
        }

        const value = (parsed as Record<string, unknown>).discardedCount
        if (typeof value === 'number' && Number.isFinite(value)) {
            return value
        }

        if (typeof value === 'string') {
            const numeric = Number.parseInt(value, 10)
            return Number.isFinite(numeric) ? numeric : 0
        }

        return 0
    } catch {
        return 0
    }
}

function isFinalGateDropEvent(event: ProtocolEventDto): boolean {
    if (!finalGateDecisionNames.has(normalize(event.name))) {
        return false
    }

    const disposition = parseFinalGateDisposition(event.outputSummary)
    return disposition === 'drop'
}

function parseFinalGateDisposition(outputSummary: string | null | undefined): string | null {
    if (!outputSummary) return null

    try {
        const parsed = JSON.parse(outputSummary) as unknown
        if (!parsed || typeof parsed !== 'object' || Array.isArray(parsed)) {
            return null
        }

        const value = (parsed as Record<string, unknown>).disposition
        return typeof value === 'string' ? normalize(value) : null
    } catch {
        return null
    }
}

function isHighRiskReReviewPass(pass: ReviewProtocolPass): boolean {
    if (normalize(pass.passKind) === 'prorvaugmentation') {
        return true
    }

    return normalize(pass.reason).startsWith('high-risk file')
}

function isCommentRelevanceDiscardEvent(event: ProtocolEventDto): boolean {
    const category = deriveEventCategory(event.kind, event.name, event.eventCategory)
    if (category !== 'comment-relevance') {
        return false
    }

    // The sibling AI-call evaluator event shares the category but is not a
    // discard signal, so it is excluded by restricting to the operational
    // filter/evaluator events below.
    if (!commentRelevanceDiscardNames.has(normalize(event.name))) {
        return false
    }

    return parseCommentRelevanceDiscardedCount(event.outputSummary) > 0
}

export const traceChipDefinitions: ReadonlyArray<TraceChipDefinition> = [
    {
        id: 'droppedByGate',
        label: 'Dropped by gate',
        group: 'gate-outcome',
        matches: (_pass, event) => isFinalGateDropEvent(event),
    },
    {
        id: 'highRiskReReview',
        label: 'High-risk re-review',
        group: 'risk-rereview',
        matches: pass => isHighRiskReReviewPass(pass),
    },
    {
        id: 'commentRelevanceDiscarded',
        label: 'Comment-relevance discarded',
        group: 'pre-gate-override',
        matches: (_pass, event) => isCommentRelevanceDiscardEvent(event),
    },
    {
        id: 'memoryReconsiderations',
        label: 'Memory reconsiderations',
        group: 'pre-gate-override',
        matches: (_pass, event) => normalize(event.name) === 'memory_reconsideration_completed',
    },
    {
        id: 'errorsPresent',
        label: 'Errors present',
        group: 'failure-cost',
        matches: (_pass, event) => (event.error ?? '').trim().length > 0,
    },
    {
        id: 'toolCallFailed',
        label: 'Tool call failed',
        group: 'failure-cost',
        matches: (_pass, event) => normalize(event.kind) === 'toolcall' && normalize(event.toolOutcome) === 'failed',
    },
]

const chipById = new Map<TraceChipId, TraceChipDefinition>(
    traceChipDefinitions.map(definition => [definition.id, definition]),
)

const validChipIds = new Set<TraceChipId>(traceChipDefinitions.map(definition => definition.id))

/** True when a string is a known chip id. */
export function isTraceChipId(value: string): value is TraceChipId {
    return validChipIds.has(value as TraceChipId)
}

/**
 * Evaluates a row against the active chip set. Same-group chips OR; chips from
 * different groups AND. An empty active set matches everything.
 */
export function rowMatchesActiveChips(
    pass: ReviewProtocolPass,
    event: ProtocolEventDto,
    activeChipIds: ReadonlySet<TraceChipId>,
): boolean {
    if (activeChipIds.size === 0) {
        return true
    }

    const groupsWithActiveChips = new Map<TraceChipGroup, TraceChipDefinition[]>()
    for (const id of activeChipIds) {
        const definition = chipById.get(id)
        if (!definition) continue
        const existing = groupsWithActiveChips.get(definition.group) ?? []
        existing.push(definition)
        groupsWithActiveChips.set(definition.group, existing)
    }

    for (const definitions of groupsWithActiveChips.values()) {
        const groupMatches = definitions.some(definition => definition.matches(pass, event))
        if (!groupMatches) {
            return false
        }
    }

    return true
}

/** Parses a comma-separated chip token list (e.g. from the URL) into known ids, order-stable and de-duplicated. */
export function parseTraceChipParam(value: string | string[] | null | undefined): TraceChipId[] {
    const raw = Array.isArray(value) ? value.join(',') : value ?? ''
    const seen = new Set<TraceChipId>()
    const result: TraceChipId[] = []
    for (const token of raw.split(',')) {
        const trimmed = token.trim()
        if (isTraceChipId(trimmed) && !seen.has(trimmed)) {
            seen.add(trimmed)
            result.push(trimmed)
        }
    }
    return result
}

/** Serializes the active chips to a stable, definition-ordered URL token list, or null when none are active. */
export function serializeTraceChips(activeChipIds: ReadonlySet<TraceChipId>): string | null {
    const ordered = traceChipDefinitions
        .filter(definition => activeChipIds.has(definition.id))
        .map(definition => definition.id)
    return ordered.length > 0 ? ordered.join(',') : null
}

/** Display cap for chip count badges; counts at or above this render as "1000+". */
export const TRACE_CHIP_COUNT_CAP = 1000

/** Formats a raw chip match count for the badge, capping at "1000+". */
export function formatTraceChipCount(count: number): string {
    return count >= TRACE_CHIP_COUNT_CAP ? `${TRACE_CHIP_COUNT_CAP}+` : String(count)
}
