// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { describe, expect, it } from 'vitest'
import { originLabel, passKindLabel } from '@/features/job-protocol/composables/passLabels'

describe('passKindLabel', () => {
    it('maps Baseline to "Initial review" (the security lens folds in here)', () => {
        expect(passKindLabel('Baseline', 'src/foo.ts')).toBe('Initial review')
    })

    it('maps ProRVAugmentation to "ProRV verification"', () => {
        expect(passKindLabel('ProRVAugmentation', 'src/foo.ts')).toBe('ProRV verification')
        expect(passKindLabel('ProRVAugmentation', 'src/foo.ts')).not.toBe('High-risk review')
    })

    it('maps the Synthesis kind to "Synthesis"', () => {
        expect(passKindLabel('Synthesis', 'synthesis')).toBe('Synthesis')
    })

    it('derives PR-level labels from the protocol label when the kind is absent', () => {
        expect(passKindLabel(null, 'synthesis')).toBe('Synthesis')
        expect(passKindLabel(null, 'finalization')).toBe('Finalization')
        expect(passKindLabel(undefined, 'pr-wide-review')).toBe('PR-wide review')
        expect(passKindLabel(null, 'posting')).toBe('Posting')
    })

    it('falls back to "Initial review" for a file pass with no recorded kind', () => {
        expect(passKindLabel(null, 'src/foo.ts')).toBe('Initial review')
        expect(passKindLabel(undefined, undefined)).toBe('Initial review')
    })

    it('never returns a raw enum identifier', () => {
        for (const kind of ['Baseline', 'ProRVAugmentation', 'Synthesis']) {
            const label = passKindLabel(kind, 'src/foo.ts')
            expect(label).not.toContain('Augmentation')
            expect(label).not.toBe('Baseline')
        }
    })
})

describe('originLabel', () => {
    it('maps Baseline provenance to the coarse "Initial review"', () => {
        expect(originLabel('Baseline')).toBe('Initial review')
    })

    it('maps ProRVAugmentation provenance to "ProRV verification"', () => {
        expect(originLabel('ProRVAugmentation')).toBe('ProRV verification')
    })

    it('returns null for unknown / absent origin so no badge renders', () => {
        expect(originLabel(null)).toBeNull()
        expect(originLabel(undefined)).toBeNull()
        expect(originLabel('SomethingElse')).toBeNull()
    })
})
