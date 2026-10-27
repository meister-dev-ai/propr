// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

import { enableAutoUnmount, flushPromises, mount } from '@vue/test-utils'
import { computed } from 'vue'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

const mocks = vi.hoisted(() => ({
  getElevationStatus: vi.fn(),
  startElevation: vi.fn(),
  endElevation: vi.fn(),
}))

vi.mock('@/services/elevationsService', () => mocks)
vi.mock('@/composables/useSession', () => ({
  useSession: () => ({
    isAdmin: computed(() => true),
    isAuthenticated: computed(() => true),
    isCapabilityAvailable: (key: string) => key === 'multi-tenancy',
  }),
}))

enableAutoUnmount(afterEach)

async function mountDialog(props: Record<string, unknown> = {}) {
  const { usePlatformAdminElevations } = await import('@/composables/usePlatformAdminElevations')
  await usePlatformAdminElevations().load()
  const { default: Dialog } = await import('../StartElevationDialog.vue')
  return mount(Dialog, {
    props: { isOpen: true, tenantId: 'tenant-1', tenantName: 'Acme', ...props },
    global: { stubs: { Teleport: true } },
    attachTo: document.body,
  })
}

function focusedTestId(): string | null {
  return document.activeElement?.getAttribute('data-testid') ?? document.activeElement?.getAttribute('aria-label') ?? null
}

describe('StartElevationDialog', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    mocks.getElevationStatus.mockResolvedValue({ maxDurationMinutes: 90, active: [] })
    mocks.startElevation.mockResolvedValue({ id: 'elevation-1' })
  })

  it('offers durations up to the maximum of the installation and no longer ones', async () => {
    const wrapper = await mountDialog()

    const options = wrapper
      .findAll('[data-testid="elevation-duration"] option')
      .map((option) => (option.element as HTMLOptionElement).value)
    expect(options).toEqual(['15', '30', '60', '90'])
  })

  it('starts nothing without a reason', async () => {
    const wrapper = await mountDialog()

    expect(wrapper.get('[data-testid="elevation-submit"]').attributes('disabled')).toBeDefined()
    await wrapper.get('form').trigger('submit')
    expect(mocks.startElevation).not.toHaveBeenCalled()
  })

  it('starts an elevation for the preselected client with the reason and the duration', async () => {
    const wrapper = await mountDialog({ clientId: 'client-1', clientName: 'Payments' })

    await wrapper.get('[data-testid="elevation-reason"]').setValue('SUP-1042')
    await wrapper.get('[data-testid="elevation-duration"]').setValue('30')
    await wrapper.get('form').trigger('submit')
    await flushPromises()

    expect(mocks.startElevation).toHaveBeenCalledWith({
      tenantId: 'tenant-1',
      clientId: 'client-1',
      reason: 'SUP-1042',
      durationMinutes: 30,
    })
    expect(wrapper.emitted('started')).toHaveLength(1)
    expect(wrapper.emitted('update:isOpen')?.at(-1)).toEqual([false])
  })

  it('starts an elevation for the whole tenant when no client is chosen', async () => {
    const wrapper = await mountDialog({ clients: [{ id: 'client-1', displayName: 'Payments' }] })

    await wrapper.get('[data-testid="elevation-reason"]').setValue('SUP-7')
    await wrapper.get('form').trigger('submit')
    await flushPromises()

    expect(mocks.startElevation).toHaveBeenCalledWith(expect.objectContaining({ tenantId: 'tenant-1', clientId: null }))
  })

  it('opens as a dialog named by its heading, with the focus on the reason', async () => {
    const wrapper = await mountDialog()
    await flushPromises()

    const dialog = wrapper.get('dialog')
    const heading = document.getElementById(dialog.attributes('aria-labelledby') ?? '')
    expect(heading?.textContent).toBe('Start an elevation')
    expect(focusedTestId()).toBe('elevation-reason')
  })

  it('keeps Tab and Shift+Tab inside the dialog', async () => {
    const wrapper = await mountDialog()
    await flushPromises()
    const dialog = wrapper.get('dialog')

    ;(wrapper.get('button[aria-label="Close"]').element as HTMLElement).focus()
    await dialog.trigger('keydown', { key: 'Tab', shiftKey: true })
    expect(document.activeElement?.textContent?.trim()).toBe('Cancel')

    await dialog.trigger('keydown', { key: 'Tab' })
    expect(focusedTestId()).toBe('Close')
  })

  it('closes on Escape and returns the focus to the control that opened it', async () => {
    const opener = document.createElement('button')
    opener.textContent = 'Start an elevation'
    document.body.appendChild(opener)
    opener.focus()
    const wrapper = await mountDialog({ isOpen: false })
    await wrapper.setProps({ isOpen: true })
    await flushPromises()
    expect(focusedTestId()).toBe('elevation-reason')

    await wrapper.get('dialog').trigger('keydown', { key: 'Escape' })
    expect(wrapper.emitted('update:isOpen')?.at(-1)).toEqual([false])
    await wrapper.setProps({ isOpen: false })
    await flushPromises()

    expect(document.activeElement).toBe(opener)
    opener.remove()
  })

  it('leaves a dialog opened again alone when the start of the previous opening succeeds later', async () => {
    let answerStart!: (value: unknown) => void
    mocks.startElevation.mockReturnValueOnce(new Promise((resolve) => {
      answerStart = resolve
    }))
    const wrapper = await mountDialog()
    await wrapper.get('[data-testid="elevation-reason"]').setValue('SUP-1')
    await wrapper.get('form').trigger('submit')

    await wrapper.setProps({ isOpen: false })
    await wrapper.setProps({ isOpen: true })
    await wrapper.get('[data-testid="elevation-reason"]').setValue('SUP-2')
    expect(wrapper.get('[data-testid="elevation-submit"]').attributes('disabled')).toBeUndefined()
    answerStart({ id: 'elevation-1' })
    await flushPromises()

    expect(wrapper.emitted('started')).toEqual([[{ id: 'elevation-1' }]])
    expect(wrapper.emitted('update:isOpen')).toBeUndefined()
    expect((wrapper.get('[data-testid="elevation-reason"]').element as HTMLTextAreaElement).value).toBe('SUP-2')
  })

  it('shows the refusal of a previous opening in no dialog opened again', async () => {
    let refuseStart!: (reason: unknown) => void
    mocks.startElevation.mockReturnValueOnce(new Promise((_resolve, reject) => {
      refuseStart = reject
    }))
    const wrapper = await mountDialog()
    await wrapper.get('[data-testid="elevation-reason"]').setValue('SUP-1')
    await wrapper.get('form').trigger('submit')

    await wrapper.setProps({ isOpen: false })
    await wrapper.setProps({ isOpen: true })
    refuseStart(new Error('The duration must be between 1 and 90 minutes.'))
    await flushPromises()

    expect(wrapper.find('[data-testid="elevation-error"]').exists()).toBe(false)
    expect(wrapper.emitted('started')).toBeUndefined()
  })

  it('shows why the server refused the elevation', async () => {
    mocks.startElevation.mockRejectedValue(new Error('The duration must be between 1 and 90 minutes.'))
    const wrapper = await mountDialog()

    await wrapper.get('[data-testid="elevation-reason"]').setValue('SUP-1')
    await wrapper.get('form').trigger('submit')
    await flushPromises()

    expect(wrapper.get('[data-testid="elevation-error"]').text()).toContain('between 1 and 90 minutes')
    expect(wrapper.emitted('started')).toBeUndefined()
  })
})
