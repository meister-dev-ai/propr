// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

import { describe, expect, it, vi } from 'vitest'
import { useTenantBudgetCaps } from '@/features/tenants/view-models/useTenantBudgetCaps'
import type { TenantDto } from '@/services/tenantAdminService'

function tenant(softCapUsd: number | null, hardCapUsd: number | null): TenantDto {
  return {
    id: 't1',
    slug: 'acme',
    displayName: 'Acme',
    isActive: true,
    localLoginEnabled: true,
    isEditable: true,
    createdAt: '2026-01-01T00:00:00Z',
    updatedAt: '2026-01-01T00:00:00Z',
    budget: { monthlySoftCapUsd: softCapUsd, monthlyHardCapUsd: hardCapUsd },
  }
}

describe('useTenantBudgetCaps', () => {
  it('fills the fields from the stored caps of the tenant it was given', async () => {
    const loader = vi.fn().mockResolvedValue(tenant(4000, null))
    const vm = useTenantBudgetCaps('t1', { loader })

    await vm.loadCaps()

    expect(loader).toHaveBeenCalledWith('t1')
    expect(vm.editedSoftCapUsd.value).toBe('4000')
    expect(vm.editedHardCapUsd.value).toBe('')
    expect(vm.isSaveEnabled()).toBe(false)
  })

  it('refuses to save a soft cap above the hard cap', async () => {
    const vm = useTenantBudgetCaps('t1', { loader: () => Promise.resolve(tenant(4000, 5000)) })
    await vm.loadCaps()

    vm.editedSoftCapUsd.value = '6000'

    expect(vm.validationError.value).toContain('soft cap')
    expect(vm.isSaveEnabled()).toBe(false)
  })

  it('clears the save indicator while a new load is still in flight', async () => {
    let answerSecondLoad: (loaded: TenantDto) => void = () => {}
    const secondRead = new Promise<TenantDto>(resolve => {
      answerSecondLoad = resolve
    })
    let reads = 0
    const vm = useTenantBudgetCaps('t1', {
      loader: () => {
        reads += 1
        return reads === 1 ? Promise.resolve(tenant(4000, 5000)) : secondRead
      },
      saver: () => Promise.resolve(tenant(4000, 6000)),
    })
    await vm.loadCaps()
    vm.editedHardCapUsd.value = '6000'
    await vm.saveCaps()
    expect(vm.saved.value).toBe(true)

    // The indicator describes the fields on screen. While the read it was reset for is still out, the form
    // shows the saved values, and a success message left on them claims they were just stored.
    const reloading = vm.loadCaps()
    await Promise.resolve()
    expect(vm.saved.value).toBe(false)

    answerSecondLoad(tenant(4000, 6000))
    await reloading

    expect(vm.saved.value).toBe(false)
  })

  // The section loads on mount and again when the budgeting capability arrives, so two reads can be out at
  // once. The earlier one describes the form the operator is no longer looking at.
  it('keeps the latest load when an earlier one answers after it', async () => {
    let answerFirstLoad: (loaded: TenantDto) => void = () => {}
    const firstRead = new Promise<TenantDto>(resolve => {
      answerFirstLoad = resolve
    })
    let reads = 0
    const vm = useTenantBudgetCaps('t1', {
      loader: () => {
        reads += 1
        return reads === 1 ? firstRead : Promise.resolve(tenant(4000, 5000))
      },
    })

    const firstLoad = vm.loadCaps()
    await vm.loadCaps()

    expect(vm.editedSoftCapUsd.value).toBe('4000')

    answerFirstLoad(tenant(10, 20))
    await firstLoad

    expect(vm.editedSoftCapUsd.value).toBe('4000')
    expect(vm.editedHardCapUsd.value).toBe('5000')
    expect(vm.loading.value).toBe(false)
  })

  it('keeps the latest load when an earlier one fails after it', async () => {
    let failFirstLoad: (reason: Error) => void = () => {}
    const firstRead = new Promise<TenantDto>((_, reject) => {
      failFirstLoad = reject
    })
    let reads = 0
    const vm = useTenantBudgetCaps('t1', {
      loader: () => {
        reads += 1
        return reads === 1 ? firstRead : Promise.resolve(tenant(4000, 5000))
      },
    })

    const firstLoad = vm.loadCaps()
    await vm.loadCaps()

    failFirstLoad(new Error('boom'))
    await firstLoad

    expect(vm.error.value).toBe('')
    expect(vm.editedSoftCapUsd.value).toBe('4000')
  })

  // A field that parses to NaN or Infinity is not a cap. Without an error the save stays enabled and the
  // request body serializes the value as null, which clears the cap the operator was editing.
  it.each(['abc', 'Infinity', '12,50'])('refuses %s as a cap and blocks the save', async (typed) => {
    const saver = vi.fn()
    const vm = useTenantBudgetCaps('t1', { loader: () => Promise.resolve(tenant(4000, 5000)), saver })
    await vm.loadCaps()

    vm.editedSoftCapUsd.value = typed

    expect(vm.validationError.value).toContain('must be a number')
    expect(vm.isSaveEnabled()).toBe(false)

    await vm.saveCaps()

    expect(saver).not.toHaveBeenCalled()
  })

  it('enables saving once a field differs from what is stored', async () => {
    const vm = useTenantBudgetCaps('t1', { loader: () => Promise.resolve(tenant(4000, 5000)) })
    await vm.loadCaps()

    vm.editedHardCapUsd.value = '6000'

    expect(vm.isSaveEnabled()).toBe(true)
  })

  it('sends a blank field as a cleared cap and adopts what came back', async () => {
    const saver = vi.fn().mockResolvedValue(tenant(null, 5000))
    const vm = useTenantBudgetCaps('t1', { loader: () => Promise.resolve(tenant(4000, 5000)), saver })
    await vm.loadCaps()

    vm.editedSoftCapUsd.value = ''
    await vm.saveCaps()

    expect(saver).toHaveBeenCalledWith('t1', null, 5000)
    expect(vm.editedSoftCapUsd.value).toBe('')
    expect(vm.saved.value).toBe(true)
    expect(vm.isSaveEnabled()).toBe(false)
  })

  it('reports a refused save and keeps the edit', async () => {
    const vm = useTenantBudgetCaps('t1', {
      loader: () => Promise.resolve(tenant(4000, 5000)),
      saver: () => Promise.reject(new Error('Budgeting requires a commercial license.')),
    })
    await vm.loadCaps()

    vm.editedHardCapUsd.value = '6000'
    await vm.saveCaps()

    expect(vm.error.value).toBe('Budgeting requires a commercial license.')
    expect(vm.editedHardCapUsd.value).toBe('6000')
  })

  it('reports a failed load and leaves the fields empty and idle', async () => {
    const vm = useTenantBudgetCaps('t1', { loader: () => Promise.reject(new Error('boom')) })

    await vm.loadCaps()

    expect(vm.error.value).toContain('Failed to load')
    expect(vm.loading.value).toBe(false)
    expect(vm.tenant.value).toBeNull()
    expect(vm.editedSoftCapUsd.value).toBe('')
    expect(vm.editedHardCapUsd.value).toBe('')
    expect(vm.saved.value).toBe(false)
    expect(vm.isSaveEnabled()).toBe(false)
  })
})
