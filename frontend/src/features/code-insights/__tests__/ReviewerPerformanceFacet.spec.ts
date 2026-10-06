// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import ReviewerPerformanceFacet from '../components/ReviewerPerformanceFacet.vue'
import { installControlTestViewport } from './performanceControlTestViewport'

const clients = [
  { id: 'demo', label: 'Reviewer Performance Demo' },
  { id: 'other', label: 'Other client' },
]

describe('reviewer performance facet selection', () => {
  let attached: VueWrapper | undefined

  beforeEach(installControlTestViewport)

  afterEach(() => {
    attached?.unmount()
    attached = undefined
    document.body.innerHTML = ''
    vi.unstubAllGlobals()
  })

  it('preserves All, None and individual choices as distinct populations', async () => {
    attached = mount(ReviewerPerformanceFacet, {
      props: { label: 'Clients', selection: null, items: clients },
    })
    const wrapper = attached
    const select = wrapper.getComponent({ name: 'VAutocomplete' })

    expect(select.props('placeholder')).toBe('All')
    select.vm.$emit('update:modelValue', ['demo'])
    expect(wrapper.emitted('update:selection')?.at(-1)).toEqual([['demo']])

    await wrapper.setProps({ selection: ['demo'] })
    select.vm.$emit('update:modelValue', [])
    expect(wrapper.emitted('update:selection')?.at(-1)).toEqual([[]])

    await wrapper.setProps({ selection: [] })
    expect(select.props('placeholder')).toBe('None')
  })

  it('keeps filtering read-only without changing a saved selection', async () => {
    attached = mount(ReviewerPerformanceFacet, {
      props: { label: 'Clients', selection: ['demo'], items: clients, disabled: true },
    })
    const wrapper = attached

    expect(wrapper.getComponent({ name: 'VAutocomplete' }).props('disabled')).toBe(true)
    expect(wrapper.emitted('update:selection')).toBeUndefined()
  })

  it('searches choices and restores All through the menu action', async () => {
    attached = mount(ReviewerPerformanceFacet, {
      props: { label: 'Clients', selection: ['other'], items: clients },
      attachTo: document.body,
    })
    const autocomplete = attached.getComponent({ name: 'VAutocomplete' })
    const input = autocomplete.get('input')
    await input.trigger('focus')
    await input.setValue('Demo')
    await flushPromises()

    expect(document.body.textContent).toContain('Reviewer Performance Demo')
    const all = autocomplete.findAllComponents({ name: 'VBtn' })
      .find((button) => button.text() === 'All')!
    await all.trigger('click')
    expect(attached.emitted('update:selection')?.at(-1)).toEqual([null])
    expect(autocomplete.props('search')).toBe('')
  })

  it('shows the selected client label and clears its search after choosing a result', async () => {
    attached = mount(ReviewerPerformanceFacet, {
      props: { label: 'Clients', selection: null, items: clients },
      attachTo: document.body,
    })
    const autocomplete = attached.getComponent({ name: 'VAutocomplete' })
    const input = autocomplete.get('input')
    await input.trigger('focus')
    await input.setValue('Demo')
    await flushPromises()

    const option = autocomplete.findAllComponents({ name: 'VListItem' })
      .find((item) => item.text().includes('Reviewer Performance Demo'))!
    await option.trigger('click')
    expect(attached.emitted('update:selection')?.at(-1)).toEqual([['demo']])
    await attached.setProps({ selection: ['demo'] })

    expect(attached.get('.performance-facet__selection').text()).toBe('Reviewer Performance Demo')
    expect(autocomplete.props('search')).toBe('')
  })

  it('shows the retained selected ID when captured facets have no matching label', async () => {
    attached = mount(ReviewerPerformanceFacet, {
      props: { label: 'Clients', selection: ['retained-client'], items: [], disabled: true },
    })

    expect(attached.get('.performance-facet__selection').text()).toBe('retained-client')
    expect(attached.emitted('update:selection')).toBeUndefined()
    await attached.setProps({ selection: ['demo'], items: clients })
    expect(attached.get('.performance-facet__selection').text()).toBe('Reviewer Performance Demo')
    await attached.setProps({ items: [] })
    expect(attached.get('.performance-facet__selection').text()).toBe('demo')
  })

  it('shows catalogue loading feedback before any client choices are available', async () => {
    attached = mount(ReviewerPerformanceFacet, {
      props: { label: 'Clients', selection: null, items: [], loading: true },
      attachTo: document.body,
    })
    const autocomplete = attached.getComponent({ name: 'VAutocomplete' })
    await autocomplete.get('input').trigger('keydown', { key: 'ArrowDown' })
    await flushPromises()

    expect(autocomplete.props('loading')).toBe(true)
    expect(document.body.textContent).toContain('Loading values…')
    expect(document.body.textContent).not.toContain('No matching values')
    await attached.setProps({ loading: false })
    expect(autocomplete.props('noDataText')).toBe('No matching values')
  })
})
