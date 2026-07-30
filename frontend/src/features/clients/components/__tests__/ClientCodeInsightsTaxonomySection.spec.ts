// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { flushPromises, mount } from '@vue/test-utils'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import type {
  CodeInsightCustomTag,
  CodeInsightTaxonomy,
} from '@/services/codeInsightTaxonomyService'
import ClientCodeInsightsTaxonomySection from '../ClientCodeInsightsTaxonomySection.vue'

const mocks = vi.hoisted(() => ({
  fetchTaxonomy: vi.fn(),
  createCustomTag: vi.fn(),
  updateCustomTag: vi.fn(),
  retireCustomTag: vi.fn(),
}))

vi.mock('@/services/codeInsightTaxonomyService', () => mocks)

function customTag(overrides: Partial<CodeInsightCustomTag> = {}): CodeInsightCustomTag {
  return {
    id: 'tag-1',
    slug: 'domain-rule',
    displayName: 'Domain rule',
    definition: 'Violates one of our business rules.',
    retiredAt: null,
    createdAt: '2026-07-01T00:00:00Z',
    updatedAt: '2026-07-01T00:00:00Z',
    ...overrides,
  }
}

function taxonomy(overrides: Partial<CodeInsightTaxonomy> = {}): CodeInsightTaxonomy {
  return {
    version: 1,
    coreTags: [
      {
        slug: 'security',
        displayName: 'Security',
        definition: 'An exploitable weakness.',
        characteristic: 'security',
        behaviourChanging: true,
      },
      {
        slug: 'naming-clarity',
        displayName: 'Naming and clarity',
        definition: 'Readability without a change in behaviour.',
        characteristic: 'maintainability',
        behaviourChanging: false,
      },
      {
        slug: 'performance',
        displayName: 'Performance',
        definition: 'Avoidable cost in time, memory, or I/O.',
        characteristic: 'performanceEfficiency',
        behaviourChanging: true,
      },
    ],
    customTags: [],
    ...overrides,
  }
}

function mountSection() {
  return mount(ClientCodeInsightsTaxonomySection, { props: { clientId: 'client-1' } })
}

describe('ClientCodeInsightsTaxonomySection', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    mocks.fetchTaxonomy.mockResolvedValue(taxonomy())
  })

  it('shows the core set read-only, with its quality characteristic and version', async () => {
    const wrapper = mountSection()
    await flushPromises()

    const coreTable = wrapper.get('[data-testid="core-tag-table"]')
    expect(coreTable.text()).toContain('Security')
    // The wire value is camelCase; the operator sees prose.
    expect(coreTable.text()).toContain('Performance efficiency')
    expect(coreTable.text()).not.toContain('performanceEfficiency')
    expect(coreTable.text()).toContain('Maintainability')
    // The core set carries no Edit or Retire affordance: it is installation vocabulary, not client config.
    expect(coreTable.findAll('button')).toHaveLength(0)
    expect(wrapper.text()).toContain('version 1')
  })

  it('explains that custom tags do not roll up across clients', async () => {
    const wrapper = mountSection()
    await flushPromises()

    expect(wrapper.text()).toContain('never appear in a cross-client comparison')
  })

  it('creates a custom tag and reloads the vocabulary', async () => {
    const wrapper = mountSection()
    await flushPromises()

    await wrapper.get('.section-card-header button').trigger('click')
    await wrapper.get('#taxonomySlug').setValue('domain-rule')
    await wrapper.get('#taxonomyDisplayName').setValue('Domain rule')
    await wrapper.get('#taxonomyDefinition').setValue('Violates one of our business rules.')

    mocks.createCustomTag.mockResolvedValue(customTag())
    mocks.fetchTaxonomy.mockResolvedValue(taxonomy({ customTags: [customTag()] }))

    await wrapper.get('.form-actions .btn-primary').trigger('click')
    await flushPromises()

    expect(mocks.createCustomTag).toHaveBeenCalledWith('client-1', {
      slug: 'domain-rule',
      displayName: 'Domain rule',
      definition: 'Violates one of our business rules.',
    })
    expect(wrapper.get('[data-testid="custom-tag-table"]').text()).toContain('Domain rule')
    // The editor closes on success rather than leaving a stale draft on screen.
    expect(wrapper.find('[data-testid="taxonomy-editor"]').exists()).toBe(false)
  })

  it('will not submit an incomplete draft', async () => {
    const wrapper = mountSection()
    await flushPromises()

    await wrapper.get('.section-card-header button').trigger('click')
    await wrapper.get('#taxonomySlug').setValue('domain-rule')

    // The definition is what the classifier uses to decide when the tag applies, so a tag without one
    // would be unassignable: the form must not offer to save it.
    expect(
      (wrapper.get('.form-actions .btn-primary').element as HTMLButtonElement).disabled,
    ).toBe(true)
    expect(mocks.createCustomTag).not.toHaveBeenCalled()
  })

  it('surfaces a shadowing rejection verbatim so the operator knows which mistake was made', async () => {
    const wrapper = mountSection()
    await flushPromises()

    await wrapper.get('.section-card-header button').trigger('click')
    await wrapper.get('#taxonomySlug').setValue('security')
    await wrapper.get('#taxonomyDisplayName').setValue('Security')
    await wrapper.get('#taxonomyDefinition').setValue('Tries to shadow a core type.')

    mocks.createCustomTag.mockRejectedValue(
      new Error("'security' is a core finding type. A custom tag cannot shadow one."),
    )

    await wrapper.get('.form-actions .btn-primary').trigger('click')
    await flushPromises()

    expect(wrapper.get('[data-testid="taxonomy-save-error"]').text()).toContain(
      'is a core finding type',
    )
    // The draft survives a rejection so the operator can correct it rather than retype it.
    expect(wrapper.find('[data-testid="taxonomy-editor"]').exists()).toBe(true)
  })

  it('prefills the editor when editing and updates in place', async () => {
    mocks.fetchTaxonomy.mockResolvedValue(taxonomy({ customTags: [customTag()] }))
    const wrapper = mountSection()
    await flushPromises()

    const editButton = wrapper
      .get('[data-testid="custom-tag-table"]')
      .findAll('button')
      .find((button) => button.text() === 'Edit')!
    await editButton.trigger('click')

    expect((wrapper.get('#taxonomySlug').element as HTMLInputElement).value).toBe('domain-rule')

    await wrapper.get('#taxonomyDisplayName').setValue('House rule')
    mocks.updateCustomTag.mockResolvedValue(customTag({ displayName: 'House rule' }))
    await wrapper.get('.form-actions .btn-primary').trigger('click')
    await flushPromises()

    expect(mocks.updateCustomTag).toHaveBeenCalledWith(
      'client-1',
      'tag-1',
      expect.objectContaining({ displayName: 'House rule' }),
    )
  })

  it('retires a tag and keeps it listed as retired rather than removing it', async () => {
    mocks.fetchTaxonomy.mockResolvedValue(taxonomy({ customTags: [customTag()] }))
    const wrapper = mountSection()
    await flushPromises()

    const retireButton = wrapper
      .get('[data-testid="custom-tag-table"]')
      .findAll('button')
      .find((button) => button.text() === 'Retire')!

    mocks.retireCustomTag.mockResolvedValue(customTag({ retiredAt: '2026-07-28T00:00:00Z' }))
    mocks.fetchTaxonomy.mockResolvedValue(
      taxonomy({ customTags: [customTag({ retiredAt: '2026-07-28T00:00:00Z' })] }),
    )

    await retireButton.trigger('click')
    await flushPromises()

    expect(mocks.retireCustomTag).toHaveBeenCalledWith('client-1', 'tag-1')
    // Gone from the active table, still visible under retired: a finding that carries it keeps a name.
    expect(wrapper.find('[data-testid="custom-tag-table"]').exists()).toBe(false)
    expect(wrapper.text()).toContain('1 retired tag(s)')
    expect(wrapper.text()).toContain('cannot be reused')
  })

  it('reports a load failure instead of showing an empty vocabulary', async () => {
    mocks.fetchTaxonomy.mockRejectedValue(new Error('Failed to load the finding-type taxonomy.'))
    const wrapper = mountSection()
    await flushPromises()

    expect(wrapper.get('[data-testid="taxonomy-load-error"]').text()).toContain('Failed to load')
    expect(wrapper.find('[data-testid="core-tag-table"]').exists()).toBe(false)
  })
})
