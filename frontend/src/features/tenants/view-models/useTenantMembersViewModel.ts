// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

import { computed, onMounted, reactive, ref, type ComputedRef, type Ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { UnauthorizedError } from '@/services/api'
import { TenantApiError } from '@/services/tenantApiClient'
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
  assignMemberClientAccess,
  type ClientRole,
  listMemberClientAccess,
  listTenantClients,
  removeMemberClientAccess,
  type TenantClientSummaryDto,
  type TenantMemberClientAccessDto,
} from '@/services/tenantMemberClientAccessService'
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
  // Client access panel
  expandedMembershipId: Ref<string | null>
  accessError: Ref<string>
  accessBusyMembershipId: Ref<string | null>
  draftClientId: Record<string, string>
  draftRole: Record<string, ClientRole>
  toggleClientAccess: (membershipId: string) => Promise<void>
  clientAccessFor: (membershipId: string) => TenantMemberClientAccessDto[]
  assignableClientsFor: (membershipId: string) => TenantClientSummaryDto[]
  assignClientAccess: (membershipId: string) => Promise<void>
  removeClientAccess: (membershipId: string, clientId: string) => Promise<void>
  formatClientRole: (role: ClientRole) => string
}

export interface TenantMembersService {
  listTenantMemberships: typeof listTenantMemberships
  updateTenantMembership: typeof updateTenantMembership
  deleteTenantMembership: typeof deleteTenantMembership
  listTenantClients: typeof listTenantClients
  listMemberClientAccess: typeof listMemberClientAccess
  assignMemberClientAccess: typeof assignMemberClientAccess
  removeMemberClientAccess: typeof removeMemberClientAccess
}

export interface UseTenantMembersViewModelOptions {
  tenantMembersService?: Partial<TenantMembersService>
  autoLoad?: boolean
  tenantId?: string
}

function formatRole(role: TenantRole): string {
  return role === 'tenantAdministrator' ? 'Tenant Administrator' : 'Tenant User'
}

function formatClientRole(role: ClientRole): string {
  return role === 'clientAdministrator' ? 'Client Administrator' : 'Client User'
}

export function useTenantMembersViewModel(options: UseTenantMembersViewModelOptions = {}): TenantMembersViewModel {
  const route = useRoute()
  const router = useRouter()
  const { notify } = useNotification()
  const tenantId = options.tenantId ?? String(route.params.tenantId ?? '')
  const service = options.tenantMembersService ?? {}
  const listTenantMembershipsFn = service.listTenantMemberships ?? listTenantMemberships
  const updateTenantMembershipFn = service.updateTenantMembership ?? updateTenantMembership
  const deleteTenantMembershipFn = service.deleteTenantMembership ?? deleteTenantMembership
  const listTenantClientsFn = service.listTenantClients ?? listTenantClients
  const listMemberClientAccessFn = service.listMemberClientAccess ?? listMemberClientAccess
  const assignMemberClientAccessFn = service.assignMemberClientAccess ?? assignMemberClientAccess
  const removeMemberClientAccessFn = service.removeMemberClientAccess ?? removeMemberClientAccess
  const autoLoad = options.autoLoad ?? true

  const state = ref<UiState<TenantMembersViewModelData, string>>(loadingState('Loading tenant members...'))
  const memberships = ref<TenantMembershipDto[]>([])
  const updatingMembershipId = ref<string | null>(null)
  const deletingMembershipId = ref<string | null>(null)
  const memberError = ref('')
  const editableRoles = reactive<Record<string, TenantMembershipDto['role']>>({})

  const expandedMembershipId = ref<string | null>(null)
  const accessError = ref('')
  const accessBusyMembershipId = ref<string | null>(null)
  const clientAccess = reactive<Record<string, TenantMemberClientAccessDto[]>>({})
  const tenantClients = ref<TenantClientSummaryDto[]>([])
  const tenantClientsLoaded = ref(false)
  const draftClientId = reactive<Record<string, string>>({})
  const draftRole = reactive<Record<string, ClientRole>>({})

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
      if (err instanceof UnauthorizedError || (err instanceof TenantApiError && err.status === 401)) {
        await router.push({ name: 'login' })
        return
      }
      state.value = errorState((err instanceof TenantApiError || err instanceof ApiRequestError) ? err.message : 'Failed to load tenant memberships.')
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
      if (err instanceof UnauthorizedError || (err instanceof TenantApiError && err.status === 401)) {
        await router.push({ name: 'login' })
        return
      }
      memberError.value = (err instanceof TenantApiError || err instanceof ApiRequestError) ? err.message : 'Failed to update tenant membership.'
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
      delete clientAccess[membershipId]
      if (expandedMembershipId.value === membershipId) {
        expandedMembershipId.value = null
      }
      state.value = memberships.value.length === 0
        ? empty('No tenant members are assigned yet.')
        : success({ memberships: memberships.value }, 'Tenant membership removed.')
      notify('Tenant membership removed.')
    } catch (err) {
      if (err instanceof UnauthorizedError || (err instanceof TenantApiError && err.status === 401)) {
        await router.push({ name: 'login' })
        return
      }
      memberError.value = (err instanceof TenantApiError || err instanceof ApiRequestError) ? err.message : 'Failed to remove tenant membership.'
      state.value = errorState(memberError.value)
    } finally {
      deletingMembershipId.value = null
    }
  }

  async function toggleClientAccess(membershipId: string): Promise<void> {
    if (expandedMembershipId.value === membershipId) {
      expandedMembershipId.value = null
      return
    }

    expandedMembershipId.value = membershipId
    accessError.value = ''
    draftRole[membershipId] ??= 'clientUser'
    draftClientId[membershipId] ??= ''

    await ensureTenantClientsLoaded()
    if (!clientAccess[membershipId]) {
      await loadMemberAccess(membershipId)
    }
  }

  async function ensureTenantClientsLoaded(): Promise<void> {
    if (tenantClientsLoaded.value) {
      return
    }

    try {
      tenantClients.value = await listTenantClientsFn(tenantId)
      tenantClientsLoaded.value = true
    } catch (err) {
      if (err instanceof UnauthorizedError || (err instanceof TenantApiError && err.status === 401)) {
        await router.push({ name: 'login' })
        return
      }
      accessError.value = (err instanceof TenantApiError || err instanceof ApiRequestError) ? err.message : 'Failed to load tenant clients.'
    }
  }

  async function loadMemberAccess(membershipId: string): Promise<void> {
    accessBusyMembershipId.value = membershipId
    accessError.value = ''

    try {
      clientAccess[membershipId] = await listMemberClientAccessFn(tenantId, membershipId)
    } catch (err) {
      if (err instanceof UnauthorizedError || (err instanceof TenantApiError && err.status === 401)) {
        await router.push({ name: 'login' })
        return
      }
      accessError.value = (err instanceof TenantApiError || err instanceof ApiRequestError) ? err.message : 'Failed to load client access.'
    } finally {
      accessBusyMembershipId.value = null
    }
  }

  async function assignClientAccess(membershipId: string): Promise<void> {
    const clientId = draftClientId[membershipId]
    if (!clientId) {
      return
    }
    const role = draftRole[membershipId] ?? 'clientUser'

    accessBusyMembershipId.value = membershipId
    accessError.value = ''

    try {
      const assignment = await assignMemberClientAccessFn(tenantId, membershipId, { clientId, role })
      const current = clientAccess[membershipId] ?? []
      clientAccess[membershipId] = [
        ...current.filter((entry) => entry.clientId !== assignment.clientId),
        assignment,
      ].sort((left, right) => left.clientDisplayName.localeCompare(right.clientDisplayName))
      draftClientId[membershipId] = ''
      notify('Client access granted.')
    } catch (err) {
      if (err instanceof UnauthorizedError || (err instanceof TenantApiError && err.status === 401)) {
        await router.push({ name: 'login' })
        return
      }
      accessError.value = (err instanceof TenantApiError || err instanceof ApiRequestError) ? err.message : 'Failed to grant client access.'
    } finally {
      accessBusyMembershipId.value = null
    }
  }

  async function removeClientAccess(membershipId: string, clientId: string): Promise<void> {
    accessBusyMembershipId.value = membershipId
    accessError.value = ''

    try {
      await removeMemberClientAccessFn(tenantId, membershipId, clientId)
      clientAccess[membershipId] = (clientAccess[membershipId] ?? []).filter((entry) => entry.clientId !== clientId)
      notify('Client access revoked.')
    } catch (err) {
      if (err instanceof UnauthorizedError || (err instanceof TenantApiError && err.status === 401)) {
        await router.push({ name: 'login' })
        return
      }
      accessError.value = (err instanceof TenantApiError || err instanceof ApiRequestError) ? err.message : 'Failed to revoke client access.'
    } finally {
      accessBusyMembershipId.value = null
    }
  }

  function clientAccessFor(membershipId: string): TenantMemberClientAccessDto[] {
    return clientAccess[membershipId] ?? []
  }

  function assignableClientsFor(membershipId: string): TenantClientSummaryDto[] {
    const assignedIds = new Set(clientAccessFor(membershipId).map((entry) => entry.clientId))
    return tenantClients.value.filter((client) => !assignedIds.has(client.id))
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
    expandedMembershipId,
    accessError,
    accessBusyMembershipId,
    draftClientId,
    draftRole,
    toggleClientAccess,
    clientAccessFor,
    assignableClientsFor,
    assignClientAccess,
    removeClientAccess,
    formatClientRole,
  }
}
