// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { mount } from '@vue/test-utils'
import { describe, expect, it } from 'vitest'

import FilePicker from '../FilePicker.vue'

function mountPicker(props: Record<string, unknown> = {}) {
  return mount(FilePicker, {
    props: { inputId: 'a-file', testId: 'a-file-input', ...props },
  })
}

describe('FilePicker', () => {
  it('states that nothing is picked yet', () => {
    const wrapper = mountPicker()

    expect(wrapper.get('[data-testid="a-file-input-name"]').text()).toBe('No file chosen')
  })

  it('names the file the call site holds', () => {
    const wrapper = mountPicker({ fileName: 'models.dev.json' })

    expect(wrapper.get('[data-testid="a-file-input-name"]').text()).toBe('models.dev.json')
  })

  // The call sites read the picked file off the event, so the event has to arrive as the input raised it
  // rather than as something this component assembled.
  it('forwards the native change event', async () => {
    const wrapper = mountPicker()
    const input = wrapper.get('[data-testid="a-file-input"]')

    await input.trigger('change')

    const emitted = wrapper.emitted('change')
    expect(emitted).toHaveLength(1)
    expect((emitted?.[0][0] as Event).target).toBe(input.element)
  })

  // The native input carries the interaction and stays in the tab order, so the accept types and the label
  // association have to be on it and not on the label drawn in its place.
  it('keeps the native input as the labelled control', () => {
    const wrapper = mountPicker({ accept: '.json' })
    const input = wrapper.get('[data-testid="a-file-input"]').element as HTMLInputElement

    expect(input.type).toBe('file')
    expect(input.getAttribute('accept')).toBe('.json')
    expect(wrapper.get('label').attributes('for')).toBe('a-file')
    expect(input.getAttribute('aria-describedby'))
      .toBe(wrapper.get('[data-testid="a-file-input-name"]').attributes('id'))
    // Taken out of view by the utility that leaves the control focusable, rather than by anything that would
    // remove it from the tab order.
    expect([...input.classList]).toContain('visually-hidden')
    expect(input.tabIndex).not.toBe(-1)
  })

  // A call site renders its own text for the field, such as why a file was rejected. Without the input
  // pointing at it, a screen reader announces the label and the button and never that text.
  it('points the input at the description the call site passed as well as at the file name', () => {
    const wrapper = mountPicker({ describedBy: 'a-file-error' })
    const input = wrapper.get('[data-testid="a-file-input"]').element as HTMLInputElement

    const described = input.getAttribute('aria-describedby')?.split(' ')
    expect(described).toContain('a-file-error')
    expect(described).toContain(wrapper.get('[data-testid="a-file-input-name"]').attributes('id'))
  })

  // A call site passes nothing while it has nothing to describe, and an empty id in the list would point the
  // input at an element that does not exist.
  it('leaves an empty description out of the list', () => {
    const wrapper = mountPicker({ describedBy: '' })

    expect((wrapper.get('[data-testid="a-file-input"]').element as HTMLInputElement)
      .getAttribute('aria-describedby'))
      .toBe(wrapper.get('[data-testid="a-file-input-name"]').attributes('id'))
  })

  it('disables the native input rather than only the label drawn for it', () => {
    const wrapper = mountPicker({ disabled: true })

    expect((wrapper.get('[data-testid="a-file-input"]').element as HTMLInputElement).disabled).toBe(true)
  })

  // The name is marked from the input's own test id, so a call site that marks nothing leaves both unmarked.
  it('marks the file name only when the input is marked', () => {
    const wrapper = mount(FilePicker, { props: { inputId: 'a-file' } })

    expect(wrapper.get('.file-picker__name').attributes('data-testid')).toBeUndefined()
  })
})
