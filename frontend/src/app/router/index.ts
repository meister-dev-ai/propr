// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

import { createRouter, createWebHistory } from 'vue-router'
import type { RouteLocationNormalizedGeneric, RouteLocationRaw } from 'vue-router'
import { useSession } from '@/composables/useSession'
import { RoleLevel } from '@/composables/roles'

const router = createRouter({
  history: createWebHistory(import.meta.env.BASE_URL),
  routes: [
    {
      path: '/',
      name: 'home',
      redirect: () => {
        const { isAuthenticated, isAdmin, clientRoles, tenantRoles, edition } = useSession()
        if (!isAuthenticated.value) {
          return { name: 'login' }
        }

        const hasAnyAdminRole = isAdmin.value || Object.values(clientRoles.value).some((role) => role >= RoleLevel.Administrator)
        if (hasAnyAdminRole) {
          return { name: 'clients' }
        }

        const firstTenantAdminId = Object.entries(tenantRoles.value)
          .find(([, role]) => role >= 1)?.[0]

        return firstTenantAdminId && edition.value !== 'community'
          ? { name: 'tenant-directory' }
          : { name: 'reviews' }
      },
    },
    {
      path: '/tenants',
      name: 'tenant-directory',
      component: () => import('@/features/tenants/views/TenantDirectoryView.vue'),
      meta: { requiresAuth: true, requiresTenantDirectoryAccess: true },
    },
    {
      path: '/login',
      name: 'login',
      component: () => import('@/features/auth/views/LoginView.vue'),
    },
    {
      path: '/tenants/:tenantSlug/login',
      name: 'tenant-login',
      component: () => import('@/features/tenants/views/TenantLoginView.vue'),
    },
    {
      path: '/tenants/:tenantSlug/login/callback',
      name: 'tenant-login-callback',
      component: () => import('@/features/tenants/views/TenantExternalCallbackView.vue'),
    },
    {
      path: '/tenants/:tenantId/settings',
      name: 'tenant-settings',
      component: () => import('@/features/tenants/views/TenantSettingsView.vue'),
      meta: { requiresAuth: true, requiresTenantAdmin: true },
    },
    {
      path: '/tenants/:tenantId/members',
      name: 'tenant-members',
      component: () => import('@/features/tenants/views/TenantMembersView.vue'),
      meta: { requiresAuth: true, requiresTenantAdmin: true },
    },
    {
      path: '/tenants/:tenantId/budget',
      name: 'tenant-budget-overview',
      component: () => import('@/features/tenants/views/TenantBudgetOverviewView.vue'),
      meta: { requiresAuth: true, requiresTenantAdmin: true },
    },
    {
      path: '/tenants/:tenantId/spend',
      name: 'tenant-spend',
      component: () => import('@/features/tenants/views/TenantSpendView.vue'),
      meta: { requiresAuth: true, requiresTenantAdmin: true },
    },
    {
      path: '/clients',
      name: 'clients',
      component: () => import('@/features/clients/views/ClientsView.vue'),
      meta: { requiresAuth: true, requiresClientAccess: true },
    },
    {
      path: '/reviews',
      name: 'reviews',
      component: () => import('@/features/reviews/views/ReviewHistoryView.vue'),
      meta: { requiresAuth: true },
    },
    {
      path: '/jobs/:id/protocol',
      name: 'job-protocol',
      component: () => import('@/features/job-protocol/views/JobProtocolView.vue'),
      meta: { requiresAuth: true, requiresClientAccess: true },
    },
    {
      path: '/pats',
      redirect: '/settings',
    },
    {
      path: '/settings',
      name: 'settings',
      component: () => import('@/features/settings/views/SettingsView.vue'),
      meta: { requiresAuth: true },
    },
    {
      path: '/users',
      name: 'users',
      component: () => import('@/features/users/views/UsersView.vue'),
      meta: { requiresAuth: true, requiresAdmin: true },
    },
    {
      path: '/thread-memory',
      name: 'thread-memory',
      component: () => import('@/features/thread-memory/views/ThreadMemoryView.vue'),
      meta: { requiresAuth: true, requiresAdmin: true },
    },
    {
      path: '/provider-settings',
      name: 'provider-settings',
      component: () => import('@/features/provider-settings/views/ProviderSettingsView.vue'),
      meta: { requiresAuth: true, requiresAdmin: true },
    },
    {
      path: '/licensing',
      name: 'licensing',
      component: () => import('@/features/licensing/views/LicensingView.vue'),
      meta: { requiresAuth: true, requiresAdmin: true },
    },
    {
      path: '/clients/:id/providers',
      name: 'client-detail-providers',
      redirect: (to) => ({
        name: 'client-detail',
        params: { id: to.params.id },
        query: { ...to.query, tab: 'providers' },
      }),
      meta: { requiresAuth: true, requiresClientAdmin: true },
    },
    {
      path: '/pr-review',
      name: 'pr-review',
      component: () => import('@/features/reviews/views/PrReviewView.vue'),
      meta: { requiresAuth: true, requiresClientAccess: true },
    },
    {
      path: '/403',
      name: 'access-denied',
      component: () => import('@/features/auth/views/AccessDeniedView.vue'),
    },
    {
      path: '/:id',
      name: 'client-detail',
      component: () => import('@/features/clients/views/ClientDetailView.vue'),
      meta: { requiresAuth: true, requiresClientAccess: true },
    },
    {
      path: '/:id/procursor/sources/:sourceId/events',
      name: 'client-procursor-source-events',
      component: () => import('@/features/procursor/views/ProCursorSourceEventsView.vue'),
      meta: { requiresAuth: true, requiresClientAdmin: true },
    },
  ],
})

const ACCESS_DENIED: RouteLocationRaw = { name: 'access-denied' }

function resolveTenantDirectoryGuard(
  to: RouteLocationNormalizedGeneric,
  isAdmin: boolean,
  tenantRoles: Record<string, number>,
): RouteLocationRaw | undefined {
  if (!to.meta.requiresTenantDirectoryAccess || isAdmin) {
    return undefined
  }
  const hasAnyTenantAdminRole = Object.values(tenantRoles).some((role) => role >= 1)
  return hasAnyTenantAdminRole ? undefined : ACCESS_DENIED
}

function resolveTenantAdminGuard(
  to: RouteLocationNormalizedGeneric,
  isAdmin: boolean,
  hasTenantRole: (tenantId: string, minRole: RoleLevel) => boolean,
): RouteLocationRaw | undefined {
  if (!to.meta.requiresTenantAdmin || isAdmin) {
    return undefined
  }
  const routeTenantId = typeof to.params.tenantId === 'string' ? to.params.tenantId : undefined
  return routeTenantId && hasTenantRole(routeTenantId, RoleLevel.Administrator) ? undefined : ACCESS_DENIED
}

function resolveRequiredClientRole(to: RouteLocationNormalizedGeneric): RoleLevel | null {
  if (to.meta.requiresClientAdmin) {
    return RoleLevel.Administrator
  }
  return to.meta.requiresClientAccess ? RoleLevel.User : null
}

function resolveRouteClientId(to: RouteLocationNormalizedGeneric): string | undefined {
  if (typeof to.query.clientId === 'string') {
    return to.query.clientId
  }
  return typeof to.params.id === 'string' ? to.params.id : undefined
}

function resolveClientAccessGuard(
  to: RouteLocationNormalizedGeneric,
  isAdmin: boolean,
  hasClientRole: (clientId: string, minRole: RoleLevel) => boolean,
  clientRoles: Record<string, number>,
): RouteLocationRaw | undefined {
  const requiredClientRole = resolveRequiredClientRole(to)
  if (requiredClientRole === null || isAdmin) {
    return undefined
  }

  const routeClientId = resolveRouteClientId(to)
  if (routeClientId) {
    return hasClientRole(routeClientId, requiredClientRole) ? undefined : ACCESS_DENIED
  }

  const hasAnyMatchingRole = Object.values(clientRoles).some((role) => role >= requiredClientRole)
  return hasAnyMatchingRole ? undefined : ACCESS_DENIED
}

function resolveLoginRedirectGuard(
  to: RouteLocationNormalizedGeneric,
  isAuthenticated: boolean,
): RouteLocationRaw | undefined {
  return (to.name === 'login' || to.name === 'tenant-login') && isAuthenticated ? { name: 'home' } : undefined
}

router.beforeEach((to) => {
  const { isAuthenticated, isAdmin, hasClientRole, hasTenantRole, clientRoles, tenantRoles, edition } = useSession()

  if (to.meta.requiresAuth && !isAuthenticated.value) {
    return { name: 'login' }
  }
  if (to.meta.requiresAdmin && !isAdmin.value) {
    return ACCESS_DENIED
  }
  if ((to.meta.requiresTenantDirectoryAccess || to.meta.requiresTenantAdmin) && edition.value === 'community') {
    return ACCESS_DENIED
  }

  return (
    resolveTenantDirectoryGuard(to, isAdmin.value, tenantRoles.value) ??
    resolveTenantAdminGuard(to, isAdmin.value, hasTenantRole) ??
    resolveClientAccessGuard(to, isAdmin.value, hasClientRole, clientRoles.value) ??
    resolveLoginRedirectGuard(to, isAuthenticated.value)
  )
})

export default router
