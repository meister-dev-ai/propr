import { computed, onMounted, reactive, ref, type ComputedRef, type Ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { UnauthorizedError } from '@/services/api'
import { ApiRequestError } from '@/services/userSecurityService'
import { useNotification } from '@/composables/useNotification'
import {
  deleteTenantMembership,
  listTenantMemberships,
  type TenantMembershipDto,
  type TenantRole,
  updateTenantMembership,
} from '@/services/tenantMembershipService'
import {
  empty,
  error as errorState,
  loading as loadingState,
  ready,
  saving,
  success,
  type UiState,
} from '@/types/uiState'

const SYSTEM_TENANT_ID = '11111111-1111-1111-1111-111111111111'

export interface TenantMembersViewModelData {
  memberships: TenantMembershipDto[]
}

export interface TenantMembersViewModel {
  readonly name: 'useTenantMembersViewModel'
  tenantId: string
  state: Ref<UiState<TenantMembersViewModelData, string>>
  memberships: Ref<TenantMembershipDto[]>
  updatingMembershipId: Ref<string | null>
  deletingMembershipId: Ref<string | null>
  loadError: ComputedRef<string>
  memberError: Ref<string>
  editableRoles: Record<string, TenantMembershipDto['role']>
  isSystemTenant: ComputedRef<boolean>
  isLoading: ComputedRef<boolean>
  loadMemberships: () => Promise<void>
  saveMembershipRole: (membershipId: string) => Promise<void>
  removeMembership: (membershipId: string) => Promise<void>
  formatRole: (role: TenantRole) => string
}

export interface TenantMembersService {
  listTenantMemberships: typeof listTenantMemberships
  updateTenantMembership: typeof updateTenantMembership
  deleteTenantMembership: typeof deleteTenantMembership
}

export interface UseTenantMembersViewModelOptions {
  tenantMembersService?: Partial<TenantMembersService>
  autoLoad?: boolean
  tenantId?: string
}

function formatRole(role: TenantRole): string {
  return role === 'tenantAdministrator' ? 'Tenant Administrator' : 'Tenant User'
}

export function useTenantMembersViewModel(options: UseTenantMembersViewModelOptions = {}): TenantMembersViewModel {
  const route = useRoute()
  const router = useRouter()
  const { notify } = useNotification()
  const tenantId = options.tenantId ?? String(route.params.tenantId ?? '')
  const listTenantMembershipsFn = options.tenantMembersService?.listTenantMemberships ?? listTenantMemberships
  const updateTenantMembershipFn = options.tenantMembersService?.updateTenantMembership ?? updateTenantMembership
  const deleteTenantMembershipFn = options.tenantMembersService?.deleteTenantMembership ?? deleteTenantMembership
  const autoLoad = options.autoLoad ?? true

  const state = ref<UiState<TenantMembersViewModelData, string>>(loadingState('Loading tenant members...'))
  const memberships = ref<TenantMembershipDto[]>([])
  const updatingMembershipId = ref<string | null>(null)
  const deletingMembershipId = ref<string | null>(null)
  const memberError = ref('')
  const editableRoles = reactive<Record<string, TenantMembershipDto['role']>>({})

  const isSystemTenant = computed(() => tenantId === SYSTEM_TENANT_ID)
  const isLoading = computed(() => state.value.status === 'loading')
  const loadError = computed(() => state.value.status === 'error' ? state.value.message ?? '' : '')

  async function loadMemberships(): Promise<void> {
    state.value = loadingState('Loading tenant members...')
    memberError.value = ''

    try {
      const loaded = await listTenantMembershipsFn(tenantId)
      memberships.value = loaded
      syncEditableRoles(loaded)
      state.value = loaded.length === 0
        ? empty('No tenant members are assigned yet.')
        : ready({ memberships: loaded })
    } catch (err) {
      if (err instanceof UnauthorizedError) {
        await router.push({ name: 'login' })
        return
      }
      state.value = errorState(err instanceof ApiRequestError ? err.message : 'Failed to load tenant memberships.')
    }
  }

  async function saveMembershipRole(membershipId: string): Promise<void> {
    const nextRole = editableRoles[membershipId]
    const existing = memberships.value.find((membership) => membership.id === membershipId)
    if (!existing || existing.role === nextRole) {
      return
    }

    updatingMembershipId.value = membershipId
    memberError.value = ''
    state.value = saving({ memberships: memberships.value }, 'Saving tenant membership...')

    try {
      const updated = await updateTenantMembershipFn(tenantId, membershipId, { role: nextRole })
      memberships.value = memberships.value.map((membership) => membership.id === membershipId ? updated : membership)
      editableRoles[membershipId] = updated.role
      state.value = success({ memberships: memberships.value }, 'Tenant membership updated.')
      notify('Tenant membership updated.')
    } catch (err) {
      if (err instanceof UnauthorizedError) {
        await router.push({ name: 'login' })
        return
      }
      memberError.value = err instanceof ApiRequestError ? err.message : 'Failed to update tenant membership.'
      editableRoles[membershipId] = existing.role
      state.value = errorState(memberError.value)
    } finally {
      updatingMembershipId.value = null
    }
  }

  async function removeMembership(membershipId: string): Promise<void> {
    deletingMembershipId.value = membershipId
    memberError.value = ''
    state.value = saving({ memberships: memberships.value }, 'Removing tenant membership...')

    try {
      await deleteTenantMembershipFn(tenantId, membershipId)
      memberships.value = memberships.value.filter((membership) => membership.id !== membershipId)
      delete editableRoles[membershipId]
      state.value = memberships.value.length === 0
        ? empty('No tenant members are assigned yet.')
        : success({ memberships: memberships.value }, 'Tenant membership removed.')
      notify('Tenant membership removed.')
    } catch (err) {
      if (err instanceof UnauthorizedError) {
        await router.push({ name: 'login' })
        return
      }
      memberError.value = err instanceof ApiRequestError ? err.message : 'Failed to remove tenant membership.'
      state.value = errorState(memberError.value)
    } finally {
      deletingMembershipId.value = null
    }
  }

  function syncEditableRoles(items: TenantMembershipDto[]): void {
    for (const membership of items) {
      editableRoles[membership.id] = membership.role
    }
  }

  if (autoLoad) {
    onMounted(loadMemberships)
  }

  return {
    name: 'useTenantMembersViewModel',
    tenantId,
    state,
    memberships,
    updatingMembershipId,
    deletingMembershipId,
    loadError,
    memberError,
    editableRoles,
    isSystemTenant,
    isLoading,
    loadMemberships,
    saveMembershipRole,
    removeMembership,
    formatRole,
  }
}
