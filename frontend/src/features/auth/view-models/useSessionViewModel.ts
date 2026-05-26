// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { computed, type ComputedRef, type Ref } from 'vue'
import { useSession } from '@/composables/useSession'

export interface SessionViewModelState {
  isAuthenticated: ComputedRef<boolean>
  isAdmin: ComputedRef<boolean>
  username: ComputedRef<string | null>
  edition: Ref<string>
  isCommercialEdition: ComputedRef<boolean>
  clientRoles: Ref<Record<string, number>>
  tenantRoles: Ref<Record<string, number>>
  hasLocalPassword: Ref<boolean>
}

export interface SessionViewModelActions {
  establishSession: (session: { accessToken: string; refreshToken: string }) => Promise<void>
  clearTokens: () => void
  loadClientRoles: () => Promise<void>
  hasClientRole: (clientId: string, minRole: 0 | 1) => boolean
  hasTenantRole: (tenantId: string, minRole: 0 | 1) => boolean
  isCapabilityAvailable: (key: string) => boolean
}

export interface SessionViewModel extends SessionViewModelState, SessionViewModelActions {
  hasAnyAdminRole: ComputedRef<boolean>
}

export function useSessionViewModel(): SessionViewModel {
  const session = useSession()

  const hasAnyAdminRole = computed(() => {
    if (session.isAdmin.value) return true
    return Object.values(session.clientRoles.value).some((role) => role >= 1)
      || Object.values(session.tenantRoles.value).some((role) => role >= 1)
  })

  return {
    isAuthenticated: session.isAuthenticated,
    isAdmin: session.isAdmin,
    username: session.username,
    edition: session.edition,
    isCommercialEdition: session.isCommercialEdition,
    clientRoles: session.clientRoles,
    tenantRoles: session.tenantRoles,
    hasLocalPassword: session.hasLocalPassword,
    hasAnyAdminRole,
    establishSession: session.establishSession,
    clearTokens: session.clearTokens,
    loadClientRoles: session.loadClientRoles,
    hasClientRole: session.hasClientRole,
    hasTenantRole: session.hasTenantRole,
    isCapabilityAvailable: session.isCapabilityAvailable,
  }
}
