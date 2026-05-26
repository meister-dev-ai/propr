// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { describe, expect, it } from 'vitest'
import { mount } from '@vue/test-utils'

describe('layout system primitives', () => {
  it('renders the sidebar/content shell through PageWithSidebar', async () => {
    const { default: PageWithSidebar } = await import('@/components/layout/PageWithSidebar.vue')
    const wrapper = mount(PageWithSidebar, {
      slots: {
        sidebar: '<div class="sidebar-slot">Sidebar</div>',
        default: '<div class="content-slot">Content</div>',
      },
    })

    expect(wrapper.find('.page-with-sidebar-layout').exists()).toBe(true)
    expect(wrapper.find('.sidebar-slot').exists()).toBe(true)
    expect(wrapper.find('.content-slot').exists()).toBe(true)
  })

  it('renders top-bar and footer primitives', async () => {
    const [{ default: AppTopBar }, { default: AppFooter }] = await Promise.all([
      import('@/components/navigation/AppTopBar.vue'),
      import('@/components/navigation/AppFooter.vue'),
    ])

    const topBar = mount(AppTopBar, { slots: { default: '<div>Toolbar</div>' } })
    const footer = mount(AppFooter)

    expect(topBar.text()).toContain('Toolbar')
    expect(footer.text()).toContain('Powered by')
  })
})
