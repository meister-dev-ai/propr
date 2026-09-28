// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

import { flushPromises, mount } from '@vue/test-utils'
import { beforeEach, describe, expect, it, vi } from 'vitest'

import TenantReasoningCaptureSection from '../TenantReasoningCaptureSection.vue'
import type { ReasoningCapturePolicy, TenantDto } from '@/services/tenantAdminService'

const getTenant = vi.fn()
const updateTenant = vi.fn()

vi.mock('@/services/tenantAdminService', () => ({
  getTenant: (...a: unknown[]) => getTenant(...a),
  updateTenant: (...a: unknown[]) => updateTenant(...a),
}))

const tenant = (policy: ReasoningCapturePolicy, installationCaptures: boolean): TenantDto =>
  ({
    id: 't1',
    slug: 'acme',
    displayName: 'Acme',
    isActive: true,
    localLoginEnabled: true,
    isEditable: true,
    createdAt: '2026-09-01T00:00:00Z',
    updatedAt: '2026-09-01T00:00:00Z',
    reasoningCapturePolicy: policy,
    installationDefaultCapturesReasoning: installationCaptures,
  }) as TenantDto

const section = () => mount(TenantReasoningCaptureSection, { props: { tenantId: 't1' } })

describe('TenantReasoningCaptureSection', () => {
  beforeEach(() => {
    getTenant.mockReset()
    updateTenant.mockReset()
    getTenant.mockResolvedValue(tenant('installationDefault', true))
    updateTenant.mockResolvedValue(tenant('installationDefault', true))
  })

  it('shows the policy the tenant already has', async () => {
    getTenant.mockResolvedValue(tenant('disabled', true))
    const wrapper = section()
    await flushPromises()

    expect(
      wrapper.get<HTMLInputElement>('[data-testid="tenant-reasoning-capture-disabled"]').element.checked,
    ).toBe(true)
    expect(
      wrapper.get<HTMLInputElement>('[data-testid="tenant-reasoning-capture-installationDefault"]').element.checked,
    ).toBe(false)
  })

  // "Follow the installation default" says nothing on its own, so the switch it follows has to be named.
  it.each([
    [true, 'currently on'],
    [false, 'currently off'],
  ])('names what the installation default does (%s)', async (installationCaptures, expected) => {
    getTenant.mockResolvedValue(tenant('installationDefault', installationCaptures))
    const wrapper = section()
    await flushPromises()

    expect(wrapper.get('[data-testid="tenant-reasoning-capture-installation"]').text()).toContain(expected)
  })

  it.each<ReasoningCapturePolicy>(['installationDefault', 'enabled', 'disabled'])(
    'saves the chosen policy and re-reads the tenant (%s)',
    async (choice) => {
      getTenant.mockResolvedValue(tenant('enabled', true))
      const wrapper = section()
      await flushPromises()

      await wrapper.get(`[data-testid="tenant-reasoning-capture-${choice}"]`).setValue()
      await wrapper.get('[data-testid="tenant-reasoning-capture-save"]').trigger('click')
      await flushPromises()

      expect(updateTenant).toHaveBeenCalledWith('t1', { reasoningCapturePolicy: choice })
      expect(getTenant).toHaveBeenCalledTimes(2)
      expect(wrapper.get('[data-testid="tenant-reasoning-capture-saved"]').text()).toContain('saved')
    },
  )

  // The stored value decides the next job, and another administrator may have changed it in between, so what
  // the section shows after a save is the re-read tenant and not the value it sent.
  it('shows the re-read tenant after a save', async () => {
    getTenant
      .mockResolvedValueOnce(tenant('enabled', true))
      .mockResolvedValueOnce(tenant('disabled', false))
    const wrapper = section()
    await flushPromises()

    await wrapper.get('[data-testid="tenant-reasoning-capture-installationDefault"]').setValue()
    await wrapper.get('[data-testid="tenant-reasoning-capture-save"]').trigger('click')
    await flushPromises()

    expect(
      wrapper.get<HTMLInputElement>('[data-testid="tenant-reasoning-capture-disabled"]').element.checked,
    ).toBe(true)
    expect(wrapper.get('[data-testid="tenant-reasoning-capture-installation"]').text()).toContain('currently off')
  })

  // The tenant detail route reuses this component across tenants, so a navigation changes the prop without
  // remounting. A section left on the previous tenant would show its policy and save onto the new one.
  it('reloads when the tenant id changes', async () => {
    getTenant
      .mockResolvedValueOnce(tenant('enabled', true))
      .mockResolvedValueOnce(tenant('disabled', false))
    const wrapper = section()
    await flushPromises()

    await wrapper.setProps({ tenantId: 't2' })
    await flushPromises()

    expect(getTenant).toHaveBeenLastCalledWith('t2')
    expect(
      wrapper.get<HTMLInputElement>('[data-testid="tenant-reasoning-capture-disabled"]').element.checked,
    ).toBe(true)
    expect(wrapper.get('[data-testid="tenant-reasoning-capture-installation"]').text()).toContain('currently off')
  })

  // Two sections rendered together are one browser radio group when they share a name, and choosing a policy
  // for one tenant then clears the choice for the other.
  it('keeps each tenant choice when two sections are rendered together', async () => {
    const first = mount(TenantReasoningCaptureSection, {
      props: { tenantId: 't1' },
      attachTo: document.body,
    })
    const second = mount(TenantReasoningCaptureSection, {
      props: { tenantId: 't2' },
      attachTo: document.body,
    })
    await flushPromises()

    await first.get('[data-testid="tenant-reasoning-capture-enabled"]').setValue()
    await second.get('[data-testid="tenant-reasoning-capture-disabled"]').setValue()
    await flushPromises()

    expect(
      first.get<HTMLInputElement>('[data-testid="tenant-reasoning-capture-enabled"]').element.checked,
    ).toBe(true)
    expect(
      second.get<HTMLInputElement>('[data-testid="tenant-reasoning-capture-disabled"]').element.checked,
    ).toBe(true)

    first.unmount()
    second.unmount()
  })

  // A navigation starts a second read while the first is still open. A first response arriving after the
  // second would show one tenant's policy under the other tenant's name, and save it onto that tenant.
  it('ignores a read for a tenant it has already navigated away from', async () => {
    const readNeverStarted = () => {
      throw new Error('the read for the first tenant was never started')
    }
    let answerTheFirstRead: (value: TenantDto) => void = readNeverStarted
    getTenant
      .mockImplementationOnce(
        () =>
          new Promise<TenantDto>((resolve) => {
            answerTheFirstRead = resolve
          }),
      )
      .mockResolvedValueOnce(tenant('disabled', false))

    const wrapper = section()
    await wrapper.setProps({ tenantId: 't2' })
    await flushPromises()

    // The first tenant was read and its answer is still open, so what follows is a stale response and not a
    // request that never happened.
    expect(getTenant).toHaveBeenNthCalledWith(1, 't1')
    expect(answerTheFirstRead).not.toBe(readNeverStarted)

    answerTheFirstRead(tenant('enabled', true))
    await flushPromises()

    expect(
      wrapper.get<HTMLInputElement>('[data-testid="tenant-reasoning-capture-disabled"]').element.checked,
    ).toBe(true)
    expect(wrapper.get('[data-testid="tenant-reasoning-capture-installation"]').text()).toContain('currently off')
  })

  // A form rendered over a failed read would save the value this component started at, which is a policy the
  // tenant never chose.
  it('renders no form when the tenant could not be read', async () => {
    getTenant.mockRejectedValue(new Error('the tenant is unreachable'))
    const wrapper = section()
    await flushPromises()

    expect(wrapper.find('[data-testid="tenant-reasoning-capture-save"]').exists()).toBe(false)
    expect(wrapper.get('[data-testid="tenant-reasoning-capture-error"]').text()).toContain('unreachable')
    expect(wrapper.find('[data-testid="tenant-reasoning-capture-retry"]').exists()).toBe(true)
  })

  it('reports a failed save and keeps the form', async () => {
    updateTenant.mockRejectedValue(new Error('the policy was refused'))
    const wrapper = section()
    await flushPromises()

    await wrapper.get('[data-testid="tenant-reasoning-capture-save"]').trigger('click')
    await flushPromises()

    expect(wrapper.get('[data-testid="tenant-reasoning-capture-error"]').text()).toContain('refused')
    expect(wrapper.find('[data-testid="tenant-reasoning-capture-save"]').exists()).toBe(true)
  })

  // Withholding reasoning stops the reasoning text, not the counts budgets are computed from; an operator
  // reading the control has to be able to tell.
  it('states that token counts survive a withholding policy', async () => {
    getTenant.mockResolvedValue(tenant('disabled', true))
    const wrapper = section()
    await flushPromises()

    expect(wrapper.get('[data-testid="tenant-reasoning-capture-effect"]').text()).toContain('Token counts')
  })
})
