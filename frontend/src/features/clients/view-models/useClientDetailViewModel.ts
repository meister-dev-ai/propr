// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

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
  'history',
  'dismissals',
  'prompt-overrides',
  'usage',
] as const

export type DetailTab = (typeof detailTabs)[number]
type ReviewStrategy = components['schemas']['ReviewStrategy']

export interface ClientDetailDto {
  id: string
  displayName: string
  isActive: boolean
  createdAt: string
  defaultReviewStrategy?: ReviewStrategy
  defaultReviewPipelineProfileId?: string | null
  defaultReviewPipelineProfileUpdatedAtUtc?: string | null
  scmCommentPostingEnabled: boolean
  enableProRV: boolean
  enableEvidenceBackedVerification: boolean
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
  saving: Ref<boolean>
  saveError: Ref<string>
  showDeleteDialog: Ref<boolean>
  editedDisplayName: Ref<string>
  editedDefaultReviewStrategy: Ref<ReviewStrategy>
  editedDefaultReviewPipelineProfileId: Ref<string>
  editedScmCommentPostingEnabled: Ref<boolean>
  editedEnableProRV: Ref<boolean>
  editedEnableEvidenceBackedVerification: Ref<boolean>
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
  isUsageTabAvailable: ComputedRef<boolean>
  loadClient: () => Promise<void>
  saveDisplayName: () => Promise<void>
  toggleStatus: () => Promise<void>
  saveAdvancedSettings: () => Promise<void>
  saveReviewProfile: () => Promise<void>
  isAdvancedSettingsButtonEnabled: () => boolean
  isReviewProfileButtonEnabled: () => boolean
  handleDelete: () => Promise<void>
  handleOverviewNavigate: (tab: string) => void
}

/** Injection key so sub-tab components (e.g. ClientSystemTab) share the parent
 * view's single view-model instance instead of re-instantiating it. */
export const ClientDetailVmKey: InjectionKey<ClientDetailViewModel> = Symbol('clientDetailVm')

export interface ClientDetailService {
  getClient: (clientId: string) => Promise<{ data: ClientDetailDto | null; response?: Response | { status?: number; ok?: boolean } }>
  patchClient: (clientId: string, body: Record<string, unknown>) => Promise<{ data: ClientDetailDto }>
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
  const saving = ref(false)
  const saveError = ref('')
  const showDeleteDialog = ref(false)
  const editedDisplayName = ref('')
  const editedDefaultReviewStrategy = ref<ReviewStrategy>('fileByFile')
  const editedDefaultReviewPipelineProfileId = ref('file-by-file-balanced')
  const editedScmCommentPostingEnabled = ref(true)
  const editedEnableProRV = ref(false)
  const editedEnableEvidenceBackedVerification = ref(false)
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

  function applyClient(nextClient: ClientDetailDto): void {
    client.value = nextClient
    editedDisplayName.value = nextClient.displayName
    editedDefaultReviewStrategy.value = nextClient.defaultReviewStrategy ?? 'fileByFile'
    editedDefaultReviewPipelineProfileId.value = nextClient.defaultReviewPipelineProfileId ?? 'file-by-file-balanced'
    editedScmCommentPostingEnabled.value = Boolean(nextClient.scmCommentPostingEnabled)
    editedEnableProRV.value = Boolean(nextClient.enableProRV)
    editedEnableEvidenceBackedVerification.value = Boolean(nextClient.enableEvidenceBackedVerification)
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
    try {
      const { data, response } = await getClientFn(clientId)
      if (response && (response as Response).status === 404) {
        notFound.value = true
        router.push({ name: 'clients' })
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
      notFound.value = true
      router.push({ name: 'clients' })
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
      const { data } = await patchClientFn(clientId, { displayName: editedDisplayName.value })
      applyClient(data as ClientDetailDto)
    } catch {
      saveError.value = 'Failed to save.'
    } finally {
      saving.value = false
    }
  }

  async function toggleStatus() {
    if (!canManageClient.value || !client.value) return
    saving.value = true
    try {
      const { data } = await patchClientFn(clientId, { isActive: !client.value.isActive })
      applyClient(data as ClientDetailDto)
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
      const { data } = await patchClientFn(clientId, {
        defaultReviewStrategy: editedDefaultReviewStrategy.value,
        scmCommentPostingEnabled: editedScmCommentPostingEnabled.value,
        enableProRV: editedEnableProRV.value,
        enableEvidenceBackedVerification: editedEnableEvidenceBackedVerification.value,
      })
      applyClient(data as ClientDetailDto)
    } catch {
      saveError.value = 'Failed to save review publication setting.'
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
        editedEnableProRV.value !== Boolean(client.value.enableProRV) ||
        editedEnableEvidenceBackedVerification.value !== Boolean(client.value.enableEvidenceBackedVerification) ||
        editedDefaultReviewStrategy.value !== (client.value.defaultReviewStrategy ?? 'fileByFile')
      )
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
    saving,
    saveError,
    showDeleteDialog,
    editedDisplayName,
    editedDefaultReviewStrategy,
    editedDefaultReviewPipelineProfileId,
    editedScmCommentPostingEnabled,
    editedEnableProRV,
    editedEnableEvidenceBackedVerification,
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
    isUsageTabAvailable,
    loadClient,
    saveDisplayName,
    toggleStatus,
    saveAdvancedSettings,
    saveReviewProfile,
    isAdvancedSettingsButtonEnabled,
    isReviewProfileButtonEnabled,
    handleDelete,
    handleOverviewNavigate,
  }
}
