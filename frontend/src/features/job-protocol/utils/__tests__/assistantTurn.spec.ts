// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { describe, expect, it } from 'vitest'
import { parseAssistantTurnRecord } from '../assistantTurn'

describe('parseAssistantTurnRecord', () => {
    it('returns null when the payload is not an assistant-turn record', () => {
        expect(parseAssistantTurnRecord(null)).toBeNull()
        expect(parseAssistantTurnRecord('some text')).toBeNull()
        // A bare final-review JSON body (no assistantText key) is not an envelope.
        expect(parseAssistantTurnRecord({ summary: 'ok', comments: [] })).toBeNull()
    })

    it('extracts text, reasoning, and structured tool calls', () => {
        const result = parseAssistantTurnRecord({
            assistantText: '',
            reasoning: 'thinking about the diff',
            toolCalls: [{ name: 'get_file_content', arguments: { path: 'src/Foo.cs', startLine: 1 } }],
        })

        expect(result).not.toBeNull()
        expect(result!.text).toBe('')
        expect(result!.reasoning).toBe('thinking about the diff')
        expect(result!.toolCalls).toHaveLength(1)
        expect(result!.toolCalls[0].name).toBe('get_file_content')
        expect(result!.toolCalls[0].arguments).toEqual({ path: 'src/Foo.cs', startLine: 1 })
        expect(result!.parsedReview).toBeNull()
    })

    it('parses the final review JSON out of the assistant text', () => {
        const reviewJson = JSON.stringify({
            summary: 'Looks good',
            comments: [{ severity: 'warning', file_path: 'src/Foo.cs', line_number: 12, message: 'Watch out' }],
        })

        const result = parseAssistantTurnRecord({ assistantText: reviewJson })

        expect(result).not.toBeNull()
        expect(result!.reasoning).toBeNull()
        expect(result!.toolCalls).toHaveLength(0)
        expect(result!.parsedReview?.summary).toBe('Looks good')
        expect(result!.parsedReview?.comments).toHaveLength(1)
        expect(result!.parsedReview?.comments?.[0].message).toBe('Watch out')
    })

    it('omits reasoning when it is empty or absent', () => {
        expect(parseAssistantTurnRecord({ assistantText: 'x', reasoning: '' })!.reasoning).toBeNull()
        expect(parseAssistantTurnRecord({ assistantText: 'x' })!.reasoning).toBeNull()
    })

    it('falls back to raw text when the assistant text is not valid JSON', () => {
        const result = parseAssistantTurnRecord({ assistantText: 'not json {' })
        expect(result!.parsedReview).toBeNull()
        expect(result!.text).toBe('not json {')
    })

    it('does not treat non-review JSON objects as a parsed review', () => {
        // A structured but non-review body (e.g. a synthesis output) is valid JSON, but has no review-shaped
        // field, so it must not be surfaced as parsedReview.
        const synthesisJson = JSON.stringify({ synthesis: 'merged findings', groups: 3 })

        const result = parseAssistantTurnRecord({ assistantText: synthesisJson })

        expect(result).not.toBeNull()
        expect(result!.parsedReview).toBeNull()
        expect(result!.text).toBe(synthesisJson)
    })

    it('treats a comments-only JSON body as a parsed review', () => {
        const reviewJson = JSON.stringify({
            comments: [{ severity: 'warning', file_path: 'src/Foo.cs', line_number: 3, message: 'Careful' }],
        })

        const result = parseAssistantTurnRecord({ assistantText: reviewJson })

        expect(result!.parsedReview).not.toBeNull()
        expect(result!.parsedReview?.comments).toHaveLength(1)
    })
})
