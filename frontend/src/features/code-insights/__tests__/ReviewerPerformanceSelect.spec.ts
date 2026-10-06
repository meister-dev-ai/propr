// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { defineComponent, h } from 'vue'
import ReviewerPerformanceSelect from '../components/ReviewerPerformanceSelect.vue'
import { installControlTestViewport } from './performanceControlTestViewport'

const metrics = [
  { value: 'f1', label: 'F1 score' },
  { value: 'precision', label: 'Precision' },
]

describe('reviewer performance single selection', () => {
  let wrapper: VueWrapper | undefined

  beforeEach(installControlTestViewport)

  afterEach(() => {
    wrapper?.unmount()
    wrapper = undefined
    document.body.innerHTML = ''
    vi.unstubAllGlobals()
  })

  it('opens its labelled choices by keyboard and selects a metric', async () => {
    wrapper = mount(ReviewerPerformanceSelect, {
      props: { label: 'Metric', modelValue: 'f1', items: metrics },
      attachTo: document.body,
    })
    const input = wrapper.get('input')
    expect(input.attributes('aria-label')).toBe('Metric')
    await input.trigger('keydown', { key: 'ArrowDown' })
    await flushPromises()

    const select = wrapper.getComponent({ name: 'VSelect' })
    const precision = select.findAllComponents({ name: 'VListItem' })
      .find((item) => item.text() === 'Precision')!
    await precision.trigger('click')
    expect(wrapper.emitted('update:modelValue')?.at(-1)).toEqual(['precision'])
  })

  it('keeps modal menu pointer events outside its activator and selects the chosen value', async () => {
    wrapper = mount(defineComponent({
      setup() {
        return () => h('dialog', { open: true }, [
          h(ReviewerPerformanceSelect, {
            label: 'Metric',
            modelValue: 'f1',
            items: metrics,
            containedMenu: true,
          }),
        ])
      },
    }), { attachTo: document.body })
    const control = wrapper.getComponent(ReviewerPerformanceSelect)
    await control.get('input').trigger('keydown', { key: 'ArrowDown' })
    await flushPromises()
    const select = control.getComponent({ name: 'VSelect' })
    const precision = select.findAllComponents({ name: 'VListItem' })
      .find((item) => item.text() === 'Precision')!

    expect(wrapper.get('dialog').element.contains(precision.element)).toBe(true)
    expect(control.element.contains(precision.element)).toBe(false)
    await precision.trigger('mousedown')
    await precision.trigger('click')
    expect(control.emitted('update:modelValue')?.at(-1)).toEqual(['precision'])
  })
})
