// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises } from '@vue/test-utils'
import { createApp, defineComponent, nextTick, reactive } from 'vue'
import { useJobProtocolViewModel } from '@/features/job-protocol/composables/useJobProtocolViewModel'

const mockRoute = reactive({ params: { id: 'job-abc' }, query: {} as Record<string, unknown>, name: 'job-protocol' })
const mockReplace = vi.fn().mockResolvedValue(undefined)

vi.mock('vue-router', () => ({
    useRoute: () => mockRoute,
    useRouter: () => ({ replace: mockReplace, push: vi.fn() }),
}))

const mockGet = vi.fn()
vi.mock('@/services/api', () => ({
    createAdminClient: () => ({ GET: mockGet }),
    UnauthorizedError: class UnauthorizedError extends Error {},
}))

vi.mock('markdown-it', () => ({
    default: class {
        render(s: string) {
            return `<p>${s}</p>`
        }
    },
}))

vi.mock('dompurify', () => ({
    default: { sanitize: (s: string) => s },
}))

const baseProtocol = {
    jobId: 'job-abc',
    startedAt: '2024-01-01T00:00:00Z',
    completedAt: '2024-01-01T00:01:00Z',
    totalInputTokens: 100,
    totalOutputTokens: 50,
    finalSummary: null,
    finalComments: null,
    events: [],
}

const sampleResult = { status: 'completed', result: { summary: 'ok', comments: [] } }
const sampleDetail = {
    id: 'job-abc',
    clientId: 'client-1',
    status: 2,
    aiModel: 'gpt-4.1',
    reviewTemperature: 0.3,
    tokenBreakdown: [],
    breakdownConsistent: true,
    submittedAt: '2024-01-01T00:00:00Z',
    processingStartedAt: '2024-01-01T00:00:00Z',
    completedAt: '2024-01-01T00:01:00Z',
}

function mockProtocols(protocols: Array<Record<string, unknown>>) {
    mockGet.mockImplementation((path: string, options?: { params?: { path?: { protocolId?: string } } }) => {
        if (path === '/jobs/{id}/protocol/{protocolId}') {
            const id = options?.params?.path?.protocolId
            const found = protocols.find(p => (p as { id?: string }).id === id) ?? protocols[0]
            return Promise.resolve({ data: found, response: { ok: true } })
        }
        if (path === '/jobs/{id}/protocol') {
            return Promise.resolve({ data: protocols, response: { ok: true } })
        }
        if (path === '/jobs/{id}') {
            return Promise.resolve({ data: sampleDetail, response: { ok: true } })
        }
        return Promise.resolve({ data: sampleResult, response: { ok: true } })
    })
}

async function mountViewModel(): Promise<ReturnType<typeof useJobProtocolViewModel>> {
    let vm: ReturnType<typeof useJobProtocolViewModel> | null = null
    const app = createApp(defineComponent({
        setup() {
            vm = useJobProtocolViewModel()
            return () => null
        },
    }))
    app.mount(document.createElement('div'))
    await flushPromises()
    if (!vm) throw new Error('Failed to mount view model.')
    return vm
}

describe('useJobProtocolViewModel — file → pass grouping', () => {
    beforeEach(() => {
        vi.clearAllMocks()
        mockRoute.params.id = 'job-abc'
        mockRoute.query = {}
    })

    afterEach(() => {
        vi.restoreAllMocks()
    })

    it('groups a base + augmentation pass under one file with chronological pass tabs', async () => {
        mockProtocols([
            { ...baseProtocol, id: 'pass-base', label: 'Program.cs', passKind: 'Baseline', startedAt: '2024-01-01T00:00:00Z' },
            {
                ...baseProtocol,
                id: 'pass-aug',
                label: 'Program.cs',
                fileResultId: null,
                passKind: 'ProRVAugmentation',
                reason: 'high-risk file — re-reviewed in depth',
                startedAt: '2024-01-01T00:02:00Z',
                totalInputTokens: 400,
                totalOutputTokens: 100,
            },
        ])

        const vm = await mountViewModel()
        vm.activeTab = 'traces'
        await flushPromises()

        const fileGroup = vm.fileGroups.find(group => group.path === 'Program.cs')
        expect(fileGroup).toBeTruthy()
        expect(fileGroup!.passes).toHaveLength(2)
        expect(fileGroup!.tabs.map(tab => tab.label)).toEqual(['Initial review', 'ProRV verification'])
        // Chronological: baseline first.
        expect(fileGroup!.tabs[0].id).toBe('pass-base')
        expect(fileGroup!.tabs[1].id).toBe('pass-aug')
        expect(fileGroup!.tabs[1].reason).toBe('high-risk file — re-reviewed in depth')
        expect(vm.fileGroups).toHaveLength(1)
    })

    it('exposes no extra tabs for a single-pass file', async () => {
        mockProtocols([
            { ...baseProtocol, id: 'pass-only', label: 'src/solo.ts', passKind: 'Baseline' },
        ])

        const vm = await mountViewModel()
        vm.activeTab = 'traces'
        await flushPromises()

        expect(vm.fileGroups).toHaveLength(1)
        expect(vm.activeFilePassTabs).toHaveLength(1)
    })

    it('collects job-wide passes under the PR-level group and hides 0-token bookkeeping passes', async () => {
        mockProtocols([
            { ...baseProtocol, id: 'pass-file', label: 'src/foo.ts', passKind: 'Baseline' },
            {
                ...baseProtocol,
                id: 'pass-synth',
                label: 'synthesis',
                fileResultId: null,
                passKind: 'Synthesis',
                totalInputTokens: 300,
                totalOutputTokens: 60,
            },
            {
                ...baseProtocol,
                id: 'pass-posting',
                label: 'posting',
                fileResultId: null,
                totalInputTokens: 0,
                totalOutputTokens: 0,
                finalComments: null,
            },
        ])

        const vm = await mountViewModel()
        vm.activeTab = 'traces'
        await flushPromises()

        const prLevel = vm.fileGroups.find(group => group.isPrLevel)
        expect(prLevel).toBeTruthy()
        expect(prLevel!.label).toBe('PR-level')
        // synthesis is kept (has tokens); the 0-token posting pass is hidden.
        expect(prLevel!.passes.map(p => p.id)).toEqual(['pass-synth'])
        expect(vm.fileGroups.some(group => group.passes.some(p => p.id === 'pass-posting'))).toBe(false)
    })

    it('retains a failed / 0-token PR-level pass that recorded trace events', async () => {
        mockProtocols([
            { ...baseProtocol, id: 'pass-file', label: 'src/foo.ts', passKind: 'Baseline' },
            {
                ...baseProtocol,
                id: 'pass-synth-failed',
                label: 'synthesis',
                fileResultId: null,
                passKind: 'Synthesis',
                outcome: 'Failed',
                totalInputTokens: 0,
                totalOutputTokens: 0,
                finalComments: null,
                events: [
                    { id: 'ev-1', kind: 'operational', name: 'synthesis_started', occurredAt: '2024-01-01T00:00:30Z' },
                ],
            },
        ])

        const vm = await mountViewModel()
        vm.activeTab = 'traces'
        await flushPromises()

        const prLevel = vm.fileGroups.find(group => group.isPrLevel)
        expect(prLevel).toBeTruthy()
        // A failed synthesis with trace events is NOT bookkeeping noise; it stays.
        expect(prLevel!.passes.map(p => p.id)).toEqual(['pass-synth-failed'])
    })

    it('deep-links an augmentation-origin finding to the file\'s augmentation pass', async () => {
        mockProtocols([
            { ...baseProtocol, id: 'pass-base', label: 'Program.cs', passKind: 'Baseline', startedAt: '2024-01-01T00:00:00Z' },
            {
                ...baseProtocol,
                id: 'pass-aug',
                label: 'Program.cs',
                fileResultId: null,
                passKind: 'ProRVAugmentation',
                startedAt: '2024-01-01T00:02:00Z',
                totalInputTokens: 400,
                totalOutputTokens: 100,
            },
        ])

        const vm = await mountViewModel()
        vm.activeTab = 'traces'
        await flushPromises()

        vm.selectFindingOrigin({ filePath: 'Program.cs', originPassKind: 'ProRVAugmentation' })
        await flushPromises()

        // Must land on the augmentation pass, NOT the baseline.
        expect(vm.activePassId).toBe('pass-aug')
        expect(vm.activeTab).toBe('traces')
    })

    it('deep-links a baseline-origin finding to the baseline pass', async () => {
        mockProtocols([
            { ...baseProtocol, id: 'pass-base', label: 'Program.cs', passKind: 'Baseline', startedAt: '2024-01-01T00:00:00Z' },
            {
                ...baseProtocol,
                id: 'pass-aug',
                label: 'Program.cs',
                fileResultId: null,
                passKind: 'ProRVAugmentation',
                startedAt: '2024-01-01T00:02:00Z',
                totalInputTokens: 400,
                totalOutputTokens: 100,
            },
        ])

        const vm = await mountViewModel()
        vm.activeTab = 'traces'
        await flushPromises()

        vm.selectFindingOrigin({ filePath: 'Program.cs', originPassKind: 'Baseline' })
        await flushPromises()

        expect(vm.activePassId).toBe('pass-base')
    })
})

describe('useJobProtocolViewModel — URL deep-link sync', () => {
    beforeEach(() => {
        vi.clearAllMocks()
        mockRoute.params.id = 'job-abc'
        mockRoute.query = {}
    })

    afterEach(() => {
        vi.restoreAllMocks()
    })

    it('restores file, pass, and view from the URL on load', async () => {
        mockRoute.query = { file: 'src/bar.ts', pass: 'pass-bar', view: 'traces' }
        mockProtocols([
            { ...baseProtocol, id: 'pass-foo', label: 'src/foo.ts', passKind: 'Baseline' },
            { ...baseProtocol, id: 'pass-bar', label: 'src/bar.ts', passKind: 'Baseline' },
        ])

        const vm = await mountViewModel()
        await flushPromises()

        expect(vm.activeTab).toBe('traces')
        expect(vm.activePassId).toBe('pass-bar')
        expect(vm.activeFile?.path).toBe('src/bar.ts')
    })

    it('mirrors selection changes into the URL via router.replace', async () => {
        mockProtocols([
            { ...baseProtocol, id: 'pass-foo', label: 'src/foo.ts', passKind: 'Baseline' },
            { ...baseProtocol, id: 'pass-bar', label: 'src/bar.ts', passKind: 'Baseline' },
        ])

        const vm = await mountViewModel()
        mockReplace.mockClear()

        vm.activeTab = 'traces'
        vm.activePassId = 'pass-bar'
        await nextTick()
        await flushPromises()

        expect(mockReplace).toHaveBeenCalled()
        const lastQuery = mockReplace.mock.calls.at(-1)?.[0]?.query
        expect(lastQuery.pass).toBe('pass-bar')
        expect(lastQuery.file).toBe('src/bar.ts')
        expect(lastQuery.view).toBe('traces')
    })

    it('preserves existing protocolId / eventId / clientId query params when mirroring', async () => {
        mockRoute.query = { clientId: 'client-1', protocolId: 'pass-foo', eventId: 'event-9' }
        mockProtocols([
            { ...baseProtocol, id: 'pass-foo', label: 'src/foo.ts', passKind: 'Baseline' },
        ])

        const vm = await mountViewModel()
        mockReplace.mockClear()

        vm.activeTab = 'traces'
        await nextTick()
        await flushPromises()

        const lastQuery = mockReplace.mock.calls.at(-1)?.[0]?.query
        expect(lastQuery.clientId).toBe('client-1')
        expect(lastQuery.protocolId).toBe('pass-foo')
        expect(lastQuery.eventId).toBe('event-9')
        expect(lastQuery.view).toBe('traces')
    })

    it('reconciles state FROM the URL on back/forward navigation', async () => {
        mockProtocols([
            { ...baseProtocol, id: 'pass-foo', label: 'src/foo.ts', passKind: 'Baseline' },
            { ...baseProtocol, id: 'pass-bar', label: 'src/bar.ts', passKind: 'Baseline' },
        ])

        const vm = await mountViewModel()
        vm.activeTab = 'traces'
        vm.activePassId = 'pass-foo'
        await nextTick()
        await flushPromises()
        expect(vm.activePassId).toBe('pass-foo')

        // Simulate the browser back/forward mutating only the address bar.
        mockRoute.query = { file: 'src/bar.ts', pass: 'pass-bar', view: 'traces' }
        await nextTick()
        await flushPromises()

        // The read watcher must drive the UI from the URL.
        expect(vm.activePassId).toBe('pass-bar')
        expect(vm.activeFile?.path).toBe('src/bar.ts')

        // And a back to summary view via the URL.
        mockRoute.query = { file: 'src/bar.ts', pass: 'pass-bar', view: 'summary' }
        await nextTick()
        await flushPromises()
        expect(vm.activeTab).toBe('summary')
    })

    it('ignores URL pass ids that do not exist (no crash, keeps current selection)', async () => {
        mockProtocols([
            { ...baseProtocol, id: 'pass-foo', label: 'src/foo.ts', passKind: 'Baseline' },
        ])

        const vm = await mountViewModel()
        vm.activeTab = 'traces'
        await nextTick()
        await flushPromises()
        const before = vm.activePassId

        mockRoute.query = { pass: 'does-not-exist', view: 'traces' }
        await nextTick()
        await flushPromises()

        expect(vm.activePassId).toBe(before)
    })
})
