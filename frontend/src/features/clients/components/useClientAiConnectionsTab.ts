// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { computed, onMounted, reactive, ref } from 'vue'
import {
  activateAiConnection,
  createAiConnection,
  deactivateAiConnection,
  deleteAiConnection,
  discoverAiModels,
  listAiConnections,
  updateAiConnection,
  verifyAiConnection,
} from '@/services/aiConnectionsService'
import type {
  AiAuthMode,
  AiConfiguredModelDto,
  AiConfiguredModelRequest,
  AiConnectionDto,
  AiDiscoveryMode,
  AiModelDiscoveryResultDto,
  AiProviderKind,
  AiPurpose,
  CreateAiConnectionRequest,
  DiscoverModelsRequest,
  UpdateAiConnectionRequest,
} from '@/services/aiConnectionsService'
import type { EditableBinding, EditableModel } from './aiConnectionsForm.types'
import {
  makeBindingDefaults,
  parseMapText,
  serializeMap,
  verificationLabel,
} from './aiConnectionsFormatters'

/**
 * State and orchestration for the AI-connections client tab: profile list,
 * the create/edit editor, model discovery, validation, save, and lifecycle
 * actions. Extracted from ClientAiConnectionsTab.vue; pure option tables and
 * label/parse helpers live in ./aiConnectionsFormatters.
 */
export function useClientAiConnectionsTab(props: { clientId: string }) {
  const profiles = ref<AiConnectionDto[]>([])
  const loading = ref(false)
  const discovering = ref(false)
  const saving = ref(false)
  const loadError = ref('')
  const saveError = ref('')
  const discoveryMessage = ref('')
  const busyConnectionId = ref<string | null>(null)
  const deleteTarget = ref<AiConnectionDto | null>(null)
  const viewMode = ref<'list' | 'detail'>('list')
  const advancedSettingsOpen = ref(false)
  const editingModelId = ref<string | null>(null)

  const editor = reactive({
    mode: 'create' as 'create' | 'edit',
    profileId: '',
    displayName: '',
    providerKind: 'azureOpenAi' as AiProviderKind,
    baseUrl: '',
    authMode: 'apiKey' as AiAuthMode,
    apiKey: '',
    discoveryMode: 'providerCatalog' as AiDiscoveryMode,
    defaultHeadersText: '',
    defaultQueryParamsText: '',
    models: [] as EditableModel[],
    bindings: [] as EditableBinding[],
  })

  const showListView = computed(() => viewMode.value === 'list')
  const selectedProfile = computed(() => profiles.value.find((profile) => profile.id === editor.profileId) ?? null)

  const modelsForPurpose = (purpose: AiPurpose) => {
    return editor.models.filter((model) => (purpose === 'embeddingDefault' ? model.kind === 'embedding' : model.kind === 'chat'))
  }

  const refreshProfiles = async () => {
    loading.value = true
    loadError.value = ''
    try {
      profiles.value = await listAiConnections(props.clientId)

      // Always land on the list (even when empty — it shows an empty state), never jump straight into the
      // create form. Stay in the editor only if the user was mid-edit when the refresh happened.
      if (viewMode.value !== 'detail')
      {
        viewMode.value = 'list'
      }
    } catch (error) {
      loadError.value = error instanceof Error ? error.message : 'Failed to load AI providers.'
    } finally {
      loading.value = false
    }
  }

  const resetEditor = () => {
    editor.mode = 'create'
    editor.profileId = ''
    editor.displayName = ''
    editor.providerKind = 'azureOpenAi'
    editor.baseUrl = ''
    editor.authMode = 'apiKey'
    editor.apiKey = ''
    editor.discoveryMode = 'providerCatalog'
    editor.defaultHeadersText = ''
    editor.defaultQueryParamsText = ''
    editor.models = []
    // Purpose bindings are retired from this form (purposes are assigned through the logical-model purpose
    // map), so a new connection starts with them all disabled.
    editor.bindings = makeBindingDefaults().map((binding) => ({ ...binding, isEnabled: false }))
    saveError.value = ''
    discoveryMessage.value = ''
    advancedSettingsOpen.value = false
    editingModelId.value = null
  }

  const openCreateEditor = () => {
    resetEditor()
    viewMode.value = 'detail'
  }

  const openEditEditor = (profile: AiConnectionDto) => {
    viewMode.value = 'detail'
    editor.mode = 'edit'
    editor.profileId = profile.id ?? ''
    editor.displayName = profile.displayName ?? ''
    editor.providerKind = profile.providerKind ?? 'azureOpenAi'
    editor.baseUrl = profile.baseUrl ?? ''
    editor.authMode = profile.authMode ?? 'apiKey'
    editor.apiKey = ''
    editor.discoveryMode = profile.discoveryMode ?? 'providerCatalog'
    editor.defaultHeadersText = serializeMap(profile.defaultHeaders)
    editor.defaultQueryParamsText = serializeMap(profile.defaultQueryParams)
    advancedSettingsOpen.value = Boolean(editor.defaultHeadersText || editor.defaultQueryParamsText)
    editor.models = (profile.configuredModels ?? []).map((model) => ({
      localId: model.id ?? crypto.randomUUID(),
      existingId: model.id ?? null,
      remoteModelId: model.remoteModelId ?? '',
      displayName: model.displayName ?? '',
      kind: model.supportsEmbedding ? 'embedding' : 'chat',
      tokenizerName: model.tokenizerName ?? '',
      maxInputTokens: model.maxInputTokens == null ? '' : String(model.maxInputTokens),
      maxContextTokens: model.maxContextTokens == null ? '' : String(model.maxContextTokens),
      embeddingDimensions: model.embeddingDimensions == null ? '' : String(model.embeddingDimensions),
      supportsStructuredOutput: Boolean(model.supportsStructuredOutput),
      supportsToolUse: Boolean(model.supportsToolUse),
      inputCostPer1MUsd: model.inputCostPer1MUsd == null ? '' : String(model.inputCostPer1MUsd),
      outputCostPer1MUsd: model.outputCostPer1MUsd == null ? '' : String(model.outputCostPer1MUsd),
      cachedInputCostPer1MUsd: model.cachedInputCostPer1MUsd == null ? '' : String(model.cachedInputCostPer1MUsd),
    }))
    const modelLookup = new Map<string, string>()
    for (const model of editor.models) {
      if (model.existingId) {
        modelLookup.set(model.existingId, model.localId)
      }

      if (model.remoteModelId) {
        modelLookup.set(model.remoteModelId, model.localId)
      }
    }

    editor.bindings = makeBindingDefaults().map((binding) => {
      const existing = (profile.purposeBindings ?? []).find((candidate) => candidate.purpose === binding.purpose)
      return existing
        ? {
            id: existing.id ?? null,
            purpose: existing.purpose ?? binding.purpose,
            configuredModelId: (existing.configuredModelId ? modelLookup.get(existing.configuredModelId) : undefined)
              ?? (existing.remoteModelId ? modelLookup.get(existing.remoteModelId) : undefined)
              ?? '',
            protocolMode: existing.protocolMode ?? binding.protocolMode,
            isEnabled: existing.isEnabled ?? true,
          }
        : binding
    })
    saveError.value = ''
    discoveryMessage.value = ''
  }

  const goBackToList = () => {
    if (profiles.value.length === 0) {
      return
    }

    viewMode.value = 'list'
    saveError.value = ''
    discoveryMessage.value = ''
    advancedSettingsOpen.value = false
  }

  const handleProviderKindChange = () => {
    if (editor.providerKind !== 'azureOpenAi' && editor.authMode === 'azureIdentity') {
      editor.authMode = 'apiKey'
    }
  }

  const addModel = () => {
    const newModelId = crypto.randomUUID()
    editor.models.push({
      localId: newModelId,
      existingId: null,
      remoteModelId: '',
      displayName: '',
      kind: 'chat',
      tokenizerName: '',
      maxInputTokens: '',
      maxContextTokens: '',
      embeddingDimensions: '',
      supportsStructuredOutput: true,
      supportsToolUse: true,
      inputCostPer1MUsd: '',
      outputCostPer1MUsd: '',
      cachedInputCostPer1MUsd: '',
    })
    editingModelId.value = newModelId
  }

  const removeModel = (localId: string) => {
    editor.models = editor.models.filter((model) => model.localId !== localId)
    editor.bindings = editor.bindings.map((binding) =>
      binding.configuredModelId === localId ? { ...binding, configuredModelId: '' } : binding,
    )
  }

  const buildDiscoverRequest = (): DiscoverModelsRequest => ({
    providerKind: editor.providerKind,
    baseUrl: editor.baseUrl,
    auth: {
      mode: editor.authMode,
      apiKey: editor.authMode === 'apiKey' ? editor.apiKey : undefined,
    },
    defaultHeaders: parseMapText(editor.defaultHeadersText),
    defaultQueryParams: parseMapText(editor.defaultQueryParamsText),
  })

  const applyDiscoveredModel = (existing: EditableModel, discovered: AiConfiguredModelDto) => {
    existing.displayName = discovered.displayName ?? existing.displayName
    existing.kind = discovered.supportsEmbedding ? 'embedding' : 'chat'
    existing.tokenizerName = discovered.tokenizerName ?? existing.tokenizerName
    existing.maxInputTokens = discovered.maxInputTokens == null ? existing.maxInputTokens : String(discovered.maxInputTokens)
    existing.maxContextTokens = discovered.maxContextTokens == null ? existing.maxContextTokens : String(discovered.maxContextTokens)
    existing.embeddingDimensions = discovered.embeddingDimensions == null ? existing.embeddingDimensions : String(discovered.embeddingDimensions)
    existing.supportsStructuredOutput = Boolean(discovered.supportsStructuredOutput)
    existing.supportsToolUse = Boolean(discovered.supportsToolUse)
  }

  const buildDiscoveredModel = (discovered: AiConfiguredModelDto): EditableModel => ({
    localId: discovered.id ?? crypto.randomUUID(),
    existingId: discovered.id ?? null,
    remoteModelId: discovered.remoteModelId ?? '',
    displayName: discovered.displayName ?? discovered.remoteModelId ?? '',
    kind: discovered.supportsEmbedding ? 'embedding' : 'chat',
    tokenizerName: discovered.tokenizerName ?? '',
    maxInputTokens: discovered.maxInputTokens == null ? '' : String(discovered.maxInputTokens),
    maxContextTokens: discovered.maxContextTokens == null ? '' : String(discovered.maxContextTokens),
    embeddingDimensions: discovered.embeddingDimensions == null ? '' : String(discovered.embeddingDimensions),
    supportsStructuredOutput: Boolean(discovered.supportsStructuredOutput),
    supportsToolUse: Boolean(discovered.supportsToolUse),
    inputCostPer1MUsd: discovered.inputCostPer1MUsd == null ? '' : String(discovered.inputCostPer1MUsd),
    outputCostPer1MUsd: discovered.outputCostPer1MUsd == null ? '' : String(discovered.outputCostPer1MUsd),
    cachedInputCostPer1MUsd: discovered.cachedInputCostPer1MUsd == null ? '' : String(discovered.cachedInputCostPer1MUsd),
  })

  const mergeDiscoveredModels = (discoveredModels: AiConfiguredModelDto[]) => {
    const existingByRemoteId = new Map(editor.models.map((model) => [model.remoteModelId.toLowerCase(), model]))

    for (const discovered of discoveredModels) {
      const key = (discovered.remoteModelId ?? '').toLowerCase()
      if (!key) {
        continue
      }

      const existing = existingByRemoteId.get(key)
      if (existing) {
        applyDiscoveredModel(existing, discovered)
        continue
      }

      editor.models.push(buildDiscoveredModel(discovered))
    }
  }

  const pluralizeModels = (count: number): string => `model${count === 1 ? '' : 's'}`

  const summarizeDiscovery = (response: AiModelDiscoveryResultDto, discoveredModels: AiConfiguredModelDto[]): string => {
    if (response.discoveryStatus === 'failed') {
      return (response.warnings ?? [])[0] ?? 'Model discovery returned no results.'
    }

    return `Discovered ${discoveredModels.length} ${pluralizeModels(discoveredModels.length)}.`
  }

  const handleDiscoverModels = async () => {
    discovering.value = true
    discoveryMessage.value = ''
    saveError.value = ''
    try {
      const response = await discoverAiModels(props.clientId, buildDiscoverRequest())
      const discoveredModels = response.models ?? []
      mergeDiscoveredModels(discoveredModels)
      discoveryMessage.value = summarizeDiscovery(response, discoveredModels)
    } catch (error) {
      saveError.value = error instanceof Error ? error.message : 'Failed to discover provider models.'
    } finally {
      discovering.value = false
    }
  }

  const normalizeConfiguredModels = (): AiConfiguredModelRequest[] => {
    return editor.models.map((model) => ({
      id: model.existingId || undefined,
      remoteModelId: model.remoteModelId.trim(),
      displayName: model.displayName.trim() || model.remoteModelId.trim(),
      operationKinds: [model.kind === 'embedding' ? 'embedding' : 'chat'],
      supportedProtocolModes: model.kind === 'embedding'
        ? ['auto', 'embeddings']
        : ['auto', 'responses', 'chatCompletions'],
      tokenizerName: model.kind === 'embedding' ? model.tokenizerName.trim() : undefined,
      maxInputTokens: model.kind === 'embedding' && model.maxInputTokens ? Number(model.maxInputTokens) : undefined,
      maxContextTokens: model.kind === 'chat' && model.maxContextTokens ? Number(model.maxContextTokens) : undefined,
      embeddingDimensions: model.kind === 'embedding' && model.embeddingDimensions ? Number(model.embeddingDimensions) : undefined,
      supportsStructuredOutput: model.kind === 'chat' ? model.supportsStructuredOutput : false,
      supportsToolUse: model.kind === 'chat' ? model.supportsToolUse : false,
      inputCostPer1MUsd: model.inputCostPer1MUsd ? Number(model.inputCostPer1MUsd) : undefined,
      outputCostPer1MUsd: model.outputCostPer1MUsd ? Number(model.outputCostPer1MUsd) : undefined,
      cachedInputCostPer1MUsd: model.cachedInputCostPer1MUsd ? Number(model.cachedInputCostPer1MUsd) : undefined,
      source: 'manual',
    }))
  }

  const normalizePurposeBindings = () => {
    const modelLookup = new Map(editor.models.map((model) => [model.localId, model]))
    return editor.bindings.map((binding) => ({
      id: binding.id || undefined,
      purpose: binding.purpose,
      configuredModelId: modelLookup.get(binding.configuredModelId)?.existingId || undefined,
      remoteModelId: modelLookup.get(binding.configuredModelId)?.remoteModelId || undefined,
      protocolMode: binding.protocolMode,
      isEnabled: binding.isEnabled,
    }))
  }

  const validateEditor = () => {
    if (!editor.displayName.trim()) {
      return 'Display name is required.'
    }

    if (!editor.baseUrl.trim()) {
      return 'Base URL is required.'
    }

    if (editor.authMode === 'apiKey' && !editor.apiKey.trim() && editor.mode === 'create') {
      return 'An API key is required for this provider.'
    }

    if (editor.models.length === 0) {
      return 'Add at least one configured model.'
    }

    for (const model of editor.models) {
      if (!model.remoteModelId.trim()) {
        return 'Every configured model needs a remote model ID.'
      }

      if (model.kind === 'embedding' && (!model.tokenizerName.trim() || !model.maxInputTokens || !model.embeddingDimensions)) {
        return `Embedding model '${model.remoteModelId || model.displayName || 'unnamed'}' requires tokenizer, max input tokens, and dimensions.`
      }
    }

    // Purpose bindings are no longer configured on the connection form (they moved to the logical-model
    // purpose map), so they are not validated here.
    return ''
  }

  const saveProfile = async () => {
    saveError.value = ''
    const validationError = validateEditor()
    if (validationError) {
      saveError.value = validationError
      return
    }

    const request: CreateAiConnectionRequest | UpdateAiConnectionRequest = {
      displayName: editor.displayName.trim(),
      providerKind: editor.providerKind,
      baseUrl: editor.baseUrl.trim(),
      auth: {
        mode: editor.authMode,
        apiKey: editor.authMode === 'apiKey' && editor.apiKey.trim() ? editor.apiKey.trim() : undefined,
      },
      discoveryMode: editor.discoveryMode,
      defaultHeaders: parseMapText(editor.defaultHeadersText),
      defaultQueryParams: parseMapText(editor.defaultQueryParamsText),
      configuredModels: normalizeConfiguredModels(),
      purposeBindings: normalizePurposeBindings(),
    }

    saving.value = true
    try {
      let savedProfile: AiConnectionDto
      if (editor.mode === 'edit' && editor.profileId) {
        savedProfile = await updateAiConnection(props.clientId, editor.profileId, request)
      } else {
        savedProfile = await createAiConnection(props.clientId, request as CreateAiConnectionRequest)
      }

      await refreshProfiles()

      const refreshedProfile = profiles.value.find((profile) => profile.id === savedProfile.id)
      if (refreshedProfile) {
        openEditEditor(refreshedProfile)
      } else if (profiles.value.length > 0) {
        viewMode.value = 'list'
      } else {
        openCreateEditor()
      }
    } catch (error) {
      saveError.value = error instanceof Error ? error.message : 'Failed to save AI provider.'
    } finally {
      saving.value = false
    }
  }

  const handleVerify = async (profile: AiConnectionDto) => {
    if (!profile.id) {
      return
    }

    busyConnectionId.value = profile.id
    saveError.value = ''
    try {
      const result = await verifyAiConnection(props.clientId, profile.id)
      discoveryMessage.value = result.summary ?? verificationLabel(result.status)
      await refreshProfiles()
    } catch (error) {
      saveError.value = error instanceof Error ? error.message : 'Failed to verify AI provider.'
    } finally {
      busyConnectionId.value = null
    }
  }

  const handleActivate = async (profile: AiConnectionDto) => {
    if (!profile.id) {
      return
    }

    busyConnectionId.value = profile.id
    saveError.value = ''
    try {
      await activateAiConnection(props.clientId, profile.id)
      await refreshProfiles()
    } catch (error) {
      saveError.value = error instanceof Error ? error.message : 'Failed to activate AI provider.'
    } finally {
      busyConnectionId.value = null
    }
  }

  const handleDeactivate = async (profile: AiConnectionDto) => {
    if (!profile.id) {
      return
    }

    busyConnectionId.value = profile.id
    saveError.value = ''
    try {
      await deactivateAiConnection(props.clientId, profile.id)
      await refreshProfiles()
    } catch (error) {
      saveError.value = error instanceof Error ? error.message : 'Failed to deactivate AI provider.'
    } finally {
      busyConnectionId.value = null
    }
  }

  const confirmDelete = (profile: AiConnectionDto) => {
    deleteTarget.value = profile
  }

  const handleDelete = async (profile: AiConnectionDto) => {
    if (!profile.id) {
      return
    }

    busyConnectionId.value = profile.id
    deleteTarget.value = null
    saveError.value = ''
    try {
      await deleteAiConnection(props.clientId, profile.id)
      await refreshProfiles()

      if (profiles.value.length === 0) {
        openCreateEditor()
      } else if (editor.profileId === profile.id) {
        goBackToList()
      }
    } catch (error) {
      saveError.value = error instanceof Error ? error.message : 'Failed to delete AI provider.'
    } finally {
      busyConnectionId.value = null
    }
  }

  onMounted(async () => {
    resetEditor()
    await refreshProfiles()
  })

  return {
    // state
    profiles,
    loading,
    discovering,
    saving,
    loadError,
    saveError,
    discoveryMessage,
    busyConnectionId,
    deleteTarget,
    viewMode,
    advancedSettingsOpen,
    editingModelId,
    editor,
    // derived
    showListView,
    selectedProfile,
    modelsForPurpose,
    // actions
    refreshProfiles,
    resetEditor,
    openCreateEditor,
    openEditEditor,
    goBackToList,
    handleProviderKindChange,
    addModel,
    removeModel,
    handleDiscoverModels,
    saveProfile,
    handleVerify,
    handleActivate,
    handleDeactivate,
    confirmDelete,
    handleDelete,
  }
}
