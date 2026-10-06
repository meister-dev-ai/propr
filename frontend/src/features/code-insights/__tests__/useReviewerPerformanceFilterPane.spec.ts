// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { computed, defineComponent } from 'vue'
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { useReviewerPerformanceFilterPane } from '../composables/useReviewerPerformanceFilterPane'

const breakpoint = '(max-width: 960px)'
const Harness = defineComponent({
  setup() {
    return useReviewerPerformanceFilterPane(computed(() => 2))
  },
  template: `
    <div>
      <button ref="trigger" data-test="trigger" @click="toggle">Filters</button>
      <aside v-if="open" :id="id">
        <button data-test="selector">Scope selector</button>
      </aside>
      <button data-test="outside">Outside control</button>
    </div>
  `,
})

describe('reviewer performance filter pane focus on resize', () => {
  let wrapper: VueWrapper | undefined
  let menu: HTMLElement | undefined
  let media: MediaQueryList
  let restoreMedia = () => {}

  beforeEach(() => {
    const original = window.matchMedia
    media = Object.assign(new EventTarget(), { matches: false, media: breakpoint }) as MediaQueryList
    window.matchMedia = (query) => query === breakpoint ? media : original(query)
    restoreMedia = () => {
      window.matchMedia = original
    }
  })

  afterEach(() => {
    wrapper?.unmount()
    menu?.remove()
    wrapper = undefined
    menu = undefined
    restoreMedia()
  })

  async function narrowViewport(): Promise<void> {
    const change = new Event('change')
    Object.defineProperty(change, 'matches', { value: true })
    media.dispatchEvent(change)
    await flushPromises()
  }

  function createMenu(): HTMLButtonElement {
    menu = document.createElement('div')
    menu.id = 'pane-owned-menu'
    const option = document.createElement('button')
    option.setAttribute('role', 'option')
    option.textContent = 'Selected option'
    menu.append(option)
    document.body.append(menu)
    return option
  }

  it.each(['aria-controls', 'aria-owns'])('returns focus from a teleported option referenced by %s', async (attribute) => {
    wrapper = mount(Harness, { attachTo: document.body })
    const option = createMenu()
    wrapper.get('[data-test="selector"]').element.setAttribute(attribute, `missing-menu ${menu!.id}`)
    option.focus()
    expect(wrapper.get('aside').element.contains(document.activeElement)).toBe(false)

    await narrowViewport()

    expect(wrapper.find('aside').exists()).toBe(false)
    expect(document.activeElement).toBe(wrapper.get('[data-test="trigger"]').element)
  })

  it('returns focus from a control rendered inside the pane', async () => {
    wrapper = mount(Harness, { attachTo: document.body })
    ;(wrapper.get('[data-test="selector"]').element as HTMLButtonElement).focus()

    await narrowViewport()

    expect(wrapper.find('aside').exists()).toBe(false)
    expect(document.activeElement).toBe(wrapper.get('[data-test="trigger"]').element)
  })

  it('preserves unrelated outside focus when a pane-owned menu also exists', async () => {
    wrapper = mount(Harness, { attachTo: document.body })
    createMenu()
    wrapper.get('[data-test="selector"]').element.setAttribute('aria-controls', menu!.id)
    const outside = wrapper.get('[data-test="outside"]')
    ;(outside.element as HTMLButtonElement).focus()

    await narrowViewport()

    expect(wrapper.find('aside').exists()).toBe(false)
    expect(document.activeElement).toBe(outside.element)
  })
})
