import { defineComponent } from 'vue'
import { flushPromises, mount } from '@vue/test-utils'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { useConnectionDiscovery } from '@/composables/useConnectionDiscovery'
import { connectionFixture, descriptorFixture, selectionFixture, sourceFixture } from '../support/connectionDiscoveryFixtures'

const mocks = vi.hoisted(() => ({
  connections: vi.fn(), savedScopes: vi.fn(), descriptor: vi.fn(), scopes: vi.fn(), projects: vi.fn(),
  sources: vi.fn(), selection: vi.fn(), branches: vi.fn(), filters: vi.fn(),
}))
vi.mock('@/services/providerConnectionsService', () => ({
  listProviderConnections: mocks.connections, listProviderScopes: mocks.savedScopes,
}))
vi.mock('@/services/providerDiscoveryService', () => ({
  listConnectionDescriptor: mocks.descriptor, listConnectionScopes: mocks.scopes,
  listConnectionProjects: mocks.projects, listConnectionSources: mocks.sources,
  resolveConnectionSelection: mocks.selection, listConnectionBranches: mocks.branches, listConnectionFilters: mocks.filters,
}))

function deferred<T>() {
  let resolve!: (value: T) => void
  let reject!: (reason: Error) => void
  const promise = new Promise<T>((accept, refuse) => { resolve = accept; reject = refuse })
  return { promise, resolve, reject }
}

function harness(purpose: 'crawl' | 'mention' | 'procursor' = 'crawl') {
  let discovery!: ReturnType<typeof useConnectionDiscovery>
  const wrapper = mount(defineComponent({
    setup() { discovery = useConnectionDiscovery(() => 'client-1', purpose); return () => null },
  }))
  return { discovery, wrapper }
}

beforeEach(() => {
  vi.resetAllMocks()
  mocks.connections.mockResolvedValue([connectionFixture()])
  mocks.savedScopes.mockResolvedValue([])
  mocks.descriptor.mockResolvedValue(descriptorFixture)
  mocks.scopes.mockResolvedValue([{ scopeKey: 'native-scope', displayName: 'Native boundary', savedScopeId: 'scope-1' }])
  mocks.projects.mockResolvedValue([{ projectId: 'project-1', projectName: 'Native workspace' }, { projectId: 'project-2', projectName: 'Second' }])
  mocks.sources.mockImplementation((_client, _connection, _purpose, _scope, project) => Promise.resolve([sourceFixture(project)]))
  mocks.selection.mockImplementation((_client, connection, _purpose, _scope, project) => Promise.resolve(selectionFixture(project, connection)))
  mocks.branches.mockResolvedValue([{ branchName: 'main', isDefault: true }])
  mocks.filters.mockResolvedValue([])
})

describe('connection discovery generations', () => {
  it('discards edit-resolution failure after a close and reopen', async () => {
    const { discovery, wrapper } = harness()
    const oldDescriptor = deferred<typeof descriptorFixture>()
    mocks.descriptor.mockReturnValueOnce(oldDescriptor.promise)
    const oldEdit = discovery.resolveForEdit({ provider: 'azureDevOps', providerScopePath: 'https://scm.example.com', providerProjectKey: 'project-1' })
    await flushPromises()
    discovery.reset()
    await discovery.loadConnections()
    oldDescriptor.reject(new Error('Old request failed.'))
    await oldEdit
    expect(discovery.state.unresolved).toBe(false)
    expect(discovery.state.errors.descriptor).toBe('')
    wrapper.unmount()
  })

  it('discards saved-scope read errors after another edit starts', async () => {
    const { discovery, wrapper } = harness()
    const oldScopes = deferred<never>()
    mocks.savedScopes.mockReturnValueOnce(oldScopes.promise)
    const oldEdit = discovery.resolveForEdit({ organizationScopeId: 'old-scope' })
    await flushPromises()
    const newEdit = discovery.resolveForEdit({ providerScopePath: 'https://scm.example.com', providerProjectKey: 'project-1' })
    await flushPromises()
    oldScopes.reject(new Error('Old scope failed.'))
    await Promise.all([oldEdit, newEdit])
    expect(discovery.state.errors.connections).toBe('')
    expect(discovery.state.connectionId).toBe('connection-1')
    wrapper.unmount()
  })

  it('stops stale source follow-up requests and keeps the current project', async () => {
    const { discovery, wrapper } = harness()
    await discovery.loadConnections()
    await discovery.selectConnection('connection-1')
    await discovery.selectScope('native-scope')
    const oldSources = deferred<ReturnType<typeof sourceFixture>[]>()
    mocks.sources.mockReturnValueOnce(oldSources.promise)
    const oldLoad = discovery.selectProject('project-1')
    const newLoad = discovery.selectProject('project-2')
    await newLoad
    oldSources.resolve([sourceFixture('project-1')])
    await oldLoad
    expect(mocks.selection).toHaveBeenCalledTimes(1)
    expect(mocks.selection).toHaveBeenCalledWith('client-1', 'connection-1', 'crawl', 'native-scope', 'project-2')
    expect(discovery.state.selection?.providerProjectKey).toBe('project-2')
    wrapper.unmount()
  })

  it('keeps native coordinates when the source listing is empty', async () => {
    const { discovery, wrapper } = harness()
    mocks.sources.mockResolvedValue([])
    await discovery.loadConnections()
    await discovery.selectConnection('connection-1')
    await discovery.selectScope('native-scope')
    await discovery.selectProject('project-1')
    expect(discovery.state.sources).toEqual([])
    expect(discovery.state.selection).toEqual(selectionFixture())
    expect(discovery.ready.value).toBe(true)
    wrapper.unmount()
  })

  it('requires a choice for same-host historical connections and hydrates the saved hierarchy', async () => {
    const { discovery, wrapper } = harness()
    mocks.connections.mockResolvedValue([connectionFixture('connection-1'), connectionFixture('connection-2')])
    mocks.descriptor.mockResolvedValue({ ...descriptorFixture, projectLabel: null })
    mocks.scopes.mockResolvedValue([{ scopeKey: 'owner', displayName: 'Owner' }])
    await discovery.resolveForEdit({ providerScopePath: 'https://scm.example.com', providerProjectKey: 'owner' })
    expect(discovery.state.ambiguous).toBe(true)
    expect(discovery.state.connectionId).toBe('')
    await discovery.selectSavedConnection('connection-2')
    expect(discovery.state.connectionId).toBe('connection-2')
    expect(discovery.state.scopeKey).toBe('owner')
    expect(discovery.state.unresolved).toBe(false)
    wrapper.unmount()
  })

  it('resolves a legacy organization URL through the saved native scope mapping', async () => {
    const { discovery, wrapper } = harness()
    mocks.connections.mockResolvedValue([{ ...connectionFixture(), hostBaseUrl: 'https://dev.azure.com' }])
    mocks.savedScopes.mockResolvedValue([{ id: 'scope-1', scopePath: 'https://dev.azure.com/saved-org' }])
    mocks.scopes.mockResolvedValue([{ scopeKey: 'native-scope', displayName: 'Organization', savedScopeId: 'scope-1' }])
    await discovery.resolveForEdit({ provider: 'azureDevOps', providerScopePath: 'https://dev.azure.com/saved-org', providerProjectKey: 'project-1' })
    expect(discovery.state.connectionId).toBe('connection-1')
    expect(discovery.state.scopeKey).toBe('native-scope')
    expect(discovery.state.projectId).toBe('project-1')
    expect(discovery.ready.value).toBe(true)
    wrapper.unmount()
  })

  it('leaves a failed saved project unresolved and permits retry', async () => {
    const { discovery, wrapper } = harness()
    mocks.savedScopes.mockResolvedValue([{ id: 'scope-1', scopePath: 'https://scm.example.com/native' }])
    mocks.sources.mockRejectedValueOnce(new Error('Unavailable repository listing.'))
    await discovery.resolveForEdit({ organizationScopeId: 'scope-1', providerProjectKey: 'project-1' })
    expect(discovery.state.unresolved).toBe(true)
    expect(discovery.ready.value).toBe(false)
    await discovery.selectSavedConnection('connection-1')
    expect(discovery.ready.value).toBe(true)
    expect(discovery.state.unresolved).toBe(false)
    wrapper.unmount()
  })

  it('resolves a saved native project name through project metadata', async () => {
    const { discovery, wrapper } = harness()
    mocks.savedScopes.mockResolvedValue([{ id: 'scope-1' }])
    mocks.projects.mockResolvedValue([{ projectId: 'project-1', projectName: 'Saved Workspace' }])
    await discovery.resolveForEdit({ organizationScopeId: 'scope-1', providerProjectKey: 'Saved Workspace' })
    expect(discovery.state.projectId).toBe('project-1')
    expect(discovery.state.saved?.providerProjectKey).toBe('Saved Workspace')
    wrapper.unmount()
  })

  it('prefers the saved project ID when another native project has that name', async () => {
    const { discovery, wrapper } = harness()
    mocks.savedScopes.mockResolvedValue([{ id: 'scope-1' }])
    mocks.projects.mockResolvedValue([{ projectId: 'project-1', projectName: 'Workspace' }, { projectId: 'other-project', projectName: 'PROJECT-1' }])
    await discovery.resolveForEdit({ organizationScopeId: 'scope-1', providerProjectKey: 'PROJECT-1' })
    expect(discovery.state.projectId).toBe('project-1')
    expect(discovery.state.saved?.providerProjectKey).toBe('PROJECT-1')
    expect(discovery.state.unresolved).toBe(false)
    wrapper.unmount()
  })

  it('compares historical URLs without changing their saved bytes', async () => {
    const { discovery, wrapper } = harness()
    const savedPath = 'HTTPS://SCM.EXAMPLE.COM/native/'
    mocks.savedScopes.mockResolvedValue([{ id: 'scope-1', scopePath: 'https://scm.example.com/native' }])
    await discovery.resolveForEdit({ providerScopePath: savedPath, providerProjectKey: 'project-1' })
    expect(discovery.state.connectionId).toBe('connection-1')
    expect(discovery.state.saved?.providerScopePath).toBe(savedPath)
    wrapper.unmount()
  })

  it('continues saved scope resolution after an unrelated connection read fails', async () => {
    const { discovery, wrapper } = harness()
    mocks.connections.mockResolvedValue([{ ...connectionFixture('unrelated'), hostBaseUrl: 'https://unrelated.example.com' }, connectionFixture()])
    mocks.savedScopes.mockImplementation((_client, connection) => connection === 'unrelated'
      ? Promise.reject(new Error('Unrelated request failed.')) : Promise.resolve([{ id: 'scope-1' }]))
    await discovery.resolveForEdit({ organizationScopeId: 'scope-1', providerScopePath: 'https://scm.example.com/native', providerProjectKey: 'project-1' })
    expect(discovery.state.connectionId).toBe('connection-1')
    expect(discovery.ready.value).toBe(true)
    wrapper.unmount()
  })

  it('does not silently resolve a known match when another same-host candidate is unknown', async () => {
    const { discovery, wrapper } = harness()
    mocks.connections.mockResolvedValue([connectionFixture(), connectionFixture('unknown')])
    mocks.savedScopes.mockImplementation((_client, connection) => connection === 'unknown'
      ? Promise.reject(new Error('Unknown ownership.')) : Promise.resolve([{ id: 'scope-1' }]))
    await discovery.resolveForEdit({ organizationScopeId: 'scope-1', providerScopePath: 'https://scm.example.com/native', providerProjectKey: 'project-1' })
    expect(discovery.state.ambiguous).toBe(true)
    expect(discovery.state.connectionId).toBe('')
    expect(discovery.state.connections.map(connection => connection.id)).toEqual(['connection-1', 'unknown'])
    wrapper.unmount()
  })

  it('does not request repository filters for knowledge or mention source selection', async () => {
    for (const purpose of ['procursor', 'mention'] as const) {
      const { discovery, wrapper } = harness(purpose)
      await discovery.loadConnections()
      await discovery.selectConnection('connection-1')
      await discovery.selectScope('native-scope')
      await discovery.selectProject('project-1')
      wrapper.unmount()
    }
    expect(mocks.filters).not.toHaveBeenCalled()
  })

  it('invalidates pending discovery when its owner unmounts', async () => {
    const { discovery, wrapper } = harness()
    const descriptor = deferred<typeof descriptorFixture>()
    mocks.descriptor.mockReturnValue(descriptor.promise)
    const pending = discovery.selectConnection('connection-1')
    wrapper.unmount()
    descriptor.resolve(descriptorFixture)
    await pending
    expect(discovery.state.descriptor).toBeNull()
    expect(mocks.scopes).not.toHaveBeenCalled()
  })
})
