// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { describe, expect, it } from 'vitest'
import { ref } from 'vue'
import { useTraceSearch } from '@/features/job-protocol/composables/useTraceSearch'
import type { ProtocolEventDto, ReviewProtocolPass } from '@/features/job-protocol/types'

function makeEvent(overrides: Partial<ProtocolEventDto> = {}): ProtocolEventDto {
    return {
        id: 'evt-1',
        kind: 'AiCall',
        name: 'review_call',
        occurredAt: '2024-01-01T00:00:00Z',
        inputTextSample: 'some input text',
        systemPrompt: 'system prompt',
        outputSummary: 'output summary alpha beta',
        error: null,
        ...overrides,
    } as unknown as ProtocolEventDto
}

function makePass(overrides: Partial<ReviewProtocolPass> = {}): ReviewProtocolPass {
    return {
        id: 'pass-1',
        label: 'src/foo.ts',
        modelId: 'gpt-4.1',
        fileOutcome: { filePath: 'src/foo.ts' },
        events: [],
        ...overrides,
    } as unknown as ReviewProtocolPass
}

describe('useTraceSearch', () => {
    it('memoizes buildTraceSearchableRow per event object for a stable query', () => {
        const protocols = ref<ReviewProtocolPass[]>([])
        const { buildTraceSearchableRow } = useTraceSearch(protocols)
        const pass = makePass()
        const event = makeEvent({ id: 'evt-42' })

        const first = buildTraceSearchableRow(pass, event)
        const second = buildTraceSearchableRow(pass, event)

        expect(second).toBe(first)
    })

    it('does not serve a stale row when a re-fetched event object reuses the same id with new content', () => {
        // The stripped overview row and the full detail row share an event id but are
        // distinct objects; the cache must key on identity so the full row replaces it.
        const protocols = ref<ReviewProtocolPass[]>([])
        const { buildTraceSearchableRow } = useTraceSearch(protocols)
        const pass = makePass()
        const stripped = makeEvent({ id: 'evt-x', inputTextSample: null, systemPrompt: null, outputSummary: 'Event payload omitted from the overview to keep large protocol traces responsive.' })
        const full = makeEvent({ id: 'evt-x', inputTextSample: 'real input', systemPrompt: 'real system', outputSummary: 'real output summary' })

        const strippedRow = buildTraceSearchableRow(pass, stripped)
        const fullRow = buildTraceSearchableRow(pass, full)

        expect(fullRow).not.toBe(strippedRow)
        expect(strippedRow.contextSnippet).toContain('omitted')
        expect(fullRow.contextSnippet).toContain('real output summary')
    })

    it('recomputes the row after the query text changes', () => {
        const protocols = ref<ReviewProtocolPass[]>([])
        const { buildTraceSearchableRow, setTraceFilterValue } = useTraceSearch(protocols)
        const pass = makePass()
        const event = makeEvent({ id: 'evt-7' })

        const first = buildTraceSearchableRow(pass, event)
        setTraceFilterValue('queryText', 'beta')
        const second = buildTraceSearchableRow(pass, event)

        expect(second).not.toBe(first)
        expect(second.matchedField).toBe('outputSummary')
    })

    it('derives autocomplete suggestions from pass metadata without loaded events', () => {
        const protocols = ref<ReviewProtocolPass[]>([
            makePass({ id: 'p1', fileOutcome: { filePath: 'src/a.ts' }, modelId: 'model-a', events: undefined }),
            makePass({ id: 'p2', fileOutcome: { filePath: 'src/b.ts' }, modelId: 'model-b', events: undefined }),
        ] as unknown as ReviewProtocolPass[])
        const { traceSuggestions } = useTraceSearch(protocols)

        expect(traceSuggestions.value.filePaths).toEqual(['src/a.ts', 'src/b.ts'])
        expect(traceSuggestions.value.modelIds).toEqual(['model-a', 'model-b'])
    })
})
