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
})
