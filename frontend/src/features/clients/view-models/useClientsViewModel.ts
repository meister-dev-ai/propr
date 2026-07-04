// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { computed, onMounted, ref, type ComputedRef, type Ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { UnauthorizedError, createAdminClient } from '@/services/api'
import { useSession } from '@/composables/useSession'
import {
  empty,
  error as errorState,
  loading as loadingState,
  ready,
  success,
  type UiState,
} from '@/types/uiState'
import { listTenants, type TenantDto } from '@/services/tenantAdminService'

export interface ClientListItem {
  id: string
  displayName: string
  isActive: boolean
  createdAt: string
  recentUsageTokens?: number
  tenantId?: string | null
  tenantSlug?: string | null
  tenantDisplayName?: string | null
}

export interface ClientsViewModelData {
  clients: ClientListItem[]
  visibleTenants: TenantDto[]
}

export interface ClientsViewModel {
  readonly name: 'useClientsViewModel'
  state: Ref<UiState<ClientsViewModelData, string>>
  clients: Ref<ClientListItem[]>
  visibleTenants: Ref<TenantDto[]>
  filter: Ref<string>
  tenantFilterId: Ref<string>
  showCreateForm: Ref<boolean>
  manageableTenants: ComputedRef<TenantDto[]>
  canCreateClients: ComputedRef<boolean>
  initialTenantId: ComputedRef<string>
  isLoading: ComputedRef<boolean>
  loadError: ComputedRef<string>
  loadClients: () => Promise<void>
  openCreateForm: () => void
  closeCreateForm: () => void
  onClientCreated: (client: unknown) => Promise<void>
}

export interface ClientsService {
  listClients: () => Promise<ClientListItem[]>
  listTenants: typeof listTenants
}

export interface UseClientsViewModelOptions {
  clientsService?: Partial<ClientsService>
  autoLoad?: boolean
}

async function defaultListClients(): Promise<ClientListItem[]> {
  const { data, response } = await createAdminClient().GET('/clients', {})
  if (!response.ok) {
    throw new Error('Failed to load clients.')
  }

  return (data as ClientListItem[]) ?? []
}

export function useClientsViewModel(options: UseClientsViewModelOptions = {}): ClientsViewModel {
  const route = useRoute()
  const router = useRouter()
  const { isAdmin, tenantRoles } = useSession()
  const listClientsFn = options.clientsService?.listClients ?? defaultListClients
  const listTenantsFn = options.clientsService?.listTenants ?? listTenants
  const autoLoad = options.autoLoad ?? true

  const state = ref<UiState<ClientsViewModelData, string>>(loadingState('Loading clients...'))
  const clients = ref<ClientListItem[]>([])
  const visibleTenants = ref<TenantDto[]>([])
  const filter = ref('')
  const tenantFilterId = ref('')
  const showCreateForm = ref(false)

  const hasAnyTenantAccess = computed(() => Object.keys(tenantRoles.value).length > 0)
  const manageableTenants = computed(() =>
    visibleTenants.value.filter((tenant) => isAdmin.value || tenantRoles.value[tenant.id] >= 1),
  )
  const canCreateClients = computed(() => isAdmin.value || manageableTenants.value.length > 0)
  const initialTenantId = computed(() => typeof route.query.tenantId === 'string' ? route.query.tenantId : '')
  const isLoading = computed(() => state.value.status === 'loading')
  const loadError = computed(() => state.value.status === 'error' ? state.value.message ?? '' : '')

  async function loadClients(): Promise<void> {
    state.value = loadingState('Loading clients...')

    try {
      const [loadedClients, tenants] = await Promise.all([
        listClientsFn(),
        isAdmin.value || hasAnyTenantAccess.value ? listTenantsFn() : Promise.resolve([]),
      ])

      clients.value = loadedClients
      visibleTenants.value = tenants

      if (route.query.create === 'true' && canCreateClients.value) {
        showCreateForm.value = true
      }

      if (loadedClients.length === 0) {
        const emptyMessage = canCreateClients.value
          ? 'No clients yet.'
          : 'No clients are visible to your current tenant or client memberships.'
        state.value = empty(emptyMessage)
      } else {
        state.value = ready({ clients: loadedClients, visibleTenants: tenants })
      }
    } catch (err) {
      if (err instanceof UnauthorizedError) {
        await router.push({ name: 'login' })
        return
      }

      state.value = errorState('Failed to load clients.')
    }
  }

  function openCreateForm(): void {
    if (canCreateClients.value) {
      showCreateForm.value = true
    }
  }

  function closeCreateForm(): void {
    showCreateForm.value = false
  }

  async function onClientCreated(client: unknown): Promise<void> {
    clients.value = [client as ClientListItem, ...clients.value]
    showCreateForm.value = false
    state.value = success({ clients: clients.value, visibleTenants: visibleTenants.value }, 'Client created.')

    if (route.query.create === 'true' || typeof route.query.tenantId === 'string') {
      await router.replace({ name: 'clients', query: {} })
    }
  }

  if (autoLoad) {
    onMounted(loadClients)
  }

  return {
    name: 'useClientsViewModel',
    state,
    clients,
    visibleTenants,
    filter,
    tenantFilterId,
    showCreateForm,
    manageableTenants,
    canCreateClients,
    initialTenantId,
    isLoading,
    loadError,
    loadClients,
    openCreateForm,
    closeCreateForm,
    onClientCreated,
  }
}
