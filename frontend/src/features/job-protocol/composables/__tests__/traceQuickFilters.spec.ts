// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { describe, expect, it } from 'vitest'
import type { ProtocolEventDto, ReviewProtocolPass } from '../../types'
import {
    deriveEventCategory,
    formatTraceChipCount,
    parseCommentRelevanceDiscardedCount,
    parseTraceChipParam,
    rowMatchesActiveChips,
    serializeTraceChips,
    traceChipDefinitions,
} from '../traceQuickFilters'
import type { TraceChipId } from '../traceQuickFilters'

function pass(overrides: Partial<ReviewProtocolPass> = {}): ReviewProtocolPass {
    return { id: 'pass-1', label: 'src/file.ts', ...overrides } as ReviewProtocolPass
}

function event(overrides: Partial<ProtocolEventDto> = {}): ProtocolEventDto {
    return { kind: 'operational', name: 'some_event', ...overrides } as ProtocolEventDto
}

function chipMatches(id: TraceChipId, p: ReviewProtocolPass, e: ProtocolEventDto): boolean {
    const definition = traceChipDefinitions.find(candidate => candidate.id === id)
    if (!definition) throw new Error(`Unknown chip ${id}`)
    return definition.matches(p, e)
}

describe('deriveEventCategory', () => {
    it('prefers a persisted category', () => {
        expect(deriveEventCategory('operational', 'anything', 'Comment-Relevance')).toBe('comment-relevance')
    })

    it('derives comment-relevance from the legacy name path when no category is persisted', () => {
        expect(deriveEventCategory('operational', 'comment_relevance_filter_output', null)).toBe('comment-relevance')
    })

    it('derives memory from kind for legacy rows', () => {
        expect(deriveEventCategory('memoryOperation', 'memory_reconsideration_completed', undefined)).toBe('memory')
    })

    it('falls back to operational for unknown rows', () => {
        expect(deriveEventCategory('operational', 'mystery_event', null)).toBe('operational')
    })
})

describe('parseCommentRelevanceDiscardedCount', () => {
    it('reads a numeric discardedCount', () => {
        expect(parseCommentRelevanceDiscardedCount('{"discardedCount":3}')).toBe(3)
    })

    it('reads a string discardedCount', () => {
        expect(parseCommentRelevanceDiscardedCount('{"discardedCount":"5"}')).toBe(5)
    })

    it('treats truncated/unparseable JSON as no discards', () => {
        expect(parseCommentRelevanceDiscardedCount('{"discardedCount":3, "keptCom')).toBe(0)
    })

    it('treats missing key as zero', () => {
        expect(parseCommentRelevanceDiscardedCount('{"keptCount":2}')).toBe(0)
    })

    it('treats null/empty as zero', () => {
        expect(parseCommentRelevanceDiscardedCount(null)).toBe(0)
        expect(parseCommentRelevanceDiscardedCount('')).toBe(0)
    })
})

describe('chip matching', () => {
    describe('Dropped by gate', () => {
        it('matches a review-finding-gate decision with disposition Drop', () => {
            expect(chipMatches('droppedByGate', pass(), event({ name: 'review_finding_gate_decision', outputSummary: '{"disposition":"Drop"}' }))).toBe(true)
        })

        it('matches a pr-wide final gate decision with disposition Drop', () => {
            expect(chipMatches('droppedByGate', pass(), event({ name: 'pr_wide_final_gate_decision', outputSummary: '{"disposition":"Drop"}' }))).toBe(true)
        })

        it('does not match a Publish disposition', () => {
            expect(chipMatches('droppedByGate', pass(), event({ name: 'review_finding_gate_decision', outputSummary: '{"disposition":"Publish"}' }))).toBe(false)
        })

        it('does not match a non-gate event with disposition Drop', () => {
            expect(chipMatches('droppedByGate', pass(), event({ name: 'some_other_event', outputSummary: '{"disposition":"Drop"}' }))).toBe(false)
        })

        it('does not match when the output summary is unparseable', () => {
            expect(chipMatches('droppedByGate', pass(), event({ name: 'review_finding_gate_decision', outputSummary: '{"disposition":"Dro' }))).toBe(false)
        })
    })

    describe('Comment-relevance discarded', () => {
        const discardNames = [
            'comment_relevance_filter_output',
            'comment_relevance_filter_degraded',
            'comment_relevance_evaluator_degraded',
            'comment_relevance_filter_selection_fallback',
        ]

        it.each(discardNames)('matches %s when discardedCount > 0', name => {
            expect(chipMatches('commentRelevanceDiscarded', pass(), event({ name, outputSummary: '{"discardedCount":2}' }))).toBe(true)
        })

        it('does not match when discardedCount is 0', () => {
            expect(chipMatches('commentRelevanceDiscarded', pass(), event({ name: 'comment_relevance_filter_output', outputSummary: '{"discardedCount":0}' }))).toBe(false)
        })

        it('excludes the ai_call_comment_relevance_evaluator sibling even with discards', () => {
            expect(chipMatches('commentRelevanceDiscarded', pass(), event({ kind: 'aiCall', name: 'ai_call_comment_relevance_evaluator', outputSummary: '{"discardedCount":4}' }))).toBe(false)
        })

        it('matches a legacy row by deriving the comment-relevance category from the name', () => {
            // eventCategory deliberately omitted; the category must be derived from the name.
            expect(chipMatches('commentRelevanceDiscarded', pass(), event({ name: 'comment_relevance_filter_output', eventCategory: null, outputSummary: '{"discardedCount":1}' }))).toBe(true)
        })

        it('treats a truncated output summary as no discards', () => {
            expect(chipMatches('commentRelevanceDiscarded', pass(), event({ name: 'comment_relevance_filter_output', outputSummary: '{"discardedCount":3,"discar' }))).toBe(false)
        })
    })

    describe('Memory reconsiderations', () => {
        it('matches the memory_reconsideration_completed event', () => {
            expect(chipMatches('memoryReconsiderations', pass(), event({ kind: 'memoryOperation', name: 'memory_reconsideration_completed' }))).toBe(true)
        })

        it('does not match other memory events', () => {
            expect(chipMatches('memoryReconsiderations', pass(), event({ kind: 'memoryOperation', name: 'memory_operation_failed' }))).toBe(false)
        })
    })

    describe('Errors present', () => {
        it('matches a row with a non-empty error', () => {
            expect(chipMatches('errorsPresent', pass(), event({ error: 'boom' }))).toBe(true)
        })

        it('does not match a null error', () => {
            expect(chipMatches('errorsPresent', pass(), event({ error: null }))).toBe(false)
        })

        it('does not match a whitespace-only error', () => {
            expect(chipMatches('errorsPresent', pass(), event({ error: '   ' }))).toBe(false)
        })
    })

    describe('Tool call failed', () => {
        it('matches a failed tool call', () => {
            expect(chipMatches('toolCallFailed', pass(), event({ kind: 'toolCall', toolOutcome: 'Failed' }))).toBe(true)
        })

        it('does not match a succeeded tool call', () => {
            expect(chipMatches('toolCallFailed', pass(), event({ kind: 'toolCall', toolOutcome: 'Completed' }))).toBe(false)
        })

        it('does not match a failed non-tool event', () => {
            expect(chipMatches('toolCallFailed', pass(), event({ kind: 'aiCall', toolOutcome: 'Failed' }))).toBe(false)
        })
    })
})

describe('rowMatchesActiveChips', () => {
    const droppedEvent = event({ name: 'review_finding_gate_decision', outputSummary: '{"disposition":"Drop"}' })
    const errorEvent = event({ error: 'boom' })

    it('matches everything when no chips are active', () => {
        expect(rowMatchesActiveChips(pass(), event(), new Set())).toBe(true)
    })

    it('ORs chips within the same group', () => {
        // errorsPresent and toolCallFailed are both in failure-cost.
        const active = new Set<TraceChipId>(['errorsPresent', 'toolCallFailed'])
        expect(rowMatchesActiveChips(pass(), errorEvent, active)).toBe(true)
        expect(rowMatchesActiveChips(pass(), event({ kind: 'toolCall', toolOutcome: 'Failed' }), active)).toBe(true)
        expect(rowMatchesActiveChips(pass(), event(), active)).toBe(false)
    })

    it('ANDs chips across different groups', () => {
        // droppedByGate (gate-outcome) AND errorsPresent (failure-cost).
        const active = new Set<TraceChipId>(['droppedByGate', 'errorsPresent'])
        // A drop event without an error fails the failure-cost group.
        expect(rowMatchesActiveChips(pass(), droppedEvent, active)).toBe(false)
        // A drop event that also carries an error passes both groups.
        const dropAndError = event({ name: 'review_finding_gate_decision', outputSummary: '{"disposition":"Drop"}', error: 'boom' })
        expect(rowMatchesActiveChips(pass(), dropAndError, active)).toBe(true)
    })
})

describe('URL serialization', () => {
    it('serializes active chips in definition order', () => {
        expect(serializeTraceChips(new Set<TraceChipId>(['errorsPresent', 'droppedByGate']))).toBe('droppedByGate,errorsPresent')
    })

    it('serializes an empty set to null', () => {
        expect(serializeTraceChips(new Set())).toBeNull()
    })

    it('parses a comma-separated token list, ignoring unknown and duplicate tokens', () => {
        expect(parseTraceChipParam('droppedByGate,bogus,droppedByGate,errorsPresent')).toEqual(['droppedByGate', 'errorsPresent'])
    })

    it('parses array-form query values', () => {
        expect(parseTraceChipParam(['droppedByGate', 'errorsPresent'])).toEqual(['droppedByGate', 'errorsPresent'])
    })

    it('round-trips through serialize and parse', () => {
        const active = new Set<TraceChipId>(['memoryReconsiderations', 'toolCallFailed'])
        const serialized = serializeTraceChips(active)
        expect(serialized).not.toBeNull()
        expect(new Set(parseTraceChipParam(serialized))).toEqual(active)
    })
})

describe('formatTraceChipCount', () => {
    it('formats counts below the cap directly', () => {
        expect(formatTraceChipCount(0)).toBe('0')
        expect(formatTraceChipCount(42)).toBe('42')
        expect(formatTraceChipCount(999)).toBe('999')
    })

    it('caps at 1000+', () => {
        expect(formatTraceChipCount(1000)).toBe('1000+')
        expect(formatTraceChipCount(5000)).toBe('1000+')
    })
})
