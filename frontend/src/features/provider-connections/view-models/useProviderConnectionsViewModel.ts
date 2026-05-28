// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { computed, onMounted, reactive, ref, watch, type ComputedRef, type Ref } from 'vue'
import { useNotification } from '@/composables/useNotification'
import { useSession } from '@/composables/useSession'
import {
  getEnabledProviderOptions,
  getProviderDefaultHostBaseUrl,
  getSupportedAuthenticationKinds,
  requiresUserName,
  listProviderActivationStatuses,
  type ProviderActivationStatusDto,
} from '@/services/providerActivationService'
import {
  createProviderConnection,
  createProviderScope,
  deleteProviderConnection,
  deleteProviderScope,
  deleteReviewerIdentity,
  getReviewerIdentity,
  listProviderConnections,
  listProviderScopes,
  resolveReviewerIdentityCandidates,
  setReviewerIdentity,
  updateProviderConnection,
  updateProviderScope,
  verifyProviderConnection,
  type ClientReviewerIdentityDto,
  type ClientScmConnectionDto,
  type ClientScmScopeDto,
  type CreateClientProviderConnectionRequest,
  type CreateClientProviderScopeRequest,
  type PatchClientProviderConnectionRequest,
  type PatchClientProviderScopeRequest,
  type ProviderConnectionReadinessLevel,
  type ResolvedReviewerIdentityResponse,
  type ScmAuthenticationKind,
  type ScmProviderFamily,
  type SetClientReviewerIdentityRequest,
} from '@/services/providerConnectionsService'

export interface ProviderConnectionsService {
  listProviderActivationStatuses: () => Promise<ProviderActivationStatusDto[]>
  listProviderConnections: (clientId: string) => Promise<ClientScmConnectionDto[]>
  createProviderConnection: (clientId: string, request: CreateClientProviderConnectionRequest) => Promise<ClientScmConnectionDto>
  updateProviderConnection: (
    clientId: string,
    connectionId: string,
    request: PatchClientProviderConnectionRequest,
  ) => Promise<ClientScmConnectionDto>
  verifyProviderConnection: (clientId: string, connectionId: string) => Promise<ClientScmConnectionDto>
  deleteProviderConnection: (clientId: string, connectionId: string) => Promise<void>
  listProviderScopes: (clientId: string, connectionId: string) => Promise<ClientScmScopeDto[]>
  createProviderScope: (
    clientId: string,
    connectionId: string,
    request: CreateClientProviderScopeRequest,
  ) => Promise<ClientScmScopeDto>
  updateProviderScope: (
    clientId: string,
    connectionId: string,
    scopeId: string,
    request: PatchClientProviderScopeRequest,
  ) => Promise<ClientScmScopeDto>
  deleteProviderScope: (clientId: string, connectionId: string, scopeId: string) => Promise<void>
  resolveReviewerIdentityCandidates: (
    clientId: string,
    connectionId: string,
    search: string,
  ) => Promise<ResolvedReviewerIdentityResponse[]>
  getReviewerIdentity: (clientId: string, connectionId: string) => Promise<ClientReviewerIdentityDto | null>
  setReviewerIdentity: (
    clientId: string,
    connectionId: string,
    request: SetClientReviewerIdentityRequest,
  ) => Promise<ClientReviewerIdentityDto>
  deleteReviewerIdentity: (clientId: string, connectionId: string) => Promise<void>
}

export interface ProviderConnectionsViewModel {
  readonly name: 'useProviderConnectionsViewModel'
  clientId: string
  connections: Ref<ClientScmConnectionDto[]>
  scopes: Ref<ClientScmScopeDto[]>
  reviewerIdentity: Ref<ClientReviewerIdentityDto | null>
  reviewerCandidates: Ref<ResolvedReviewerIdentityResponse[]>
  providerStatuses: Ref<ProviderActivationStatusDto[]>
  loading: Ref<boolean>
  scopesLoading: Ref<boolean>
  reviewerLoading: Ref<boolean>
  saving: Ref<boolean>
  scopeSaving: Ref<boolean>
  showCreateForm: Ref<boolean>
  error: Ref<string>
  createError: Ref<string>
  editError: Ref<string>
  scopeError: Ref<string>
  reviewerError: Ref<string>
  providerOptionsError: Ref<string>
  busyConnectionId: Ref<string | null>
  selectedConnectionId: Ref<string | null>
  editingConnectionId: Ref<string | null>
  reviewerSearch: Ref<string>
  selectedReviewerExternalUserId: Ref<string | null>
  detailActiveTab: Ref<string>
  createForm: {
    providerFamily: ScmProviderFamily
    hostBaseUrl: string
    authenticationKind: ScmAuthenticationKind
    userName: string
    oAuthTenantId: string
    oAuthClientId: string
    gitHubAppId: string
    gitHubAppInstallationId: string
    displayName: string
    secret: string
    isActive: boolean
  }
  editForm: {
    providerFamily: ScmProviderFamily
    displayName: string
    hostBaseUrl: string
    authenticationKind: ScmAuthenticationKind
    userName: string
    oAuthTenantId: string
    oAuthClientId: string
    gitHubAppId: string
    gitHubAppInstallationId: string
    secret: string
    isActive: boolean
  }
  scopeForm: {
    scopeType: string
    externalScopeId: string
    scopePath: string
    displayName: string
    isEnabled: boolean
  }
  selectedConnection: ComputedRef<ClientScmConnectionDto | null>
  editSecretRequired: ComputedRef<boolean>
  providerOptions: ComputedRef<Array<{ value: ScmProviderFamily; label: string }>>
  hasEnabledProviderOptions: ComputedRef<boolean>
  providerLimitReached: ComputedRef<boolean>
  multipleProviderUpgradeMessage: ComputedRef<string>
  canCreateConnection: ComputedRef<boolean>
  openConnectionDetail: (connectionId: string) => void
  closeConnectionDetail: () => void
  loadConnections: () => Promise<void>
  handleCreateConnection: () => Promise<void>
  handleSaveConnectionEdit: (connectionId: string) => Promise<void>
  handleVerifyConnection: (connectionId: string) => Promise<void>
  handleDeleteConnection: (connectionId: string) => Promise<void>
  handleCreateScope: () => Promise<void>
  toggleScope: (scope: ClientScmScopeDto) => Promise<void>
  handleDeleteScope: (scopeId: string) => Promise<void>
  resolveReviewerCandidatesForSelectedConnection: () => Promise<void>
  handleSaveReviewerIdentity: () => Promise<void>
  handleClearReviewerIdentity: () => Promise<void>
  formatProvider: (providerFamily: ScmProviderFamily) => string
  formatVerification: (verificationStatus: string) => string
  formatReadiness: (readinessLevel: ProviderConnectionReadinessLevel) => string
  verificationChipClass: (verificationStatus: string) => string
  readinessChipClass: (readinessLevel: ProviderConnectionReadinessLevel) => string
}

export interface UseProviderConnectionsViewModelOptions {
  clientId: string
  onDetailOpenChange?: (value: boolean) => void
  providerConnectionsService?: Partial<ProviderConnectionsService>
  autoLoad?: boolean
}

export function useProviderConnectionsViewModel(
  options: UseProviderConnectionsViewModelOptions,
): ProviderConnectionsViewModel {
  const { notify } = useNotification()
  const { capabilities } = useSession()
  const clientId = options.clientId
  const providerConnectionsService = options.providerConnectionsService
  const autoLoad = options.autoLoad ?? true

  const listProviderActivationStatusesFn =
    providerConnectionsService?.listProviderActivationStatuses ?? listProviderActivationStatuses
  const listProviderConnectionsFn = providerConnectionsService?.listProviderConnections ?? listProviderConnections
  const createProviderConnectionFn = providerConnectionsService?.createProviderConnection ?? createProviderConnection
  const updateProviderConnectionFn = providerConnectionsService?.updateProviderConnection ?? updateProviderConnection
  const verifyProviderConnectionFn = providerConnectionsService?.verifyProviderConnection ?? verifyProviderConnection
  const deleteProviderConnectionFn = providerConnectionsService?.deleteProviderConnection ?? deleteProviderConnection
  const listProviderScopesFn = providerConnectionsService?.listProviderScopes ?? listProviderScopes
  const createProviderScopeFn = providerConnectionsService?.createProviderScope ?? createProviderScope
  const updateProviderScopeFn = providerConnectionsService?.updateProviderScope ?? updateProviderScope
  const deleteProviderScopeFn = providerConnectionsService?.deleteProviderScope ?? deleteProviderScope
  const resolveReviewerIdentityCandidatesFn =
    providerConnectionsService?.resolveReviewerIdentityCandidates ?? resolveReviewerIdentityCandidates
  const getReviewerIdentityFn = providerConnectionsService?.getReviewerIdentity ?? getReviewerIdentity
  const setReviewerIdentityFn = providerConnectionsService?.setReviewerIdentity ?? setReviewerIdentity
  const deleteReviewerIdentityFn = providerConnectionsService?.deleteReviewerIdentity ?? deleteReviewerIdentity

  const connections = ref<ClientScmConnectionDto[]>([])
  const scopes = ref<ClientScmScopeDto[]>([])
  const reviewerIdentity = ref<ClientReviewerIdentityDto | null>(null)
  const reviewerCandidates = ref<ResolvedReviewerIdentityResponse[]>([])
  const providerStatuses = ref<ProviderActivationStatusDto[]>([])

  const loading = ref(false)
  const scopesLoading = ref(false)
  const reviewerLoading = ref(false)
  const saving = ref(false)
  const scopeSaving = ref(false)
  const showCreateForm = ref(false)
  const error = ref('')
  const createError = ref('')
  const editError = ref('')
  const scopeError = ref('')
  const reviewerError = ref('')
  const providerOptionsError = ref('')
  const busyConnectionId = ref<string | null>(null)
  const selectedConnectionId = ref<string | null>(null)
  const editingConnectionId = ref<string | null>(null)
  const reviewerSearch = ref('')
  const selectedReviewerExternalUserId = ref<string | null>(null)
  const detailActiveTab = ref('settings')

  let scopesLoadRequestVersion = 0
  let reviewerIdentityLoadRequestVersion = 0

  const createForm = reactive({
    providerFamily: 'github' as ScmProviderFamily,
    hostBaseUrl: 'https://github.com',
    authenticationKind: 'personalAccessToken' as ScmAuthenticationKind,
    userName: '',
    oAuthTenantId: '',
    oAuthClientId: '',
    gitHubAppId: '',
    gitHubAppInstallationId: '',
    displayName: '',
    secret: '',
    isActive: true,
  })

  const editForm = reactive({
    providerFamily: 'github' as ScmProviderFamily,
    displayName: '',
    hostBaseUrl: '',
    authenticationKind: 'personalAccessToken' as ScmAuthenticationKind,
    userName: '',
    oAuthTenantId: '',
    oAuthClientId: '',
    gitHubAppId: '',
    gitHubAppInstallationId: '',
    secret: '',
    isActive: true,
  })

  const scopeForm = reactive({
    scopeType: 'organization',
    externalScopeId: '',
    scopePath: '',
    displayName: '',
    isEnabled: true,
  })

  const selectedConnection = computed(
    () => connections.value.find((connection) => connection.id === selectedConnectionId.value) ?? null,
  )
  const editSecretRequired = computed(() => {
    if (!selectedConnection.value) {
      return false
    }

    return selectedConnection.value.authenticationKind !== editForm.authenticationKind
  })
  const providerOptions = computed(() => getEnabledProviderOptions(providerStatuses.value))
  const hasEnabledProviderOptions = computed(() => providerOptions.value.length > 0)
  const multipleProvidersCapability = computed(
    () => capabilities.value.find((capability) => capability.key === 'multiple-scm-providers') ?? null,
  )
  const providerLimitReached = computed(
    () => multipleProvidersCapability.value?.isAvailable === false && connections.value.length > 0,
  )
  const multipleProviderUpgradeMessage = computed(() =>
    providerLimitReached.value
      ? multipleProvidersCapability.value?.message
        ?? 'A commercial license is required to configure more than one SCM provider connection, including in self-hosted deployments.'
      : '',
  )
  const canCreateConnection = computed(() => hasEnabledProviderOptions.value && !providerLimitReached.value)
  const selectedReviewerCandidate = computed(
    () => reviewerCandidates.value.find((candidate) => candidate.externalUserId === selectedReviewerExternalUserId.value) ?? null,
  )

  function openConnectionDetail(connectionId: string) {
    selectedConnectionId.value = connectionId
    detailActiveTab.value = 'settings'
    options.onDetailOpenChange?.(true)
    const connection = connections.value.find((candidate) => candidate.id === connectionId)
    if (connection) {
      startEdit(connection)
    }
  }

  function closeConnectionDetail() {
    selectedConnectionId.value = null
    options.onDetailOpenChange?.(false)
    cancelEdit()
  }

  function applyCreateProviderDefaults(providerFamily: ScmProviderFamily) {
    createForm.providerFamily = providerFamily
    createForm.hostBaseUrl = getProviderDefaultHostBaseUrl(providerFamily)
    createForm.authenticationKind = getPreferredAuthenticationKind(providerFamily, createForm.hostBaseUrl)
    createForm.userName = ''

    if (providerFamily !== 'azureDevOps') {
      createForm.oAuthTenantId = ''
      createForm.oAuthClientId = ''
    }

    if (providerFamily !== 'github') {
      createForm.gitHubAppId = ''
      createForm.gitHubAppInstallationId = ''
    }
  }

  function syncCreateProviderSelection() {
    if (providerOptions.value.some((option) => option.value === createForm.providerFamily)) {
      return
    }

    const fallbackProvider = providerOptions.value[0]?.value
    if (fallbackProvider) {
      applyCreateProviderDefaults(fallbackProvider)
    }
  }

  function normalizeAuthenticationKind(
    providerFamily: ScmProviderFamily,
    hostBaseUrl: string,
    authenticationKind: ScmAuthenticationKind,
  ): ScmAuthenticationKind {
    const supportedKinds = getSupportedAuthenticationKinds(providerFamily, hostBaseUrl)
    return supportedKinds.includes(authenticationKind) ? authenticationKind : supportedKinds[0]
  }

  function getPreferredAuthenticationKind(providerFamily: ScmProviderFamily, hostBaseUrl: string): ScmAuthenticationKind {
    return getSupportedAuthenticationKinds(providerFamily, hostBaseUrl)[0]
  }

  function clearOAuthFields(form: typeof createForm | typeof editForm) {
    form.oAuthTenantId = ''
    form.oAuthClientId = ''
  }

  function clearGitHubAppFields(form: typeof createForm | typeof editForm) {
    form.gitHubAppId = ''
    form.gitHubAppInstallationId = ''
  }

  function clearUserNameField(form: typeof createForm | typeof editForm) {
    form.userName = ''
  }

  function parseOptionalPositiveNumber(value: string | number): number | null {
    const trimmed = String(value).trim()
    if (!trimmed) {
      return null
    }

    const parsed = Number(trimmed)
    return Number.isInteger(parsed) && parsed > 0 ? parsed : null
  }

  watch(() => createForm.providerFamily, (providerFamily) => {
    if (!providerOptions.value.some((option) => option.value === providerFamily) && providerOptions.value.length > 0) {
      applyCreateProviderDefaults(providerOptions.value[0].value)
      return
    }

    if (providerFamily === 'azureDevOps') {
      createForm.hostBaseUrl = getProviderDefaultHostBaseUrl(providerFamily)
      createForm.authenticationKind = getPreferredAuthenticationKind(providerFamily, createForm.hostBaseUrl)
      scopeForm.scopeType = 'organization'
      return
    }

    createForm.authenticationKind = getPreferredAuthenticationKind(providerFamily, createForm.hostBaseUrl)
    clearUserNameField(createForm)
    clearOAuthFields(createForm)
    createForm.hostBaseUrl = getProviderDefaultHostBaseUrl(providerFamily)

    if (providerFamily !== 'github') {
      clearGitHubAppFields(createForm)
    }
  })

  watch(() => createForm.authenticationKind, (authenticationKind) => {
    if (authenticationKind !== 'oauthClientCredentials') {
      clearOAuthFields(createForm)
    }

    if (!requiresUserName(createForm.providerFamily, createForm.hostBaseUrl, authenticationKind)) {
      clearUserNameField(createForm)
    }

    if (authenticationKind !== 'appInstallation') {
      clearGitHubAppFields(createForm)
    }
  })

  watch(() => createForm.hostBaseUrl, (hostBaseUrl) => {
    createForm.authenticationKind = normalizeAuthenticationKind(createForm.providerFamily, hostBaseUrl, createForm.authenticationKind)
    if (!requiresUserName(createForm.providerFamily, hostBaseUrl, createForm.authenticationKind)) {
      clearUserNameField(createForm)
    }
  })

  watch(() => editForm.authenticationKind, (authenticationKind) => {
    if (authenticationKind !== 'oauthClientCredentials') {
      clearOAuthFields(editForm)
    }

    if (!requiresUserName(editForm.providerFamily, editForm.hostBaseUrl, authenticationKind)) {
      clearUserNameField(editForm)
    }

    if (authenticationKind !== 'appInstallation') {
      clearGitHubAppFields(editForm)
    }
  })

  watch(() => editForm.hostBaseUrl, (hostBaseUrl) => {
    if (!hostBaseUrl) {
      return
    }

    editForm.authenticationKind = normalizeAuthenticationKind(editForm.providerFamily, hostBaseUrl, editForm.authenticationKind)
    if (!requiresUserName(editForm.providerFamily, hostBaseUrl, editForm.authenticationKind)) {
      clearUserNameField(editForm)
    }
  })

  watch(selectedConnectionId, async (connectionId) => {
    reviewerCandidates.value = []
    selectedReviewerExternalUserId.value = null
    reviewerError.value = ''

    scopesLoadRequestVersion += 1
    reviewerIdentityLoadRequestVersion += 1
    const scopeRequestVersion = scopesLoadRequestVersion
    const reviewerRequestVersion = reviewerIdentityLoadRequestVersion

    if (!connectionId) {
      scopes.value = []
      reviewerIdentity.value = null
      scopesLoading.value = false
      reviewerLoading.value = false
      return
    }

    scopes.value = []
    reviewerIdentity.value = null

    await Promise.all([
      loadScopes(connectionId, scopeRequestVersion),
      loadReviewerIdentity(connectionId, reviewerRequestVersion),
    ])
  })

  if (autoLoad) {
    onMounted(() => {
      void Promise.all([loadConnections(), loadProviderOptions()])
    })
  }

  async function loadProviderOptions() {
    providerOptionsError.value = ''

    try {
      providerStatuses.value = await listProviderActivationStatusesFn()
      syncCreateProviderSelection()
    } catch (loadError) {
      providerOptionsError.value = loadError instanceof Error ? loadError.message : 'Failed to load enabled provider families.'
      providerStatuses.value = []
    }
  }

  async function loadConnections() {
    loading.value = true
    error.value = ''

    try {
      connections.value = await listProviderConnectionsFn(clientId)
    } catch (loadError) {
      error.value = loadError instanceof Error ? loadError.message : 'Failed to load provider connections.'
    } finally {
      loading.value = false
    }
  }

  async function loadScopes(connectionId: string, requestVersion = ++scopesLoadRequestVersion) {
    scopesLoading.value = true
    scopeError.value = ''
    try {
      const nextScopes = await listProviderScopesFn(clientId, connectionId)
      if (requestVersion !== scopesLoadRequestVersion || connectionId !== selectedConnectionId.value) {
        return
      }

      scopes.value = nextScopes
    } catch (loadError) {
      if (requestVersion !== scopesLoadRequestVersion || connectionId !== selectedConnectionId.value) {
        return
      }

      scopeError.value = loadError instanceof Error ? loadError.message : 'Failed to load provider scopes.'
    } finally {
      if (requestVersion === scopesLoadRequestVersion && connectionId === selectedConnectionId.value) {
        scopesLoading.value = false
      }
    }
  }

  async function loadReviewerIdentity(connectionId: string, requestVersion = ++reviewerIdentityLoadRequestVersion) {
    reviewerLoading.value = true
    reviewerError.value = ''
    try {
      const nextReviewerIdentity = await getReviewerIdentityFn(clientId, connectionId)
      if (requestVersion !== reviewerIdentityLoadRequestVersion || connectionId !== selectedConnectionId.value) {
        return
      }

      reviewerIdentity.value = nextReviewerIdentity
    } catch (loadError) {
      if (requestVersion !== reviewerIdentityLoadRequestVersion || connectionId !== selectedConnectionId.value) {
        return
      }

      reviewerError.value = loadError instanceof Error ? loadError.message : 'Failed to load reviewer identity.'
    } finally {
      if (requestVersion === reviewerIdentityLoadRequestVersion && connectionId === selectedConnectionId.value) {
        reviewerLoading.value = false
      }
    }
  }

  async function handleCreateConnection() {
    if (!createForm.displayName.trim() || !createForm.hostBaseUrl.trim() || !createForm.secret.trim()) {
      createError.value = 'Display name, host base URL, and secret are required.'
      return
    }

    if (requiresUserName(createForm.providerFamily, createForm.hostBaseUrl, createForm.authenticationKind) && !createForm.userName.trim()) {
      createError.value = 'User name is required for Azure DevOps Server Windows user-account connections.'
      return
    }

    if (createForm.authenticationKind === 'appInstallation') {
      if (!parseOptionalPositiveNumber(createForm.gitHubAppId) || !parseOptionalPositiveNumber(createForm.gitHubAppInstallationId)) {
        createError.value = 'GitHub App ID and Installation ID are required for GitHub App connections.'
        return
      }
    }

    createError.value = ''
    saving.value = true
    try {
      const created = await createProviderConnectionFn(clientId, {
        providerFamily: createForm.providerFamily,
        hostBaseUrl: createForm.hostBaseUrl.trim(),
        authenticationKind: createForm.authenticationKind,
        userName: createForm.userName.trim() || null,
        oAuthTenantId: createForm.oAuthTenantId.trim() || null,
        oAuthClientId: createForm.oAuthClientId.trim() || null,
        gitHubAppId: createForm.authenticationKind === 'appInstallation'
          ? parseOptionalPositiveNumber(createForm.gitHubAppId)
          : null,
        gitHubAppInstallationId: createForm.authenticationKind === 'appInstallation'
          ? parseOptionalPositiveNumber(createForm.gitHubAppInstallationId)
          : null,
        displayName: createForm.displayName.trim(),
        secret: createForm.secret,
        isActive: createForm.isActive,
      })

      connections.value.unshift(created)
      selectedConnectionId.value = created.id
      showCreateForm.value = false
      resetCreateForm()
      notify('Provider connection created.')
      options.onDetailOpenChange?.(true)
    } catch (saveError) {
      createError.value = saveError instanceof Error ? saveError.message : 'Failed to create provider connection.'
    } finally {
      saving.value = false
    }
  }

  function resetCreateForm() {
    applyCreateProviderDefaults(
      providerOptions.value.some((option) => option.value === 'github')
        ? 'github'
        : providerOptions.value[0]?.value ?? 'github',
    )
    clearOAuthFields(createForm)
    clearUserNameField(createForm)
    clearGitHubAppFields(createForm)
    createForm.displayName = ''
    createForm.secret = ''
    createForm.isActive = true
    createError.value = ''
  }

  function startEdit(connection: ClientScmConnectionDto) {
    editingConnectionId.value = connection.id
    editForm.providerFamily = connection.providerFamily
    editForm.displayName = connection.displayName
    editForm.hostBaseUrl = connection.hostBaseUrl
    editForm.authenticationKind = normalizeAuthenticationKind(connection.providerFamily, connection.hostBaseUrl, connection.authenticationKind)
    editForm.userName = connection.userName ?? ''
    editForm.oAuthTenantId = connection.oAuthTenantId ?? ''
    editForm.oAuthClientId = connection.oAuthClientId ?? ''
    editForm.gitHubAppId = connection.gitHubAppId?.toString() ?? ''
    editForm.gitHubAppInstallationId = connection.gitHubAppInstallationId?.toString() ?? ''
    editForm.secret = ''
    editForm.isActive = connection.isActive
    editError.value = ''
  }

  function cancelEdit() {
    editingConnectionId.value = null
    editForm.providerFamily = 'github'
    editForm.displayName = ''
    editForm.hostBaseUrl = ''
    editForm.authenticationKind = getPreferredAuthenticationKind('github', 'https://github.com')
    clearUserNameField(editForm)
    clearOAuthFields(editForm)
    clearGitHubAppFields(editForm)
    editForm.secret = ''
    editForm.isActive = true
    editError.value = ''
  }

  async function handleSaveConnectionEdit(connectionId: string) {
    if (requiresUserName(editForm.providerFamily, editForm.hostBaseUrl, editForm.authenticationKind) && !editForm.userName.trim()) {
      editError.value = 'User name is required for Azure DevOps Server Windows user-account connections.'
      return
    }

    if (editSecretRequired.value && !editForm.secret.trim()) {
      editError.value = editForm.providerFamily === 'github'
        ? editForm.authenticationKind === 'appInstallation'
          ? 'A GitHub App private key is required when switching to GitHub App authentication.'
          : 'A personal access token is required when switching away from GitHub App authentication.'
        : 'A replacement secret is required when switching Azure DevOps authentication modes.'
      return
    }

    if (editForm.authenticationKind === 'appInstallation') {
      if (!parseOptionalPositiveNumber(editForm.gitHubAppId) || !parseOptionalPositiveNumber(editForm.gitHubAppInstallationId)) {
        editError.value = 'GitHub App ID and Installation ID are required for GitHub App connections.'
        return
      }
    }

    busyConnectionId.value = connectionId
    editError.value = ''
    try {
      const updated = await updateProviderConnectionFn(clientId, connectionId, {
        displayName: editForm.displayName.trim(),
        hostBaseUrl: editForm.hostBaseUrl.trim(),
        authenticationKind: editForm.authenticationKind,
        userName: editForm.userName.trim() || null,
        oAuthTenantId: editForm.oAuthTenantId.trim() || null,
        oAuthClientId: editForm.oAuthClientId.trim() || null,
        gitHubAppId: editForm.authenticationKind === 'appInstallation'
          ? parseOptionalPositiveNumber(editForm.gitHubAppId)
          : null,
        gitHubAppInstallationId: editForm.authenticationKind === 'appInstallation'
          ? parseOptionalPositiveNumber(editForm.gitHubAppInstallationId)
          : null,
        secret: editForm.secret.trim() || undefined,
        isActive: editForm.isActive,
      })

      replaceConnection(updated)
      cancelEdit()
      notify('Provider connection updated.')
    } catch (saveError) {
      editError.value = saveError instanceof Error ? saveError.message : 'Failed to update provider connection.'
    } finally {
      busyConnectionId.value = null
    }
  }

  async function handleVerifyConnection(connectionId: string) {
    busyConnectionId.value = connectionId
    try {
      const verified = await verifyProviderConnectionFn(clientId, connectionId)
      replaceConnection(verified)
      notify('Provider connection verification updated.')
    } catch (verifyError) {
      notify(verifyError instanceof Error ? verifyError.message : 'Failed to verify provider connection.', 'error')
    } finally {
      busyConnectionId.value = null
    }
  }

  async function handleDeleteConnection(connectionId: string) {
    busyConnectionId.value = connectionId
    try {
      await deleteProviderConnectionFn(clientId, connectionId)
      connections.value = connections.value.filter((connection) => connection.id !== connectionId)
      if (selectedConnectionId.value === connectionId) {
        selectedConnectionId.value = connections.value[0]?.id ?? null
        options.onDetailOpenChange?.(selectedConnectionId.value !== null)
      }
      notify('Provider connection deleted.')
    } catch (deleteError) {
      notify(deleteError instanceof Error ? deleteError.message : 'Failed to delete provider connection.', 'error')
    } finally {
      busyConnectionId.value = null
    }
  }

  async function handleCreateScope() {
    if (!selectedConnection.value) {
      return
    }

    if (!scopeForm.scopeType.trim() || !scopeForm.externalScopeId.trim() || !scopeForm.scopePath.trim() || !scopeForm.displayName.trim()) {
      scopeError.value = 'Scope type, external scope ID, scope path, and display name are required.'
      return
    }

    scopeSaving.value = true
    scopeError.value = ''
    try {
      const created = await createProviderScopeFn(clientId, selectedConnection.value.id, {
        scopeType: scopeForm.scopeType.trim(),
        externalScopeId: scopeForm.externalScopeId.trim(),
        scopePath: scopeForm.scopePath.trim(),
        displayName: scopeForm.displayName.trim(),
        isEnabled: scopeForm.isEnabled,
      })
      scopes.value.unshift(created)
      resetScopeForm()
      notify('Provider scope created.')
    } catch (saveError) {
      scopeError.value = saveError instanceof Error ? saveError.message : 'Failed to create provider scope.'
    } finally {
      scopeSaving.value = false
    }
  }

  function resetScopeForm() {
    scopeForm.scopeType = 'organization'
    scopeForm.externalScopeId = ''
    scopeForm.scopePath = ''
    scopeForm.displayName = ''
    scopeForm.isEnabled = true
    scopeError.value = ''
  }

  async function toggleScope(scope: ClientScmScopeDto) {
    if (!selectedConnection.value) {
      return
    }

    try {
      const updated = await updateProviderScopeFn(clientId, selectedConnection.value.id, scope.id, {
        displayName: scope.displayName,
        isEnabled: !scope.isEnabled,
      })
      scopes.value = scopes.value.map((entry) => entry.id === updated.id ? updated : entry)
    } catch (updateError) {
      notify(updateError instanceof Error ? updateError.message : 'Failed to update provider scope.', 'error')
    }
  }

  async function handleDeleteScope(scopeId: string) {
    if (!selectedConnection.value) {
      return
    }

    try {
      await deleteProviderScopeFn(clientId, selectedConnection.value.id, scopeId)
      scopes.value = scopes.value.filter((scope) => scope.id !== scopeId)
      notify('Provider scope deleted.')
    } catch (deleteError) {
      notify(deleteError instanceof Error ? deleteError.message : 'Failed to delete provider scope.', 'error')
    }
  }

  async function resolveReviewerCandidatesForSelectedConnection() {
    if (!selectedConnection.value) {
      return
    }

    if (!reviewerSearch.value.trim()) {
      reviewerError.value = 'Search text is required.'
      return
    }

    reviewerLoading.value = true
    reviewerError.value = ''
    try {
      reviewerCandidates.value = await resolveReviewerIdentityCandidatesFn(
        clientId,
        selectedConnection.value.id,
        reviewerSearch.value.trim(),
      )
      selectedReviewerExternalUserId.value = reviewerCandidates.value[0]?.externalUserId ?? null
    } catch (resolveError) {
      reviewerError.value = resolveError instanceof Error ? resolveError.message : 'Failed to resolve reviewer identities.'
    } finally {
      reviewerLoading.value = false
    }
  }

  async function handleSaveReviewerIdentity() {
    if (!selectedConnection.value || !selectedReviewerCandidate.value) {
      return
    }

    reviewerLoading.value = true
    reviewerError.value = ''
    try {
      reviewerIdentity.value = await setReviewerIdentityFn(clientId, selectedConnection.value.id, {
        externalUserId: selectedReviewerCandidate.value.externalUserId,
        login: selectedReviewerCandidate.value.login,
        displayName: selectedReviewerCandidate.value.displayName,
        isBot: selectedReviewerCandidate.value.isBot,
      })
      reviewerCandidates.value = []
      selectedReviewerExternalUserId.value = null
      reviewerSearch.value = ''
      notify('Reviewer trigger saved.')
    } catch (saveError) {
      reviewerError.value = saveError instanceof Error ? saveError.message : 'Failed to save reviewer identity.'
    } finally {
      reviewerLoading.value = false
    }
  }

  async function handleClearReviewerIdentity() {
    if (!selectedConnection.value) {
      return
    }

    reviewerLoading.value = true
    reviewerError.value = ''
    try {
      await deleteReviewerIdentityFn(clientId, selectedConnection.value.id)
      reviewerIdentity.value = null
      notify('Reviewer trigger cleared.')
    } catch (deleteError) {
      reviewerError.value = deleteError instanceof Error ? deleteError.message : 'Failed to clear reviewer identity.'
    } finally {
      reviewerLoading.value = false
    }
  }

  function replaceConnection(updated: ClientScmConnectionDto) {
    connections.value = connections.value.map((connection) => connection.id === updated.id ? updated : connection)
  }

  function formatProvider(providerFamily: ScmProviderFamily): string {
    switch (providerFamily) {
      case 'gitLab':
        return 'GitLab'
      case 'forgejo':
        return 'Forgejo'
      case 'azureDevOps':
        return 'Azure DevOps'
      default:
        return 'GitHub'
    }
  }

  function formatVerification(verificationStatus: string): string {
    if (!verificationStatus) {
      return 'Unknown'
    }

    return verificationStatus.charAt(0).toUpperCase() + verificationStatus.slice(1)
  }

  function formatReadiness(readinessLevel: ProviderConnectionReadinessLevel): string {
    switch (readinessLevel) {
      case 'configured':
        return 'Configured'
      case 'degraded':
        return 'Degraded'
      case 'onboardingReady':
        return 'Onboarding Ready'
      case 'workflowComplete':
        return 'Workflow Complete'
      default:
        return 'Unknown'
    }
  }

  function verificationChipClass(verificationStatus: string): string {
    switch (verificationStatus) {
      case 'verified':
        return 'chip-success'
      case 'failed':
        return 'chip-danger'
      default:
        return 'chip-muted'
    }
  }

  function readinessChipClass(readinessLevel: ProviderConnectionReadinessLevel): string {
    switch (readinessLevel) {
      case 'workflowComplete':
        return 'chip-success'
      case 'degraded':
        return 'chip-danger'
      case 'onboardingReady':
        return 'chip-warning'
      default:
        return 'chip-muted'
    }
  }

  return {
    name: 'useProviderConnectionsViewModel',
    clientId,
    connections,
    scopes,
    reviewerIdentity,
    reviewerCandidates,
    providerStatuses,
    loading,
    scopesLoading,
    reviewerLoading,
    saving,
    scopeSaving,
    showCreateForm,
    error,
    createError,
    editError,
    scopeError,
    reviewerError,
    providerOptionsError,
    busyConnectionId,
    selectedConnectionId,
    editingConnectionId,
    reviewerSearch,
    selectedReviewerExternalUserId,
    detailActiveTab,
    createForm,
    editForm,
    scopeForm,
    selectedConnection,
    editSecretRequired,
    providerOptions,
    hasEnabledProviderOptions,
    providerLimitReached,
    multipleProviderUpgradeMessage,
    canCreateConnection,
    openConnectionDetail,
    closeConnectionDetail,
    loadConnections,
    handleCreateConnection,
    handleSaveConnectionEdit,
    handleVerifyConnection,
    handleDeleteConnection,
    handleCreateScope,
    toggleScope,
    handleDeleteScope,
    resolveReviewerCandidatesForSelectedConnection,
    handleSaveReviewerIdentity,
    handleClearReviewerIdentity,
    formatProvider,
    formatVerification,
    formatReadiness,
    verificationChipClass,
    readinessChipClass,
  }
}
