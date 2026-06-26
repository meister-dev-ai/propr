// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { describe, expect, it } from 'vitest'
import { mount } from '@vue/test-utils'
import PassSelector from '@/features/job-protocol/components/PassSelector.vue'
import type { FileGroup, PassTab } from '@/features/job-protocol/types'

function passTab(overrides: Partial<PassTab> = {}): PassTab {
    return {
        id: 'pass-1',
        label: 'Initial review',
        reason: null,
        tokens: 1000,
        findingCount: 0,
        failed: false,
        ...overrides,
    }
}

function fileGroup(overrides: Partial<FileGroup> = {}): FileGroup {
    return {
        path: 'src/foo.ts',
        label: 'src/foo.ts',
        isPrLevel: false,
        directory: 'src',
        filename: 'foo.ts',
        passes: [],
        tabs: [],
        totalTokens: 1000,
        totalFindings: 0,
        ...overrides,
    }
}

describe('PassSelector — pass tab strip', () => {
    it('does not render a tab strip for a single-pass file', () => {
        const wrapper = mount(PassSelector, {
            props: {
                fileGroups: [fileGroup({ tabs: [passTab()] })],
                activeFilePath: 'src/foo.ts',
                passTabs: [passTab()],
                activePassId: 'pass-1',
            },
        })

        expect(wrapper.find('[data-testid="pass-tab-strip"]').exists()).toBe(false)
        expect(wrapper.findAll('[role="tab"]')).toHaveLength(0)
    })

    it('renders one tab per pass for a multi-pass file with token + finding chips and the Failed pill', () => {
        const tabs = [
            passTab({ id: 'pass-1', label: 'Initial review', tokens: 24700, findingCount: 1 }),
            passTab({ id: 'pass-2', label: 'High-risk review', tokens: 56800, findingCount: 1, failed: true }),
        ]
        const wrapper = mount(PassSelector, {
            props: {
                fileGroups: [fileGroup({ tabs })],
                activeFilePath: 'src/foo.ts',
                passTabs: tabs,
                activePassId: 'pass-1',
            },
        })

        const tabEls = wrapper.findAll('[role="tab"]')
        expect(tabEls).toHaveLength(2)
        expect(tabEls[0].text()).toContain('Initial review')
        expect(tabEls[1].text()).toContain('High-risk review')
        expect(wrapper.text()).toContain('Failed')
        // Active tab is conveyed beyond colour via aria-selected.
        expect(tabEls[0].attributes('aria-selected')).toBe('true')
        expect(tabEls[1].attributes('aria-selected')).toBe('false')
    })

    it('emits select-pass when a tab is clicked', async () => {
        const tabs = [passTab({ id: 'pass-1' }), passTab({ id: 'pass-2', label: 'High-risk review' })]
        const wrapper = mount(PassSelector, {
            props: {
                fileGroups: [fileGroup({ tabs })],
                activeFilePath: 'src/foo.ts',
                passTabs: tabs,
                activePassId: 'pass-1',
            },
        })

        await wrapper.findAll('[role="tab"]')[1].trigger('click')
        expect(wrapper.emitted('select-pass')?.[0]).toEqual(['pass-2'])
    })

    it('exposes the file dropdown as a listbox of options and emits select-file', async () => {
        const groups = [
            fileGroup({ path: 'src/foo.ts', label: 'src/foo.ts', tabs: [passTab()] }),
            fileGroup({ path: 'src/bar.ts', label: 'src/bar.ts', directory: 'src', filename: 'bar.ts', tabs: [passTab({ id: 'pass-2' })] }),
        ]
        const wrapper = mount(PassSelector, {
            props: {
                fileGroups: groups,
                activeFilePath: 'src/foo.ts',
                passTabs: [passTab()],
                activePassId: 'pass-1',
            },
        })

        await wrapper.find('[data-testid="file-trigger"]').trigger('click')
        const listbox = wrapper.find('[role="listbox"]')
        expect(listbox.exists()).toBe(true)
        const options = wrapper.findAll('[role="option"]')
        expect(options).toHaveLength(2)

        const barOption = options.find(option => option.text().includes('bar.ts'))
        await barOption!.trigger('click')
        expect(wrapper.emitted('select-file')?.[0]).toEqual(['src/bar.ts'])
    })
})
