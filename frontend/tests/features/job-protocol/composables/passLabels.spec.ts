// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { describe, expect, it } from 'vitest'
import { originLabel, passKindLabel } from '@/features/job-protocol/composables/passLabels'

describe('passKindLabel', () => {
    it('maps Baseline to "Initial review" (the security lens folds in here)', () => {
        expect(passKindLabel('Baseline', 'src/foo.ts')).toBe('Initial review')
    })

    it('falls back to the default label for the retired ProRVAugmentation kind', () => {
        // The raw kind is no longer handled; legacy file rows carrying it render as the default.
        expect(passKindLabel('ProRVAugmentation', 'src/foo.ts')).toBe('Initial review')
    })

    it('maps the Synthesis kind to "Synthesis"', () => {
        expect(passKindLabel('Synthesis', 'synthesis')).toBe('Synthesis')
    })

    it('renders a multi-pass union pass as "Pass N" using the index parsed from the reason', () => {
        expect(passKindLabel('MultiPassUnion', 'src/foo.ts', 'multi-pass union review-pass-list pass #2')).toBe('Pass 2')
        expect(passKindLabel('MultiPassUnion', 'src/foo.ts', 'multi-pass union review-pass-list pass #3')).toBe('Pass 3')
    })

    it('adding another pass yields the next "Pass N" without any code change', () => {
        const nextIndex = 4
        expect(passKindLabel('MultiPassUnion', 'src/foo.ts', `multi-pass union review-pass-list pass #${nextIndex}`)).toBe('Pass 4')
    })

    it('never surfaces the old "Second opinion" wording for a union pass', () => {
        expect(passKindLabel('MultiPassUnion', 'src/foo.ts', 'multi-pass union review-pass-list pass #2')).not.toBe('Second opinion')
    })

    it('falls back to a generic label for a union pass whose reason carries no index', () => {
        expect(passKindLabel('MultiPassUnion', 'src/foo.ts', null)).toBe('Additional pass')
        expect(passKindLabel('MultiPassUnion', 'src/foo.ts')).toBe('Additional pass')
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

    it('returns null for the retired ProRVAugmentation provenance so no badge renders', () => {
        expect(originLabel('ProRVAugmentation')).toBeNull()
    })

    it('renders a multi-pass union finding as "Pass N" using its per-finding origin index', () => {
        expect(originLabel('MultiPassUnion', 2)).toBe('Pass 2')
        expect(originLabel('MultiPassUnion', 3)).toBe('Pass 3')
        expect(originLabel('MultiPassUnion', 2)).not.toBe('Second opinion')
        expect(originLabel('MultiPassUnion', 2)).not.toBe('Additional pass')
    })

    it('falls back to the coarse "Additional pass" for a union finding with no origin index', () => {
        expect(originLabel('MultiPassUnion')).toBe('Additional pass')
        expect(originLabel('MultiPassUnion', null)).toBe('Additional pass')
        expect(originLabel('MultiPassUnion')).not.toBe('Second opinion')
    })

    it('returns null for unknown / absent origin so no badge renders', () => {
        expect(originLabel(null)).toBeNull()
        expect(originLabel(undefined)).toBeNull()
        expect(originLabel('SomethingElse')).toBeNull()
    })
})
