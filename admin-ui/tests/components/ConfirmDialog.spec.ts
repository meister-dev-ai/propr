// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { describe, it, expect, vi } from 'vitest'
import { mount } from '@vue/test-utils'

async function importConfirmDialog() {
  const mod = await import('@/components/ConfirmDialog.vue')
  return mod.default
}

describe('ConfirmDialog', () => {
  it('renders when open prop is true', async () => {
    const ConfirmDialog = await importConfirmDialog()
    const wrapper = mount(ConfirmDialog, {
      props: { open: true, message: 'Are you sure?' },
    })
    expect(wrapper.text()).toContain('Are you sure?')
    expect(wrapper.find('button').exists()).toBe(true)
  })

  it('does not render content when open prop is false', async () => {
    const ConfirmDialog = await importConfirmDialog()
    const wrapper = mount(ConfirmDialog, {
      props: { open: false, message: 'Are you sure?' },
    })
    expect(wrapper.text()).not.toContain('Are you sure?')
  })

  it('emits confirm when Confirm button is clicked', async () => {
    const ConfirmDialog = await importConfirmDialog()
    const wrapper = mount(ConfirmDialog, {
      props: { open: true, message: 'Delete?' },
    })
    await wrapper.find('button.btn-danger').trigger('click')
    expect(wrapper.emitted('confirm')).toBeTruthy()
  })

  it('emits cancel when Cancel button is clicked', async () => {
    const ConfirmDialog = await importConfirmDialog()
    const wrapper = mount(ConfirmDialog, {
      props: { open: true, message: 'Delete?' },
    })
    const buttons = wrapper.findAll('button')
    const cancelBtn = buttons.find((b) => b.text() === 'Cancel')
    await cancelBtn!.trigger('click')
    expect(wrapper.emitted('cancel')).toBeTruthy()
  })
})
