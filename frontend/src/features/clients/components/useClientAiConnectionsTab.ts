// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { computed, onMounted, onUnmounted, reactive, ref } from 'vue'
import {
  activateAiConnection,
  createAiConnection,
  deactivateAiConnection,
  deleteAiConnection,
  discoverAiModels,
  dispatchProviderAction,
  probeAiConnection,
  listAiConnections,
  listPermittedProviders,
  readProviderActionInvocation,
  submitProviderActionValues,
  updateAiConnection,
  verifyAiConnection,
} from '@/services/aiConnectionsService'
import { ApiFieldValidationError } from '@/services/aiConnectionsService'
import type {
  AiAuthMode,
  AiProviderActionDispatchDto,
  AiComputedFieldDto,
  AiDeclaredFieldDto,
  AiConfiguredModelDto,
  AiConfiguredModelRequest,
  AiConnectionAvailabilityDto,
  AiConnectionDto,
  AiDeclaredActionDto,
  AiProviderActionInvocationDto,
  AiProviderActionResultDto,
  AiDiscoveryMode,
  AiModelDiscoveryResultDto,
  AiProtocolMode,
  AiProviderKind,
  PermittedProviderDescriptor,
  ProviderCredentialField,
  AiPurpose,
  CreateAiConnectionRequest,
  DiscoverModelsRequest,
  UpdateAiConnectionRequest,
} from '@/services/aiConnectionsService'
import type { EditableBinding, EditableModel, ModelKind } from './aiConnectionsForm.types'
import {
  autoProtocolMode,
  embeddingsProtocolMode,
  isConnectionUnavailable,
  makeBindingDefaults,
  modeLabel,
  modeOptions,
  offeredModeOptions,
  parseMapText,
  providerGuidance,
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
    // Empty until the server says which families there are. The console names no family of its own, so there is
    // no family it could default to before the permitted-providers answer arrives.
    providerKind: '' as AiProviderKind | '',
    baseUrl: '',
    authMode: '' as AiAuthMode | '',
    // Keyed by the field names the selected family declares. Values typed under a name the current mode does
    // not declare are kept rather than discarded, so switching back to that mode does not lose the entry; only
    // the declared ones are ever submitted.
    credentials: {} as Record<string, string>,
    // Keyed by the configuration field names the selected family declares. Kept for the same reason as the
    // credentials above: a value typed under a field another family declares stays in the form, and only the
    // fields the selected family declares and shows are submitted.
    declaredValues: {} as Record<string, string>,
    discoveryMode: 'providerCatalog' as AiDiscoveryMode,
    defaultHeadersText: '',
    defaultQueryParamsText: '',
    models: [] as EditableModel[],
    bindings: [] as EditableBinding[],
  })

  // What this client may configure, straight from the server: every family this build has a driver for, each
  // flagged with whether the tenant permits it, named as the family names itself, with the shapes it speaks and
  // authenticates with, the fields each authentication mode needs, and what it says about the connection boxes.
  // Everything this screen shows about a family is read from here, and that keeps a family named in the
  // enum but unimplemented from being offered, and what lets a family supplied by an add-in this build has never
  // seen render with a name and a usable form.
  const knownProviders = ref<PermittedProviderDescriptor[]>([])
  const providersRestricted = ref(false)

  const permittedProviderKinds = computed(() =>
    knownProviders.value.filter((provider) => provider.isPermitted).map((provider) => provider.providerKind),
  )

  /** The family the editor is on, as the server described it, or undefined for one it did not describe. */
  const selectedProvider = computed(() =>
    knownProviders.value.find((provider) => provider.providerKind === editor.providerKind),
  )

  // Every family the server described as permitted, named as the family names itself. The family the editor is
  // already on is kept in the list even when the server did not describe it, so opening a profile whose add-in
  // is gone shows the family it is stored against instead of silently moving it to another one.
  const availableProviderOptions = computed(() => {
    const options = modeOptions(
      knownProviders.value
        .filter((provider) => provider.isPermitted)
        .map((provider) => ({ value: provider.providerKind, label: provider.label })),
    )

    return editor.providerKind && !options.some((option) => option.value === editor.providerKind)
      ? [...options, { value: editor.providerKind, label: editor.providerKind }]
      : options
  })

  /** What to call one family, from what the server reported. A family it did not describe reads as its key. */
  const providerLabel = (providerKind: AiProviderKind | null | undefined): string => {
    const described = knownProviders.value.find((provider) => provider.providerKind === providerKind)
    return described?.label || providerKind || 'Unknown'
  }

  // The authentication modes the selected family declares, straight from the same response, each named as the
  // server named it. A shape the family flagged as superseded is left out unless the editor is already on it, so
  // a new connection is offered the shapes the family still asks for while a profile saved under a superseded
  // one opens on it and can be edited without its credential moving to another shape. A family the server did
  // not describe leaves the mode the profile is stored under in the list, so the form reads as what the profile
  // holds; the server refuses anything it cannot take.
  const authModeOptions = computed(() => {
    const declared = offeredModeOptions(selectedProvider.value?.authModes, editor.authMode || null)
    if (selectedProvider.value || !editor.authMode) {
      return declared
    }

    return [{ value: editor.authMode, label: editor.authMode }]
  })

  const authModeLabel = (authMode: AiAuthMode | null | undefined) => modeLabel(authModeOptions.value, authMode)

  // The protocol modes the selected family speaks, each named as the server named it. Read where a model's shapes
  // are written, so no model is ever saved as speaking a shape its family cannot serve.
  const protocolModeOptions = computed(() => modeOptions(selectedProvider.value?.protocolModes))

  // What to say about the display-name, base-URL and query-parameter boxes: the selected family's own text over
  // family-neutral text for whatever it leaves unsaid.
  const guidance = computed(() => providerGuidance(selectedProvider.value?.connectionForm))

  // The credential a family needs is one key for most of them and several values for some — an access key id
  // and a secret access key, a service-account document — so the form renders what the driver declared for the
  // selected mode instead of one key box. A family the server did not describe declares nothing here, and the
  // credential boxes stay empty rather than guessing at a shape.
  const credentialFields = computed<ProviderCredentialField[]>(() =>
    editor.authMode ? selectedProvider.value?.credentialFields?.[editor.authMode] ?? [] : [],
  )

  const requiredCredentialFields = computed(() => credentialFields.value.filter((field) => field.isRequired))

  // The configuration fields the selected family declares, straight from the server. A family the permitted list
  // does not carry — one whose driver this build no longer has — is still open in the editor, and the profile it
  // was saved against carries its own copy, so the form for it still renders.
  const declaredFields = computed<AiDeclaredFieldDto[]>(() => {
    if (selectedProvider.value) {
      return selectedProvider.value.declaredFields ?? []
    }

    const profile = profiles.value.find((candidate) => candidate.id === editor.profileId)
    if (!profile || profile.providerKind !== editor.providerKind) {
      return []
    }

    return profile.declaredFields ?? []
  })

  // What the connection will hold once saved: the value in the form, the family's default where the form has
  // none. Read by the visibility conditions, so a condition answers against the value that will be in effect
  // rather than only against what the operator has touched.
  const effectiveDeclaredValues = computed<Record<string, string>>(() => {
    const values: Record<string, string> = {}
    for (const field of declaredFields.value) {
      if (!field.name) {
        continue
      }

      const entered = editor.declaredValues[field.name]
      if (entered !== undefined) {
        values[field.name] = entered
      } else if (field.defaultValue != null) {
        values[field.name] = field.defaultValue
      }
    }

    return values
  })

  const isFieldVisible = (field: AiDeclaredFieldDto): boolean =>
    !field.visibleWhen
    || effectiveDeclaredValues.value[field.visibleWhen.fieldName ?? ''] === field.visibleWhen.equalsValue

  // A hidden field is not rendered, not submitted and not validated, so an operator cannot be blocked by a
  // message about a field the form does not show.
  const visibleDeclaredFields = computed(() => declaredFields.value.filter(isFieldVisible))

  // The read-only values the server computed for this profile, by field name. They are recomputed on every read
  // and never submitted, so the form shows what the server last returned.
  const computedDeclaredValues = computed<Record<string, AiComputedFieldDto>>(() => {
    const profile = profiles.value.find((candidate) => candidate.id === editor.profileId)
    const values: Record<string, AiComputedFieldDto> = {}
    for (const computed of profile?.computedFields ?? []) {
      if (computed.name) {
        values[computed.name] = computed
      }
    }

    return values
  })

  // Which secret-marked declared fields already hold a stored value, so the form says "set" and offers to replace
  // one instead of showing an empty box that looks unconfigured.
  const storedDeclaredSecretNames = computed<string[]>(() => {
    const profile = profiles.value.find((candidate) => candidate.id === editor.profileId)
    return profile?.declaredSecretNames ?? []
  })

  // Messages the server keyed to a declared field, cleared whenever a save is attempted again.
  const declaredFieldErrors = ref<Record<string, string>>({})

  const declaredFieldError = (name: string | null | undefined): string =>
    (name && declaredFieldErrors.value[name]) || ''

  // Only what the family declares, shows, and does not compute is sent. A computed value is derived and a hidden
  // one was never asked about, and the server refuses a name the family does not declare.
  const collectDeclaredValues = (): Record<string, string> => {
    const collected: Record<string, string> = {}
    for (const field of visibleDeclaredFields.value) {
      if (field.isComputed || !field.name) {
        continue
      }

      const entered = editor.declaredValues[field.name]

      // A secret box left empty means "keep what is stored", which the credential boxes mean.
      if (field.isSecret) {
        if (entered?.trim()) {
          collected[field.name] = entered
        }

        continue
      }

      // A field nobody touched carries the family's default, or nothing at all. Sending an empty string for it
      // would store a blank over the default; sending nothing leaves the stored value alone, and a field the
      // operator did clear carries the empty string they left.
      const value = entered !== undefined ? entered : field.defaultValue
      if (value != null) {
        collected[field.name] = value
      }
    }

    return collected
  }

  const missingCredentialField = computed(() =>
    requiredCredentialFields.value.find((field) => !(editor.credentials[field.name] ?? '').trim()),
  )

  // Only the fields the selected mode declares are sent. A value typed under another mode stays in the form and
  // is not submitted, because the family has nowhere to put it and the server refuses a name it does not read.
  const collectCredential = (): Record<string, string> | undefined => {
    const collected: Record<string, string> = {}
    for (const field of credentialFields.value) {
      const value = (editor.credentials[field.name] ?? '').trim()
      if (value) {
        collected[field.name] = value
      }
    }

    // Nothing entered means "keep whatever is stored" on an edit, which an empty body asks the server
    // for. A stored credential is never returned to the browser, so it cannot be resent.
    return Object.keys(collected).length === 0 ? undefined : collected
  }

  // Whether any box of the selected mode's credential was filled in. Entering one replaces the whole stored
  // credential, so the rest have to be entered too.
  const credentialWasEntered = computed(() => collectCredential() !== undefined)

  const isProviderPermitted = (providerKind: AiProviderKind | null | undefined) =>
    knownProviders.value.length === 0
    || (!!providerKind && permittedProviderKinds.value.includes(providerKind))

  // The family the profile names, as the permitted-providers endpoint answers for it. The two reasons need
  // different fixes — one is the tenant's allow-list, the other is a build with no driver — so they are reported
  // apart, not as one vague refusal.
  const providerFamilyAvailability = (
    providerKind: AiProviderKind | null | undefined,
  ): AiConnectionAvailabilityDto | undefined => {
    if (!providerKind || isProviderPermitted(providerKind)) {
      return undefined
    }

    return {
      state: 'unavailable',
      reason: knownProviders.value.some((provider) => provider.providerKind === providerKind)
        ? 'providerFamilyNotPermitted'
        : 'providerFamilyAbsent',
      providerIdentity: providerKind,
      unresolvedValues: [],
    }
  }

  /**
   * Whether a stored profile can serve a review, and what stands in the way when it cannot.
   *
   * The server answers two questions that neither subsumes: the profile carries its own availability, covering
   * the identity and vocabulary it was stored with, and the permitted-providers endpoint says which families
   * this build has a driver for. A profile stored against a family that resolves, is permitted, and has no
   * driver passes the first and fails the second, so both are consulted and the profile's own answer wins.
   */
  const connectionAvailability = (profile: AiConnectionDto): AiConnectionAvailabilityDto | undefined =>
    isConnectionUnavailable(profile) ? profile.availability : providerFamilyAvailability(profile.providerKind)

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

      // A policy failure must not hide the profiles: the list is the more important of the two, and the server
      // enforces the policy regardless of what this screen manages to display.
      try {
        const permitted = await listPermittedProviders(props.clientId)
        knownProviders.value = permitted.providers ?? []
        providersRestricted.value = permitted.isRestricted
      } catch {
        knownProviders.value = []
        providersRestricted.value = false
      }

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
    // Cleared before the shapes below are read. A superseded shape stays in the list only while the editor is on
    // it, so leaving the previously opened profile's shape selected here would keep that shape offered and make
    // it what the new connection starts on.
    editor.authMode = ''
    // The first family the server offers, and then the first authentication mode that family offers a new
    // connection. Nothing is offered before the permitted-providers answer arrives, which leaves both empty
    // until it does.
    editor.providerKind = availableProviderOptions.value[0]?.value ?? ''
    editor.baseUrl = ''
    editor.authMode = authModeOptions.value[0]?.value ?? ''
    editor.credentials = {}
    editor.declaredValues = {}
    editor.discoveryMode = 'providerCatalog'
    editor.defaultHeadersText = ''
    editor.defaultQueryParamsText = ''
    editor.models = []
    // Purpose bindings are retired from this form (purposes are assigned through the logical-model purpose
    // map), so a new connection starts with them all disabled.
    editor.bindings = makeBindingDefaults().map((binding) => ({ ...binding, isEnabled: false }))
    saveError.value = ''
    discoveryMessage.value = ''
    clearProbeResult()
    advancedSettingsOpen.value = false
    editingModelId.value = null
  }

  const openCreateEditor = () => {
    resetEditor()
    viewMode.value = 'detail'
  }

  const openEditEditor = (profile: AiConnectionDto) => {
    viewMode.value = 'detail'
    // A probe result belongs to the credential that was in the form when it ran, so it is dropped when a
    // different profile is opened rather than left to look like this one's outcome.
    clearProbeResult()
    editor.mode = 'edit'
    editor.profileId = profile.id ?? ''
    editor.displayName = profile.displayName ?? ''
    editor.providerKind = profile.providerKind ?? ''
    editor.baseUrl = profile.baseUrl ?? ''
    editor.authMode = profile.authMode ?? ''
    editor.credentials = {}
    // A stored secret is never returned, so its box starts empty and the form reports it as set from the names
    // the server listed.
    editor.declaredValues = { ...(profile.providerSettings ?? {}) }
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
      cacheWriteCostPer1MUsd: model.cacheWriteCostPer1MUsd == null ? '' : String(model.cacheWriteCostPer1MUsd),
      supportsReasoning: model.supportsReasoning ?? false,
      supportsPromptCaching: model.supportsPromptCaching ?? false,
      reasoningContentField: model.reasoningContentField ?? '',
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

  // Families read different authentication modes, so the mode selected for the previous family can be one the new
  // one cannot use. Moving to a mode the new family declares keeps the form on a combination the server accepts.
  // A family that declares none has nothing to move to, and the save refuses it rather than sending the mode
  // that was already selected.
  const handleProviderKindChange = () => {
    const offered = authModeOptions.value
    const firstOffered = offered[0]?.value
    if (firstOffered && !offered.some((option) => option.value === editor.authMode)) {
      editor.authMode = firstOffered
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
      cacheWriteCostPer1MUsd: '',
      supportsReasoning: false,
      supportsPromptCaching: false,
      reasoningContentField: '',
    })
    editingModelId.value = newModelId
  }

  const removeModel = (localId: string) => {
    editor.models = editor.models.filter((model) => model.localId !== localId)
    editor.bindings = editor.bindings.map((binding) =>
      binding.configuredModelId === localId ? { ...binding, configuredModelId: '' } : binding,
    )
  }

  // The declared settings ride along with the probe and the discovery. A family whose verification reads one of
  // them is otherwise asked about a configuration the saved connection will not have, and answers about that one.
  const buildDiscoverRequest = (): DiscoverModelsRequest => ({
    providerKind: editor.providerKind as AiProviderKind,
    baseUrl: editor.baseUrl,
    auth: {
      mode: editor.authMode as AiAuthMode,
      fields: collectCredential(),
    },
    defaultHeaders: parseMapText(editor.defaultHeadersText),
    defaultQueryParams: parseMapText(editor.defaultQueryParamsText),
    providerSettings: collectDeclaredValues(),
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
    cacheWriteCostPer1MUsd: discovered.cacheWriteCostPer1MUsd == null ? '' : String(discovered.cacheWriteCostPer1MUsd),
    supportsReasoning: discovered.supportsReasoning ?? false,
    supportsPromptCaching: discovered.supportsPromptCaching ?? false,
    reasoningContentField: discovered.reasoningContentField ?? '',
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

  // A driver's warnings are the actionable half of a successful discovery — that a Bedrock model may only answer
  // through an inference profile, or that Vertex publishes no model list at all. Dropping them left an operator
  // with a count and a later rejection that explained nothing.
  const summarizeDiscovery = (response: AiModelDiscoveryResultDto, discoveredModels: AiConfiguredModelDto[]): string => {
    const warnings = (response.warnings ?? []).filter(Boolean)

    if (response.discoveryStatus === 'failed') {
      return warnings[0] ?? 'Model discovery returned no results.'
    }

    const found = `Discovered ${discoveredModels.length} ${pluralizeModels(discoveredModels.length)}.`
    return [found, ...warnings].join(' ')
  }

  // Probing before saving is the point: an operator can find out that a key or a base URL is wrong without first
  // committing it to storage. The provider's own categorised reason is shown, since "failed" alone is not
  // actionable — a bad key, an unreachable host and a rejected request need different fixes.
  const probing = ref(false)
  const probeMessage = ref('')
  const probeFailed = ref(false)

  const clearProbeResult = () => {
    probeMessage.value = ''
    probeFailed.value = false
  }

  // The probe carries the credential in the request, because it deliberately tests the form's values rather than
  // anything stored. A saved profile's key is never returned to the browser, so on an existing profile the field
  // is blank and there is nothing to probe with until the operator types one. Offering the button then would
  // produce a refusal that reads like a broken connection instead of a missing input — re-checking the stored
  // credential is what Verify does.
  const canProbe = computed(() => missingCredentialField.value === undefined)

  const handleProbeConnection = async () => {
    probing.value = true
    probeMessage.value = ''
    probeFailed.value = false
    saveError.value = ''
    try {
      const result = await probeAiConnection(props.clientId, buildDiscoverRequest())
      probeFailed.value = result.status !== 'verified'
      probeMessage.value = probeFailed.value
        ? [result.summary, result.actionHint].filter(Boolean).join(' ')
        : result.summary || 'The provider accepted the connection.'
    } catch (error) {
      probeFailed.value = true
      probeMessage.value = error instanceof Error ? error.message : 'Failed to probe the provider connection.'
    } finally {
      probing.value = false
    }
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

  /**
   * The protocol modes a model of this workload may be called through: what the selected family declares, narrowed
   * to the ones that fit the workload.
   *
   * Read from the family rather than fixed, because a fixed list writes shapes onto a model whose family cannot
   * serve them — a model of a family that speaks only its own protocol saved as speaking chat completions, which
   * no call can then use. A family the server did not describe states nothing, and nothing is sent, which leaves
   * a stored model with the shapes it already had rather than clearing them.
   */
  const protocolModesForKind = (kind: ModelKind): AiProtocolMode[] | undefined => {
    const declared = protocolModeOptions.value.map((option) => option.value)
    const fitting = kind === 'embedding'
      ? declared.filter((mode) => mode === autoProtocolMode || mode === embeddingsProtocolMode)
      : declared.filter((mode) => mode !== embeddingsProtocolMode)

    return fitting.length > 0 ? fitting : undefined
  }

  const normalizeConfiguredModels = (): AiConfiguredModelRequest[] => {
    return editor.models.map((model) => ({
      id: model.existingId || undefined,
      remoteModelId: model.remoteModelId.trim(),
      displayName: model.displayName.trim() || model.remoteModelId.trim(),
      operationKinds: [model.kind === 'embedding' ? 'embedding' : 'chat'],
      supportedProtocolModes: protocolModesForKind(model.kind),
      tokenizerName: model.kind === 'embedding' ? model.tokenizerName.trim() : undefined,
      maxInputTokens: model.kind === 'embedding' && model.maxInputTokens ? Number(model.maxInputTokens) : undefined,
      maxContextTokens: model.kind === 'chat' && model.maxContextTokens ? Number(model.maxContextTokens) : undefined,
      embeddingDimensions: model.kind === 'embedding' && model.embeddingDimensions ? Number(model.embeddingDimensions) : undefined,
      supportsStructuredOutput: model.kind === 'chat' ? model.supportsStructuredOutput : false,
      supportsToolUse: model.kind === 'chat' ? model.supportsToolUse : false,
      inputCostPer1MUsd: model.inputCostPer1MUsd ? Number(model.inputCostPer1MUsd) : undefined,
      outputCostPer1MUsd: model.outputCostPer1MUsd ? Number(model.outputCostPer1MUsd) : undefined,
      cachedInputCostPer1MUsd: model.cachedInputCostPer1MUsd ? Number(model.cachedInputCostPer1MUsd) : undefined,
      cacheWriteCostPer1MUsd: model.cacheWriteCostPer1MUsd ? Number(model.cacheWriteCostPer1MUsd) : undefined,
      supportsReasoning: model.supportsReasoning,
      supportsPromptCaching: model.supportsPromptCaching,
      reasoningContentField: model.reasoningContentField.trim() || undefined,
      source: 'manual',
    }))
  }

  const normalizePurposeBindings = () => {
    const modelLookup = new Map(editor.models.map((model) => [model.localId, model]))
    // Every purpose gets a row in the editor, pre-enabled for the common ones, so a profile that binds nothing
    // directly still carries rows with no model chosen. Those are placeholders rather than bindings, and sending
    // them asks the server to bind a purpose to nothing.
    return editor.bindings
      .filter((binding) => modelLookup.has(binding.configuredModelId))
      .map((binding) => ({
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

    if (!editor.providerKind) {
      return 'Choose a provider family.'
    }

    if (!editor.baseUrl.trim()) {
      return 'Base URL is required.'
    }

    // A family that declares no authentication mode leaves whichever mode was selected for the previous family
    // in place, because there is none to move to. Submitting that would send a mode this family cannot read, so
    // the save stops here and names the family instead.
    if (authModeOptions.value.length === 0) {
      return `${providerLabel(editor.providerKind || undefined)} declares no authentication mode, `
        + 'so a profile cannot be saved against it.'
    }

    // The same rule for the case where the family does declare modes and the selected one is not among them.
    // Only a change of provider kind moves the selection, so opening a stored profile whose family has since
    // narrowed what it declares leaves a mode in the form that the family can no longer read. The alternatives
    // are named because the operator has to pick one of them to get past this.
    if (!authModeOptions.value.some((option) => option.value === editor.authMode)) {
      const offered = authModeOptions.value.map((option) => option.label).join(', ')
      return `${providerLabel(editor.providerKind || undefined)} does not authenticate with `
        + `${authModeLabel(editor.authMode || undefined)}. Choose one of: ${offered}.`
    }

    // On an edit the boxes start blank and leaving all of them blank means "keep what is stored", so the
    // required fields are only insisted on when the profile is being created or a credential was entered.
    // Entering one box replaces the whole stored credential: a field map carrying an access key id and no
    // secret access key is refused by the server, so it is caught here where the boxes are.
    if ((editor.mode === 'create' || credentialWasEntered.value) && missingCredentialField.value) {
      return `${missingCredentialField.value.label} is required for this provider.`
    }

    // A required declared field with nothing in it stops the save here, with the message on that field, rather
    // than travelling to the server and coming back as a refusal the form cannot place.
    const missingDeclared = visibleDeclaredFields.value.find((field) => {
      if (!field.isRequired || field.isComputed || !field.name) {
        return false
      }

      // What will be in effect, not only what was typed. A field the family declared a default for is supplied
      // by that default when the box is left alone, and the server accepts it on the same basis, so refusing it
      // here is the form telling an operator to fill in a value that is already going to be sent.
      if ((effectiveDeclaredValues.value[field.name] ?? '').trim()) {
        return false
      }

      // A stored secret satisfies its field: leaving the box empty keeps what is stored.
      return !(field.isSecret && storedDeclaredSecretNames.value.includes(field.name))
    })

    if (missingDeclared) {
      declaredFieldErrors.value = { [missingDeclared.name!]: `${missingDeclared.label} is required.` }
      return `${missingDeclared.label} is required.`
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
    declaredFieldErrors.value = {}
    const validationError = validateEditor()
    if (validationError) {
      saveError.value = validationError
      return
    }

    // Both are set by the time validation passes, and that lets the editor hold them empty until the
    // server has said which families and authentication modes there are.
    const request: CreateAiConnectionRequest | UpdateAiConnectionRequest = {
      displayName: editor.displayName.trim(),
      providerKind: editor.providerKind as AiProviderKind,
      baseUrl: editor.baseUrl.trim(),
      auth: {
        mode: editor.authMode as AiAuthMode,
        fields: collectCredential(),
      },
      discoveryMode: editor.discoveryMode,
      defaultHeaders: parseMapText(editor.defaultHeadersText),
      defaultQueryParams: parseMapText(editor.defaultQueryParamsText),
      configuredModels: normalizeConfiguredModels(),
      purposeBindings: normalizePurposeBindings(),
      providerSettings: collectDeclaredValues(),
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
      declaredFieldErrors.value = readDeclaredFieldErrors(error)
    } finally {
      saving.value = false
    }
  }

  // A refusal the server keyed to `providerSettings.<field name>` belongs on that input. Anything keyed to
  // something else stays in the summary line, where the rest of the form's refusals already go.
  const readDeclaredFieldErrors = (error: unknown): Record<string, string> => {
    if (!(error instanceof ApiFieldValidationError)) {
      return {}
    }

    const prefix = 'providerSettings.'
    const attached: Record<string, string> = {}
    for (const [key, messages] of Object.entries(error.fieldErrors)) {
      if (key.startsWith(prefix) && messages.length > 0) {
        attached[key.slice(prefix.length)] = messages[0]
      }
    }

    return attached
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


  /**
   * One run of a declared action, as the page holds it while it is open.
   *
   * A dispatch returns before the family's work finishes, so what the page follows from then on is the run. The
   * values are the ones the operator is filling in for it; they belong to the run and are never saved.
   */
  type DeclaredActionRun = {
    connectionId: string
    action: AiDeclaredActionDto
    started: boolean
    busy: boolean
    error: string
    invocation: AiProviderActionInvocationDto | null
    result: AiProviderActionResultDto | null
    values: Record<string, string>
  }

  const actionRun = ref<DeclaredActionRun | null>(null)
  let actionPoll: ReturnType<typeof setInterval> | null = null
  let actionPollInFlight = false

  /** How often an open run is re-read. Long enough not to hammer the API while an operator is at the provider. */
  const actionPollIntervalMs = 3000

  /**
   * The operations this connection is offered: the ones its family declares, narrowed to the ids the server says
   * apply to this connection. Nothing here is hardcoded for any family.
   *
   * The two are separate because they answer different questions. The declaration says which operations the
   * family has, and the server asks the family which of them this connection is in a state for — a family whose
   * credential is written by its own sign-in offers the sign-in before that has happened and the disconnect
   * after. A family that offers none renders no buttons.
   */
  const declaredActionsFor = (profile: AiConnectionDto): AiDeclaredActionDto[] => {
    const offered = profile.offeredActionIds ?? []
    return (profile.declaredActions ?? []).filter((action) => !!action.id && offered.includes(action.id))
  }

  /**
   * The values to ask the operator for, which is either the form the family asked for or, once the operator has
   * been sent to the provider, the action's own declared inputs. The second is the fallback every flow keeps:
   * an operator whose browser is not on the machine running the host completes the flow by pasting instead.
   */
  const actionFormFields = computed<AiDeclaredFieldDto[]>(() => {
    const run = actionRun.value
    if (!run || !run.started) {
      return []
    }

    if (run.result?.kind === 'showForm') {
      return run.result.fields ?? []
    }

    return run.result?.kind === 'openUrl' ? (run.action.inputs ?? []) : []
  })

  const stopPollingAction = () => {
    if (actionPoll) {
      clearInterval(actionPoll)
      actionPoll = null
    }
  }

  const isActionRunOpen = (invocation: AiProviderActionInvocationDto | null): boolean =>
    invocation?.state === 'Pending'

  /**
   * Whether an operation is in flight. One run is held at a time, so starting a second would replace the first:
   * the first would stop being polled while still open at the host, and its answer would arrive with nothing
   * watching for it. A second operation is offered once the operator closes the one that is running.
   */
  const isActionRunActive = computed<boolean>(() => {
    const run = actionRun.value
    if (!run) {
      return false
    }

    return run.busy || !run.started || isActionRunOpen(run.invocation)
  })

  const pollActionRun = async () => {
    const run = actionRun.value

    // One read at a time. The interval does not wait for the previous read, so two in flight can answer out of
    // order and the older one would write its state over the newer.
    if (!run?.invocation?.id || actionPollInFlight) {
      return
    }

    actionPollInFlight = true
    try {
      const invocation = await readProviderActionInvocation(run.invocation.id)

      // The read outlives the run it was started for. A response for a run the operator has closed or replaced
      // is dropped here, because applying it would stop the poll that is following the current run.
      if (actionRun.value !== run) {
        return
      }

      run.invocation = invocation

      if (!isActionRunOpen(invocation)) {
        stopPollingAction()
        await refreshProfiles()
      }
    } catch {
      // A read that failed says nothing about the run, so the poll keeps going: stopping on a transient failure
      // would leave an operator watching a flow the page had quietly given up following.
    } finally {
      actionPollInFlight = false
    }
  }

  const followActionRun = () => {
    stopPollingAction()

    if (isActionRunOpen(actionRun.value?.invocation ?? null)) {
      actionPoll = setInterval(() => {
        void pollActionRun()
      }, actionPollIntervalMs)
    }
  }

  const applyActionDispatch = async (run: DeclaredActionRun, dispatch: AiProviderActionDispatchDto) => {
    // A dispatch that belongs to a run the page has closed or replaced is dropped. The call outlives the run it
    // came from, so without this a late answer opens a tab for an operation the operator has left, and calls
    // followActionRun, which would re-point the poll at whichever run is current.
    if (actionRun.value !== run) {
      return
    }

    run.invocation = dispatch.invocation ?? null
    run.result = dispatch.result ?? null

    // A field the family declared a default for starts out holding it, so what the form shows is what the family
    // receives. Without this a boolean declared true renders unchecked: an operator who leaves it alone submits
    // no value for it at all, while one who ticks and unticks it submits "false", from the same visible state.
    // A name that already holds a value is left as it is, so seeding never overwrites an answer.
    for (const field of actionFormFields.value) {
      if (field.name && !field.isComputed && field.defaultValue != null && run.values[field.name] === undefined) {
        run.values[field.name] = field.defaultValue
      }
    }

    // A new tab with the opener detached, never an assignment to the current location: the address was written
    // by the provider family, and assigning one to this window would run a script address in the admin origin.
    if (dispatch.result?.kind === 'openUrl' && dispatch.result.url) {
      window.open(dispatch.result.url, '_blank', 'noopener,noreferrer')
    }

    followActionRun()

    if (!isActionRunOpen(run.invocation)) {
      await refreshProfiles()
    }
  }

  /**
   * Opens the affordance for one declared action. Where the family stated a requirement the deployment has to
   * meet, the operator reads it before anything starts; where it stated none there is nothing to read and the
   * action starts at once.
   */
  const openDeclaredAction = async (profile: AiConnectionDto, action: AiDeclaredActionDto) => {
    if (!profile.id || isActionRunActive.value) {
      return
    }

    stopPollingAction()
    actionRun.value = {
      connectionId: profile.id,
      action,
      started: false,
      busy: false,
      error: '',
      invocation: null,
      result: null,
      values: {},
    }

    if (!action.coLocationNotice) {
      await startDeclaredAction()
    }
  }

  const startDeclaredAction = async () => {
    const run = actionRun.value
    if (!run || run.busy) {
      return
    }

    run.busy = true
    run.error = ''
    run.started = true
    try {
      // The family key goes back exactly as the server reported it beside the action. A console cannot compose
      // one: a key is a constant the family declares about itself.
      const dispatch = await dispatchProviderAction(
        run.connectionId,
        run.action.addInKey ?? '',
        run.action.id ?? '',
      )
      await applyActionDispatch(run, dispatch)
    } catch (error) {
      run.error = error instanceof Error ? error.message : 'Failed to start the provider action.'
    } finally {
      run.busy = false
    }
  }

  /**
   * The values this run submits: what the operator entered for an input the family asks for. A computed input
   * is derived by the family and the connection form leaves those out of what it saves, so the action path
   * leaves them out too. The host refuses an input the action does not declare as submittable.
   */
  const submittableActionValues = (run: DeclaredActionRun): Record<string, string> => {
    const submittable: Record<string, string> = {}
    for (const field of actionFormFields.value) {
      if (field.name && !field.isComputed && run.values[field.name] !== undefined) {
        submittable[field.name] = run.values[field.name]
      }
    }

    return submittable
  }

  const submitDeclaredActionValues = async () => {
    const run = actionRun.value
    if (!run?.invocation?.id || run.busy) {
      return
    }

    // A required input with nothing in it is refused here, where the message can name the field, rather than at
    // the family, which answers with a refusal of its own wording against an invocation the operator has to read.
    const missing = actionFormFields.value.find(
      (field) => field.isRequired && !field.isComputed && !(run.values[field.name ?? ''] ?? '').trim(),
    )

    if (missing) {
      run.error = `${missing.label} is required.`
      return
    }

    run.busy = true
    run.error = ''
    try {
      const dispatch = await submitProviderActionValues(run.invocation.id, submittableActionValues(run))
      run.values = {}
      await applyActionDispatch(run, dispatch)
    } catch (error) {
      run.error = error instanceof Error ? error.message : 'Failed to submit the values the action asked for.'
    } finally {
      run.busy = false
    }
  }

  const closeDeclaredAction = () => {
    stopPollingAction()
    actionRun.value = null
  }

  onUnmounted(stopPollingAction)

  onMounted(async () => {
    await refreshProfiles()

    // Reset after the load rather than before it: a new profile starts on the first family the server offers,
    // and nothing is offered until the permitted-providers answer has arrived.
    resetEditor()
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
    availableProviderOptions,
    authModeOptions,
    protocolModeOptions,
    providerLabel,
    authModeLabel,
    guidance,
    credentialFields,
    declaredFields,
    visibleDeclaredFields,
    computedDeclaredValues,
    storedDeclaredSecretNames,
    declaredFieldError,
    isProviderPermitted,
    connectionAvailability,
    providersRestricted,
    actionRun,
    actionFormFields,
    isActionRunActive,
    declaredActionsFor,
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
    handleProbeConnection,
    probing,
    probeMessage,
    probeFailed,
    canProbe,
    saveProfile,
    handleVerify,
    handleActivate,
    handleDeactivate,
    confirmDelete,
    handleDelete,
    openDeclaredAction,
    startDeclaredAction,
    submitDeclaredActionValues,
    closeDeclaredAction,
  }
}
