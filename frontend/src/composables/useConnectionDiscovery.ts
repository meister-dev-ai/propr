// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { computed, onBeforeUnmount, reactive, watch } from 'vue'
import { listProviderConnections, listProviderScopes, type ClientScmConnectionDto } from '@/services/providerConnectionsService'
import {
  listConnectionDescriptor, listConnectionScopes, listConnectionProjects, listConnectionSources,
  listConnectionBranches, listConnectionFilters, resolveConnectionSelection,
  type DiscoveryDescriptor, type DiscoveryScope, type DiscoveryProject, type DiscoverySource,
  type DiscoveryBranch, type DiscoveryFilter, type DiscoverySelection, type DiscoverySourceKind, type DiscoveryPurpose,
} from '@/services/providerDiscoveryService'

interface SavedSelection {
  provider?: string | null
  organizationScopeId?: string | null
  providerScopePath?: string | null
  providerProjectKey?: string | null
}

type Stage = 'connections' | 'descriptor' | 'scopes' | 'projects' | 'sources' | 'branches'
const stages: Stage[] = ['connections', 'descriptor', 'scopes', 'projects', 'sources', 'branches']

function compareCoordinate(value?: string | null) {
  const normalized = value?.trim().replace(/\/+$/, '') ?? ''
  try { return new URL(normalized).href.replace(/\/+$/, '').toLowerCase() }
  catch { return normalized }
}

function sameHost(left: string, right: string) {
  try { return new URL(left).origin.toLowerCase() === new URL(right).origin.toLowerCase() }
  catch { return false }
}

export function useConnectionDiscovery(clientId: () => string, purpose: DiscoveryPurpose, active: () => boolean = () => true) {
  const state = reactive({
    connections: [] as ClientScmConnectionDto[], connectionId: '', descriptor: null as DiscoveryDescriptor | null,
    scopes: [] as DiscoveryScope[], scopeKey: '', projects: [] as DiscoveryProject[], projectId: '',
    sourceKind: '' as DiscoverySourceKind | '', sources: [] as DiscoverySource[], sourceKey: '',
    filters: [] as DiscoveryFilter[], branches: [] as DiscoveryBranch[],
    selection: null as DiscoverySelection | null, saved: null as SavedSelection | null,
    unresolved: false, ambiguous: false,
    loading: { connections: false, descriptor: false, scopes: false, projects: false, sources: false, branches: false },
    errors: { connections: '', descriptor: '', scopes: '', projects: '', sources: '', branches: '' },
  })
  let generation = 0
  const savedScopeIds = new Map<string, string>()
  const requests = Object.fromEntries(stages.map(stage => [stage, 0])) as Record<Stage, number>
  const selectionKey = () => [clientId(), state.connectionId, state.scopeKey, state.projectId, state.sourceKind, state.sourceKey].join('\u0000')
  const selectedSource = computed(() => state.sources.find(source => sourceKey(source) === state.sourceKey))
  const ready = computed(() => Boolean(state.selection) && !state.loading.sources && !state.errors.sources)

  function capture() {
    const capturedGeneration = generation
    const capturedClient = clientId()
    return () => generation === capturedGeneration && clientId() === capturedClient && active()
  }

  function sourceKey(source: DiscoverySource): string {
    return source.canonicalSourceRef ? source.canonicalSourceRef.provider + '::' + source.canonicalSourceRef.value : ''
  }

  function invalidate(from: Stage) {
    for (const stage of stages.slice(stages.indexOf(from))) {
      requests[stage]++
      state.loading[stage] = false
      state.errors[stage] = ''
    }
  }

  function clearSources() {
    invalidate('sources')
    state.sources = []
    state.filters = []
    state.sourceKey = ''
    state.branches = []
    state.selection = null
  }

  function clearScopes() {
    invalidate('scopes')
    state.scopes = []
    state.scopeKey = ''
    state.projects = []
    state.projectId = ''
    clearSources()
  }

  function reset() {
    generation++
    invalidate('connections')
    state.connections = []
    state.connectionId = ''
    state.descriptor = null
    state.sourceKind = ''
    state.saved = null
    savedScopeIds.clear()
    state.unresolved = false
    state.ambiguous = false
    clearScopes()
  }

  async function run<T>(stage: Stage, load: () => Promise<T>, apply: (value: T) => void): Promise<boolean> {
    const currentGeneration = generation
    const request = ++requests[stage]
    const key = selectionKey()
    const fresh = () => currentGeneration === generation && request === requests[stage] && key === selectionKey() && active()
    state.loading[stage] = true
    state.errors[stage] = ''
    try {
      const value = await load()
      if (!fresh()) return false
      apply(value)
      return true
    } catch (cause) {
      if (fresh()) state.errors[stage] = cause instanceof Error ? cause.message : 'Discovery could not be completed.'
      return false
    } finally {
      if (currentGeneration === generation && request === requests[stage]) state.loading[stage] = false
    }
  }

  async function loadConnections() {
    if (!clientId() || !active()) return false
    return run('connections', () => listProviderConnections(clientId()), connections => {
      state.connections = connections.filter(connection => connection.isActive)
    })
  }

  async function selectConnection(connectionId: string) {
    generation++
    invalidate('descriptor')
    state.connectionId = connectionId
    state.descriptor = null
    state.sourceKind = ''
    state.unresolved = false
    state.ambiguous = false
    clearScopes()
    if (!connectionId) return false
    const currentGeneration = generation
    if (!await run('descriptor', () => listConnectionDescriptor(clientId(), connectionId, purpose), descriptor => {
      state.descriptor = descriptor
      state.sourceKind = descriptor.sourceKinds?.[0]?.kind ?? ''
    }) || currentGeneration !== generation) return false
    return run('scopes', () => listConnectionScopes(clientId(), connectionId, purpose), scopes => { state.scopes = scopes })
  }

  async function loadSources() {
    clearSources()
    if (!state.connectionId || !state.scopeKey || !state.sourceKind || state.descriptor?.projectLabel && !state.projectId) return false
    const kind = state.sourceKind
    const project = state.projectId || undefined
    const client = clientId()
    const connection = state.connectionId
    const scope = state.scopeKey
    const current = capture()
    const key = selectionKey()
    return run('sources', async () => {
      const sources = await listConnectionSources(client, connection, purpose, scope, project, kind)
      if (!current() || key !== selectionKey()) throw new Error('Selection changed.')
      const selection = await resolveConnectionSelection(client, connection, purpose, scope, project)
      if (!current() || key !== selectionKey()) throw new Error('Selection changed.')
      const filters = purpose === 'crawl' || purpose === 'webhook'
        ? await listConnectionFilters(client, connection, purpose, scope, project) : []
      return { sources, selection, filters }
    }, result => {
      state.sources = result.sources
      state.selection = result.selection
      state.filters = result.filters
    })
  }

  async function selectScope(scopeKey: string) {
    invalidate('projects')
    state.scopeKey = scopeKey
    state.projects = []
    state.projectId = ''
    clearSources()
    if (!scopeKey || !state.connectionId) return false
    if (state.descriptor?.projectLabel) {
      return run('projects', () => listConnectionProjects(clientId(), state.connectionId, purpose, scopeKey), projects => { state.projects = projects })
    }
    return loadSources()
  }

  async function selectProject(projectId: string) {
    state.projectId = projectId
    return loadSources()
  }

  async function selectSourceKind(kind: DiscoverySourceKind | '') {
    state.sourceKind = kind
    return loadSources()
  }

  async function selectSource(key: string) {
    invalidate('branches')
    state.sourceKey = key
    state.branches = []
    const source = selectedSource.value
    if (!source || !state.descriptor?.supportsBranches || !source.canonicalSourceRef || !source.sourceKind) return false
    return run('branches', () => listConnectionBranches(clientId(), state.connectionId, purpose, state.scopeKey,
      source.providerProjectKey ?? '', source.sourceKind!, source.canonicalSourceRef!), branches => { state.branches = branches })
  }

  async function resolveForEdit(saved: SavedSelection) {
    reset()
    state.saved = saved
    const currentGeneration = generation
    const connectionsLoaded = await loadConnections()
    if (generation !== currentGeneration || !active()) return
    if (!connectionsLoaded) { state.unresolved = true; return }
    const candidates = state.connections.filter(connection => !saved.provider || connection.providerFamily === saved.provider)
    const matches: ClientScmConnectionDto[] = []
    const unknown = new Set<string>()
    const path = compareCoordinate(saved.providerScopePath)
    for (const connection of candidates) {
      try {
        const scopes = await listProviderScopes(clientId(), connection.id)
        if (generation !== currentGeneration || !active()) return
        const matchedScope = saved.organizationScopeId
          ? scopes.find(scope => scope.id === saved.organizationScopeId)
          : scopes.find(scope => compareCoordinate(scope.scopePath) === path)
        if (matchedScope?.id) savedScopeIds.set(connection.id, matchedScope.id)
        if (matchedScope || !saved.organizationScopeId && compareCoordinate(connection.hostBaseUrl) === path) matches.push(connection)
      } catch {
        if (generation !== currentGeneration || !active()) return
        if (!path || sameHost(connection.hostBaseUrl, path)) {
          matches.push(connection)
          unknown.add(connection.id)
        }
      }
    }
    state.connections = matches
    if (matches.length !== 1 || unknown.size > 0) {
      state.unresolved = true
      state.ambiguous = matches.length > 1 || unknown.size > 0
      if (unknown.size) state.errors.connections = 'Some matching connections could not be checked. Choose a connection or retry discovery.'
      return
    }
    await selectSavedConnection(matches[0]!.id)
  }

  async function selectSavedConnection(connectionId: string) {
    const saved = state.saved
    if (!saved || !state.connections.some(connection => connection.id === connectionId)) return
    state.errors.connections = ''
    const expectedGeneration = generation + 1
    const selected = await selectConnection(connectionId)
    if (generation !== expectedGeneration || !active()) return
    if (!selected) { state.unresolved = true; return }
    const editGeneration = generation
    const savedScopeId = saved.organizationScopeId || savedScopeIds.get(connectionId)
    const scope = savedScopeId
      ? state.scopes.find(scope => scope.savedScopeId === savedScopeId)
      : state.scopes.find(scope => compareCoordinate(scope.scopeKey) === compareCoordinate(saved.providerScopePath) || scope.scopeKey === saved.providerProjectKey)
    if (!scope?.scopeKey) { state.unresolved = true; return }
    const scopeSelected = await selectScope(scope.scopeKey)
    if (generation !== editGeneration || !active() || state.scopeKey !== scope.scopeKey) return
    if (!scopeSelected) { state.unresolved = true; return }
    if (state.descriptor?.projectLabel && saved.providerProjectKey) {
      const savedProject = saved.providerProjectKey.toLowerCase()
      const idMatches = state.projects.filter(project => project.projectId?.toLowerCase() === savedProject)
      const projects = idMatches.length ? idMatches : state.projects.filter(project => project.projectName?.toLowerCase() === savedProject)
      if (projects.length !== 1 || !projects[0]?.projectId) {
        state.unresolved = true
        state.errors.projects = 'The saved project could not be resolved through this connection.'
        return
      }
      const projectId = projects[0].projectId
      const projectSelected = await selectProject(projectId)
      if (generation !== editGeneration || !active() || state.scopeKey !== scope.scopeKey || state.projectId !== projectId) return
      state.unresolved = !projectSelected || !ready.value
    } else {
      state.unresolved = !ready.value
    }
  }

  watch(() => [clientId(), active()], () => reset(), { flush: 'sync' })
  onBeforeUnmount(reset)
  return { state, ready, selectedSource, sourceKey, reset, capture, loadConnections, selectConnection,
    selectScope, selectProject, selectSourceKind, selectSource, loadSources, resolveForEdit, selectSavedConnection }
}
