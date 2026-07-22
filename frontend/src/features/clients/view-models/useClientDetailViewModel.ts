// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

import { computed, onMounted, ref, watch, type ComputedRef, type InjectionKey, type Ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { createAdminClient } from '@/services/api'
import { useSession } from '@/composables/useSession'
import type { components } from '@/services/generated/openapi'

const detailTabs = [
  'config',
  'crawl-configs',
  'webhooks',
  'providers',
  'procursor',
  'ai',
  'budget',
  'history',
  'dismissals',
  'prompt-overrides',
  'usage',
  'spend',
] as const

export type DetailTab = (typeof detailTabs)[number]
export type ReviewPassEntry = components['schemas']['ReviewPassEntry']
export type ReviewReasoningEffort = components['schemas']['ReviewReasoningEffort']
export type BudgetConfig = components['schemas']['BudgetConfigDto']

export interface ClientDetailDto {
  id: string
  displayName: string
  isActive: boolean
  createdAt: string
  defaultReviewPipelineProfileId?: string | null
  defaultReviewPipelineProfileUpdatedAtUtc?: string | null
  scmCommentPostingEnabled: boolean
  enableEvidenceBackedVerification: boolean
  enableMultiPassUnion: boolean
  includeLinkedItemsInContext: boolean
  enableLanguageRobustScreening: boolean
  reviewPasses?: ReviewPassEntry[] | null
  baselineReasoningEffort?: ReviewReasoningEffort | null
  budgetConfig?: BudgetConfig | null
}

export interface ReviewProfileCatalogItemDto {
  profileId: string
  displayName: string
  isDefault: boolean
}

export interface ClientReviewProfileDto {
  clientId: string
  defaultReviewPipelineProfileId: string
  source: 'systemDefault' | 'clientDefault'
  updatedAtUtc?: string | null
}

export interface ClientDetailViewModel {
  readonly name: 'useClientDetailViewModel'
  clientId: string
  client: Ref<ClientDetailDto | null>
  loading: Ref<boolean>
  notFound: Ref<boolean>
  loadError: Ref<boolean>
  saving: Ref<boolean>
  saveError: Ref<string>
  showDeleteDialog: Ref<boolean>
  editedDisplayName: Ref<string>
  editedDefaultReviewPipelineProfileId: Ref<string>
  editedScmCommentPostingEnabled: Ref<boolean>
  editedEnableEvidenceBackedVerification: Ref<boolean>
  editedEnableMultiPassUnion: Ref<boolean>
  editedIncludeLinkedItemsInContext: Ref<boolean>
  editedEnableLanguageRobustScreening: Ref<boolean>
  editedReviewPasses: Ref<ReviewPassEntry[]>
  editedBaselineReasoningEffort: Ref<ReviewReasoningEffort>
  editedMonthlyBudgetSoftCapUsd: Ref<string>
  editedMonthlyBudgetHardCapUsd: Ref<string>
  editedPullRequestBudgetSoftCapUsd: Ref<string>
  editedPullRequestBudgetHardCapUsd: Ref<string>
  editedIncrementBudgetSoftCapUsd: Ref<string>
  editedIncrementBudgetHardCapUsd: Ref<string>
  reviewProfiles: Ref<ReviewProfileCatalogItemDto[]>
  clientReviewProfile: Ref<ClientReviewProfileDto | null>
  isProviderDetailOpen: Ref<boolean>
  isWebhookDetailOpen: Ref<boolean>
  canManageClient: ComputedRef<boolean>
  canViewClient: ComputedRef<boolean>
  availableTabs: ComputedRef<DetailTab[]>
  activeTab: Ref<DetailTab>
  providerUpgradeMessage: ComputedRef<string>
  isCrawlConfigsAvailable: ComputedRef<boolean>
  isBudgetingAvailable: ComputedRef<boolean>
  budgetingUpgradeMessage: ComputedRef<string>
  isUsageTabAvailable: ComputedRef<boolean>
  loadClient: () => Promise<void>
  saveDisplayName: () => Promise<void>
  toggleStatus: () => Promise<void>
  saveAdvancedSettings: () => Promise<void>
  saveReviewProfile: () => Promise<void>
  saveBudgetConfig: () => Promise<void>
  isAdvancedSettingsButtonEnabled: () => boolean
  isBudgetButtonEnabled: () => boolean
  isReviewProfileButtonEnabled: () => boolean
  handleDelete: () => Promise<void>
  handleOverviewNavigate: (tab: string) => void
}

/** Injection key so sub-tab components (e.g. ClientSystemTab) share the parent
 * view's single view-model instance instead of re-instantiating it. */
export const ClientDetailVmKey: InjectionKey<ClientDetailViewModel> = Symbol('clientDetailVm')

export interface ClientDetailService {
  getClient: (clientId: string) => Promise<{ data: ClientDetailDto | null; response?: Response | { status?: number; ok?: boolean } }>
  patchClient: (clientId: string, body: Record<string, unknown>) => Promise<{ data?: ClientDetailDto | null; error?: unknown; response?: Response | { status?: number; ok?: boolean } }>
  getReviewProfiles: () => Promise<{ data: { profiles: ReviewProfileCatalogItemDto[] } | null }>
  getClientReviewProfile: (clientId: string) => Promise<{ data: ClientReviewProfileDto | null }>
  putClientReviewProfile: (clientId: string, body: { defaultReviewPipelineProfileId: string | null }) => Promise<{ data: ClientReviewProfileDto }>
  deleteClient: (clientId: string) => Promise<unknown>
}

export interface UseClientDetailViewModelOptions {
  clientDetailService?: Partial<ClientDetailService>
  autoLoad?: boolean
}

async function defaultGetClient(clientId: string) {
  return createAdminClient().GET('/clients/{clientId}', {
    params: { path: { clientId } },
  })
}

async function defaultPatchClient(clientId: string, body: Record<string, unknown>) {
  return createAdminClient().PATCH('/clients/{clientId}', {
    params: { path: { clientId } },
    body,
  })
}

async function defaultGetReviewProfiles() {
  return createAdminClient().GET('/admin/review-profiles', {})
}

async function defaultGetClientReviewProfile(clientId: string) {
  return createAdminClient().GET('/admin/clients/{clientId}/review-profile', {
    params: { path: { clientId } },
  })
}

async function defaultPutClientReviewProfile(clientId: string, body: { defaultReviewPipelineProfileId: string | null }) {
  return createAdminClient().PUT('/admin/clients/{clientId}/review-profile', {
    params: { path: { clientId } },
    body,
  })
}

async function defaultDeleteClient(clientId: string) {
  return createAdminClient().DELETE('/clients/{clientId}', {
    params: { path: { clientId } },
  })
}

function isDetailTab(value: string): value is DetailTab {
  return (detailTabs as readonly string[]).includes(value)
}

/** Normalizes a persisted review-pass list into a stable, ordinal-sorted list with
 * contiguous zero-based ordinals so the edited list and the server echo compare cleanly.
 * Each pass keeps its optional lens (null is an ordinary resample pass), optional scope
 * (null is the per-file default), and shadow flag so all three are sent to the server and
 * count toward the dirty check. Entries with an empty/missing configuredModelId are dropped
 * — a half-configured row is never sent to the server (which would reject it) and never
 * counts toward the dirty check. */
export function normalizeReviewPasses(passes: ReviewPassEntry[] | null | undefined): ReviewPassEntry[] {
  return [...(passes ?? [])]
    .filter((pass) => (pass.configuredModelId ?? '') !== '')
    .sort((left, right) => (left.ordinal ?? 0) - (right.ordinal ?? 0))
    .map((pass, index) => ({
      ordinal: index,
      configuredModelId: pass.configuredModelId ?? '',
      lens: pass.lens ?? null,
      scope: pass.scope ?? null,
      shadow: pass.shadow ?? false,
      reasoningEffort: pass.reasoningEffort ?? 'none',
    }))
}

/** Pulls a human-readable message out of an ASP.NET ValidationProblem body, preferring the specific
 * field error (e.g. the review-pass validation message) over the generic problem title. Falls back to
 * the supplied default when the body carries nothing useful. */
function extractValidationMessage(error: unknown, fallback: string): string {
  if (!error || typeof error !== 'object') {
    return fallback
  }

  const problem = error as { detail?: unknown; title?: unknown; errors?: Record<string, unknown> }

  if (typeof problem.detail === 'string' && problem.detail.trim().length > 0) {
    return problem.detail
  }

  if (problem.errors && typeof problem.errors === 'object') {
    for (const value of Object.values(problem.errors)) {
      if (Array.isArray(value) && typeof value[0] === 'string' && value[0].trim().length > 0) {
        return value[0]
      }
    }
  }

  if (typeof problem.title === 'string' && problem.title.trim().length > 0) {
    return problem.title
  }

  return fallback
}

/** True when a patch response did not yield a client to apply (network error, 4xx, or empty body).
 * Typed loosely so it accepts both the service-interface shape and openapi-fetch's raw response. */
function isFailedPatch(result: { data?: unknown; error?: unknown; response?: Response | { status?: number; ok?: boolean } }): boolean {
  return !!result.error || !result.data || (result.response !== undefined && result.response.ok === false)
}

/** Two pass lists are equal when they carry the same ordered (configured-model id, lens, scope, shadow,
 * reasoning effort) tuples. */
/** Formats a stored numeric USD cap for a text input: null/undefined becomes an empty field ("no limit"). */
export function capToInput(value: number | null | undefined): string {
  return value == null ? '' : String(value)
}

/**
 * Parses a USD cap input back to a number, or null when blank ("no limit"). Accepts a number as well as a
 * string because a `<input type="number">` bound with v-model coerces its value to a number in the browser, so
 * an edited cap arrives here as a number rather than the string it started as.
 */
export function capFromInput(value: string | number | null | undefined): number | null {
  if (typeof value === 'number') {
    return Number.isFinite(value) ? value : null
  }
  const trimmed = (value ?? '').trim()
  return trimmed === '' ? null : Number(trimmed)
}

export function reviewPassesEqual(left: ReviewPassEntry[], right: ReviewPassEntry[]): boolean {
  if (left.length !== right.length) {
    return false
  }

  return left.every(
    (pass, index) =>
      (pass.configuredModelId ?? '') === (right[index]?.configuredModelId ?? '') &&
      (pass.lens ?? null) === (right[index]?.lens ?? null) &&
      (pass.scope ?? null) === (right[index]?.scope ?? null) &&
      (pass.shadow ?? false) === (right[index]?.shadow ?? false) &&
      (pass.reasoningEffort ?? 'none') === (right[index]?.reasoningEffort ?? 'none'),
  )
}

export function useClientDetailViewModel(options: UseClientDetailViewModelOptions = {}): ClientDetailViewModel {
  const router = useRouter()
  const route = useRoute()
  const { getCapability, hasClientRole } = useSession()
  const clientId = route.params.id as string
  const getClientFn = options.clientDetailService?.getClient ?? defaultGetClient
  const patchClientFn = options.clientDetailService?.patchClient ?? defaultPatchClient
  const getReviewProfilesFn = options.clientDetailService?.getReviewProfiles ?? defaultGetReviewProfiles
  const getClientReviewProfileFn = options.clientDetailService?.getClientReviewProfile ?? defaultGetClientReviewProfile
  const putClientReviewProfileFn = options.clientDetailService?.putClientReviewProfile ?? defaultPutClientReviewProfile
  const deleteClientFn = options.clientDetailService?.deleteClient ?? defaultDeleteClient
  const autoLoad = options.autoLoad ?? true

  const isProviderDetailOpen = ref(false)
  const isWebhookDetailOpen = ref(false)
  const client = ref<ClientDetailDto | null>(null)
  const loading = ref(false)
  const notFound = ref(false)
  const loadError = ref(false)
  const saving = ref(false)
  const saveError = ref('')
  const showDeleteDialog = ref(false)
  const editedDisplayName = ref('')
  const editedDefaultReviewPipelineProfileId = ref('file-by-file-balanced')
  const editedScmCommentPostingEnabled = ref(true)
  const editedEnableEvidenceBackedVerification = ref(false)
  const editedEnableMultiPassUnion = ref(false)
  const editedIncludeLinkedItemsInContext = ref(true)
  const editedEnableLanguageRobustScreening = ref(false)
  const editedReviewPasses = ref<ReviewPassEntry[]>([])
  const editedBaselineReasoningEffort = ref<ReviewReasoningEffort>('none')
  const editedMonthlyBudgetSoftCapUsd = ref('')
  const editedMonthlyBudgetHardCapUsd = ref('')
  const editedPullRequestBudgetSoftCapUsd = ref('')
  const editedPullRequestBudgetHardCapUsd = ref('')
  const editedIncrementBudgetSoftCapUsd = ref('')
  const editedIncrementBudgetHardCapUsd = ref('')
  const reviewProfiles = ref<ReviewProfileCatalogItemDto[]>([])
  const clientReviewProfile = ref<ClientReviewProfileDto | null>(null)

  const canManageClient = computed(() => hasClientRole(clientId, 1))
  const canViewClient = computed(() => hasClientRole(clientId, 0))
  const isProCursorTokenUsageReportingEnabled =
    import.meta.env.VITE_FEATURE_PROCURSOR_TOKEN_USAGE_REPORTING !== 'false'
  const providerUpgradeMessage = computed(
    () => getCapability('multiple-scm-providers')?.message ?? '',
  )
  const crawlConfigsCapability = computed(() => getCapability('crawl-configs'))
  const isCrawlConfigsAvailable = computed(
    () => crawlConfigsCapability.value?.isAvailable === true,
  )
  const budgetingCapability = computed(() => getCapability('budgeting'))
  const isBudgetingAvailable = computed(() => budgetingCapability.value?.isAvailable === true)
  const budgetingUpgradeMessage = computed(() => budgetingCapability.value?.message ?? '')
  const isUsageTabAvailable = computed(() => isProCursorTokenUsageReportingEnabled)
  const availableTabs = computed<DetailTab[]>(() => {
    const tabs: DetailTab[] = ['history']

    if (canManageClient.value) {
      return [...detailTabs]
    }

    if (isUsageTabAvailable.value) {
      tabs.push('usage')
    }

    return tabs
  })
  const defaultDetailTab = computed<DetailTab>(() =>
    canManageClient.value ? 'config' : 'history',
  )
  const activeTab = ref<DetailTab>(defaultDetailTab.value)

  function applyClient(nextClient: ClientDetailDto | null | undefined): void {
    // A failed save (e.g. a 400) resolves with `data === undefined` under openapi-fetch. Never assign a
    // falsy client: doing so corrupts `client.value` and the next render dereferences `client.value.…`,
    // blanking the whole page. Callers that see no data surface an error and skip this apply.
    if (!nextClient) {
      return
    }

    client.value = nextClient
    editedDisplayName.value = nextClient.displayName
    editedDefaultReviewPipelineProfileId.value = nextClient.defaultReviewPipelineProfileId ?? 'file-by-file-balanced'
    editedScmCommentPostingEnabled.value = Boolean(nextClient.scmCommentPostingEnabled)
    editedEnableEvidenceBackedVerification.value = Boolean(nextClient.enableEvidenceBackedVerification)
    editedEnableMultiPassUnion.value = Boolean(nextClient.enableMultiPassUnion)
    editedIncludeLinkedItemsInContext.value = Boolean(nextClient.includeLinkedItemsInContext)
    editedEnableLanguageRobustScreening.value = Boolean(nextClient.enableLanguageRobustScreening)
    editedReviewPasses.value = normalizeReviewPasses(nextClient.reviewPasses)
    editedBaselineReasoningEffort.value = nextClient.baselineReasoningEffort ?? 'none'
    editedMonthlyBudgetSoftCapUsd.value = capToInput(nextClient.budgetConfig?.monthlySoftCapUsd)
    editedMonthlyBudgetHardCapUsd.value = capToInput(nextClient.budgetConfig?.monthlyHardCapUsd)
    editedPullRequestBudgetSoftCapUsd.value = capToInput(nextClient.budgetConfig?.pullRequestSoftCapUsd)
    editedPullRequestBudgetHardCapUsd.value = capToInput(nextClient.budgetConfig?.pullRequestHardCapUsd)
    editedIncrementBudgetSoftCapUsd.value = capToInput(nextClient.budgetConfig?.incrementSoftCapUsd)
    editedIncrementBudgetHardCapUsd.value = capToInput(nextClient.budgetConfig?.incrementHardCapUsd)
  }

  function applyClientReviewProfile(nextProfile: ClientReviewProfileDto): void {
    clientReviewProfile.value = nextProfile
    editedDefaultReviewPipelineProfileId.value = nextProfile.defaultReviewPipelineProfileId
  }

  function syncActiveTabFromRoute() {
    const requestedTab =
      typeof route.query?.tab === 'string' ? route.query.tab : null
    if (
      requestedTab &&
      isDetailTab(requestedTab) &&
      availableTabs.value.includes(requestedTab)
    ) {
      activeTab.value = requestedTab
      return
    }

    activeTab.value = defaultDetailTab.value
  }

  syncActiveTabFromRoute()

  async function loadClient(): Promise<void> {
    loading.value = true
    notFound.value = false
    loadError.value = false
    try {
      const { data, response } = await getClientFn(clientId)
      if (response && (response as Response).status === 404) {
        notFound.value = true
        router.push({ name: 'clients' })
        return
      }
      if (!data) {
        // A non-404 error response — openapi-fetch resolves non-2xx with data: undefined rather than
        // throwing — is a load failure, not a missing record. Surface it and keep the user on the page
        // instead of masquerading as "not found" and redirecting away.
        loadError.value = true
        return
      }
      applyClient(data as ClientDetailDto)

      const [catalogResult, profileResult] = await Promise.allSettled([
        getReviewProfilesFn(),
        getClientReviewProfileFn(clientId),
      ])

      reviewProfiles.value = catalogResult.status === 'fulfilled'
        ? (catalogResult.value.data?.profiles ?? []) as ReviewProfileCatalogItemDto[]
        : []

      if (profileResult.status === 'fulfilled' && profileResult.value.data) {
        applyClientReviewProfile(profileResult.value.data as ClientReviewProfileDto)
      }
    } catch {
      // Network/transport failure (or a thrown non-2xx) — a load error, not a missing record. Do not
      // redirect: keep the user on the page so a retry is possible.
      loadError.value = true
    } finally {
      loading.value = false
    }
  }

  if (autoLoad) {
    onMounted(loadClient)
  }

  watch(
    () => route.query?.tab,
    () => {
      syncActiveTabFromRoute()
    },
  )

  watch(availableTabs, () => {
    if (!availableTabs.value.includes(activeTab.value)) {
      activeTab.value = defaultDetailTab.value
    }
  })

  watch(activeTab, (tab) => {
    const nextTab = tab === 'config' ? undefined : tab
    const currentTab =
      typeof route.query?.tab === 'string' ? route.query.tab : undefined

    if (currentTab === nextTab) {
      return
    }

    const nextQuery = { ...(route.query ?? {}) }
    if (nextTab) {
      nextQuery.tab = nextTab
    } else {
      delete nextQuery.tab
    }

    if (typeof router.replace === 'function') {
      router.replace({ query: nextQuery })
      return
    }

    router.push({ query: nextQuery })
  })

  function handleOverviewNavigate(tab: string) {
    if (!isDetailTab(tab)) {
      return
    }

    if (
      !availableTabs.value.includes(tab) ||
      (tab === 'usage' && !isUsageTabAvailable.value)
    ) {
      return
    }

    activeTab.value = tab
  }

  async function saveDisplayName() {
    if (!canManageClient.value || !client.value) return
    saving.value = true
    saveError.value = ''
    try {
      const result = await patchClientFn(clientId, { displayName: editedDisplayName.value })
      if (isFailedPatch(result)) {
        saveError.value = extractValidationMessage(result.error, 'Failed to save.')
        return
      }
      applyClient(result.data as ClientDetailDto | null | undefined)
    } catch {
      saveError.value = 'Failed to save.'
    } finally {
      saving.value = false
    }
  }

  async function toggleStatus() {
    if (!canManageClient.value || !client.value) return
    saving.value = true
    saveError.value = ''
    try {
      const result = await patchClientFn(clientId, { isActive: !client.value.isActive })
      if (isFailedPatch(result)) {
        saveError.value = extractValidationMessage(result.error, 'Failed to update status.')
        return
      }
      applyClient(result.data as ClientDetailDto | null | undefined)
    } catch {
      saveError.value = 'Failed to update status.'
    } finally {
      saving.value = false
    }
  }

  async function saveAdvancedSettings() {
    if (!canManageClient.value || !client.value) return
    saving.value = true
    saveError.value = ''
    try {
      const patchBody: Record<string, unknown> = {
        scmCommentPostingEnabled: editedScmCommentPostingEnabled.value,
        enableEvidenceBackedVerification: editedEnableEvidenceBackedVerification.value,
        enableMultiPassUnion: editedEnableMultiPassUnion.value,
        includeLinkedItemsInContext: editedIncludeLinkedItemsInContext.value,
        enableLanguageRobustScreening: editedEnableLanguageRobustScreening.value,
        baselineReasoningEffort: editedBaselineReasoningEffort.value,
      }

      // The review-pass list is edited on the AI Connections tab but shares this save path with the System tab.
      // Send it only when it actually changed, so saving an unrelated System-tab toggle can never clobber a
      // concurrently-edited (or still-loading) pass list with a stale value.
      const normalizedReviewPasses = normalizeReviewPasses(editedReviewPasses.value)
      if (!reviewPassesEqual(normalizedReviewPasses, normalizeReviewPasses(client.value.reviewPasses))) {
        patchBody.reviewPasses = normalizedReviewPasses
      }

      const result = await patchClientFn(clientId, patchBody)
      if (isFailedPatch(result)) {
        // Surface the server's validation message (e.g. a duplicate configured-model rejection) so the user
        // sees why the save was refused instead of a blank page.
        saveError.value = extractValidationMessage(result.error, 'Failed to save review publication setting.')
        return
      }
      applyClient(result.data as ClientDetailDto | null | undefined)
    } catch {
      saveError.value = 'Failed to save review publication setting.'
    } finally {
      saving.value = false
    }
  }

  function currentEditedBudgetConfig(): BudgetConfig {
    return {
      monthlySoftCapUsd: capFromInput(editedMonthlyBudgetSoftCapUsd.value),
      monthlyHardCapUsd: capFromInput(editedMonthlyBudgetHardCapUsd.value),
      pullRequestSoftCapUsd: capFromInput(editedPullRequestBudgetSoftCapUsd.value),
      pullRequestHardCapUsd: capFromInput(editedPullRequestBudgetHardCapUsd.value),
      incrementSoftCapUsd: capFromInput(editedIncrementBudgetSoftCapUsd.value),
      incrementHardCapUsd: capFromInput(editedIncrementBudgetHardCapUsd.value),
    }
  }

  async function saveBudgetConfig() {
    if (!canManageClient.value || !client.value) return
    saving.value = true
    saveError.value = ''
    try {
      // The budget config is patched as a whole, so a blank field (null) clears that individual cap.
      const result = await patchClientFn(clientId, { budgetConfig: currentEditedBudgetConfig() })
      if (isFailedPatch(result)) {
        saveError.value = extractValidationMessage(result.error, 'Failed to save budget.')
        return
      }
      applyClient(result.data as ClientDetailDto | null | undefined)
    } catch {
      saveError.value = 'Failed to save budget.'
    } finally {
      saving.value = false
    }
  }

  async function saveReviewProfile() {
    if (!canManageClient.value || !client.value) return
    saving.value = true
    saveError.value = ''
    try {
      const requestedProfileId = reviewProfiles.value.some((profile) => profile.profileId === editedDefaultReviewPipelineProfileId.value)
        ? editedDefaultReviewPipelineProfileId.value
        : null
      const { data } = await putClientReviewProfileFn(clientId, {
        defaultReviewPipelineProfileId: requestedProfileId,
      })
      applyClientReviewProfile(data as ClientReviewProfileDto)
      client.value = {
        ...client.value,
        defaultReviewPipelineProfileId: (data as ClientReviewProfileDto).defaultReviewPipelineProfileId,
        defaultReviewPipelineProfileUpdatedAtUtc: (data as ClientReviewProfileDto).updatedAtUtc ?? null,
      }
    } catch {
      saveError.value = 'Failed to save review profile.'
    } finally {
      saving.value = false
    }
  }

  function isAdvancedSettingsButtonEnabled(): boolean {
    return (
      !saving.value &&
      client.value !== null &&
      (
        editedScmCommentPostingEnabled.value !== Boolean(client.value.scmCommentPostingEnabled) ||
        editedEnableEvidenceBackedVerification.value !== Boolean(client.value.enableEvidenceBackedVerification) ||
        editedEnableMultiPassUnion.value !== Boolean(client.value.enableMultiPassUnion) ||
        editedIncludeLinkedItemsInContext.value !== Boolean(client.value.includeLinkedItemsInContext) ||
        editedEnableLanguageRobustScreening.value !== Boolean(client.value.enableLanguageRobustScreening) ||
        editedBaselineReasoningEffort.value !== (client.value.baselineReasoningEffort ?? 'none') ||
        !reviewPassesEqual(editedReviewPasses.value, normalizeReviewPasses(client.value.reviewPasses))
      )
    )
  }

  function isBudgetButtonEnabled(): boolean {
    if (saving.value || client.value === null) {
      return false
    }
    const stored = client.value.budgetConfig ?? {}
    const edited = currentEditedBudgetConfig()
    return (
      edited.monthlySoftCapUsd !== (stored.monthlySoftCapUsd ?? null) ||
      edited.monthlyHardCapUsd !== (stored.monthlyHardCapUsd ?? null) ||
      edited.pullRequestSoftCapUsd !== (stored.pullRequestSoftCapUsd ?? null) ||
      edited.pullRequestHardCapUsd !== (stored.pullRequestHardCapUsd ?? null) ||
      edited.incrementSoftCapUsd !== (stored.incrementSoftCapUsd ?? null) ||
      edited.incrementHardCapUsd !== (stored.incrementHardCapUsd ?? null)
    )
  }

  function isReviewProfileButtonEnabled(): boolean {
    return (
      !saving.value &&
      clientReviewProfile.value !== null &&
      editedDefaultReviewPipelineProfileId.value !== clientReviewProfile.value.defaultReviewPipelineProfileId
    )
  }

  async function handleDelete() {
    if (!canManageClient.value) return
    try {
      await deleteClientFn(clientId)
      router.push({ name: 'clients' })
    } catch {
      router.push({ name: 'clients' })
    }
  }

  return {
    name: 'useClientDetailViewModel',
    clientId,
    client,
    loading,
    notFound,
    loadError,
    saving,
    saveError,
    showDeleteDialog,
    editedDisplayName,
    editedDefaultReviewPipelineProfileId,
    editedScmCommentPostingEnabled,
    editedEnableEvidenceBackedVerification,
    editedEnableMultiPassUnion,
    editedIncludeLinkedItemsInContext,
    editedEnableLanguageRobustScreening,
    editedReviewPasses,
    editedBaselineReasoningEffort,
    editedMonthlyBudgetSoftCapUsd,
    editedMonthlyBudgetHardCapUsd,
    editedPullRequestBudgetSoftCapUsd,
    editedPullRequestBudgetHardCapUsd,
    editedIncrementBudgetSoftCapUsd,
    editedIncrementBudgetHardCapUsd,
    reviewProfiles,
    clientReviewProfile,
    isProviderDetailOpen,
    isWebhookDetailOpen,
    canManageClient,
    canViewClient,
    availableTabs,
    activeTab,
    providerUpgradeMessage,
    isCrawlConfigsAvailable,
    isBudgetingAvailable,
    budgetingUpgradeMessage,
    isUsageTabAvailable,
    loadClient,
    saveDisplayName,
    toggleStatus,
    saveAdvancedSettings,
    saveReviewProfile,
    saveBudgetConfig,
    isAdvancedSettingsButtonEnabled,
    isBudgetButtonEnabled,
    isReviewProfileButtonEnabled,
    handleDelete,
    handleOverviewNavigate,
  }
}
