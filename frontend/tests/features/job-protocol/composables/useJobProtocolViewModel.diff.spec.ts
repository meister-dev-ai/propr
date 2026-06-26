// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license information.

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises } from '@vue/test-utils'
import { createApp, defineComponent, nextTick } from 'vue'
import { useJobProtocolViewModel } from '@/features/job-protocol/composables/useJobProtocolViewModel'
import type { components } from '@/types'

type FileDiffDto = components['schemas']['FileDiffDto']

vi.mock('vue-router', () => ({
    useRoute: () => ({ params: {}, query: {}, name: 'job-protocol' }),
    useRouter: () => ({ replace: vi.fn().mockResolvedValue(undefined), push: vi.fn() }),
}))

vi.mock('@/services/api', () => ({
    createAdminClient: () => ({}),
}))

vi.mock('@/composables/useSession', () => ({
    useSession: () => ({ getAccessToken: () => 'test-token' }),
}))

const stubRuntime = { mode: 'live' as const, isMock: false, apiBaseUrl: 'https://api.test' }

vi.mock('@/app/runtime/runtimeContext', () => ({
    getActiveRuntime: () => stubRuntime,
    setActiveRuntime: vi.fn(),
    provideRuntime: vi.fn(),
}))

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

    if (!vm) {
        throw new Error('Failed to mount view model.')
    }

    return vm
}

describe('useJobProtocolViewModel diff state', () => {
    let fetchMock: ReturnType<typeof vi.fn>
    let originalFetch: typeof globalThis.fetch

    beforeEach(() => {
        originalFetch = global.fetch
        fetchMock = vi.fn()
        global.fetch = fetchMock as unknown as typeof globalThis.fetch
    })

    afterEach(() => {
        global.fetch = originalFetch
        vi.restoreAllMocks()
    })

    it('loadFileDiff fetches the diff endpoint and stores the response', async () => {
        const expected: FileDiffDto = {
            filePath: 'src/Services/Foo.cs',
            unifiedDiff: '+added\n-removed',
            changeType: 'Modified',
            isBinary: false,
            originalPath: null,
            availability: 'Available',
            availabilityMessage: null,
        } as FileDiffDto
        fetchMock.mockResolvedValueOnce({
            ok: true,
            json: async () => expected,
        })

        const vm = await mountViewModel()

        await vm.loadFileDiff('job-1', 'file-result-1')
        await nextTick()

        expect(fetchMock).toHaveBeenCalledWith(
            expect.stringContaining('/reviewing/jobs/job-1/files/file-result-1/diff'),
            expect.objectContaining({ headers: expect.any(Object) }),
        )
        expect(vm.fileDiff).toEqual(expected)
        expect(vm.diffLoading).toBe(false)
        expect(vm.diffError).toBeNull()
    })

    it('loadFileDiff caches the diff for repeated calls with the same fileResultId', async () => {
        fetchMock.mockResolvedValueOnce({
            ok: true,
            json: async () => ({
                filePath: 'src/Services/Foo.cs',
                unifiedDiff: '+added',
                changeType: 'Modified',
                isBinary: false,
                originalPath: null,
                availability: 'Available',
                availabilityMessage: null,
            }),
        })

        const vm = await mountViewModel()

        await vm.loadFileDiff('job-1', 'file-result-1')
        await nextTick()

        expect(fetchMock).toHaveBeenCalledTimes(1)

        await vm.loadFileDiff('job-1', 'file-result-1')
        await nextTick()

        expect(fetchMock).toHaveBeenCalledTimes(1)
        expect(vm.fileDiff?.filePath).toBe('src/Services/Foo.cs')
    })

    it('loadFileDiff records the error message when the fetch fails', async () => {
        fetchMock.mockResolvedValueOnce({
            ok: false,
            status: 502,
        })

        const vm = await mountViewModel()

        await vm.loadFileDiff('job-1', 'file-result-1')
        await nextTick()

        expect(vm.fileDiff).toBeNull()
        expect(vm.diffError).toContain('502')
        expect(vm.diffLoading).toBe(false)
    })

    it('switches the detail tab to events when clearDiff is called', async () => {
        const vm = await mountViewModel()
        vm.detailTab = 'diff'
        vm.clearDiff()
        expect(vm.detailTab).toBe('events')
        expect(vm.fileDiff).toBeNull()
    })
})
