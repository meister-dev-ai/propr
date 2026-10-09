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

let capabilityState: Array<{ key: string; isAvailable: boolean; message?: string | null }> = []

vi.mock('@/composables/useSession', () => ({
  useSession: () => ({
    getCapability: (key: string) => capabilityState.find((capability) => capability.key === key) ?? null,
  }),
}))

function setCapabilities(capabilities: Array<{ key: string; isAvailable: boolean; message?: string }>) {
  capabilityState = capabilities.map((capability) => ({
    key: capability.key,
    isAvailable: capability.isAvailable,
    message: capability.message ?? null,
  }))
}

const discovery = vi.hoisted(() => ({ descriptor: vi.fn(), scopes: vi.fn(), projects: vi.fn(), sources: vi.fn(), selection: vi.fn(), connections: vi.fn(), savedScopes: vi.fn() }))
vi.mock('@/services/providerDiscoveryService', () => ({
 listConnectionDescriptor: discovery.descriptor, listConnectionScopes: discovery.scopes, listConnectionProjects: discovery.projects,
 listConnectionSources: discovery.sources, resolveConnectionSelection: discovery.selection, listConnectionBranches: vi.fn(), listConnectionFilters: vi.fn(),
}))
vi.mock('@/services/providerConnectionsService', () => ({ listProviderConnections: discovery.connections, listProviderScopes: discovery.savedScopes }))
vi.mock('@/services/providerActivationService', () => ({ formatProviderFamily: (family: string) => family }))
import { connectionFixture, descriptorFixture, sourceFixture, selectionFixture } from '../../../../../tests/support/connectionDiscoveryFixtures'

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
    Object.values(discovery).forEach(mock => mock.mockReset())
    discovery.connections.mockResolvedValue([connectionFixture()])
    discovery.descriptor.mockResolvedValue(descriptorFixture)
    discovery.scopes.mockResolvedValue([{ scopeKey: 'native-scope', displayName: 'Boundary', savedScopeId: 'scope-1' }])
    discovery.projects.mockResolvedValue([{ projectId: 'project-1', projectName: 'Workspace' }])
    discovery.sources.mockResolvedValue([sourceFixture()])
    discovery.selection.mockResolvedValue(selectionFixture())
    discovery.savedScopes.mockResolvedValue([{ id: 'scope-1', scopePath: 'https://scm.example.com/native', scopeKind: 'organization', isEnabled: true }])
    setCapabilities([{ key: 'mention-answering', isAvailable: true }])
  })

  it('shows what a license would give instead of an empty table when mention answering is off', async () => {
    setCapabilities([
      { key: 'mention-answering', isAvailable: false, message: 'Mention answering requires a commercial license.' },
    ])

    const wrapper = mountTab()
    await flushPromises()

    expect(get).not.toHaveBeenCalled()
    expect(wrapper.text()).toContain('Mention answering is unavailable')
    expect(wrapper.text()).toContain('Mention answering requires a commercial license.')
    expect(wrapper.text()).not.toContain('New Config')
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

  async function select(wrapper: ReturnType<typeof mountTab>) {
    await wrapper.find('.section-card-header-actions .btn-primary').trigger('click')
    await flushPromises()
    await wrapper.find('#mention-connection').setValue('connection-1')
    await flushPromises()
    await wrapper.find('#mention-scope').setValue('native-scope')
    await flushPromises()
    if (wrapper.find('#mention-project').exists()) {
      await wrapper.find('#mention-project').setValue('project-1')
      await flushPromises()
    }
  }
  it('does not apply a closed descriptor request to a reopened form', async () => {
    get.mockResolvedValue(okResponse([]))
    let release: (value: unknown) => void = () => {}
    discovery.descriptor.mockImplementationOnce(() => new Promise(resolve => { release = resolve }))
    const wrapper = mountTab()
    await flushPromises()
    await wrapper.find('.section-card-header-actions .btn-primary').trigger('click')
    await flushPromises()
    void wrapper.find('#mention-connection').setValue('connection-1')
    await wrapper.find('.mention-form-actions .btn-secondary').trigger('click')
    await wrapper.find('.section-card-header-actions .btn-primary').trigger('click')
    await flushPromises()
    release(descriptorFixture)
    await flushPromises()
    expect((wrapper.find('#mention-connection').element as HTMLSelectElement).value).toBe('')
    expect(wrapper.find('#mention-scope').exists()).toBe(false)
  })
  it('stores the native coordinates and repository identity', async () => {
    get.mockResolvedValue(okResponse([])); post.mockResolvedValue(okResponse({ id: 'new' }))
    const wrapper = mountTab(); await flushPromises(); await select(wrapper)
    await wrapper.find('.mention-repo-list input').setValue(true)
    await wrapper.find('form').trigger('submit'); await flushPromises()
    expect(post.mock.calls[0][1].body).toMatchObject({
      connectionId: 'connection-1', scopeKey: 'native-scope', provider: 'azureDevOps',
      providerScopePath: 'https://scm.example.com/native', providerProjectKey: 'project-1',
      repoFilters: [{ repositoryId: 'repo-1', canonicalSourceRef: 'repo-1', sourceProvider: 'azureDevOps' }],
    })
    expect(discovery.sources).toHaveBeenCalledWith('client-1', 'connection-1', 'mention', 'native-scope', 'project-1', 'repository')
  })
  it('offers active connections without defaulting the provider', async () => {
    get.mockResolvedValue(okResponse([]))
    discovery.connections.mockResolvedValue([connectionFixture(), { ...connectionFixture('inactive'), isActive: false }])
    const wrapper = mountTab(); await flushPromises()
    await wrapper.find('.section-card-header-actions .btn-primary').trigger('click'); await flushPromises()
    expect(wrapper.find('#mention-connection').findAll('option').map(option => option.text())).toEqual(['Select a connection', 'connection-1'])
    expect(discovery.descriptor).not.toHaveBeenCalled()
  })
  it.each(['github', 'gitLab', 'forgejo'] as const)('stores %s native coordinates without a project stage', async provider => {
    get.mockResolvedValue(okResponse([])); post.mockResolvedValue(okResponse({ id: 'new' }))
    discovery.connections.mockResolvedValue([connectionFixture('connection-1', provider)])
    discovery.descriptor.mockResolvedValue({ ...descriptorFixture, provider, scopeLabel: 'Owner', projectLabel: null })
    discovery.sources.mockResolvedValue([{ ...sourceFixture('native-scope', '101'), organizationScopeId: null, providerScopePath: 'https://scm.example.com', canonicalSourceRef: { provider, value: 'native/101' } }])
    discovery.selection.mockResolvedValue({ ...selectionFixture('native-scope'), provider, organizationScopeId: null, providerScopePath: 'https://scm.example.com' })
    const wrapper = mountTab(); await flushPromises(); await select(wrapper)
    expect(wrapper.find('#mention-project').exists()).toBe(false)
    await wrapper.find('.mention-repo-list input').setValue(true)
    await wrapper.find('form').trigger('submit'); await flushPromises()
    expect(post.mock.calls[0][1].body).toMatchObject({ provider, providerProjectKey: 'native-scope', repoFilters: [{ repositoryId: '101', canonicalSourceRef: 'native/101', sourceProvider: provider }] })
  })
  it('preserves inaccessible saved repositories on an unrelated edit without filter replacement', async () => {
    get.mockResolvedValue(okResponse([{ id: 'cfg-1', clientId: 'client-1', provider: 'azureDevOps', organizationScopeId: 'scope-1', providerScopePath: 'https://scm.example.com/native', providerProjectKey: 'project-1', scanIntervalSeconds: 60, isActive: true, repoFilters: [{ id: 'retained-filter', repositoryId: 'inaccessible', displayName: 'Saved Repository' }] }]))
    patch.mockResolvedValue(okResponse({}))
    const wrapper = mountTab(); await flushPromises()
    await wrapper.find('.action-btn[title="Edit"]').trigger('click'); await flushPromises()
    expect(wrapper.text()).toContain('Saved Repository')
    expect((wrapper.find('#mention-scope').element as HTMLSelectElement).disabled).toBe(true)
    await wrapper.find('#mentionScanInterval').setValue(90)
    await wrapper.find('form').trigger('submit'); await flushPromises()
    expect(patch.mock.calls[0][1].body.repoFilters).toBeUndefined()
    expect(patch.mock.calls[0][1].body.scanIntervalSeconds).toBe(90)
  })
  it('sends a changed repository set with the resolved connection identity', async () => {
    get.mockResolvedValue(okResponse([{ id: 'cfg-1', clientId: 'client-1', provider: 'azureDevOps', organizationScopeId: 'scope-1', providerScopePath: 'https://scm.example.com/native', providerProjectKey: 'project-1', scanIntervalSeconds: 60, isActive: true, repoFilters: [{ id: 'retained-filter', repositoryId: 'inaccessible', displayName: 'Saved Repository' }] }]))
    patch.mockResolvedValue(okResponse({}))
    const wrapper = mountTab(); await flushPromises()
    await wrapper.find('.action-btn[title="Edit"]').trigger('click'); await flushPromises()
    const choices = wrapper.findAll('.mention-repo-list label')
    await choices.find(choice => choice.text().includes('Saved Repository'))!.find('input').setValue(false)
    await choices.find(choice => choice.text().includes('Repository One'))!.find('input').setValue(true)
    await wrapper.find('form').trigger('submit'); await flushPromises()
    expect(patch.mock.calls[0][1].body).toMatchObject({ connectionId: 'connection-1', scopeKey: 'native-scope', repoFilters: [{ repositoryId: 'repo-1', canonicalSourceRef: 'repo-1', sourceProvider: 'azureDevOps' }] })
  })

  it.each(['saved-canonical-bytes', null])('preserves retained repository coordinates while adding a new repository (%s)', async savedCanonical => {
    get.mockResolvedValue(okResponse([{ id: 'cfg-1', clientId: 'client-1', provider: 'azureDevOps', organizationScopeId: 'scope-1', providerScopePath: 'https://scm.example.com/native', providerProjectKey: 'project-1', scanIntervalSeconds: 60, isActive: true, repoFilters: [{ id: 'retained-filter', repositoryId: 'repo-1', canonicalSourceRef: savedCanonical, sourceProvider: null, displayName: 'Saved Repository' }] }]))
    discovery.sources.mockResolvedValue([
      { ...sourceFixture(), displayName: 'Renamed Repository', canonicalSourceRef: { provider: 'azureDevOps', value: 'new-live-canonical' } },
      { ...sourceFixture('project-1', 'repo-2'), displayName: 'New Repository', canonicalSourceRef: { provider: 'azureDevOps', value: 'new-repository-canonical' } },
    ])
    patch.mockResolvedValue(okResponse({}))
    const wrapper = mountTab(); await flushPromises()
    await wrapper.find('.action-btn[title="Edit"]').trigger('click'); await flushPromises()
    expect(wrapper.text()).toContain('Renamed Repository')
    const choices = wrapper.findAll('.mention-repo-list label')
    await choices.find(choice => choice.text().includes('New Repository'))!.find('input').setValue(true)
    await wrapper.find('form').trigger('submit'); await flushPromises()
    const filters = patch.mock.calls[0][1].body.repoFilters
    expect(filters.find((filter: { repositoryId: string }) => filter.repositoryId === 'repo-1')).toMatchObject({
      repositoryId: 'repo-1', canonicalSourceRef: savedCanonical ?? undefined, sourceProvider: undefined,
    })
    expect(filters.find((filter: { repositoryId: string }) => filter.repositoryId === 'repo-2')).toMatchObject({
      repositoryId: 'repo-2', canonicalSourceRef: 'new-repository-canonical', sourceProvider: 'azureDevOps',
    })
    wrapper.unmount()
  })

  it('refuses changed repository membership without resolved discovery but permits unchanged edits', async () => {
    get.mockResolvedValue(okResponse([{ id: 'cfg-1', clientId: 'client-1', providerScopePath: 'https://saved.example.com', providerProjectKey: 'saved-project', scanIntervalSeconds: 60, repoFilters: [{ repositoryId: 'one', displayName: 'One' }, { repositoryId: 'two', displayName: 'Two' }] }]))
    discovery.connections.mockResolvedValue([])
    patch.mockResolvedValue(okResponse({}))
    const wrapper = mountTab(); await flushPromises()
    await wrapper.find('.action-btn[title="Edit"]').trigger('click'); await flushPromises()
    const checkbox = wrapper.find('.mention-repo-list input')
    await checkbox.setValue(false)
    await wrapper.find('form').trigger('submit'); await flushPromises()
    expect(patch).not.toHaveBeenCalled()
    expect(wrapper.text()).toContain('Resolve the connection before changing repositories.')
    await checkbox.setValue(true)
    await wrapper.find('#mentionScanInterval').setValue(90)
    await wrapper.find('form').trigger('submit'); await flushPromises()
    expect(patch.mock.calls[0][1].body.repoFilters).toBeUndefined()
  })

  it('does not apply repositories from an abandoned scope', async () => {
    get.mockResolvedValue(okResponse([]))
    discovery.descriptor.mockResolvedValue({ ...descriptorFixture, projectLabel: null })
    discovery.scopes.mockResolvedValue([{ scopeKey: 'native-scope', displayName: 'One' }, { scopeKey: 'other', displayName: 'Two' }])
    let release: (value: unknown) => void = () => {}
    discovery.sources.mockImplementationOnce(() => new Promise(resolve => { release = resolve })).mockResolvedValue([{ ...sourceFixture('other'), displayName: 'Current Repository' }])
    const wrapper = mountTab(); await flushPromises()
    await wrapper.find('.section-card-header-actions .btn-primary').trigger('click'); await flushPromises()
    await wrapper.find('#mention-connection').setValue('connection-1'); await flushPromises()
    void wrapper.find('#mention-scope').setValue('native-scope')
    await wrapper.find('#mention-scope').setValue('other'); await flushPromises()
    release([{ ...sourceFixture(), displayName: 'Abandoned Repository' }]); await flushPromises()
    expect(wrapper.text()).toContain('Current Repository')
    expect(wrapper.text()).not.toContain('Abandoned Repository')
  })
})
