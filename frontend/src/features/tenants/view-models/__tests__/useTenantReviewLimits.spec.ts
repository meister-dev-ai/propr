// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

import { describe, expect, it, vi } from 'vitest'
import { ref } from 'vue'
import {
  formatConcurrentReviews,
  formatReviewDuration,
  useTenantReviewLimits,
} from '@/features/tenants/view-models/useTenantReviewLimits'
import type { TenantDto, TenantReviewExecutionLimits } from '@/services/tenantAdminService'

function tenant(id: string, limits: TenantReviewExecutionLimits | null): TenantDto {
  return {
    id,
    slug: id,
    displayName: id,
    isActive: true,
    localLoginEnabled: true,
    isEditable: true,
    createdAt: '2026-01-01T00:00:00Z',
    updatedAt: '2026-01-01T00:00:00Z',
    reviewExecutionLimits: limits,
  }
}

describe('useTenantReviewLimits', () => {
  it('fills the fields from the stored limits, with the duration in minutes', async () => {
    const loader = vi.fn().mockResolvedValue(tenant('t1', { maxConcurrentReviews: 2, maxReviewDurationSeconds: 1800 }))
    const vm = useTenantReviewLimits('t1', { loader })

    await vm.loadLimits()

    expect(loader).toHaveBeenCalledWith('t1')
    expect(vm.editedConcurrentReviews.value).toBe('2')
    expect(vm.editedDurationMinutes.value).toBe('30')
    expect(vm.isSaveEnabled()).toBe(false)
  })

  it('reads a tenant without limits as two blank fields', async () => {
    const vm = useTenantReviewLimits('t1', { loader: () => Promise.resolve(tenant('t1', null)) })

    await vm.loadLimits()

    expect(vm.limits.value).toEqual({ maxConcurrentReviews: null, maxReviewDurationSeconds: null })
    expect(vm.editedConcurrentReviews.value).toBe('')
    expect(vm.editedDurationMinutes.value).toBe('')
  })

  it('stores the duration in seconds and a blank field as no limit', async () => {
    const saver = vi.fn().mockResolvedValue({ maxConcurrentReviews: null, maxReviewDurationSeconds: 90 })
    const vm = useTenantReviewLimits('t1', {
      loader: () => Promise.resolve(tenant('t1', { maxConcurrentReviews: 3, maxReviewDurationSeconds: null })),
      saver,
    })
    await vm.loadLimits()

    vm.editedConcurrentReviews.value = ''
    vm.editedDurationMinutes.value = 1.5
    await vm.saveLimits()

    expect(saver).toHaveBeenCalledWith('t1', { maxConcurrentReviews: null, maxReviewDurationSeconds: 90 })
    expect(vm.saved.value).toBe(true)
    expect(vm.limits.value).toEqual({ maxConcurrentReviews: null, maxReviewDurationSeconds: 90 })
  })

  it.each([
    ['0', '', 'Concurrent reviews'],
    ['1.5', '', 'Concurrent reviews'],
    ['', '0.5', 'maximum review duration'],
    ['', '1441', 'maximum review duration'],
  ])('refuses to save values the backend refuses (%s concurrent, %s minutes)', async (concurrent, minutes, message) => {
    const saver = vi.fn()
    const vm = useTenantReviewLimits('t1', {
      loader: () => Promise.resolve(tenant('t1', null)),
      saver,
    })
    await vm.loadLimits()

    vm.editedConcurrentReviews.value = concurrent
    vm.editedDurationMinutes.value = minutes
    await vm.saveLimits()

    expect(vm.validationError.value).toContain(message)
    expect(vm.isSaveEnabled()).toBe(false)
    expect(saver).not.toHaveBeenCalled()
  })

  it('accepts the bounds of the backend: one review, one minute and one day', async () => {
    const saver = vi.fn().mockResolvedValue({ maxConcurrentReviews: 1, maxReviewDurationSeconds: 86_400 })
    const vm = useTenantReviewLimits('t1', { loader: () => Promise.resolve(tenant('t1', null)), saver })
    await vm.loadLimits()

    vm.editedConcurrentReviews.value = 1
    vm.editedDurationMinutes.value = 1
    expect(vm.validationError.value).toBe('')

    vm.editedDurationMinutes.value = 1440
    await vm.saveLimits()

    expect(saver).toHaveBeenCalledWith('t1', { maxConcurrentReviews: 1, maxReviewDurationSeconds: 86_400 })
  })

  it('shows the message of a refused save and keeps the stored values', async () => {
    const vm = useTenantReviewLimits('t1', {
      loader: () => Promise.resolve(tenant('t1', { maxConcurrentReviews: 2, maxReviewDurationSeconds: null })),
      saver: () => Promise.reject(new Error('Admin role required.')),
    })
    await vm.loadLimits()

    vm.editedConcurrentReviews.value = 4
    await vm.saveLimits()

    expect(vm.error.value).toBe('Admin role required.')
    expect(vm.saved.value).toBe(false)
    expect(vm.limits.value).toEqual({ maxConcurrentReviews: 2, maxReviewDurationSeconds: null })
  })

  it('reports a failed read and offers nothing to save', async () => {
    const vm = useTenantReviewLimits('t1', { loader: () => Promise.reject(new Error('offline')) })

    await vm.loadLimits()

    expect(vm.error.value).toContain('could not be loaded')
    expect(vm.limits.value).toBeNull()
    expect(vm.isSaveEnabled()).toBe(false)
  })

  // The tenant route reuses the section, so a read for the previous tenant can answer after the read for the next
  // one. The form shows the tenant on screen, and a save goes to that tenant.
  it('keeps the newest read when the tenant changes and saves to the tenant it shows', async () => {
    let answerFirstRead: (loaded: TenantDto) => void = () => {}
    const firstRead = new Promise<TenantDto>(resolve => {
      answerFirstRead = resolve
    })
    const tenantId = ref('t1')
    const saver = vi.fn().mockResolvedValue({ maxConcurrentReviews: 5, maxReviewDurationSeconds: null })
    const vm = useTenantReviewLimits(tenantId, {
      loader: id => (id === 't1' ? firstRead : Promise.resolve(tenant('t2', { maxConcurrentReviews: 4, maxReviewDurationSeconds: null }))),
      saver,
    })

    const staleLoad = vm.loadLimits()
    tenantId.value = 't2'
    await vm.loadLimits()
    answerFirstRead(tenant('t1', { maxConcurrentReviews: 1, maxReviewDurationSeconds: 60 }))
    await staleLoad

    expect(vm.editedConcurrentReviews.value).toBe('4')

    vm.editedConcurrentReviews.value = 5
    await vm.saveLimits()

    expect(saver).toHaveBeenCalledWith('t2', { maxConcurrentReviews: 5, maxReviewDurationSeconds: null })
  })

  it('keeps the form of the tenant shown when a save of the tenant before answers late', async () => {
    let answerSave: (stored: TenantReviewExecutionLimits) => void = () => {}
    const tenantId = ref('t1')
    const saver = vi.fn().mockReturnValue(new Promise<TenantReviewExecutionLimits>(resolve => {
      answerSave = resolve
    }))
    const vm = useTenantReviewLimits(tenantId, {
      loader: id => Promise.resolve(id === 't1'
        ? tenant('t1', { maxConcurrentReviews: 1, maxReviewDurationSeconds: null })
        : tenant('t2', { maxConcurrentReviews: 4, maxReviewDurationSeconds: 600 })),
      saver,
    })
    await vm.loadLimits()

    vm.editedConcurrentReviews.value = 2
    const pendingSave = vm.saveLimits()
    tenantId.value = 't2'
    await vm.loadLimits()
    answerSave({ maxConcurrentReviews: 2, maxReviewDurationSeconds: null })
    await pendingSave

    expect(saver).toHaveBeenCalledWith('t1', { maxConcurrentReviews: 2, maxReviewDurationSeconds: null })
    expect(vm.limits.value).toEqual({ maxConcurrentReviews: 4, maxReviewDurationSeconds: 600 })
    expect(vm.editedConcurrentReviews.value).toBe('4')
    expect(vm.editedDurationMinutes.value).toBe('10')
    expect(vm.saved.value).toBe(false)
    expect(vm.saving.value).toBe(false)
  })
})

describe('review limit readouts', () => {
  it.each([
    [null, 'No limit'],
    [1, '1 review at a time'],
    [3, '3 reviews at a time'],
  ])('writes %s concurrent reviews as "%s"', (value, expected) => {
    expect(formatConcurrentReviews(value)).toBe(expected)
  })

  it.each([
    [null, 'No limit'],
    [60, '1 minute'],
    [90, '1 minute 30 seconds'],
    [1800, '30 minutes'],
    [5400, '1 hour 30 minutes'],
    [86_400, '24 hours'],
  ])('writes a duration of %s seconds as "%s"', (seconds, expected) => {
    expect(formatReviewDuration(seconds)).toBe(expected)
  })
})
