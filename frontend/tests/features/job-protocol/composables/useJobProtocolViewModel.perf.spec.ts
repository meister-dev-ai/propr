// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises } from '@vue/test-utils'
import { createApp, defineComponent, reactive } from 'vue'
import { useJobProtocolViewModel } from '@/features/job-protocol/composables/useJobProtocolViewModel'

const mockRoute = reactive({ params: { id: 'job-abc' }, query: {} as Record<string, unknown>, name: 'job-protocol' })

vi.mock('vue-router', () => ({
    useRoute: () => mockRoute,
    useRouter: () => ({ replace: vi.fn().mockResolvedValue(undefined), push: vi.fn() }),
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
    totalInputTokens: 100,
    totalOutputTokens: 50,
    finalSummary: null,
    finalComments: null,
    events: [],
}

// The poll keys off the job's lifecycle status (a camelCase string from the generated
// JobStatus union), so the detail mock must carry a string status, controllable per test.
type JobDetailStatus = 'pending' | 'processing' | 'completed' | 'failed' | 'cancelled'

function makeSampleDetail(status: JobDetailStatus) {
    return {
        id: 'job-abc',
        clientId: 'client-1',
        status,
        aiModel: 'gpt-4.1',
        reviewTemperature: 0.3,
        tokenBreakdown: [],
        breakdownConsistent: true,
        submittedAt: '2024-01-01T00:00:00Z',
        processingStartedAt: '2024-01-01T00:00:00Z',
        completedAt: '2024-01-01T00:01:00Z',
    }
}

function sampleEvent(id: string) {
    return { id, kind: 'AiCall', name: 'review_call', occurredAt: '2024-01-01T00:00:30Z', outputSummary: 'ok' }
}

type Overview = Record<string, unknown> & { id: string }

function setupReview(opts: {
    list: Overview[]
    detailEventsById?: Record<string, Array<Record<string, unknown>>>
    resultStatus?: string
    // Job lifecycle status returned by GET /jobs/{id}; this is what now drives the 3s poll.
    detailStatus?: JobDetailStatus
}) {
    const detail = makeSampleDetail(opts.detailStatus ?? 'completed')
    mockGet.mockImplementation((path: string, options?: { params?: { path?: { protocolId?: string } } }) => {
        if (path === '/jobs/{id}/protocol/{protocolId}') {
            const id = options?.params?.path?.protocolId ?? ''
            const overview = opts.list.find(p => p.id === id) ?? opts.list[0]
            const events = opts.detailEventsById?.[id] ?? [sampleEvent(`${id}-e1`)]
            return Promise.resolve({ data: { ...overview, events }, response: { ok: true } })
        }
        if (path === '/jobs/{id}/protocol') {
            return Promise.resolve({ data: opts.list, response: { ok: true } })
        }
        if (path === '/jobs/{id}') {
            return Promise.resolve({ data: detail, response: { ok: true } })
        }
        return Promise.resolve({ data: { status: opts.resultStatus ?? 'completed', result: { summary: 'ok', comments: [] } }, response: { ok: true } })
    })
}

function countDetailCalls(protocolId: string): number {
    return mockGet.mock.calls.filter(
        (call: unknown[]) =>
            call[0] === '/jobs/{id}/protocol/{protocolId}'
            && (call[1] as { params?: { path?: { protocolId?: string } } } | undefined)?.params?.path?.protocolId === protocolId,
    ).length
}

function countOverviewCalls(): number {
    return mockGet.mock.calls.filter((call: unknown[]) => call[0] === '/jobs/{id}/protocol').length
}

function mountViewModel(): ReturnType<typeof useJobProtocolViewModel> {
    let vm: ReturnType<typeof useJobProtocolViewModel> | null = null
    const app = createApp(defineComponent({
        setup() {
            vm = useJobProtocolViewModel()
            return () => null
        },
    }))
    app.mount(document.createElement('div'))
    if (!vm) throw new Error('Failed to mount view model.')
    return vm
}

describe('useJobProtocolViewModel — large-review performance', () => {
    beforeEach(() => {
        vi.clearAllMocks()
        mockRoute.params.id = 'job-abc'
        mockRoute.query = {}
    })

    afterEach(() => {
        vi.restoreAllMocks()
        vi.useRealTimers()
    })

    it('does not re-fetch a completed active pass on the next poll, and keeps its loaded events', async () => {
        vi.useFakeTimers()
        mockRoute.query = { pass: 'p-done' }
        setupReview({
            list: [
                { ...baseProtocol, id: 'p-done', label: 'a.ts', passKind: 'Baseline', completedAt: '2024-01-01T00:01:00Z' },
                { ...baseProtocol, id: 'p-run', label: 'b.ts', passKind: 'Baseline', completedAt: null },
            ],
            detailEventsById: { 'p-done': [sampleEvent('done-e1'), sampleEvent('done-e2')] },
            // The job is still processing, so the poll runs even though the active pass is complete.
            detailStatus: 'processing',
            resultStatus: 'processing',
        })

        const vm = mountViewModel()
        await vi.advanceTimersByTimeAsync(0)
        await vi.advanceTimersByTimeAsync(0)

        expect((vm.protocols.find(p => p.id === 'p-done')?.events ?? []).length).toBe(2)
        const before = countDetailCalls('p-done')

        await vi.advanceTimersByTimeAsync(3000)

        // Completed pass is preserved across the poll: no new detail fetch, events intact.
        expect(countDetailCalls('p-done')).toBe(before)
        expect((vm.protocols.find(p => p.id === 'p-done')?.events ?? []).length).toBe(2)
    })

    it('re-fetches an in-progress active pass on poll so newly recorded events appear', async () => {
        vi.useFakeTimers()
        mockRoute.query = { pass: 'p-run' }
        setupReview({
            list: [{ ...baseProtocol, id: 'p-run', label: 'b.ts', passKind: 'Baseline', completedAt: null }],
            detailStatus: 'processing',
            resultStatus: 'processing',
        })

        const vm = mountViewModel()
        await vi.advanceTimersByTimeAsync(0)
        await vi.advanceTimersByTimeAsync(0)
        expect(vm.activePassId).toBe('p-run')
        const before = countDetailCalls('p-run')

        await vi.advanceTimersByTimeAsync(3000)

        // In-progress active pass is re-fetched on poll so new events surface.
        expect(countDetailCalls('p-run')).toBeGreaterThan(before)
    })

    it('stops polling a completed job even when it carries a dangling (completedAt=null) protocol', async () => {
        vi.useFakeTimers()
        mockRoute.query = { pass: 'p-done' }
        // Regression guard: a Completed job that carries a dangling, never-stamped protocol
        // (completedAt=null) used to poll forever under the old `.some(!completedAt)` heuristic.
        // The poll now keys off the terminal job status, so it must never arm.
        setupReview({
            list: [
                { ...baseProtocol, id: 'p-done', label: 'a.ts', passKind: 'Baseline', completedAt: '2024-01-01T00:01:00Z' },
                { ...baseProtocol, id: 'p-dangling', label: 'b.ts', passKind: 'Baseline', completedAt: null },
            ],
            detailStatus: 'completed',
        })

        mountViewModel()
        await vi.advanceTimersByTimeAsync(0)
        await vi.advanceTimersByTimeAsync(0)
        expect(countOverviewCalls()).toBe(1)

        // Advance well past two poll cadences: no 3s poll re-fetches the overview.
        await vi.advanceTimersByTimeAsync(3000)
        await vi.advanceTimersByTimeAsync(3000)

        expect(countOverviewCalls()).toBe(1)
    })

    it('keeps polling while the job status is processing', async () => {
        vi.useFakeTimers()
        mockRoute.query = { pass: 'p-run' }
        setupReview({
            list: [{ ...baseProtocol, id: 'p-run', label: 'b.ts', passKind: 'Baseline', completedAt: null }],
            detailStatus: 'processing',
        })

        mountViewModel()
        await vi.advanceTimersByTimeAsync(0)
        await vi.advanceTimersByTimeAsync(0)
        const before = countOverviewCalls()
        expect(before).toBe(1)

        // The job is processing, so the 3s tick re-fetches the overview.
        await vi.advanceTimersByTimeAsync(3000)

        expect(countOverviewCalls()).toBeGreaterThan(before)
    })

    it('keeps polling when a poll tick\'s job-detail fetch transiently fails', async () => {
        vi.useFakeTimers()
        mockRoute.query = { pass: 'p-run' }

        // A processing job arms the 3 s poll. After it is armed, a later GET /jobs/{id} resolves with
        // NO data (openapi-fetch returns { data: undefined } on a transient failure rather than throwing).
        // The poll must survive that tick: an undefined/error job status must NOT be treated as terminal.
        const processingList = [{ ...baseProtocol, id: 'p-run', label: 'b.ts', passKind: 'Baseline', completedAt: null }]
        const processingDetail = makeSampleDetail('processing')
        let detailCallCount = 0
        mockGet.mockImplementation((path: string) => {
            if (path === '/jobs/{id}/protocol/{protocolId}') {
                return Promise.resolve({ data: { ...processingList[0], events: [sampleEvent('p-run-e1')] }, response: { ok: true } })
            }
            if (path === '/jobs/{id}/protocol') {
                return Promise.resolve({ data: processingList, response: { ok: true } })
            }
            if (path === '/jobs/{id}') {
                detailCallCount += 1
                // First detail fetch (mount load) reports processing so the poll arms; every later tick's
                // detail fetch transiently misses (no data, not-ok response).
                if (detailCallCount <= 1) {
                    return Promise.resolve({ data: processingDetail, response: { ok: true } })
                }
                return Promise.resolve({ data: undefined, response: { ok: false } })
            }
            return Promise.resolve({ data: { status: 'processing', result: { summary: 'ok', comments: [] } }, response: { ok: true } })
        })

        mountViewModel()
        await vi.advanceTimersByTimeAsync(0)
        await vi.advanceTimersByTimeAsync(0)
        const armed = countOverviewCalls()
        expect(armed).toBe(1)

        // First tick: its detail fetch transiently misses, but the poll must stay armed.
        await vi.advanceTimersByTimeAsync(3000)
        const afterFailedTick = countOverviewCalls()
        expect(afterFailedTick).toBeGreaterThan(armed)

        // Subsequent tick: the overview is still fetched, proving the poll survived the detail miss.
        // Under the old teardown-on-non-processing logic the interval would have been cleared here.
        await vi.advanceTimersByTimeAsync(3000)
        expect(countOverviewCalls()).toBeGreaterThan(afterFailedTick)
    })

    it('loads Traces-tab passes in a bounded background queue, not all at once', async () => {
        const deferreds = new Map<string, () => void>()
        const detailRequested: string[] = []
        const list = ['p1', 'p2', 'p3', 'p4'].map(id => ({
            ...baseProtocol,
            id,
            label: `${id}.ts`,
            passKind: 'Baseline',
            completedAt: '2024-01-01T00:01:00Z',
        }))

        mockGet.mockImplementation((path: string, options?: { params?: { path?: { protocolId?: string } } }) => {
            if (path === '/jobs/{id}/protocol/{protocolId}') {
                const id = options?.params?.path?.protocolId ?? ''
                detailRequested.push(id)
                return new Promise(resolve => {
                    deferreds.set(id, () =>
                        resolve({ data: { ...list.find(p => p.id === id), events: [sampleEvent(`${id}-e1`)] }, response: { ok: true } }))
                })
            }
            if (path === '/jobs/{id}/protocol') {
                return Promise.resolve({ data: list, response: { ok: true } })
            }
            if (path === '/jobs/{id}') {
                return Promise.resolve({ data: makeSampleDetail('completed'), response: { ok: true } })
            }
            return Promise.resolve({ data: { status: 'completed', result: { summary: 'ok', comments: [] } }, response: { ok: true } })
        })

        const vm = mountViewModel()
        await flushPromises()

        const activeId = vm.activePassId
        expect(activeId).toBeTruthy()
        expect(detailRequested).toContain(activeId)

        vm.activeTab = 'traces'
        await flushPromises()

        const afterActive = detailRequested.filter(id => id !== activeId)
        expect(afterActive.length).toBeLessThanOrEqual(2)
        expect(detailRequested.length).toBeLessThan(4)

        for (let i = 0; i < 8 && vm.loadedProtocolIds.size < 4; i++) {
            for (const resolve of [...deferreds.values()]) resolve()
            await flushPromises()
        }

        expect(vm.loadedProtocolIds.size).toBe(4)
    })

    it('aborts the Traces-tab background backfill when leaving the tab', async () => {
        const deferreds = new Map<string, () => void>()
        const detailRequested: string[] = []
        const list = ['p1', 'p2', 'p3', 'p4'].map(id => ({
            ...baseProtocol,
            id,
            label: `${id}.ts`,
            passKind: 'Baseline',
            completedAt: '2024-01-01T00:01:00Z',
        }))

        mockGet.mockImplementation((path: string, options?: { params?: { path?: { protocolId?: string } } }) => {
            if (path === '/jobs/{id}/protocol/{protocolId}') {
                const id = options?.params?.path?.protocolId ?? ''
                detailRequested.push(id)
                return new Promise(resolve => {
                    deferreds.set(id, () =>
                        resolve({ data: { ...list.find(p => p.id === id), events: [sampleEvent(`${id}-e1`)] }, response: { ok: true } }))
                })
            }
            if (path === '/jobs/{id}/protocol') {
                return Promise.resolve({ data: list, response: { ok: true } })
            }
            if (path === '/jobs/{id}') {
                return Promise.resolve({ data: makeSampleDetail('completed'), response: { ok: true } })
            }
            return Promise.resolve({ data: { status: 'completed', result: { summary: 'ok', comments: [] } }, response: { ok: true } })
        })

        const vm = mountViewModel()
        await flushPromises()
        vm.activeTab = 'traces'
        await flushPromises()

        // Backfill has dispatched the active pass + a bounded first wave; at least one pass
        // is still queued (workers are blocked on the unresolved fetches).
        const requestedAtAbort = [...detailRequested]
        expect(requestedAtAbort.length).toBeLessThan(4)

        // Leave the tab — this must cancel the queue.
        vm.activeTab = 'summary'
        for (const resolve of [...deferreds.values()]) resolve()
        await flushPromises()

        // No further passes were fetched after the abort: the queue stopped dispatching.
        expect(detailRequested).toEqual(requestedAtAbort)
    })

    it('does not stack overlapping protocol overview requests when a poll tick fires mid-load', async () => {
        vi.useFakeTimers()

        const processingList = [{ ...baseProtocol, id: 'p-run', label: 'b.ts', passKind: 'Baseline', completedAt: null }]
        // After the first (fast) overview resolves and arms the 3 s poll, hold every subsequent overview
        // fetch open so a poll stays in flight while the next tick fires.
        let overviewCallCount = 0
        const releasers: Array<() => void> = []
        mockGet.mockImplementation((path: string) => {
            if (path === '/jobs/{id}/protocol') {
                overviewCallCount += 1
                if (overviewCallCount === 1) {
                    return Promise.resolve({ data: processingList, response: { ok: true } })
                }
                return new Promise(resolve => {
                    releasers.push(() => resolve({ data: processingList, response: { ok: true } }))
                })
            }
            if (path === '/jobs/{id}') {
                return Promise.resolve({ data: makeSampleDetail('processing'), response: { ok: true } })
            }
            return Promise.resolve({ data: { status: 'processing', result: { summary: 'ok', comments: [] } }, response: { ok: true } })
        })

        mountViewModel()
        // First load completes and, because the review is still processing, arms the 3 s poll.
        await flushPromises()
        expect(countOverviewCalls()).toBe(1)

        // First poll tick fires and its overview fetch is held open (in flight).
        await vi.advanceTimersByTimeAsync(3000)
        expect(countOverviewCalls()).toBe(2)

        // A second tick fires while the previous poll is still in flight: the in-flight guard must skip
        // it, so no third /jobs/{id}/protocol request is issued.
        await vi.advanceTimersByTimeAsync(3000)
        expect(countOverviewCalls()).toBe(2)

        // Drain the held fetch so the timer queue can settle cleanly.
        for (const release of releasers) release()
        await flushPromises()
    })
})
