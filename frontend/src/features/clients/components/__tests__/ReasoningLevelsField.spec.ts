// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { mount } from '@vue/test-utils'
import { describe, expect, it } from 'vitest'
import type { AiReasoningLevelSource } from '@/services/aiConnectionsService'
import ReasoningLevelsField from '../ReasoningLevelsField.vue'
import { normalizeReasoningLevels } from '../aiConnectionsFormatters'

function mountField(levels: string[] | null, source: AiReasoningLevelSource | null, detect = false) {
  const state = { levels, source, detect }
  const wrapper = mount(ReasoningLevelsField, {
    props: {
      testId: 'levels',
      levels,
      source,
      detect,
      'onUpdate:levels': (value: string[] | null) => {
        state.levels = value
        void wrapper.setProps({ levels: value })
      },
      'onUpdate:source': (value: AiReasoningLevelSource | null) => {
        state.source = value
        void wrapper.setProps({ source: value })
      },
      'onUpdate:detect': (value: boolean) => {
        state.detect = value
        void wrapper.setProps({ detect: value })
      },
    },
  })
  return { wrapper, state }
}

describe('ReasoningLevelsField', () => {
  it('shows the levels and where they come from', () => {
    const { wrapper } = mountField(['low', 'medium', 'high'], 'catalog')

    expect(wrapper.find('[data-testid="levels-level-medium"]').exists()).toBe(true)
    expect(wrapper.find('[data-testid="levels-source"]').text()).toContain('model catalog')
  })

  // An added level is normalized as the server normalizes it, and the list is then the operator's.
  it('adds a level in the order of increasing effort and marks the list as entered', async () => {
    const { wrapper, state } = mountField(['low', 'max'], 'addIn')

    await wrapper.find('[data-testid="levels-input"]').setValue(' High ')
    await wrapper.find('[data-testid="levels-add"]').trigger('click')

    expect(state.levels).toEqual(['low', 'high', 'max'])
    expect(state.source).toBe('operator')
  })

  it('refuses a name that is not a level name', async () => {
    const { wrapper, state } = mountField(['low'], 'catalog')

    await wrapper.find('[data-testid="levels-input"]').setValue('very high')
    await wrapper.find('[data-testid="levels-add"]').trigger('click')

    expect(state.levels).toEqual(['low'])
    expect(wrapper.find('[data-testid="levels-error"]').text()).toContain('lowercase letters')
  })

  it('removes a level', async () => {
    const { wrapper, state } = mountField(['none', 'low', 'high'], 'provider')

    await wrapper.find('[data-testid="levels-remove-none"]').trigger('click')

    expect(state.levels).toEqual(['low', 'high'])
    expect(state.source).toBe('operator')
  })

  it('asks the save to detect the levels again, replacing a list an operator entered', async () => {
    const { wrapper, state } = mountField(['max'], 'operator')

    await wrapper.find('[data-testid="levels-detect"]').trigger('click')

    expect(state.levels).toBeNull()
    expect(state.source).toBeNull()
    expect(state.detect).toBe(true)
    expect(wrapper.find('[data-testid="levels-source"]').text()).toContain('detects the levels again')
  })

  it('offers no detection for a list that a source filled', () => {
    const { wrapper } = mountField(['low', 'high'], 'catalog')

    expect(wrapper.find('[data-testid="levels-detect"]').exists()).toBe(false)
  })
})

describe('normalizeReasoningLevels', () => {
  it('sorts the shared names by effort and keeps the source order of a list with another name', () => {
    expect(normalizeReasoningLevels(['MAX', 'none', 'low', 'low'])).toEqual(['none', 'low', 'max'])
    expect(normalizeReasoningLevels(['high', 'low', 'ultra'])).toEqual(['high', 'low', 'ultra'])
    expect(normalizeReasoningLevels(['', 'two words', '1x'])).toEqual([])
  })
})
