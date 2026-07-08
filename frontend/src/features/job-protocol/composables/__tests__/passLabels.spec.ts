// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { describe, expect, it } from 'vitest'
import { originLabel, passKindLabel } from '../passLabels'

describe('originLabel', () => {
    it('renders "Pass N" for a numbered multi-pass union finding', () => {
        expect(originLabel('MultiPassUnion', 2)).toBe('Pass 2')
    })

    it('appends the specialist lens as "Pass N · Security"', () => {
        expect(originLabel('MultiPassUnion', 3, 'security')).toBe('Pass 3 · Security')
    })

    it('falls back to a generic label when the union pass carries no index', () => {
        expect(originLabel('MultiPassUnion', null)).toBe('Additional pass')
    })

    it('renders the lens even without an index', () => {
        expect(originLabel('MultiPassUnion', null, 'security')).toBe('Additional pass · Security')
    })

    it('maps the baseline kind to its label regardless of lens', () => {
        expect(originLabel('Baseline')).toBe('Initial review')
    })

    it('returns null for an unknown origin so no badge is rendered', () => {
        expect(originLabel(null)).toBeNull()
        expect(originLabel('Nonsense')).toBeNull()
        // Legacy rows carrying the retired raw pass kind fall through to no badge.
        expect(originLabel('ProRVAugmentation')).toBeNull()
    })
})

describe('passKindLabel', () => {
    it('derives "Pass N" for a multi-pass union pass from its reason', () => {
        expect(passKindLabel('MultiPassUnion', null, 'multi-pass union security-model pass #2')).toBe('Pass 2')
    })

    it('labels the baseline pass', () => {
        expect(passKindLabel('Baseline', null)).toBe('Initial review')
    })
})
