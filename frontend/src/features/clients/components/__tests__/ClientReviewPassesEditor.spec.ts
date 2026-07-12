// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { mount } from '@vue/test-utils'
import { describe, expect, it } from 'vitest'
import type { AiConnectionDto } from '@/services/aiConnectionsService'
import type { components } from '@/types'
import ClientReviewPassesEditor from '../ClientReviewPassesEditor.vue'

type ReviewPassEntry = components['schemas']['ReviewPassEntry']

// One connection carrying three chat-capable configured models.
const connections = [
    {
        id: 'conn-1',
        displayName: 'Azure',
        configuredModels: [
            { id: 'm1', displayName: 'Model One', supportsChat: true },
            { id: 'm2', displayName: 'Model Two', supportsChat: true },
            { id: 'm3', displayName: 'Model Three', supportsChat: true },
        ],
    },
] as unknown as AiConnectionDto[]

function modelOptionValues(wrapper: ReturnType<typeof mount>, rowIndex: number): string[] {
    const selects = wrapper.findAll('[data-testid="review-pass-model"]')
    return selects[rowIndex].findAll('option').map(option => option.attributes('value') ?? '')
}

function lastEmittedPasses(wrapper: ReturnType<typeof mount>): ReviewPassEntry[] {
    const emissions = wrapper.emitted('update:modelValue')
    expect(emissions).toBeTruthy()
    return emissions![emissions!.length - 1][0] as ReviewPassEntry[]
}

describe('ClientReviewPassesEditor model greying', () => {
    it("excludes a model chosen by another row while keeping the row's own selection", () => {
        const modelValue: ReviewPassEntry[] = [
            { ordinal: 0, configuredModelId: 'm1' },
            { ordinal: 1, configuredModelId: 'm2' },
        ]

        const wrapper = mount(ClientReviewPassesEditor, {
            props: { modelValue, connections },
        })

        // Row 0 keeps its own m1 and offers the free m3, but never m2 (taken by row 1).
        const row0 = modelOptionValues(wrapper, 0)
        expect(row0).toContain('m1')
        expect(row0).toContain('m3')
        expect(row0).not.toContain('m2')

        // Row 1 keeps its own m2 and offers the free m3, but never m1 (taken by row 0).
        const row1 = modelOptionValues(wrapper, 1)
        expect(row1).toContain('m2')
        expect(row1).toContain('m3')
        expect(row1).not.toContain('m1')
    })

    it('excludes a model only from another row under the SAME lens (tuple distinctness)', () => {
        // Both rows carry the security lens: the model bound by row 0 is taken from row 1's options.
        const sameLens: ReviewPassEntry[] = [
            { ordinal: 0, configuredModelId: 'm1', lens: 'security' },
            { ordinal: 1, configuredModelId: 'm2', lens: 'security' },
        ]
        const sameLensWrapper = mount(ClientReviewPassesEditor, { props: { modelValue: sameLens, connections } })
        expect(modelOptionValues(sameLensWrapper, 1)).not.toContain('m1')

        // Row 0 is an ordinary pass and row 1 a security pass: the same model may be bound under both lenses, so
        // row 1 still offers m1 even though row 0 uses it. This is the dogfood [resample, security] shape.
        const differentLens: ReviewPassEntry[] = [
            { ordinal: 0, configuredModelId: 'm1' },
            { ordinal: 1, configuredModelId: 'm2', lens: 'security' },
        ]
        const differentLensWrapper = mount(ClientReviewPassesEditor, { props: { modelValue: differentLens, connections } })
        expect(modelOptionValues(differentLensWrapper, 1)).toContain('m1')
    })
})

describe('ClientReviewPassesEditor lens selector', () => {
    it('offers None, Security, and ProRV lens options per row', () => {
        const wrapper = mount(ClientReviewPassesEditor, {
            props: { modelValue: [{ ordinal: 0, configuredModelId: 'm1' }] as ReviewPassEntry[], connections },
        })

        const lensOptions = wrapper
            .find('[data-testid="review-pass-lens"]')
            .findAll('option')
            .map(option => option.attributes('value') ?? '')
        expect(lensOptions).toEqual(['', 'security', 'prorv'])
    })

    it('emits the chosen lens on the entry', async () => {
        const wrapper = mount(ClientReviewPassesEditor, {
            props: { modelValue: [{ ordinal: 0, configuredModelId: 'm1' }] as ReviewPassEntry[], connections },
        })

        await wrapper.find('[data-testid="review-pass-lens"]').setValue('security')

        const emitted = lastEmittedPasses(wrapper)
        expect(emitted).toEqual([{ ordinal: 0, configuredModelId: 'm1', lens: 'security', scope: null, shadow: false }])
    })

    it('emits a null lens when a pass is switched back to None', async () => {
        const wrapper = mount(ClientReviewPassesEditor, {
            props: {
                modelValue: [{ ordinal: 0, configuredModelId: 'm1', lens: 'security' }] as ReviewPassEntry[],
                connections,
            },
        })

        await wrapper.find('[data-testid="review-pass-lens"]').setValue('')

        expect(lastEmittedPasses(wrapper)).toEqual([{ ordinal: 0, configuredModelId: 'm1', lens: null, scope: null, shadow: false }])
    })

    it('hydrates the lens from the persisted value', () => {
        const wrapper = mount(ClientReviewPassesEditor, {
            props: {
                modelValue: [{ ordinal: 0, configuredModelId: 'm1', lens: 'security' }] as ReviewPassEntry[],
                connections,
            },
        })

        expect((wrapper.find('[data-testid="review-pass-lens"]').element as HTMLSelectElement).value).toBe('security')
    })
})

describe('ClientReviewPassesEditor scope and shadow', () => {
    it('offers Per-file and PR-wide scope options per row', () => {
        const wrapper = mount(ClientReviewPassesEditor, {
            props: { modelValue: [{ ordinal: 0, configuredModelId: 'm1' }] as ReviewPassEntry[], connections },
        })

        const scopeOptions = wrapper
            .find('[data-testid="review-pass-scope"]')
            .findAll('option')
            .map(option => option.attributes('value') ?? '')
        expect(scopeOptions).toEqual(['', 'pr_wide'])
    })

    it('emits pr_wide scope and the shadow flag on the entry', async () => {
        const wrapper = mount(ClientReviewPassesEditor, {
            props: { modelValue: [{ ordinal: 0, configuredModelId: 'm1' }] as ReviewPassEntry[], connections },
        })

        await wrapper.find('[data-testid="review-pass-scope"]').setValue('pr_wide')
        await wrapper.find('[data-testid="review-pass-shadow"]').setValue(true)

        expect(lastEmittedPasses(wrapper)).toEqual([
            { ordinal: 0, configuredModelId: 'm1', lens: null, scope: 'pr_wide', shadow: true },
        ])
    })

    it('emits a null scope when a pass is switched back to Per-file', async () => {
        const wrapper = mount(ClientReviewPassesEditor, {
            props: {
                modelValue: [{ ordinal: 0, configuredModelId: 'm1', scope: 'pr_wide' }] as ReviewPassEntry[],
                connections,
            },
        })

        await wrapper.find('[data-testid="review-pass-scope"]').setValue('')

        expect(lastEmittedPasses(wrapper)).toEqual([{ ordinal: 0, configuredModelId: 'm1', lens: null, scope: null, shadow: false }])
    })

    it('hydrates scope and shadow from the persisted value', () => {
        const wrapper = mount(ClientReviewPassesEditor, {
            props: {
                modelValue: [{ ordinal: 0, configuredModelId: 'm1', scope: 'pr_wide', shadow: true }] as ReviewPassEntry[],
                connections,
            },
        })

        expect((wrapper.find('[data-testid="review-pass-scope"]').element as HTMLSelectElement).value).toBe('pr_wide')
        expect((wrapper.find('[data-testid="review-pass-shadow"]').element as HTMLInputElement).checked).toBe(true)
    })
})
