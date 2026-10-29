// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

import { flushPromises, mount } from '@vue/test-utils'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { ref } from 'vue'

import TenantReviewLimitsSection from '../TenantReviewLimitsSection.vue'
import type { TenantDto, TenantReviewExecutionLimits } from '@/services/tenantAdminService'

const getTenant = vi.fn()
const updateTenantReviewLimits = vi.fn()
const isAdmin = ref(true)

vi.mock('@/services/tenantAdminService', () => ({
  getTenant: (...a: unknown[]) => getTenant(...a),
  updateTenantReviewLimits: (...a: unknown[]) => updateTenantReviewLimits(...a),
}))

vi.mock('@/composables/useSession', () => ({
  useSession: () => ({ isAdmin }),
}))

const tenant = (limits: TenantReviewExecutionLimits | null): TenantDto =>
  ({
    id: 't1',
    slug: 'acme',
    displayName: 'Acme',
    isActive: true,
    localLoginEnabled: true,
    isEditable: true,
    createdAt: '2026-09-01T00:00:00Z',
    updatedAt: '2026-09-01T00:00:00Z',
    reviewExecutionLimits: limits,
  }) as TenantDto

const section = () => mount(TenantReviewLimitsSection, { props: { tenantId: 't1' } })

describe('TenantReviewLimitsSection', () => {
  beforeEach(() => {
    getTenant.mockReset()
    updateTenantReviewLimits.mockReset()
    isAdmin.value = true
    getTenant.mockResolvedValue(tenant({ maxConcurrentReviews: 2, maxReviewDurationSeconds: 1800 }))
  })

  it('lets a platform administrator change both limits', async () => {
    updateTenantReviewLimits.mockResolvedValue({ maxConcurrentReviews: 1, maxReviewDurationSeconds: 600 })
    const wrapper = section()
    await flushPromises()

    expect(wrapper.get<HTMLInputElement>('[data-testid="tenant-max-concurrent-reviews"]').element.value).toBe('2')
    expect(wrapper.get<HTMLInputElement>('[data-testid="tenant-max-review-duration"]').element.value).toBe('30')

    await wrapper.get('[data-testid="tenant-max-concurrent-reviews"]').setValue('1')
    await wrapper.get('[data-testid="tenant-max-review-duration"]').setValue('10')
    await wrapper.get('[data-testid="tenant-review-limits-save"]').trigger('click')
    await flushPromises()

    expect(updateTenantReviewLimits).toHaveBeenCalledWith('t1', { maxConcurrentReviews: 1, maxReviewDurationSeconds: 600 })
    expect(wrapper.find('[data-testid="tenant-review-limits-saved"]').exists()).toBe(true)
  })

  it('clears a limit whose field a platform administrator empties', async () => {
    updateTenantReviewLimits.mockResolvedValue({ maxConcurrentReviews: 2, maxReviewDurationSeconds: null })
    const wrapper = section()
    await flushPromises()

    await wrapper.get('[data-testid="tenant-max-review-duration"]').setValue('')
    await wrapper.get('[data-testid="tenant-review-limits-save"]').trigger('click')
    await flushPromises()

    expect(updateTenantReviewLimits).toHaveBeenCalledWith('t1', { maxConcurrentReviews: 2, maxReviewDurationSeconds: null })
  })

  it('shows a tenant administrator the limits without a form', async () => {
    isAdmin.value = false
    const wrapper = section()
    await flushPromises()

    expect(wrapper.find('[data-testid="tenant-max-concurrent-reviews"]').exists()).toBe(false)
    expect(wrapper.find('[data-testid="tenant-review-limits-save"]').exists()).toBe(false)
    expect(wrapper.get('[data-testid="tenant-max-concurrent-reviews-readout"]').text()).toBe('2 reviews at a time')
    expect(wrapper.get('[data-testid="tenant-max-review-duration-readout"]').text()).toBe('30 minutes')
    expect(wrapper.get('[data-testid="tenant-review-limits-readonly"]').text()).toContain('platform administrator')
  })

  it('shows a tenant without limits as having none', async () => {
    isAdmin.value = false
    getTenant.mockResolvedValue(tenant(null))
    const wrapper = section()
    await flushPromises()

    expect(wrapper.get('[data-testid="tenant-max-concurrent-reviews-readout"]').text()).toBe('No limit')
    expect(wrapper.get('[data-testid="tenant-max-review-duration-readout"]').text()).toBe('No limit')
  })

  it('reports a value the backend would refuse and keeps the save disabled', async () => {
    const wrapper = section()
    await flushPromises()

    await wrapper.get('[data-testid="tenant-max-concurrent-reviews"]').setValue('0')

    expect(wrapper.get('[data-testid="tenant-review-limits-validation"]').text()).toContain('at least 1')
    expect(wrapper.get<HTMLButtonElement>('[data-testid="tenant-review-limits-save"]').element.disabled).toBe(true)
  })

  it('offers to read again when the limits could not be read', async () => {
    getTenant.mockRejectedValueOnce(new Error('offline'))
    const wrapper = section()
    await flushPromises()

    expect(wrapper.find('[data-testid="tenant-max-concurrent-reviews"]').exists()).toBe(false)
    expect(wrapper.find('[data-testid="tenant-review-limits-unavailable"]').exists()).toBe(true)

    await wrapper.get('[data-testid="tenant-review-limits-retry"]').trigger('click')
    await flushPromises()

    expect(getTenant).toHaveBeenCalledTimes(2)
    expect(wrapper.get<HTMLInputElement>('[data-testid="tenant-max-concurrent-reviews"]').element.value).toBe('2')
  })
})
