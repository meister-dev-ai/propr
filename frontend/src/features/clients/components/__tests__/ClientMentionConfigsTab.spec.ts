// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import ClientMentionConfigsTab from '@/features/clients/components/ClientMentionConfigsTab.vue'

const get = vi.fn()
const post = vi.fn()
const patch = vi.fn()
const del = vi.fn()

vi.mock('@/services/api', () => ({
  createAdminClient: () => ({ GET: get, POST: post, PATCH: patch, DELETE: del }),
  getApiErrorMessage: (_error: unknown, fallback: string) => fallback,
}))

vi.mock('@/composables/useNotification', () => ({
  useNotification: () => ({ notify: vi.fn() }),
}))

const listAdoOrganizationScopes = vi.fn()
const listAdoProjects = vi.fn()
const listAdoCrawlFilters = vi.fn()

vi.mock('@/services/adoDiscoveryService', () => ({
  listAdoOrganizationScopes: (...args: unknown[]) => listAdoOrganizationScopes(...args),
  listAdoProjects: (...args: unknown[]) => listAdoProjects(...args),
  listAdoCrawlFilters: (...args: unknown[]) => listAdoCrawlFilters(...args),
}))

function okResponse(data: unknown) {
  return { data, error: undefined, response: { ok: true } }
}

function mountTab() {
  return mount(ClientMentionConfigsTab, {
    props: { clientId: 'client-1' },
    global: {
      stubs: {
        ProgressOrb: true,
        ModalDialog: { props: ['isOpen'], template: '<div v-if="isOpen"><slot /></div>' },
        ConfirmDialog: { props: ['open'], template: '<div v-if="open" class="confirm" />' },
      },
    },
  })
}

describe('ClientMentionConfigsTab', () => {
  beforeEach(() => {
    get.mockReset()
    post.mockReset()
    patch.mockReset()
    del.mockReset()
    // Reset first: a mockReturnValueOnce left queued by an earlier test outlives a plain mockResolvedValue.
    listAdoOrganizationScopes.mockReset().mockResolvedValue([])
    listAdoProjects.mockReset().mockResolvedValue([])
    listAdoCrawlFilters.mockReset().mockResolvedValue([])
  })

  it('tells an operator plainly that nothing is answered when no configuration exists', async () => {
    get.mockResolvedValue(okResponse([]))

    const wrapper = mountTab()
    await flushPromises()

    expect(wrapper.text()).toContain('This client answers no mentions')
  })

  it('lists the repositories a configuration answers on', async () => {
    get.mockResolvedValue(
      okResponse([
        {
          id: 'cfg-1',
          clientId: 'client-1',
          provider: 'azureDevOps',
          providerScopePath: 'https://dev.azure.com/org',
          providerProjectKey: 'proj',
          scanIntervalSeconds: 90,
          isActive: true,
          createdAt: '2026-08-11T00:00:00Z',
          repoFilters: [{ id: 'f1', repositoryId: 'repo-guid', displayName: 'payments' }],
        },
      ]),
    )

    const wrapper = mountTab()
    await flushPromises()

    expect(wrapper.text()).toContain('payments')
    expect(wrapper.text()).toContain('proj')
    expect(wrapper.text()).toContain('90s')
  })

  it('shows only this client\'s configurations', async () => {
    get.mockResolvedValue(
      okResponse([
        {
          id: 'cfg-other',
          clientId: 'someone-else',
          provider: 'azureDevOps',
          providerScopePath: 'https://dev.azure.com/org',
          providerProjectKey: 'other-project',
          scanIntervalSeconds: 60,
          isActive: true,
          createdAt: '2026-08-11T00:00:00Z',
          repoFilters: [{ id: 'f9', repositoryId: 'other-repo', displayName: 'other-repo' }],
        },
      ]),
    )

    const wrapper = mountTab()
    await flushPromises()

    expect(wrapper.text()).not.toContain('other-project')
    expect(wrapper.text()).toContain('This client answers no mentions')

    // The local filter is the second line of defence. Asserting the query proves the server was asked to
    // scope the read, which is the contract that survives a component rewrite.
    expect(get).toHaveBeenCalledWith('/admin/mention-configurations', {
      params: { query: { clientId: 'client-1' } },
    })
  })

  it('refuses to save a configuration naming no repository', async () => {
    get.mockResolvedValue(okResponse([]))

    const wrapper = mountTab()
    await flushPromises()

    // The header's New Config button is the only way in; the empty state describes, it does not duplicate it.
    await wrapper.find('.section-card-header-actions .btn-primary').trigger('click')
    await flushPromises()

    // The form opens with nothing picked, so submitting it must not reach the server.
    await wrapper.find('form').trigger('submit')
    await flushPromises()

    expect(post).not.toHaveBeenCalled()
    expect(wrapper.text()).toContain('Select at least one repository')
  })

  it('does not let a closed edit form select a project in the next one', async () => {
    get.mockResolvedValue(
      okResponse([
        {
          id: 'cfg-1',
          clientId: 'client-1',
          provider: 'azureDevOps',
          providerScopePath: 'https://dev.azure.com/org',
          providerProjectKey: 'proj-guid',
          scanIntervalSeconds: 60,
          isActive: true,
          createdAt: '2026-08-11T00:00:00Z',
          repoFilters: [{ id: 'f1', repositoryId: 'repo-guid', displayName: 'payments-api' }],
        },
      ]),
    )
    listAdoProjects.mockResolvedValue([{ organizationScopeId: 'scope-1', projectId: 'proj-guid', projectName: 'Payments' }])

    // The edit form's organization request is still outstanding when the operator abandons it.
    let releaseScopes: (value: unknown[]) => void = () => {}
    listAdoOrganizationScopes.mockReturnValueOnce(new Promise((resolve) => (releaseScopes = resolve)))

    const wrapper = mountTab()
    await flushPromises()

    await wrapper.find('.action-btn[title="Edit"]').trigger('click')
    await wrapper.find('.mention-form-actions .btn-secondary').trigger('click')

    listAdoOrganizationScopes.mockResolvedValue([
      { id: 'scope-1', organizationUrl: 'https://dev.azure.com/org', displayName: 'org', isEnabled: true },
    ])
    await wrapper.find('.section-card-header-actions .btn-primary').trigger('click')
    await flushPromises()

    // Only now does the abandoned request answer. It must not reach into the form now on screen.
    releaseScopes([
      { id: 'scope-1', organizationUrl: 'https://dev.azure.com/org', displayName: 'org', isEnabled: true },
    ])
    await flushPromises()

    expect((wrapper.find('#mentionProjectKey').element as HTMLSelectElement).value).toBe('')
  })

  it('stores the provider repository id behind a picked repository name', async () => {
    get.mockResolvedValue(okResponse([]))
    post.mockResolvedValue(okResponse({ id: 'cfg-new' }))
    listAdoOrganizationScopes.mockResolvedValue([
      { id: 'scope-1', organizationUrl: 'https://dev.azure.com/org', displayName: 'org', isEnabled: true },
    ])
    listAdoProjects.mockResolvedValue([{ organizationScopeId: 'scope-1', projectId: 'proj-guid', projectName: 'Payments' }])
    listAdoCrawlFilters.mockResolvedValue([
      {
        canonicalSourceRef: { provider: 'azureDevOps', value: 'repo-guid' },
        displayName: 'payments-api',
        branchSuggestions: [],
      },
    ])

    const wrapper = mountTab()
    await flushPromises()

    await wrapper.find('.section-card-header-actions .btn-primary').trigger('click')
    await flushPromises()

    await wrapper.find('#mentionScopePath').setValue('scope-1')
    await flushPromises()
    await wrapper.find('#mentionProjectKey').setValue('proj-guid')
    await flushPromises()

    // The operator picks a name; the id underneath is what scanning matches on.
    expect(wrapper.text()).toContain('payments-api')
    await wrapper.find('.mention-repo-list input[type="checkbox"]').setValue(true)
    await wrapper.find('form').trigger('submit')
    await flushPromises()

    expect(post).toHaveBeenCalledTimes(1)
    expect(post.mock.calls[0][1].body).toMatchObject({
      providerScopePath: 'https://dev.azure.com/org',
      providerProjectKey: 'proj-guid',
      repoFilters: [
        {
          repositoryId: 'repo-guid',
          displayName: 'payments-api',
          canonicalSourceRef: 'repo-guid',
          sourceProvider: 'azureDevOps',
        },
      ],
    })
  })
})
