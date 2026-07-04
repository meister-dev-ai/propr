import { computed, onMounted, ref, type ComputedRef, type Ref } from 'vue'
import { useRouter } from 'vue-router'
import { UnauthorizedError } from '@/services/api'
import { ApiRequestError } from '@/services/userSecurityService'
import { useNotification } from '@/composables/useNotification'
import { useSession } from '@/composables/useSession'
import {
  createTenant,
  listTenants,
  type CreateTenantRequest,
  type TenantDto,
} from '@/services/tenantAdminService'
import {
  empty,
  error as errorState,
  loading as loadingState,
  ready,
  saving,
  success,
  type UiState,
} from '@/types/uiState'

export interface TenantDirectoryViewModelData {
  tenants: TenantDto[]
}

export interface TenantDirectoryViewModel {
  readonly name: 'useTenantDirectoryViewModel'
  state: Ref<UiState<TenantDirectoryViewModelData, string>>
  tenants: Ref<TenantDto[]>
  creating: Ref<boolean>
  createError: Ref<string>
  canCreateTenants: ComputedRef<boolean>
  isLoading: ComputedRef<boolean>
  loadError: ComputedRef<string>
  loadTenants: () => Promise<void>
  handleCreateTenant: (request: CreateTenantRequest) => Promise<void>
  buildClientBootstrapRoute: (tenantId: string) => { name: 'clients'; query: { create: 'true'; tenantId: string } }
  canCreateClientForTenant: (tenantId: string) => boolean
  isTenantEditable: (tenant: TenantDto) => boolean
}

export interface TenantDirectoryService {
  listTenants: typeof listTenants
  createTenant: typeof createTenant
}

export interface UseTenantDirectoryViewModelOptions {
  tenantDirectoryService?: Partial<TenantDirectoryService>
  autoLoad?: boolean
}

function buildClientBootstrapRoute(tenantId: string) {
  return {
    name: 'clients' as const,
    query: {
      create: 'true' as const,
      tenantId,
    },
  }
}

export function useTenantDirectoryViewModel(options: UseTenantDirectoryViewModelOptions = {}): TenantDirectoryViewModel {
  const router = useRouter()
  const { notify } = useNotification()
  const { isAdmin, hasTenantRole, edition } = useSession()
  const listTenantsFn = options.tenantDirectoryService?.listTenants ?? listTenants
  const createTenantFn = options.tenantDirectoryService?.createTenant ?? createTenant
  const autoLoad = options.autoLoad ?? true

  const state = ref<UiState<TenantDirectoryViewModelData, string>>(loadingState('Loading tenants...'))
  const tenants = ref<TenantDto[]>([])
  const creating = ref(false)
  const createError = ref('')

  const canCreateTenants = computed(() => isAdmin.value && edition.value !== 'community')
  const isLoading = computed(() => state.value.status === 'loading')
  const loadError = computed(() => state.value.status === 'error' ? state.value.message ?? '' : '')

  async function loadTenants(): Promise<void> {
    state.value = loadingState('Loading tenants...')

    try {
      tenants.value = await listTenantsFn()
      state.value = tenants.value.length === 0
        ? empty('No tenant administration access is currently assigned to your account.')
        : ready({ tenants: tenants.value })
    } catch (err) {
      if (err instanceof UnauthorizedError) {
        await router.push({ name: 'login' })
        return
      }
      if (err instanceof ApiRequestError) {
        state.value = errorState(err.message)
        return
      }
      state.value = errorState('Failed to load visible tenants.')
    }
  }

  async function handleCreateTenant(request: CreateTenantRequest): Promise<void> {
    creating.value = true
    createError.value = ''
    state.value = saving({ tenants: tenants.value }, 'Creating tenant...')

    try {
      const created = await createTenantFn(request)
      tenants.value = [...tenants.value, created]
      state.value = success({ tenants: tenants.value }, 'Tenant created.')
      notify('Tenant created.')
      await router.push(buildClientBootstrapRoute(created.id))
    } catch (err) {
      if (err instanceof UnauthorizedError) {
        await router.push({ name: 'login' })
        return
      }
      createError.value = err instanceof ApiRequestError
        ? err.message
        : 'Failed to create tenant.'
      state.value = errorState(createError.value)
    } finally {
      creating.value = false
    }
  }

  function canCreateClientForTenant(tenantId: string): boolean {
    return isAdmin.value || hasTenantRole(tenantId, 1)
  }

  function isTenantEditable(tenant: TenantDto): boolean {
    return tenant.isEditable !== false
  }

  if (autoLoad) {
    onMounted(loadTenants)
  }

  return {
    name: 'useTenantDirectoryViewModel',
    state,
    tenants,
    creating,
    createError,
    canCreateTenants,
    isLoading,
    loadError,
    loadTenants,
    handleCreateTenant,
    buildClientBootstrapRoute,
    canCreateClientForTenant,
    isTenantEditable,
  }
}
