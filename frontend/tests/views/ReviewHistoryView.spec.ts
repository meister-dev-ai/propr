// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { beforeEach, describe, expect, it, vi } from 'vitest'
import { defineComponent, h } from 'vue'
import { flushPromises, mount } from '@vue/test-utils'

const refreshMock = vi.fn(async () => {})

vi.mock('@/features/reviews/components/ReviewHistorySection.vue', () => ({
  default: defineComponent({
    name: 'ReviewHistorySection',
    setup(_props, { expose }) {
      expose({ refresh: refreshMock })
      return () => h('div', { class: 'review-history-section-stub' }, 'history section')
    },
  }),
}))

describe('ReviewHistoryView', () => {
  beforeEach(() => {
    refreshMock.mockClear()
  })

  it('renders the shared sidebar/top-bar shell and refresh action', async () => {
    const { default: ReviewHistoryView } = await import('@/features/reviews/views/ReviewHistoryView.vue')
    const wrapper = mount(ReviewHistoryView)

    expect(wrapper.find('.page-with-sidebar-layout').exists()).toBe(true)
    expect(wrapper.find('.app-nav-drawer').exists()).toBe(true)
    expect(wrapper.find('.app-top-bar').exists()).toBe(true)
    expect(wrapper.text()).toContain('Review History')
    expect(wrapper.text()).toContain('Global History')

    await wrapper.find('button.btn-primary').trigger('click')
    await flushPromises()

    expect(refreshMock).toHaveBeenCalledOnce()
  })
})
