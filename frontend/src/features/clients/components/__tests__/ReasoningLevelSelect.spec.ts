// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { mount } from '@vue/test-utils'
import { describe, expect, it } from 'vitest'
import ReasoningLevelSelect from '../ReasoningLevelSelect.vue'

function mountSelect(props: {
  modelValue: string | null
  levels: string[] | null
  inheritedLevel?: string | null
  inheritedFrom?: string
}) {
  const state = { level: props.modelValue }
  const wrapper = mount(ReasoningLevelSelect, {
    props: {
      inheritedLevel: null,
      inheritedFrom: '',
      ...props,
      testId: 'level',
      'onUpdate:modelValue': (value: string | null) => {
        state.level = value
        void wrapper.setProps({ modelValue: value })
      },
    },
  })
  return { wrapper, state }
}

function options(wrapper: ReturnType<typeof mountSelect>['wrapper']) {
  return wrapper.find('[data-testid="level"]').findAll('option').map((option) => [option.attributes('value'), option.text()])
}

describe('ReasoningLevelSelect', () => {
  it('offers the inherited level with its source first, then the levels of the model', () => {
    const { wrapper } = mountSelect({
      modelValue: null,
      levels: ['low', 'medium', 'high'],
      inheritedLevel: 'high',
      inheritedFrom: 'the logical model',
    })

    expect(options(wrapper)).toEqual([
      ['', 'Inherit (high, from the logical model)'],
      ['low', 'low'],
      ['medium', 'medium'],
      ['high', 'high'],
    ])
  })

  it('names the provider default when no other place sets a level', () => {
    const { wrapper } = mountSelect({ modelValue: null, levels: ['low'] })

    expect(options(wrapper)[0]).toEqual(['', 'Provider default'])
  })

  // A model that no source knows the levels of receives a level as configured, so every shared name is offered.
  it('offers the shared level names while the levels of the model are unknown', () => {
    const { wrapper } = mountSelect({ modelValue: null, levels: null })

    expect(options(wrapper).map(([value]) => value)).toEqual(['', 'none', 'minimal', 'low', 'medium', 'high', 'xhigh', 'max'])
  })

  it('emits the chosen level, and null for the first option', async () => {
    const { wrapper, state } = mountSelect({ modelValue: null, levels: ['low', 'high'] })

    await wrapper.find('[data-testid="level"]').setValue('high')
    expect(state.level).toBe('high')

    await wrapper.find('[data-testid="level"]').setValue('')
    expect(state.level).toBeNull()
  })

  it('selects the stored level', () => {
    const { wrapper } = mountSelect({ modelValue: 'medium', levels: ['low', 'medium', 'high'] })

    expect((wrapper.find('[data-testid="level"]').element as HTMLSelectElement).value).toBe('medium')
    expect(wrapper.find('[data-testid="level-warning"]').exists()).toBe(false)
  })

  // A stored level falls out of the list when an operator edits the levels of the model or points the logical model at
  // another model. The select still shows the stored level, and the warning below it contains the level the model
  // receives instead.
  it('keeps a stored level the model does not accept and shows the nearest lower level it receives', () => {
    const { wrapper } = mountSelect({ modelValue: 'xhigh', levels: ['none', 'low', 'medium', 'high'] })

    expect((wrapper.find('[data-testid="level"]').element as HTMLSelectElement).value).toBe('xhigh')
    expect(options(wrapper).map(([value]) => value)).toEqual(['', 'none', 'low', 'medium', 'high', 'xhigh'])
    expect(wrapper.find('[data-testid="level-warning"]').text()).toBe(
      'The model does not accept xhigh. It receives high, the nearest lower level it accepts.',
    )
  })

  it('states that the model receives no level when it accepts no lower level', () => {
    const { wrapper } = mountSelect({ modelValue: 'low', levels: ['high', 'max'] })

    expect(wrapper.find('[data-testid="level-warning"]').text()).toBe(
      'The model does not accept low or a lower level, so it receives no level.',
    )
  })

  it('shows no warning while the levels of the model are unknown', () => {
    const { wrapper } = mountSelect({ modelValue: 'max', levels: null })

    expect(wrapper.find('[data-testid="level-warning"]').exists()).toBe(false)
  })

  it('passes attributes such as aria-label and disabled to the select', () => {
    const wrapper = mount(ReasoningLevelSelect, {
      props: { modelValue: null, levels: null, inheritedLevel: null, inheritedFrom: '', testId: 'level' },
      attrs: { 'aria-label': 'Triage reasoning level', disabled: true },
    })

    const select = wrapper.find('[data-testid="level"]')
    expect(select.attributes('aria-label')).toBe('Triage reasoning level')
    expect(select.attributes('disabled')).toBeDefined()
  })
})
