// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { defineComponent, h } from 'vue'

let protocolResponse: { data?: unknown; error?: unknown } = {}

const get = vi.fn(async (path: string) => {
  if (path === '/jobs/{id}/protocol') {
    return protocolResponse
  }
  if (path === '/jobs/{id}') {
    return { data: { status: 'completed', contentWithheld: true } }
  }
  return { data: undefined }
})

vi.mock('vue-router', () => ({
  useRoute: () => ({ params: { id: 'job-1' }, query: {} }),
  useRouter: () => ({ push: vi.fn().mockResolvedValue(undefined), replace: vi.fn().mockResolvedValue(undefined) }),
}))

vi.mock('@/services/api', () => ({
  createAdminClient: () => ({ GET: get }),
}))

vi.mock('@/services/jobsService', () => ({
  restartJob: vi.fn(),
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

const mountedWrappers: { unmount: () => void }[] = []

async function mountViewModel(): Promise<JobProtocolViewModel> {
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
  return vm
}

afterEach(() => {
  while (mountedWrappers.length > 0) {
    mountedWrappers.pop()!.unmount()
  }
})

describe('job protocol of a tenant whose content needs an elevation', () => {
  beforeEach(() => {
    get.mockClear()
    protocolResponse = {
      error: {
        error: 'This content belongs to a tenant.',
        code: 'elevation_required',
        tenantId: 'tenant-1',
        clientId: 'client-1',
      },
    }
  })

  it('offers an elevation in place of the protocol and reports no error', async () => {
    const vm = await mountViewModel()

    expect(vm.elevationRequired).toEqual({ tenantId: 'tenant-1', clientId: 'client-1' })
    expect(vm.error).toBe('')
  })

  it('loads the protocol when the view reloads it after the server granted it', async () => {
    const vm = await mountViewModel()
    expect(vm.protocols).toEqual([])
    protocolResponse = {
      data: [{ id: 'pass-1', jobId: 'job-1', label: 'src/payments.ts', attemptNumber: 1, startedAt: '2026-10-26T10:00:00Z' }],
    }

    await vm.reload()

    expect(vm.elevationRequired).toBeNull()
    expect(vm.error).toBe('')
    expect(vm.protocols.map((protocol) => protocol.id)).toEqual(['pass-1'])
  })
})
