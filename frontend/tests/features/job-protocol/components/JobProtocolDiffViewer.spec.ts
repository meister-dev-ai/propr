// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license information.

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { mount } from '@vue/test-utils'
import { nextTick } from 'vue'
import JobProtocolDiffViewer from '@/features/job-protocol/components/JobProtocolDiffViewer.vue'
import type { components } from '@/types'

type FileDiffDto = components['schemas']['FileDiffDto']

function createDiff(overrides: Partial<FileDiffDto> = {}): FileDiffDto {
    return {
        filePath: 'src/Services/ReviewService.cs',
        unifiedDiff: 'diff --git a/src/Foo.cs b/src/Foo.cs\n@@ -1,1 +1,2 @@\n+added\n-removed',
        changeType: 'Modified',
        isBinary: false,
        originalPath: null,
        availability: 'Available',
        availabilityMessage: null,
        ...overrides,
    } as FileDiffDto
}

function createUiDouble() {
    return {
        draw: vi.fn(),
        syntaxHighlight: vi.fn(),
    }
}

let lastUi: ReturnType<typeof createUiDouble> | null = null
let lastCallOptions: unknown = null
let lastContainer: HTMLElement | null = null
let lastDiffContent: string | null = null

vi.mock('diff2html/lib/ui/js/diff2html-ui-base.js', () => ({
    // vitest 4: `new Diff2HtmlUI()` requires the impl to be a function/class, not an arrow.
    Diff2HtmlUI: vi.fn().mockImplementation(function (container: HTMLElement, diff: string, options: unknown) {
        lastUi = createUiDouble()
        lastCallOptions = options
        lastContainer = container
        lastDiffContent = diff
        return lastUi
    }),
}))

describe('JobProtocolDiffViewer', () => {
    beforeEach(() => {
        lastUi = null
        lastCallOptions = null
        lastContainer = null
        lastDiffContent = null
    })

    afterEach(() => {
        vi.clearAllMocks()
    })

    it('renders unified diff content when availability is Available', async () => {
        const wrapper = mount(JobProtocolDiffViewer, {
            props: {
                fileResultId: 'file-result-1',
                diff: createDiff(),
            },
            global: {
                stubs: {
                    ProgressOrb: { template: '<div class="orb-stub" />' },
                },
            },
        })

        await nextTick()
        await nextTick()
        expect(wrapper.find('[data-testid="diff-viewer"]').exists()).toBe(true)
        expect(wrapper.find('[data-testid="diff-binary"]').exists()).toBe(false)
        expect(wrapper.find('[data-testid="diff-error"]').exists()).toBe(false)
    })

    it('shows a binary file fallback when availability is Binary', () => {
        const wrapper = mount(JobProtocolDiffViewer, {
            props: {
                fileResultId: 'file-result-1',
                diff: createDiff({
                    isBinary: true,
                    availability: 'Binary',
                    unifiedDiff: '',
                    availabilityMessage: 'Binary files do not have a renderable diff.',
                }),
            },
            global: {
                stubs: {
                    ProgressOrb: { template: '<div class="orb-stub" />' },
                },
            },
        })

        expect(wrapper.find('[data-testid="diff-binary"]').exists()).toBe(true)
        expect(wrapper.find('[data-testid="diff-binary"]').text()).toContain('Binary file')
        expect(wrapper.find('[data-testid="diff-viewer"]').exists()).toBe(false)
    })

    it('shows a no-file fallback when no file is associated with the protocol pass', () => {
        const wrapper = mount(JobProtocolDiffViewer, {
            props: {
                fileResultId: null,
            },
            global: {
                stubs: {
                    ProgressOrb: { template: '<div class="orb-stub" />' },
                },
            },
        })

        expect(wrapper.find('[data-testid="diff-no-file"]').exists()).toBe(true)
        expect(wrapper.find('[data-testid="diff-no-file"]').text()).toContain('No file associated')
    })

    it('shows a provider-unavailable fallback when diffError is set', () => {
        const wrapper = mount(JobProtocolDiffViewer, {
            props: {
                fileResultId: 'file-result-1',
                diffError: 'SCM unreachable',
            },
            global: {
                stubs: {
                    ProgressOrb: { template: '<div class="orb-stub" />' },
                },
            },
        })

        expect(wrapper.find('[data-testid="diff-error"]').exists()).toBe(true)
        expect(wrapper.find('[data-testid="diff-error"]').text()).toContain('SCM unreachable')
    })

    it('shows a not-found fallback when availability is NotFound', () => {
        const wrapper = mount(JobProtocolDiffViewer, {
            props: {
                fileResultId: 'file-result-1',
                diff: createDiff({
                    availability: 'NotFound',
                    availabilityMessage: 'File was not found in PR.',
                    unifiedDiff: '',
                }),
            },
            global: {
                stubs: {
                    ProgressOrb: { template: '<div class="orb-stub" />' },
                },
            },
        })

        expect(wrapper.find('[data-testid="diff-error"]').exists()).toBe(true)
        expect(wrapper.find('[data-testid="diff-error"]').text()).toContain('File was not found in PR')
    })

    it('shows the file path header above the diff viewer', async () => {
        const wrapper = mount(JobProtocolDiffViewer, {
            props: {
                fileResultId: 'file-result-1',
                diff: createDiff(),
            },
            global: {
                stubs: {
                    ProgressOrb: { template: '<div class="orb-stub" />' },
                },
            },
        })

        await nextTick()
        await nextTick()
        const header = wrapper.find('[data-testid="diff-file-path"]')
        expect(header.exists()).toBe(true)
        expect(header.text()).toContain('src/Services/ReviewService.cs')
    })

    it('renders with line-by-line output format by default', async () => {
        mount(JobProtocolDiffViewer, {
            props: {
                fileResultId: 'file-result-1',
                diff: createDiff(),
            },
            global: {
                stubs: {
                    ProgressOrb: { template: '<div class="orb-stub" />' },
                },
            },
        })

        await nextTick()
        await nextTick()
        expect(lastCallOptions).toBeTruthy()
        const options = lastCallOptions as { outputFormat?: string } | null
        expect(options?.outputFormat).toBe('line-by-line')
    })

    it('switches to side-by-side output when the split button is clicked', async () => {
        const wrapper = mount(JobProtocolDiffViewer, {
            props: {
                fileResultId: 'file-result-1',
                diff: createDiff(),
            },
            global: {
                stubs: {
                    ProgressOrb: { template: '<div class="orb-stub" />' },
                },
            },
        })

        await nextTick()
        await nextTick()

        const splitButton = wrapper.find('[data-testid="diff-mode-side-by-side"]')
        expect(splitButton.exists()).toBe(true)
        await splitButton.trigger('click')
        await nextTick()
        await nextTick()

        const options = lastCallOptions as { outputFormat?: string } | null
        expect(options?.outputFormat).toBe('side-by-side')
    })

    it('applies the d2h-dark-color-scheme class for the dark theme', async () => {
        const wrapper = mount(JobProtocolDiffViewer, {
            props: {
                fileResultId: 'file-result-1',
                diff: createDiff(),
            },
            global: {
                stubs: {
                    ProgressOrb: { template: '<div class="orb-stub" />' },
                },
            },
        })

        await nextTick()
        await nextTick()
        expect(wrapper.find('.d2h-dark-color-scheme').exists()).toBe(true)
    })

    it('passes a real multi-hunk unified diff to diff2html verbatim, without client-side pre-processing', async () => {
        // A true unified diff as emitted server-side: "---"/"+++" headers and two separate "@@" hunks
        // (the changes are far enough apart that the 3-line context windows do not merge).
        const unifiedDiff = [
            '--- a/src/Sample.cs',
            '+++ b/src/Sample.cs',
            '@@ -12,7 +12,7 @@',
            ' L12',
            ' L13',
            ' L14',
            '-L15',
            '+L15-CHANGED',
            ' L16',
            ' L17',
            ' L18',
            '@@ -42,7 +42,7 @@',
            ' L42',
            ' L43',
            ' L44',
            '-L45',
            '+L45-CHANGED',
            ' L46',
            ' L47',
            ' L48',
        ].join('\n')

        mount(JobProtocolDiffViewer, {
            props: {
                fileResultId: 'file-result-1',
                diff: createDiff({ unifiedDiff }),
            },
            global: {
                stubs: {
                    ProgressOrb: { template: '<div class="orb-stub" />' },
                },
            },
        })

        await nextTick()
        await nextTick()

        // The defensive hunk-header synthesis must not fire for a real unified diff: the payload
        // reaches diff2html unchanged, with both "@@" hunk headers preserved.
        expect(lastDiffContent).toBe(unifiedDiff)
        expect((lastDiffContent ?? '').match(/^@@ /gm)?.length).toBe(2)
    })

    it('does not inject inline-thread rows or render the thread slot when no inlineThreads are passed', async () => {
        const wrapper = mount(JobProtocolDiffViewer, {
            props: {
                fileResultId: 'file-result-1',
                diff: createDiff(),
            },
            slots: {
                // A thread slot is provided but must never render without anchored threads.
                thread: '<div class="should-not-render" />',
            },
            global: {
                stubs: {
                    ProgressOrb: { template: '<div class="orb-stub" />' },
                },
            },
        })

        await nextTick()
        await nextTick()

        // No DOM post-processing for the default (no-threads) caller: behavior is unchanged.
        expect(wrapper.find('.d2h-inline-thread-row').exists()).toBe(false)
        expect(wrapper.find('.should-not-render').exists()).toBe(false)
        // The off-diff host stays empty.
        expect(wrapper.find('.diff-inline-thread-host').element.children.length).toBe(0)
    })

    it('configures diff2html with large-diff protection options', async () => {
        mount(JobProtocolDiffViewer, {
            props: {
                fileResultId: 'file-result-1',
                diff: createDiff(),
            },
            global: {
                stubs: {
                    ProgressOrb: { template: '<div class="orb-stub" />' },
                },
            },
        })

        await nextTick()
        await nextTick()
        const options = lastCallOptions as { diffMaxChanges?: number; diffMaxLineLength?: number } | null
        expect(options?.diffMaxChanges).toBe(5000)
        expect(options?.diffMaxLineLength).toBe(1000)
    })
})
