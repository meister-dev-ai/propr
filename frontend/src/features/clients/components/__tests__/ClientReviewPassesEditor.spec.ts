// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { mount } from '@vue/test-utils'
import { describe, expect, it } from 'vitest'
import type { AiConnectionDto } from '@/services/aiConnectionsService'
import type { LogicalModelResponse } from '@/services/logicalModelsService'
import type { components } from '@/types'
import ClientReviewPassesEditor from '../ClientReviewPassesEditor.vue'

type ReviewPassEntry = components['schemas']['ReviewPassEntry']

// Two chat logical models and one embedding model — a review pass may only pick a chat model.
const logicalModels = [
    { id: 'lm1', name: 'deep', capability: 'chat' },
    { id: 'lm2', name: 'fast', capability: 'chat' },
    { id: 'lm3', name: 'embed', capability: 'embedding' },
] as unknown as LogicalModelResponse[]

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

// Add/edit happens in a modal: open it via the row's Edit button, then read/drive the fields inside it.
async function openEdit(wrapper: ReturnType<typeof mount>, rowIndex = 0) {
    await wrapper.findAll('[data-testid="review-pass-edit"]')[rowIndex].trigger('click')
}

async function save(wrapper: ReturnType<typeof mount>) {
    await wrapper.find('[data-testid="review-pass-save"]').trigger('click')
}

function optionValues(wrapper: ReturnType<typeof mount>, testId: string): string[] {
    return wrapper.find(`[data-testid="${testId}"]`).findAll('option').map(option => option.attributes('value') ?? '')
}

async function modelOptionsForRow(wrapper: ReturnType<typeof mount>, rowIndex: number): Promise<string[]> {
    await openEdit(wrapper, rowIndex)
    return optionValues(wrapper, 'review-pass-model')
}

function lastEmittedPasses(wrapper: ReturnType<typeof mount>): ReviewPassEntry[] {
    const emissions = wrapper.emitted('update:modelValue')
    expect(emissions).toBeTruthy()
    return emissions![emissions!.length - 1][0] as ReviewPassEntry[]
}

describe('ClientReviewPassesEditor model greying', () => {
    it("excludes a model chosen by another row while keeping the edited row's own selection", async () => {
        const modelValue: ReviewPassEntry[] = [
            { ordinal: 0, configuredModelId: 'm1' },
            { ordinal: 1, configuredModelId: 'm2' },
        ]

        const wrapper = mount(ClientReviewPassesEditor, {
            props: { modelValue, connections },
        })

        // Editing row 0 keeps its own m1 and offers the free m3, but never m2 (taken by row 1).
        const row0 = await modelOptionsForRow(wrapper, 0)
        expect(row0).toContain('m1')
        expect(row0).toContain('m3')
        expect(row0).not.toContain('m2')

        // Editing row 1 keeps its own m2 and offers the free m3, but never m1 (taken by row 0).
        const row1 = await modelOptionsForRow(wrapper, 1)
        expect(row1).toContain('m2')
        expect(row1).toContain('m3')
        expect(row1).not.toContain('m1')
    })

    it('excludes a model only from another row under the SAME lens (tuple distinctness)', async () => {
        // Both rows carry the security lens: the model bound by row 0 is taken from row 1's options.
        const sameLens: ReviewPassEntry[] = [
            { ordinal: 0, configuredModelId: 'm1', lens: 'security' },
            { ordinal: 1, configuredModelId: 'm2', lens: 'security' },
        ]
        const sameLensWrapper = mount(ClientReviewPassesEditor, { props: { modelValue: sameLens, connections } })
        expect(await modelOptionsForRow(sameLensWrapper, 1)).not.toContain('m1')

        // Row 0 is an ordinary pass and row 1 a security pass: the same model may be bound under both lenses, so
        // row 1 still offers m1 even though row 0 uses it. This is the dogfood [resample, security] shape.
        const differentLens: ReviewPassEntry[] = [
            { ordinal: 0, configuredModelId: 'm1' },
            { ordinal: 1, configuredModelId: 'm2', lens: 'security' },
        ]
        const differentLensWrapper = mount(ClientReviewPassesEditor, { props: { modelValue: differentLens, connections } })
        expect(await modelOptionsForRow(differentLensWrapper, 1)).toContain('m1')
    })
})

describe('ClientReviewPassesEditor lens selector', () => {
    it('offers None, Security, and ProRV lens options', async () => {
        const wrapper = mount(ClientReviewPassesEditor, {
            props: { modelValue: [{ ordinal: 0, configuredModelId: 'm1' }] as ReviewPassEntry[], connections },
        })

        await openEdit(wrapper)
        expect(optionValues(wrapper, 'review-pass-lens')).toEqual(['', 'security', 'prorv'])
    })

    it('emits the chosen lens on the entry after saving', async () => {
        const wrapper = mount(ClientReviewPassesEditor, {
            props: { modelValue: [{ ordinal: 0, configuredModelId: 'm1' }] as ReviewPassEntry[], connections },
        })

        await openEdit(wrapper)
        await wrapper.find('[data-testid="review-pass-lens"]').setValue('security')
        await save(wrapper)

        expect(lastEmittedPasses(wrapper)).toEqual([{ ordinal: 0, configuredModelId: 'm1', lens: 'security', scope: null, shadow: false, reasoningEffort: 'none' }])
    })

    it('emits a null lens when a pass is switched back to None', async () => {
        const wrapper = mount(ClientReviewPassesEditor, {
            props: {
                modelValue: [{ ordinal: 0, configuredModelId: 'm1', lens: 'security' }] as ReviewPassEntry[],
                connections,
            },
        })

        await openEdit(wrapper)
        await wrapper.find('[data-testid="review-pass-lens"]').setValue('')
        await save(wrapper)

        expect(lastEmittedPasses(wrapper)).toEqual([{ ordinal: 0, configuredModelId: 'm1', lens: null, scope: null, shadow: false, reasoningEffort: 'none' }])
    })

    it('hydrates the lens from the persisted value', async () => {
        const wrapper = mount(ClientReviewPassesEditor, {
            props: {
                modelValue: [{ ordinal: 0, configuredModelId: 'm1', lens: 'security' }] as ReviewPassEntry[],
                connections,
            },
        })

        await openEdit(wrapper)
        expect((wrapper.find('[data-testid="review-pass-lens"]').element as HTMLSelectElement).value).toBe('security')
    })
})

describe('ClientReviewPassesEditor scope and shadow', () => {
    it('offers Per-file and PR-wide scope options', async () => {
        const wrapper = mount(ClientReviewPassesEditor, {
            props: { modelValue: [{ ordinal: 0, configuredModelId: 'm1' }] as ReviewPassEntry[], connections },
        })

        await openEdit(wrapper)
        expect(optionValues(wrapper, 'review-pass-scope')).toEqual(['', 'pr_wide'])
    })

    it('emits pr_wide scope and the shadow flag on the entry after saving', async () => {
        const wrapper = mount(ClientReviewPassesEditor, {
            props: { modelValue: [{ ordinal: 0, configuredModelId: 'm1' }] as ReviewPassEntry[], connections },
        })

        await openEdit(wrapper)
        await wrapper.find('[data-testid="review-pass-scope"]').setValue('pr_wide')
        await wrapper.find('[data-testid="review-pass-shadow"]').setValue(true)
        await save(wrapper)

        expect(lastEmittedPasses(wrapper)).toEqual([
            { ordinal: 0, configuredModelId: 'm1', lens: null, scope: 'pr_wide', shadow: true, reasoningEffort: 'none' },
        ])
    })

    it('emits a null scope when a pass is switched back to Per-file', async () => {
        const wrapper = mount(ClientReviewPassesEditor, {
            props: {
                modelValue: [{ ordinal: 0, configuredModelId: 'm1', scope: 'pr_wide' }] as ReviewPassEntry[],
                connections,
            },
        })

        await openEdit(wrapper)
        await wrapper.find('[data-testid="review-pass-scope"]').setValue('')
        await save(wrapper)

        expect(lastEmittedPasses(wrapper)).toEqual([{ ordinal: 0, configuredModelId: 'm1', lens: null, scope: null, shadow: false, reasoningEffort: 'none' }])
    })

    it('hydrates scope and shadow from the persisted value', async () => {
        const wrapper = mount(ClientReviewPassesEditor, {
            props: {
                modelValue: [{ ordinal: 0, configuredModelId: 'm1', scope: 'pr_wide', shadow: true }] as ReviewPassEntry[],
                connections,
            },
        })

        await openEdit(wrapper)
        expect((wrapper.find('[data-testid="review-pass-scope"]').element as HTMLSelectElement).value).toBe('pr_wide')
        expect((wrapper.find('[data-testid="review-pass-shadow"]').element as HTMLInputElement).checked).toBe(true)
    })
})

describe('ClientReviewPassesEditor reasoning effort', () => {
    it('offers None, Low, Medium, and High reasoning options', async () => {
        const wrapper = mount(ClientReviewPassesEditor, {
            props: { modelValue: [{ ordinal: 0, configuredModelId: 'm1' }] as ReviewPassEntry[], connections },
        })

        await openEdit(wrapper)
        expect(optionValues(wrapper, 'review-pass-reasoning')).toEqual(['none', 'low', 'medium', 'high'])
    })

    it('defaults a new pass to None reasoning effort', async () => {
        const wrapper = mount(ClientReviewPassesEditor, {
            props: { modelValue: [] as ReviewPassEntry[], connections },
        })

        await wrapper.find('[data-testid="review-passes-add"]').trigger('click')
        expect((wrapper.find('[data-testid="review-pass-reasoning"]').element as HTMLSelectElement).value).toBe('none')
    })

    it('emits the chosen reasoning effort on the entry after saving', async () => {
        const wrapper = mount(ClientReviewPassesEditor, {
            props: { modelValue: [{ ordinal: 0, configuredModelId: 'm1' }] as ReviewPassEntry[], connections },
        })

        await openEdit(wrapper)
        await wrapper.find('[data-testid="review-pass-reasoning"]').setValue('high')
        await save(wrapper)

        expect(lastEmittedPasses(wrapper)).toEqual([
            { ordinal: 0, configuredModelId: 'm1', lens: null, scope: null, shadow: false, reasoningEffort: 'high' },
        ])
    })

    it('hydrates the reasoning effort from the persisted value', async () => {
        const wrapper = mount(ClientReviewPassesEditor, {
            props: {
                modelValue: [{ ordinal: 0, configuredModelId: 'm1', reasoningEffort: 'medium' }] as ReviewPassEntry[],
                connections,
            },
        })

        await openEdit(wrapper)
        expect((wrapper.find('[data-testid="review-pass-reasoning"]').element as HTMLSelectElement).value).toBe('medium')
    })
})

describe('ClientReviewPassesEditor logical models', () => {
    it('offers the chat logical models (not embedding) alongside the raw-model option', async () => {
        const wrapper = mount(ClientReviewPassesEditor, {
            props: { modelValue: [{ ordinal: 0, configuredModelId: 'm1' }] as ReviewPassEntry[], connections, logicalModels },
        })

        await openEdit(wrapper)
        const options = optionValues(wrapper, 'review-pass-logical-model')
        expect(options).toEqual(['', 'deep', 'fast'])
        expect(options).not.toContain('embed')
    })

    it('hides the raw connection/model/reasoning inputs when a logical model is chosen', async () => {
        const wrapper = mount(ClientReviewPassesEditor, {
            props: { modelValue: [{ ordinal: 0, configuredModelId: 'm1' }] as ReviewPassEntry[], connections, logicalModels },
        })

        await openEdit(wrapper)
        await wrapper.find('[data-testid="review-pass-logical-model"]').setValue('deep')

        expect(wrapper.find('[data-testid="review-pass-connection"]').exists()).toBe(false)
        expect(wrapper.find('[data-testid="review-pass-model"]').exists()).toBe(false)
        expect(wrapper.find('[data-testid="review-pass-reasoning"]').exists()).toBe(false)
        expect(wrapper.find('[data-testid="review-pass-from-logical"]').exists()).toBe(true)
    })

    it('emits a logical-model pass with no raw model or reasoning effort after saving', async () => {
        const wrapper = mount(ClientReviewPassesEditor, {
            props: { modelValue: [{ ordinal: 0, configuredModelId: 'm1' }] as ReviewPassEntry[], connections, logicalModels },
        })

        await openEdit(wrapper)
        await wrapper.find('[data-testid="review-pass-logical-model"]').setValue('deep')
        await save(wrapper)

        expect(lastEmittedPasses(wrapper)).toEqual([
            { ordinal: 0, logicalModelName: 'deep', lens: null, scope: null, shadow: false },
        ])
    })

    it('hydrates a persisted logical-model pass and hides the raw inputs', async () => {
        const wrapper = mount(ClientReviewPassesEditor, {
            props: {
                modelValue: [{ ordinal: 0, logicalModelName: 'deep' }] as ReviewPassEntry[],
                connections,
                logicalModels,
            },
        })

        await openEdit(wrapper)
        expect((wrapper.find('[data-testid="review-pass-logical-model"]').element as HTMLSelectElement).value).toBe('deep')
        expect(wrapper.find('[data-testid="review-pass-connection"]').exists()).toBe(false)
    })

    it('reveals the raw inputs again when switched back to the raw-model option', async () => {
        const wrapper = mount(ClientReviewPassesEditor, {
            props: {
                modelValue: [{ ordinal: 0, logicalModelName: 'deep' }] as ReviewPassEntry[],
                connections,
                logicalModels,
            },
        })

        await openEdit(wrapper)
        await wrapper.find('[data-testid="review-pass-logical-model"]').setValue('')

        // The raw pickers reappear, and with no raw model chosen yet the draft is incomplete, so Save is disabled.
        expect(wrapper.find('[data-testid="review-pass-connection"]').exists()).toBe(true)
        expect(wrapper.find('[data-testid="review-pass-save"]').attributes('disabled')).toBeDefined()
    })

    it('excludes a logical model taken by another row under the same tuple', async () => {
        const wrapper = mount(ClientReviewPassesEditor, {
            props: {
                modelValue: [
                    { ordinal: 0, logicalModelName: 'deep' },
                    { ordinal: 1, logicalModelName: 'fast' },
                ] as ReviewPassEntry[],
                connections,
                logicalModels,
            },
        })

        // Editing row 1 keeps its own 'fast' and the blank raw option, but never 'deep' (taken by row 0).
        await openEdit(wrapper, 1)
        const row1 = optionValues(wrapper, 'review-pass-logical-model')
        expect(row1).toContain('fast')
        expect(row1).toContain('')
        expect(row1).not.toContain('deep')
    })
})
