// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises } from '@vue/test-utils'
import { createApp, defineComponent, reactive } from 'vue'
import { useJobProtocolViewModel } from '@/features/job-protocol/composables/useJobProtocolViewModel'

const mockRoute = reactive({ params: { id: 'job-abc' }, query: { clientId: 'client-1' } as Record<string, unknown>, name: 'job-protocol' })
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

const stopJobMock = vi.fn()
const restartJobMock = vi.fn()
vi.mock('@/services/jobsService', () => ({
    stopJob: (...args: unknown[]) => stopJobMock(...args),
    restartJob: (...args: unknown[]) => restartJobMock(...args),
}))

let assignedAdmin = true
vi.mock('@/composables/useSession', () => ({
    useSession: () => ({
        hasClientRole: (clientId: string, minRole: number) => clientId === 'client-1' && (assignedAdmin ? 1 : 0) >= minRole,
    }),
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
    id: 'pass-1',
    jobId: 'job-abc',
    label: 'src/foo.ts',
    startedAt: '2024-01-01T00:00:00Z',
    completedAt: '2024-01-01T00:01:00Z',
    totalInputTokens: 100,
    totalOutputTokens: 50,
    finalSummary: null,
    finalComments: null,
    events: [],
}

const sampleResult = { status: 'processing', result: { summary: '', comments: [] } }

function detailWithStatus(status: string) {
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
        completedAt: null,
    }
}

function mockEndpoints(status: string) {
    mockGet.mockImplementation((path: string) => {
        if (path === '/jobs/{id}/protocol') {
            return Promise.resolve({ data: [baseProtocol], response: { ok: true } })
        }
        if (path === '/jobs/{id}') {
            return Promise.resolve({ data: detailWithStatus(status), response: { ok: true } })
        }
        return Promise.resolve({ data: sampleResult, response: { ok: true } })
    })
}

const mountedApps: ReturnType<typeof createApp>[] = []

async function mountViewModel(): Promise<ReturnType<typeof useJobProtocolViewModel>> {
    let vm: ReturnType<typeof useJobProtocolViewModel> | null = null
    const app = createApp(defineComponent({
        setup() {
            vm = useJobProtocolViewModel()
            return () => null
        },
    }))
    app.mount(document.createElement('div'))
    mountedApps.push(app)
    await flushPromises()
    if (!vm) throw new Error('Failed to mount view model.')
    return vm
}

describe('useJobProtocolViewModel — stop action', () => {
    beforeEach(() => {
        vi.clearAllMocks()
        assignedAdmin = true
        mockRoute.params.id = 'job-abc'
        mockRoute.query = { clientId: 'client-1' }
    })

    afterEach(() => {
        mountedApps.forEach((app) => app.unmount())
        mountedApps.length = 0
        vi.restoreAllMocks()
    })

    it('offers Stop for a running job when the user is a client-administrator', async () => {
        mockEndpoints('processing')
        const vm = await mountViewModel()
        expect(vm.canStop).toBe(true)
    })

    it('offers Stop for a queued (pending) job', async () => {
        mockEndpoints('pending')
        const vm = await mountViewModel()
        expect(vm.canStop).toBe(true)
    })

    it('hides Stop for a non-administrator', async () => {
        assignedAdmin = false
        mockEndpoints('processing')
        const vm = await mountViewModel()
        expect(vm.canStop).toBe(false)
    })

    it('hides Stop for a terminal job', async () => {
        mockEndpoints('completed')
        const vm = await mountViewModel()
        expect(vm.canStop).toBe(false)
    })

    it('calls the stop service and reloads the protocol', async () => {
        mockEndpoints('processing')
        const vm = await mountViewModel()
        stopJobMock.mockResolvedValue({ jobId: 'job-abc', status: 'stopped' })
        mockGet.mockClear()

        await vm.stop()
        await flushPromises()

        expect(stopJobMock).toHaveBeenCalledWith('job-abc')
        // The protocol is reloaded after a successful stop.
        expect(mockGet).toHaveBeenCalledWith('/jobs/{id}/protocol', expect.anything())
    })

    it('does not call the stop service for a terminal job', async () => {
        mockEndpoints('completed')
        const vm = await mountViewModel()

        await vm.stop()
        await flushPromises()

        expect(stopJobMock).not.toHaveBeenCalled()
    })
})
