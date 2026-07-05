// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { describe, expect, it } from 'vitest'
import { parseUnionContributions, parseUnionPassIndex } from '../multiPassUnionContribution'

describe('parseUnionContributions', () => {
    it('maps per-pass catch counts and models by 1-based pass index (baseline = Pass 1)', () => {
        const output = JSON.stringify({
            perPassCatchCounts: [3, 0, 2],
            perPassModels: ['tier-baseline', 'gpt-5.4', 'codex'],
            unionCount: 5,
        })

        const map = parseUnionContributions(output)

        expect(map.get(1)).toEqual({ catchCount: 3, model: 'tier-baseline' })
        // A zero contribution is meaningful and preserved, not dropped.
        expect(map.get(2)).toEqual({ catchCount: 0, model: 'gpt-5.4' })
        expect(map.get(3)).toEqual({ catchCount: 2, model: 'codex' })
        expect(map.size).toBe(3)
    })

    it('tolerates a missing model entry by yielding a null model', () => {
        const map = parseUnionContributions(JSON.stringify({ perPassCatchCounts: [1, 4], perPassModels: ['tier'] }))

        expect(map.get(2)).toEqual({ catchCount: 4, model: null })
    })

    it('returns an empty map for missing or malformed payloads', () => {
        expect(parseUnionContributions(null).size).toBe(0)
        expect(parseUnionContributions(undefined).size).toBe(0)
        expect(parseUnionContributions('not json').size).toBe(0)
        expect(parseUnionContributions('{}').size).toBe(0)
    })
})

describe('parseUnionPassIndex', () => {
    it('extracts the 1-based index from a union resample reason', () => {
        expect(parseUnionPassIndex('multi-pass union Second opinion pass #2')).toBe(2)
        expect(parseUnionPassIndex('multi-pass union resample pass #3')).toBe(3)
    })

    it('returns null when no index is present', () => {
        expect(parseUnionPassIndex(null)).toBeNull()
        expect(parseUnionPassIndex(undefined)).toBeNull()
        expect(parseUnionPassIndex('high-risk file re-review')).toBeNull()
    })
})
