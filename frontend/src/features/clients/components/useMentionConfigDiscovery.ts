import { computed, reactive } from 'vue'
import {
  listAdoCrawlFilters,
  listAdoOrganizationScopes,
  listAdoProjects,
} from '@/services/adoDiscoveryService'
import { listProviderConnections, type ScmProviderFamily } from '@/services/providerConnectionsService'
import {
  listProviderRepositoryOptions,
  listProviderScopeOptions,
} from '@/services/providerDiscoveryService'

/** An option in the first dropdown: an Azure DevOps organization scope, or a provider connection. */
export interface MentionHostOption {
  /** Identifies the option. An organization scope id on Azure DevOps, a connection id otherwise. */
  id: string
  label: string
  /** Stored as the configuration's providerScopePath. */
  scopePath: string
  /** The connection id passed to the discovery endpoints. */
  connectionId: string
}

/** An option in the second dropdown: an Azure DevOps project, or an owner, organization or group. */
export interface MentionScopeOption {
  /** Stored as the configuration's providerProjectKey. */
  id: string
  label: string
}

/** A repository the configuration can select. */
export interface MentionRepositoryOption {
  repositoryId: string
  displayName: string
  canonicalSourceRef?: string
  sourceProvider?: string
}

/**
 * Loads the three dropdowns on the mention configuration form for any provider.
 *
 * The form has the same three levels on every provider, and the configuration stores the same three values:
 * a scope path, a project key, and repositories by their provider-native id. Only the endpoints differ. Azure
 * DevOps has organization scopes the client configures separately, then projects within one, so the first two
 * levels come from listAdoOrganizationScopes and listAdoProjects. GitHub, GitLab and Forgejo are reached at
 * their connection's own host, so the first level is the client's connections for that provider and the
 * second is listProviderScopeOptions, which returns owners, organizations or groups.
 */
export function useMentionConfigDiscovery(clientId: () => string) {
  const state = reactive({
    provider: 'azureDevOps' as ScmProviderFamily,

    hostId: '',
    hosts: [] as MentionHostOption[],
    loadingHosts: false,
    hostError: '',

    scopeId: '',
    scopes: [] as MentionScopeOption[],
    loadingScopes: false,
    scopeError: '',

    repositories: [] as MentionRepositoryOption[],
    loadingRepositories: false,
    repositoryError: '',

    // True when a saved configuration's scope path matches none of the client's current connections or
    // organization scopes. The form then lists the stored repositories instead of an empty dropdown.
    unresolvedScope: false,
  })

  // Each loader increments its own counter before starting and compares afterwards, so a response that
  // arrives after a newer request was made is discarded instead of overwriting it.
  let hostsRequestId = 0
  let scopesRequestId = 0
  let repositoriesRequestId = 0

  // Per-loader counters are not enough for resolveForEdit, which awaits several loads and chooses what to
  // select between them. Closing or reopening the form has to invalidate that whole sequence, or a
  // continuation from the previous form matches its saved configuration against the list loaded for the new
  // one and selects the wrong scope.
  let formRequestId = 0

  const selectedHost = computed(() => state.hosts.find((host) => host.id === state.hostId))

  /** The scope path of the current selection, which is what a configuration stores. */
  const scopePath = computed(() => selectedHost.value?.scopePath ?? '')

  const isAzureDevOps = computed(() => state.provider === 'azureDevOps')

  function toMessage(cause: unknown, fallback: string) {
    return cause instanceof Error && cause.message ? cause.message : fallback
  }

  function clearRepositories() {
    repositoriesRequestId += 1
    state.repositories = []
    state.loadingRepositories = false
    state.repositoryError = ''
  }

  function clearScopes() {
    scopesRequestId += 1
    state.scopeId = ''
    state.scopes = []
    state.loadingScopes = false
    state.scopeError = ''
    clearRepositories()
  }

  function reset() {
    formRequestId += 1
    hostsRequestId += 1
    state.hostId = ''
    state.hosts = []
    state.loadingHosts = false
    state.hostError = ''
    state.unresolvedScope = false
    clearScopes()
  }

  /**
   * Points the form at a provider. Everything picked under the previous one is dropped, because a repository
   * belonging to one provider must not be submitted against another.
   */
  async function selectProvider(provider: ScmProviderFamily) {
    reset()
    state.provider = provider
    await loadHosts()
  }

  async function loadHosts() {
    const requestId = ++hostsRequestId
    state.loadingHosts = true
    state.hostError = ''

    try {
      const hosts = isAzureDevOps.value
        ? await loadAzureOrganizations()
        : await loadProviderConnections(state.provider)

      if (requestId !== hostsRequestId) {
        return
      }

      state.hosts = hosts
    } catch (cause) {
      if (requestId !== hostsRequestId) {
        return
      }

      state.hosts = []
      state.hostError = toMessage(cause, 'Failed to load where this client is reached.')
    } finally {
      if (requestId === hostsRequestId) {
        state.loadingHosts = false
      }
    }
  }

  // Azure DevOps organizations are configured per client and stored by ProPR, so they are read from ProPR
  // rather than from the provider. The other providers have no such record: their host is the connection's
  // own, so loadProviderConnections below lists the client's connections instead.
  async function loadAzureOrganizations(): Promise<MentionHostOption[]> {
    const scopes = await listAdoOrganizationScopes(clientId())
    return scopes
      .filter((scope) => Boolean(scope.isEnabled))
      .map((scope) => ({
        id: scope.id,
        label: scope.displayName || scope.organizationUrl,
        scopePath: scope.organizationUrl,
        connectionId: scope.connectionId,
      }))
      .sort((left, right) => left.label.localeCompare(right.label))
  }

  async function loadProviderConnections(provider: ScmProviderFamily): Promise<MentionHostOption[]> {
    const connections = await listProviderConnections(clientId())
    return connections
      .filter((connection) => connection.providerFamily === provider && connection.isActive)
      .map((connection) => ({
        id: connection.id,
        label: connection.displayName || connection.hostBaseUrl,
        scopePath: connection.hostBaseUrl,
        connectionId: connection.id,
      }))
      .sort((left, right) => left.label.localeCompare(right.label))
  }

  async function loadScopes(hostId: string) {
    const requestId = ++scopesRequestId
    state.loadingScopes = true
    state.scopeError = ''

    try {
      const host = state.hosts.find((candidate) => candidate.id === hostId)

      // Azure DevOps projects are listed from the organization scope selected above; the other providers list
      // owners, organizations or groups from the connection. Both produce a MentionScopeOption whose id is
      // stored as the configuration's project key.
      const scopes = isAzureDevOps.value
        ? (await listAdoProjects(clientId(), hostId)).map((project) => ({
            id: project.projectId ?? '',
            label: project.projectName || project.projectId || '',
          }))
        : (await listProviderScopeOptions(clientId(), state.provider, host?.connectionId ?? '')).map((scope) => ({
            id: scope.scopePath ?? '',
            label: scope.displayName || scope.scopePath || '',
          }))

      if (requestId !== scopesRequestId || state.hostId !== hostId) {
        return
      }

      state.scopes = scopes
        .filter((scope) => scope.id.length > 0)
        .sort((left, right) => left.label.localeCompare(right.label))
    } catch (cause) {
      if (requestId !== scopesRequestId || state.hostId !== hostId) {
        return
      }

      state.scopes = []
      state.scopeError = toMessage(cause, 'Failed to load what this connection can reach.')
    } finally {
      if (requestId === scopesRequestId && state.hostId === hostId) {
        state.loadingScopes = false
      }
    }
  }

  async function loadRepositories(hostId: string, scopeId: string) {
    const requestId = ++repositoriesRequestId
    state.loadingRepositories = true
    state.repositoryError = ''

    try {
      const host = state.hosts.find((candidate) => candidate.id === hostId)

      // Two endpoints because the identifiers differ. Azure DevOps repositories are addressed through the
      // organization scope and project and carry a canonical source reference; on the other providers the
      // discovery endpoint returns the provider's own repository id, which the configuration stores.
      const repositories = isAzureDevOps.value
        ? (await listAdoCrawlFilters(clientId(), hostId, scopeId)).map((option) => ({
            repositoryId: option.canonicalSourceRef?.value ?? '',
            displayName: option.displayName ?? '',
            canonicalSourceRef: option.canonicalSourceRef?.value ?? undefined,
            sourceProvider: option.canonicalSourceRef?.provider ?? undefined,
          }))
        : (
            await listProviderRepositoryOptions(clientId(), state.provider, host?.connectionId ?? '', scopeId)
          ).map((option) => ({
            repositoryId: option.repositoryId ?? '',
            displayName: option.displayName ?? '',
            canonicalSourceRef: option.repositoryId ?? undefined,
            sourceProvider: state.provider,
          }))

      if (requestId !== repositoriesRequestId || state.hostId !== hostId || state.scopeId !== scopeId) {
        return
      }

      state.repositories = repositories
        .filter((repository) => repository.repositoryId.length > 0)
        .sort((left, right) =>
          (left.displayName || left.repositoryId).localeCompare(right.displayName || right.repositoryId),
        )
    } catch (cause) {
      if (requestId !== repositoriesRequestId || state.hostId !== hostId || state.scopeId !== scopeId) {
        return
      }

      state.repositories = []
      state.repositoryError = toMessage(cause, 'Failed to load repositories.')
    } finally {
      if (
        requestId === repositoriesRequestId &&
        state.hostId === hostId &&
        state.scopeId === scopeId
      ) {
        state.loadingRepositories = false
      }
    }
  }

  async function selectHost(hostId: string) {
    state.hostId = hostId
    clearScopes()

    if (hostId) {
      await loadScopes(hostId)
    }
  }

  async function selectScope(scopeId: string) {
    state.scopeId = scopeId
    clearRepositories()

    if (state.hostId && scopeId) {
      await loadRepositories(state.hostId, scopeId)
    }
  }

  /**
   * Points the pickers at what a saved configuration already targets, so editing offers repository names
   * rather than bare ids.
   */
  async function resolveForEdit(
    provider: ScmProviderFamily,
    savedScopePath: string,
    savedProjectKey: string,
  ) {
    reset()
    state.provider = provider
    const formRequest = formRequestId

    await loadHosts()
    if (formRequest !== formRequestId) {
      return
    }

    const wanted = savedScopePath.trim().replace(/\/+$/, '').toLowerCase()
    const match = state.hosts.find(
      (host) => host.scopePath.trim().replace(/\/+$/, '').toLowerCase() === wanted,
    )

    if (!match?.id) {
      state.unresolvedScope = true
      return
    }

    state.hostId = match.id
    await loadScopes(match.id)
    if (formRequest !== formRequestId) {
      return
    }

    state.scopeId = savedProjectKey
    if (savedProjectKey) {
      await loadRepositories(match.id, savedProjectKey)
    }
  }

  return {
    state,
    scopePath,
    isAzureDevOps,
    loadHosts,
    selectProvider,
    selectHost,
    selectScope,
    resolveForEdit,
    reset,
  }
}
