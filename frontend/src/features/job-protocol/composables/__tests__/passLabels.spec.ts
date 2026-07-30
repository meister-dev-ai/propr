// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { describe, expect, it } from 'vitest'
import { modelLabel, modelTitle, originLabel, passKindLabel, symbolLabel } from '../passLabels'

describe('originLabel', () => {
    it('renders "Pass N" for a numbered multi-pass union finding', () => {
        expect(originLabel('MultiPassUnion', 2)).toBe('Pass 2')
    })

    it('appends the specialist lens as "Pass N · Security"', () => {
        expect(originLabel('MultiPassUnion', 3, 'security')).toBe('Pass 3 · Security')
    })

    it('renders the PR-wide scope marker as "Pass N · PR-wide"', () => {
        expect(originLabel('MultiPassUnion', 2, 'pr_wide')).toBe('Pass 2 · PR-wide')
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

describe('modelLabel', () => {
    it('prefers the configured name, because that is what an operator chooses between', () => {
        expect(modelLabel('gpt-5.4-mini', 'thrifty-reviewer')).toBe('thrifty-reviewer')
    })

    it('falls back to the remote model when no logical model is in play', () => {
        expect(modelLabel('gpt-5.4-mini', null)).toBe('gpt-5.4-mini')
    })

    it('renders no badge when nothing was recorded rather than inventing one', () => {
        expect(modelLabel(null, null)).toBeNull()
        expect(modelLabel('   ', '  ')).toBeNull()
    })
})

describe('modelTitle', () => {
    it('shows both identities, because a logical name can be repointed', () => {
        expect(modelTitle('gpt-5.4-mini', 'thrifty')).toBe('Logical model "thrifty", served by gpt-5.4-mini')
    })

    it('names the remote model on its own when there is no logical model', () => {
        expect(modelTitle('gpt-5.4-mini', null)).toBe('Model gpt-5.4-mini')
    })

    it('has nothing to say when nothing was recorded', () => {
        expect(modelTitle(null, null)).toBeNull()
    })
})

describe('symbolLabel', () => {
    it('renders a method as a call, so it reads as code', () => {
        expect(symbolLabel('Process', 'Method')).toBe('Process()')
        expect(symbolLabel('handle_refund', 'Function')).toBe('handle_refund()')
    })

    it('leaves a type or module as its plain name', () => {
        expect(symbolLabel('RefundProcessor', 'Class')).toBe('RefundProcessor')
        expect(symbolLabel('payments', 'Module')).toBe('payments')
    })

    it('renders nothing when the line was never placed in a definition', () => {
        expect(symbolLabel(null)).toBeNull()
        expect(symbolLabel('   ', 'Method')).toBeNull()
    })
})
