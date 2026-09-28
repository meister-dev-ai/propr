// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { defineComponent, h } from 'vue'

interface JobDetailStub {
  status: string
  admissionRefusalReason?: string | null
  heldUntil?: string | null
}

let jobDetail: JobDetailStub = { status: 'processing' }
const push = vi.fn()
const restartJobMock = vi.fn()

const get = vi.fn(async (path: string) => {
  if (path === '/jobs/{id}/protocol') {
    return { data: [] }
  }
  if (path === '/jobs/{id}') {
    return { data: jobDetail }
  }
  return { data: undefined }
})

vi.mock('vue-router', () => ({
  useRoute: () => ({ params: { id: 'job-1' }, query: {} }),
  useRouter: () => ({ push, replace: vi.fn() }),
}))

vi.mock('@/services/api', () => ({
  createAdminClient: () => ({ GET: get }),
}))

vi.mock('@/services/jobsService', () => ({
  restartJob: (id: string) => restartJobMock(id),
  stopJob: vi.fn(),
}))

vi.mock('@/composables/useSession', () => ({
  useSession: () => ({ hasClientRole: () => true, getAccessToken: () => null }),
}))

vi.mock('@/services/findingDismissalsService', () => ({
  createDismissal: vi.fn(),
}))

vi.mock('@/services/codeInsightFindingsService', () => ({
  fetchFindingClassifications: vi.fn().mockResolvedValue([]),
}))

import { useJobProtocolViewModel } from '@/features/job-protocol/composables/useJobProtocolViewModel'

type JobProtocolViewModel = ReturnType<typeof useJobProtocolViewModel>

/** The wrappers this file has mounted, unmounted after every test so no poll timer outlives it. */
const mountedWrappers: { unmount: () => void }[] = []

/** Mounts the view model in a host component, so its mount and unmount hooks run as they do in the view. */
async function mountViewModel(): Promise<{ vm: JobProtocolViewModel }> {
  let vm!: JobProtocolViewModel
  const wrapper = mount(
    defineComponent({
      setup() {
        vm = useJobProtocolViewModel()
        return () => h('div')
      },
    }),
  )
  mountedWrappers.push(wrapper)
  await flushPromises()
  return { vm }
}

// A test that fails before it reaches its own unmount leaves the composable polling, and those ticks arrive
// during later tests.
afterEach(() => {
  while (mountedWrappers.length > 0) {
    mountedWrappers.pop()!.unmount()
  }
})

/** How many times the job detail was fetched, which is one per load of the view or poll tick. */
function detailFetchCount(): number {
  return get.mock.calls.filter(([path]) => path === '/jobs/{id}').length
}

describe('job protocol polling lifecycle', () => {
  beforeEach(() => {
    vi.useFakeTimers()
    get.mockClear()
    push.mockReset()
    restartJobMock.mockReset()
    jobDetail = { status: 'processing' }
  })

  afterEach(() => {
    vi.useRealTimers()
  })

  // A refused job ended before any model call and only a restart changes it, so the poll that was armed while
  // it was still running has to stop. Left armed it requests the job every three seconds for as long as the
  // view stays open.
  it('stops polling once a running job turns out to have been refused', async () => {
    await mountViewModel()
    expect(detailFetchCount()).toBe(1)

    jobDetail = { status: 'admissionRefused', admissionRefusalReason: 'The diff is larger than this client allows.' }
    await vi.advanceTimersByTimeAsync(3000)
    await flushPromises()
    expect(detailFetchCount()).toBe(2)

    await vi.advanceTimersByTimeAsync(9000)
    await flushPromises()

    expect(detailFetchCount()).toBe(2)
  })

  // A held job is released by the worker once its hour has passed and then runs, so the view has to keep
  // polling and show the run it was waiting for.
  it('polls a held job until its hold is released and it runs', async () => {
    const { vm } = await mountViewModel()

    const heldUntil = new Date(Date.now() + 3000).toISOString()
    jobDetail = { status: 'admissionHeld', heldUntil }
    await vi.advanceTimersByTimeAsync(3000)
    await flushPromises()

    expect(detailFetchCount()).toBe(2)
    expect(vm.admissionState).toBe('held')

    // The hold has passed and the worker has queued the job, which the next tick has to pick up.
    jobDetail = { status: 'processing' }
    await vi.advanceTimersByTimeAsync(3000)
    await flushPromises()

    expect(detailFetchCount()).toBe(3)
    expect(vm.jobStatus).toBe('processing')
    expect(vm.admissionState).toBeNull()
  })

  // Opening the route on a job that is already held is the common case: the hold is decided before anyone
  // looks at the review. Armed only for a running job, the view would stay on the hold until it is reopened.
  it('polls a job that was already held when the view opened', async () => {
    jobDetail = { status: 'admissionHeld', heldUntil: new Date(Date.now() + 3000).toISOString() }

    const { vm } = await mountViewModel()
    expect(detailFetchCount()).toBe(1)

    jobDetail = { status: 'processing' }
    await vi.advanceTimersByTimeAsync(3000)
    await flushPromises()

    expect(detailFetchCount()).toBe(2)
    expect(vm.jobStatus).toBe('processing')
  })
})

describe('job protocol admission banner state', () => {
  beforeEach(() => {
    get.mockClear()
    push.mockReset()
    restartJobMock.mockReset()
  })

  it('carries the refusal reason as the message of a refused job', async () => {
    jobDetail = { status: 'admissionRefused', admissionRefusalReason: 'The diff is larger than this client allows.' }

    const { vm } = await mountViewModel()

    expect(vm.admissionState).toBe('refused')
    expect(vm.admissionMessage).toBe('The diff is larger than this client allows.')
  })

  it('carries the hold explanation as the message of a held job', async () => {
    jobDetail = { status: 'admissionHeld', heldUntil: '2026-07-15T10:00:00Z' }

    const { vm } = await mountViewModel()

    expect(vm.admissionState).toBe('held')
    expect(vm.admissionMessage).toContain('hourly review limit')
  })

  it('carries no message for a job admission never refused or held', async () => {
    jobDetail = { status: 'completed' }

    const { vm } = await mountViewModel()

    expect(vm.admissionState).toBeNull()
    expect(vm.admissionMessage).toBeNull()
  })
})

describe('job protocol restart', () => {
  beforeEach(() => {
    get.mockClear()
    push.mockReset()
    restartJobMock.mockReset()
    jobDetail = { status: 'failed' }
  })

  // The route watcher loads the job the navigation moved to, so a second load here would fetch the protocol,
  // the result and the detail of the queued review twice.
  it('opens the queued review after a restart and leaves the load to the route change', async () => {
    restartJobMock.mockResolvedValue({ jobId: 'job-2' })
    push.mockResolvedValue(undefined)

    const { vm } = await mountViewModel()
    expect(detailFetchCount()).toBe(1)
    await vm.restart()

    expect(push).toHaveBeenCalledWith(expect.objectContaining({ params: { id: 'job-2' } }))
    expect(vm.dismissToast).toEqual({ message: 'Review restarted.', isError: false })
    expect(vm.restarting).toBe(false)
    expect(detailFetchCount()).toBe(1)
  })

  // No navigation happens, so nothing else loads the view and the restart has to.
  it('refreshes this view when the restart queued the job the view is already on', async () => {
    restartJobMock.mockResolvedValue({ jobId: 'job-1' })

    const { vm } = await mountViewModel()
    expect(detailFetchCount()).toBe(1)
    await vm.restart()

    expect(push).not.toHaveBeenCalled()
    expect(detailFetchCount()).toBe(2)
  })

  // The restart queued a review; only the navigation to it failed. Reporting that as a failed restart would
  // tell the operator to restart again, which queues a second review.
  it('reports a restart whose navigation failed as restarted, and says where to find the review', async () => {
    restartJobMock.mockResolvedValue({ jobId: 'job-2' })
    push.mockRejectedValue(new Error('Navigation aborted.'))

    const { vm } = await mountViewModel()
    await vm.restart()

    expect(vm.dismissToast?.isError).toBe(true)
    expect(vm.dismissToast?.message).toContain('Review restarted')
    expect(vm.dismissToast?.message).toContain('review history')
    expect(vm.restarting).toBe(false)
    // The view stayed on the source job, so it is refreshed here.
    expect(detailFetchCount()).toBe(2)
  })

  it('reports a refused restart with the message the server gave', async () => {
    restartJobMock.mockRejectedValue(new Error('Only failed review jobs can be restarted.'))

    const { vm } = await mountViewModel()
    await vm.restart()

    expect(vm.dismissToast).toEqual({
      message: 'Only failed review jobs can be restarted.',
      isError: true,
    })
    expect(push).not.toHaveBeenCalled()
    expect(vm.restarting).toBe(false)
  })
})
