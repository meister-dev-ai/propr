// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { describe, expect, it, vi } from 'vitest'
import { mount } from '@vue/test-utils'
import JobProtocolCommentGroups from '@/features/job-protocol/components/JobProtocolCommentGroups.vue'
import { originLabel } from '@/features/job-protocol/composables/passLabels'
import type { JobProtocolViewModel } from '@/features/job-protocol/composables/useJobProtocolViewModel'

function stubVm(overrides: Partial<Record<string, unknown>> = {}): JobProtocolViewModel {
    const selectFindingOrigin = vi.fn()
    return {
        renderMarkdown: (s: string) => `<p>${s}</p>`,
        commentOriginLabel: (comment: { originPassKind?: string | null }) => originLabel(comment.originPassKind),
        selectFindingOrigin,
        routeClientId: undefined,
        dismissingIds: new Set<string>(),
        commentKey: () => 'k',
        dismissComment: vi.fn(),
        ...overrides,
    } as unknown as JobProtocolViewModel
}

describe('JobProtocolCommentGroups — origin badges', () => {
    it('renders an origin badge with a mapped label when originPassKind is set', () => {
        const wrapper = mount(JobProtocolCommentGroups, {
            props: {
                vm: stubVm(),
                groups: [
                    {
                        directory: 'src/foo.ts',
                        comments: [
                            { severity: 'error', message: 'finding A', filePath: 'src/foo.ts', lineNumber: 42, originPassKind: 'ProRVAugmentation' },
                        ],
                    },
                ],
                emptyMessage: 'none',
                showOrigin: true,
            },
        })

        const badge = wrapper.find('[data-testid="origin-badge"]')
        expect(badge.exists()).toBe(true)
        expect(badge.text()).toContain('ProRV verification')
        expect(badge.attributes('aria-label')).toBe('Found by ProRV verification on src/foo.ts')
    })

    it('omits the badge when originPassKind is null', () => {
        const wrapper = mount(JobProtocolCommentGroups, {
            props: {
                vm: stubVm(),
                groups: [
                    {
                        directory: 'src/foo.ts',
                        comments: [
                            { severity: 'warning', message: 'finding B', filePath: 'src/foo.ts', lineNumber: 7, originPassKind: null },
                        ],
                    },
                ],
                emptyMessage: 'none',
                showOrigin: true,
            },
        })

        expect(wrapper.find('[data-testid="origin-badge"]').exists()).toBe(false)
    })

    it('does not render badges when showOrigin is not set', () => {
        const wrapper = mount(JobProtocolCommentGroups, {
            props: {
                vm: stubVm(),
                groups: [
                    {
                        directory: 'src/foo.ts',
                        comments: [
                            { severity: 'error', message: 'finding C', filePath: 'src/foo.ts', lineNumber: 1, originPassKind: 'Baseline' },
                        ],
                    },
                ],
                emptyMessage: 'none',
            },
        })

        expect(wrapper.find('[data-testid="origin-badge"]').exists()).toBe(false)
    })

    it('renders an "Outside your changes" badge when the relation is outsideChange', () => {
        const wrapper = mount(JobProtocolCommentGroups, {
            props: {
                vm: stubVm(),
                groups: [
                    {
                        directory: 'src/foo.ts',
                        comments: [
                            {
                                severity: 'error',
                                message: 'pre-existing defect',
                                filePath: 'src/foo.ts',
                                lineNumber: 244,
                                originPassKind: 'Baseline',
                                changedLineRelation: 'outsideChange',
                            },
                        ],
                    },
                ],
                emptyMessage: 'none',
                showOrigin: true,
            },
        })

        const badge = wrapper.find('[data-testid="outside-change-badge"]')
        expect(badge.exists()).toBe(true)
        expect(badge.text()).toContain('Outside your changes')
    })

    it('renders the outside-change badge even when origin badges are hidden', () => {
        const wrapper = mount(JobProtocolCommentGroups, {
            props: {
                vm: stubVm(),
                groups: [
                    {
                        directory: 'src/foo.ts',
                        comments: [
                            {
                                severity: 'warning',
                                message: 'pre-existing defect',
                                filePath: 'src/foo.ts',
                                lineNumber: 244,
                                changedLineRelation: 'outsideChange',
                            },
                        ],
                    },
                ],
                emptyMessage: 'none',
            },
        })

        expect(wrapper.find('[data-testid="outside-change-badge"]').exists()).toBe(true)
        expect(wrapper.find('[data-testid="origin-badge"]').exists()).toBe(false)
    })

    it('omits the outside-change badge for on-changed-line and adjacent relations', () => {
        const wrapper = mount(JobProtocolCommentGroups, {
            props: {
                vm: stubVm(),
                groups: [
                    {
                        directory: 'src/foo.ts',
                        comments: [
                            { severity: 'error', message: 'on the change', filePath: 'src/foo.ts', lineNumber: 12, changedLineRelation: 'onChangedLine' },
                            { severity: 'warning', message: 'next to the change', filePath: 'src/foo.ts', lineNumber: 17, changedLineRelation: 'adjacentToChange' },
                            { severity: 'note', message: 'unclassified', filePath: 'src/foo.ts', lineNumber: 3 },
                        ],
                    },
                ],
                emptyMessage: 'none',
                showOrigin: true,
            },
        })

        expect(wrapper.find('[data-testid="outside-change-badge"]').exists()).toBe(false)
    })

    it('deep-links to the origin trace when the badge is clicked', async () => {
        const selectFindingOrigin = vi.fn()
        const wrapper = mount(JobProtocolCommentGroups, {
            props: {
                vm: stubVm({ selectFindingOrigin }),
                groups: [
                    {
                        directory: 'src/foo.ts',
                        comments: [
                            { severity: 'error', message: 'finding D', filePath: 'src/foo.ts', lineNumber: 42, originPassKind: 'Baseline' },
                        ],
                    },
                ],
                emptyMessage: 'none',
                showOrigin: true,
            },
        })

        await wrapper.find('[data-testid="origin-badge"]').trigger('click')
        expect(selectFindingOrigin).toHaveBeenCalledTimes(1)
    })
})
