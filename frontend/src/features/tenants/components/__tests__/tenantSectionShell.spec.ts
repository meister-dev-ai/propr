// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { flushPromises, mount } from '@vue/test-utils'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import TenantModelCatalogSection from '../TenantModelCatalogSection.vue'
import TenantProviderAllowListSection from '../TenantProviderAllowListSection.vue'

vi.mock('@/services/modelCatalogService', () => ({
  listTenantOverrides: vi.fn().mockResolvedValue([]),
  listTenantProviders: vi.fn().mockResolvedValue([]),
  listTenantModels: vi.fn().mockResolvedValue([]),
  upsertTenantOverride: vi.fn(),
  deleteTenantOverride: vi.fn(),
  defineTenantModel: vi.fn(),
}))

vi.mock('@/services/tenantAdminService', () => ({
  getTenant: vi.fn().mockResolvedValue({ allowedAiProviderKinds: [], allowedAiEndpointHosts: [] }),
  updateTenant: vi.fn(),
}))

// The tenant settings page composes its sections from one shell: a section-card, a header carrying an h2 and a
// subtitle, and a body. Two sections were built on a different one (a bare card with an h3), so they read as
// belonging to another screen. This pins the shell rather than the pixels — the classes are what carry it.
describe('the tenant settings sections share one shell', () => {
  beforeEach(() => {
    vi.clearAllMocks()
  })

  it.each([
    ['model pricing overrides', TenantModelCatalogSection],
    ['permitted providers', TenantProviderAllowListSection],
  ])('%s uses the page section-card shell', async (_name, component) => {
    const wrapper = mount(component, { props: { tenantId: 'tenant-1' } })
    await flushPromises()

    expect(wrapper.element.classList.contains('section-card')).toBe(true)
    expect(wrapper.find('.section-card-header h2').exists()).toBe(true)
    expect(wrapper.find('.section-card-header .section-subtitle').exists()).toBe(true)
    expect(wrapper.find('.section-card-body').exists()).toBe(true)

    // The older shell's markers, so a revert cannot pass unnoticed.
    expect(wrapper.find('header.section-header').exists()).toBe(false)
    expect(wrapper.find('h3').exists()).toBe(false)
  })

  // The two ways to start an override are alternatives, not steps: they belong on one row, which is also why
  // they have to be the same button size as the rest of the page.
  it('offers both override entry points side by side, sized like the page actions', async () => {
    const wrapper = mount(TenantModelCatalogSection, { props: { tenantId: 'tenant-1' } })
    await flushPromises()

    const actions = wrapper.find('.override-add')
    expect(actions.exists()).toBe(true)

    const buttons = actions.findAll('button')
    expect(buttons).toHaveLength(2)
    expect(buttons.map(button => button.text())).toEqual([
      'Browse catalog…',
      'Define a model the catalog does not list…',
    ])
    for (const button of buttons) {
      expect(button.classes()).toContain('btn-sm')
      expect(button.classes()).not.toContain('btn-xs')
    }
  })
})
