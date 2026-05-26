// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { describe, expect, it } from 'vitest'
import { mount } from '@vue/test-utils'

describe('form system primitives', () => {
  it('renders a titled FormSection', async () => {
    const { default: FormSection } = await import('@/components/forms/FormSection.vue')
    const wrapper = mount(FormSection, {
      props: { title: 'Configuration' },
      slots: { default: '<input name="example" />' },
    })

    expect(wrapper.text()).toContain('Configuration')
    expect(wrapper.find('input[name="example"]').exists()).toBe(true)
  })

  it('emits ToggleField updates from checkbox changes', async () => {
    const { default: ToggleField } = await import('@/components/forms/ToggleField.vue')
    const wrapper = mount(ToggleField, {
      props: { modelValue: true, label: 'Enable', hint: 'Optional hint' },
    })

    await wrapper.find('input[type="checkbox"]').setValue(false)

    expect(wrapper.text()).toContain('Enable')
    expect(wrapper.text()).toContain('Optional hint')
    expect(wrapper.emitted('update:modelValue')).toEqual([[false]])
  })
})
