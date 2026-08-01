// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { mount } from '@vue/test-utils'
import { describe, expect, it } from 'vitest'
import JobProtocolEventModal from '../JobProtocolEventModal.vue'
import type { JobProtocolViewModel } from '@/features/job-protocol/composables/useJobProtocolViewModel'

/**
 * A finding withheld as a cross-increment duplicate is kept rather than dropped, so an operator has to be
 * able to see which findings were withheld, what each one repeated, and how close the match was. Without the
 * score on screen there is no way to tell a threshold set too aggressively from a quiet review.
 */
function viewModel(parsedInputResult: unknown): JobProtocolViewModel {
    return {
        selectedMergedEvent: {
            callDetails: { kind: 'operational', name: 'dedup_suppressed_findings' },
        },
        parsedInputResult,
        formatTokens: (value: unknown) => String(value),
        hasToolTiming: () => false,
        closeMergedEvent: () => {},
    } as unknown as JobProtocolViewModel
}

function mountModal(parsedInputResult: unknown) {
    return mount(JobProtocolEventModal, {
        props: { vm: viewModel(parsedInputResult) },
        global: {
            stubs: {
                ModalDialog: { template: '<div><slot /></div>' },
            },
        },
    })
}

describe('JobProtocolEventModal suppressed findings', () => {
    it('shows each withheld finding with the thread it repeated and the match score', () => {
        const wrapper = mountModal({
            suppressedCount: 1,
            findings: [
                {
                    ordinal: 0,
                    filePath: '/src/Agents.cs',
                    lineNumber: 142,
                    reasonCode: 'posted_finding_duplicate',
                    matchedProviderThreadId: 4242,
                    matchScore: 0.9312,
                },
            ],
        })

        const text = wrapper.text()
        expect(text).toContain('posted_finding_duplicate')
        expect(text).toContain('/src/Agents.cs')
        expect(text).toContain('L142')
        expect(text).toContain('Duplicates thread 4242')
        expect(text).toContain('similarity 0.93')
        expect(text).toContain('kept, but not posted')
    })

    it('labels a withheld pull-request-level finding rather than showing an empty anchor', () => {
        const wrapper = mountModal({
            suppressedCount: 1,
            findings: [
                {
                    ordinal: 3,
                    filePath: null,
                    lineNumber: null,
                    reasonCode: 'posted_finding_duplicate',
                    matchedProviderThreadId: 77,
                    matchScore: 0.88,
                },
            ],
        })

        expect(wrapper.text()).toContain('Pull request level')
    })

    it('does not claim a matched thread for a suppression that recorded none', () => {
        const wrapper = mountModal({
            suppressedCount: 1,
            findings: [
                {
                    ordinal: 1,
                    filePath: '/src/Agents.cs',
                    lineNumber: 10,
                    reasonCode: 'carried_forward_source',
                    matchedProviderThreadId: null,
                    matchScore: null,
                },
            ],
        })

        const text = wrapper.text()
        expect(text).toContain('No matching thread was recorded')
        expect(text).not.toContain('Duplicates thread')
    })
})
