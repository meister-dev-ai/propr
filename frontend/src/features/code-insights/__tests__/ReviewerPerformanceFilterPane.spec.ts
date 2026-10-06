// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { afterEach, describe, expect, it, vi } from 'vitest'
import { mount, type VueWrapper } from '@vue/test-utils'
import ReviewerPerformanceFilterPane from '../components/ReviewerPerformanceFilterPane.vue'

vi.mock('../components/ReviewerPerformanceSelect.vue', async () => {
  const { performanceSelectStub } = await import('./performanceControlStubs')
  return { default: performanceSelectStub }
})

describe('reviewer performance report controls during pane relocation', () => {
  let wrapper: VueWrapper | undefined

  afterEach(() => {
    wrapper?.unmount()
    wrapper = undefined
    document.body.innerHTML = ''
    vi.restoreAllMocks()
  })

  function mountPane(): VueWrapper {
    return mount(ReviewerPerformanceFilterPane, {
      attachTo: document.body,
      props: {
        scope: { from: '2026-09-01', to: '2026-09-02' },
        facets: { clients: [], repositories: [], models: [], types: [], qualifiers: [] },
        selectedView: 0,
        viewCount: 1,
        comparison: false,
        horizontal: false,
        alignment: 'calendar',
        frozen: false,
        disabled: false,
        clientsLoading: false,
      },
      slots: {
        reports: '<input aria-label="Report control"><input aria-label="Draft snapshot name">',
      },
    })
  }

  it('retains report focus and local draft state across relocation and scope selection', async () => {
    wrapper = mountPane()
    await wrapper.get('[aria-label="Draft snapshot name"]').setValue('Unsubmitted draft')
    wrapper.get<HTMLInputElement>('[aria-label="Report control"]').element.focus()

    await wrapper.setProps({ horizontal: true, viewCount: 2, comparison: true })
    expect(wrapper.get<HTMLInputElement>('[aria-label="Draft snapshot name"]').element.value)
      .toBe('Unsubmitted draft')
    expect(document.activeElement).toBe(wrapper.get('[aria-label="Report control"]').element)

    await wrapper.setProps({ selectedView: 1 })
    expect(wrapper.get<HTMLInputElement>('[aria-label="Draft snapshot name"]').element.value)
      .toBe('Unsubmitted draft')
    expect(document.activeElement).toBe(wrapper.get('[aria-label="Report control"]').element)

    await wrapper.setProps({ horizontal: false, viewCount: 1, comparison: false, selectedView: 0 })
    expect(wrapper.get<HTMLInputElement>('[aria-label="Draft snapshot name"]').element.value)
      .toBe('Unsubmitted draft')
    expect(document.activeElement).toBe(wrapper.get('[aria-label="Report control"]').element)
  })

  it('leaves focus on another control when report sections move', async () => {
    wrapper = mountPane()
    const close = wrapper.get<HTMLButtonElement>('header button')
    close.element.focus()

    await wrapper.setProps({ horizontal: true, viewCount: 2, comparison: true })
    expect(document.activeElement).toBe(close.element)
  })

  it('retains the relocation focus repair through another update in the same render cycle', async () => {
    wrapper = mountPane()
    const report = wrapper.get<HTMLInputElement>('[aria-label="Report control"]').element
    const pane = wrapper.element as HTMLElement
    const insertBefore = pane.insertBefore.bind(pane)
    let moved = false
    vi.spyOn(pane, 'insertBefore').mockImplementation((node, reference) => {
      const inserted = insertBefore(node, reference)
      if (!moved && node.contains(report)) {
        moved = true
        report.blur()
        wrapper!.vm.$forceUpdate()
      }
      return inserted
    })
    report.focus()

    await wrapper.setProps({ horizontal: true, viewCount: 2, comparison: true })
    expect(moved).toBe(true)
    expect(document.activeElement).toBe(report)
  })

  it.each([false, true])('preserves the focus handoff for an owned menu with expanded=%s', async (expanded) => {
    wrapper = mountPane()
    const report = wrapper.get<HTMLInputElement>('[aria-label="Report control"]')
    report.element.setAttribute('aria-controls', 'test-report-menu')
    report.element.setAttribute('aria-expanded', String(expanded))
    const menu = document.createElement('div')
    menu.id = 'test-report-menu'
    const option = document.createElement('button')
    option.textContent = 'Saved comparison'
    menu.append(option)
    document.body.append(menu)
    option.focus()

    await wrapper.setProps({ horizontal: true, viewCount: 2, comparison: true })
    expect(document.activeElement).toBe(expanded ? option : report.element)
  })
})
