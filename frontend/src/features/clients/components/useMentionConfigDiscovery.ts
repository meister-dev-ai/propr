import { computed, reactive } from 'vue'
import {
  listAdoCrawlFilters,
  listAdoOrganizationScopes,
  listAdoProjects,
  type AdoCrawlFilterOptionDto,
  type AdoProjectOptionDto,
  type ClientAdoOrganizationScopeDto,
} from '@/services/adoDiscoveryService'

/**
 * Drives the organization, project and repository pickers on the mention configuration form.
 *
 * Discovery hands back the provider's own repository id as the canonical source reference, which is the
 * same key mention scanning matches on. Picking a repository therefore fills the stored filter directly,
 * with no name-to-id lookup in between.
 */
export function useMentionConfigDiscovery(clientId: () => string) {
  const state = reactive({
    organizationScopeId: '',
    organizationScopes: [] as ClientAdoOrganizationScopeDto[],
    loadingScopes: false,
    scopeError: '',

    projectId: '',
    projects: [] as AdoProjectOptionDto[],
    loadingProjects: false,
    projectError: '',

    repositories: [] as AdoCrawlFilterOptionDto[],
    loadingRepositories: false,
    repositoryError: '',

    // Set when an existing configuration points at an organization this client can no longer reach, so the
    // form can fall back to showing the stored repositories instead of an empty picker.
    unresolvedScope: false,
  })

  // Each loader carries a ticket. A slower earlier response must not overwrite the answer to a later
  // selection, so a stale ticket discards its own result.
  let scopesTicket = 0
  let projectsTicket = 0
  let repositoriesTicket = 0

  // Guarding each loader is not enough on its own. Resolving a saved configuration spans several awaits and
  // decides what to select between them, so closing the form part way through has to invalidate the whole
  // sequence: otherwise its continuation matches the configuration it was opened for against whatever list
  // is loaded by then, and selects that project in a form the operator opened for something else.
  let sessionTicket = 0

  const selectedScope = computed(() =>
    state.organizationScopes.find((scope) => scope.id === state.organizationScopeId),
  )

  /** The organization url of the current selection, which is what a configuration stores as its scope path. */
  const scopePath = computed(() => selectedScope.value?.organizationUrl ?? '')

  function repositoryIdOf(option: AdoCrawlFilterOptionDto) {
    return option.canonicalSourceRef?.value ?? ''
  }

  function providerOf(option: AdoCrawlFilterOptionDto) {
    return option.canonicalSourceRef?.provider ?? undefined
  }

  function toMessage(cause: unknown, fallback: string) {
    return cause instanceof Error && cause.message ? cause.message : fallback
  }

  function clearRepositories() {
    repositoriesTicket += 1
    state.repositories = []
    state.loadingRepositories = false
    state.repositoryError = ''
  }

  function clearProjects() {
    projectsTicket += 1
    state.projectId = ''
    state.projects = []
    state.loadingProjects = false
    state.projectError = ''
    clearRepositories()
  }

  function reset() {
    sessionTicket += 1
    scopesTicket += 1
    state.organizationScopeId = ''
    state.organizationScopes = []
    state.loadingScopes = false
    state.scopeError = ''
    state.unresolvedScope = false
    clearProjects()
  }

  async function loadOrganizationScopes() {
    const ticket = ++scopesTicket
    state.loadingScopes = true
    state.scopeError = ''

    try {
      const scopes = (await listAdoOrganizationScopes(clientId()))
        .filter((scope) => Boolean(scope.isEnabled))
        .sort((left, right) => (left.displayName ?? '').localeCompare(right.displayName ?? ''))

      if (ticket !== scopesTicket) {
        return
      }

      state.organizationScopes = scopes
    } catch (cause) {
      if (ticket !== scopesTicket) {
        return
      }

      state.organizationScopes = []
      state.scopeError = toMessage(cause, 'Failed to load organizations.')
    } finally {
      if (ticket === scopesTicket) {
        state.loadingScopes = false
      }
    }
  }

  async function loadProjects(scopeId: string) {
    const ticket = ++projectsTicket
    state.loadingProjects = true
    state.projectError = ''

    try {
      const projects = (await listAdoProjects(clientId(), scopeId)).sort((left, right) =>
        (left.projectName ?? '').localeCompare(right.projectName ?? ''),
      )

      if (ticket !== projectsTicket || state.organizationScopeId !== scopeId) {
        return
      }

      state.projects = projects
    } catch (cause) {
      if (ticket !== projectsTicket || state.organizationScopeId !== scopeId) {
        return
      }

      state.projects = []
      state.projectError = toMessage(cause, 'Failed to load Azure DevOps projects.')
    } finally {
      if (ticket === projectsTicket && state.organizationScopeId === scopeId) {
        state.loadingProjects = false
      }
    }
  }

  async function loadRepositories(scopeId: string, projectId: string) {
    const ticket = ++repositoriesTicket
    state.loadingRepositories = true
    state.repositoryError = ''

    try {
      const repositories = (await listAdoCrawlFilters(clientId(), scopeId, projectId))
        .filter((option) => repositoryIdOf(option).length > 0)
        .sort((left, right) => (left.displayName ?? '').localeCompare(right.displayName ?? ''))

      if (ticket !== repositoriesTicket || state.organizationScopeId !== scopeId || state.projectId !== projectId) {
        return
      }

      state.repositories = repositories
    } catch (cause) {
      if (ticket !== repositoriesTicket || state.organizationScopeId !== scopeId || state.projectId !== projectId) {
        return
      }

      state.repositories = []
      state.repositoryError = toMessage(cause, 'Failed to load repositories.')
    } finally {
      if (
        ticket === repositoriesTicket &&
        state.organizationScopeId === scopeId &&
        state.projectId === projectId
      ) {
        state.loadingRepositories = false
      }
    }
  }

  async function selectOrganizationScope(scopeId: string) {
    state.organizationScopeId = scopeId
    clearProjects()

    if (scopeId) {
      await loadProjects(scopeId)
    }
  }

  async function selectProject(projectId: string) {
    state.projectId = projectId
    clearRepositories()

    if (state.organizationScopeId && projectId) {
      await loadRepositories(state.organizationScopeId, projectId)
    }
  }

  /**
   * Points the pickers at what a saved configuration already targets, so editing offers repository names
   * rather than bare ids.
   */
  async function resolveForEdit(savedScopePath: string, savedProjectKey: string) {
    reset()
    const session = sessionTicket

    await loadOrganizationScopes()
    if (session !== sessionTicket) {
      return
    }

    const wanted = savedScopePath.trim().replace(/\/+$/, '').toLowerCase()
    const match = state.organizationScopes.find(
      (scope) => (scope.organizationUrl ?? '').trim().replace(/\/+$/, '').toLowerCase() === wanted,
    )

    if (!match?.id) {
      state.unresolvedScope = true
      return
    }

    state.organizationScopeId = match.id
    await loadProjects(match.id)
    if (session !== sessionTicket) {
      return
    }

    state.projectId = savedProjectKey
    if (savedProjectKey) {
      await loadRepositories(match.id, savedProjectKey)
    }
  }

  return {
    state,
    scopePath,
    repositoryIdOf,
    providerOf,
    loadOrganizationScopes,
    selectOrganizationScope,
    selectProject,
    resolveForEdit,
    reset,
  }
}
