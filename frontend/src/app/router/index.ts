// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

import { createRouter, createWebHistory } from 'vue-router'
import type { RouteLocationNormalizedGeneric, RouteLocationRaw, RouteRecordRaw } from 'vue-router'
import { useSession } from '@/composables/useSession'
import { RoleLevel } from '@/composables/roles'

/** The tenant pages that became sections, by the route name each kept and the section it now opens. */
const TENANT_SECTION_ROUTE_NAMES = {
  settings: 'tenant-settings',
  members: 'tenant-members',
  budget: 'tenant-budget-overview',
  spend: 'tenant-spend',
} as const

const TENANT_SECTION_QUERY = {
  // Settings was the whole page; its landing section is the workspace's default, so it carries no section.
  settings: '',
  members: 'members',
  budget: 'budget',
  spend: 'spend',
} as const

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
      path: '/tenants/:tenantId',
      name: 'tenant-detail',
      component: () => import('@/features/tenants/views/TenantDetailView.vue'),
      meta: { requiresAuth: true, requiresTenantAdmin: true },
    },
    // What used to be four tenant pages are sections of the workspace above. The routes keep their names and
    // their guard so existing links (and anything holding a bookmark) land on the right section instead of 404.
    ...(['settings', 'members', 'budget', 'spend'] as const).map((section): RouteRecordRaw => ({
      path: `/tenants/:tenantId/${section}`,
      name: TENANT_SECTION_ROUTE_NAMES[section],
      redirect: (to) => ({
        name: 'tenant-detail',
        params: { tenantId: to.params.tenantId },
        query: TENANT_SECTION_QUERY[section] ? { section: TENANT_SECTION_QUERY[section] } : {},
      }),
      meta: { requiresAuth: true, requiresTenantAdmin: true },
    })),
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
      path: '/code-quality',
      name: 'code-quality',
      component: () => import('@/features/code-insights/views/CodeQualityView.vue'),
      // What a developer needs from the collected findings: client access plus the licence, no extra role. The
      // capability check mirrors the nav check so a deep link cannot reach what a hidden link would not offer;
      // the server denies it too, so this guard is a courtesy rather than the boundary.
      meta: { requiresAuth: true, requiresClientAccess: true, requiresCapability: 'code-insights' },
    },
    {
      path: '/reviewer-performance',
      name: 'reviewer-performance',
      component: () => import('@/features/code-insights/views/ReviewerPerformanceView.vue'),
      // Judging the reviewer is an operator's job, and the evidence underneath it is AI-estimated and
      // uncalibrated, so it sits with the other Administration entries rather than in front of every developer.
      meta: { requiresAuth: true, requiresAnyTenantAdmin: true, requiresCapability: 'code-insights' },
    },
    {
      // The area used to be one page. A bookmark should land somewhere useful rather than on a 404.
      path: '/code-insights',
      redirect: { name: 'code-quality' },
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
      // What this installation reports about itself, and the control over it. Platform administrators only,
      // because the setting and the identity in the payload are installation-wide.
      path: '/usage-statistics',
      name: 'usage-statistics',
      component: () => import('@/features/usage-statistics/views/UsageStatisticsView.vue'),
      meta: { requiresAuth: true, requiresAdmin: true },
    },
    {
      // The whole installation's fleet. Reserved to platform administrators, because it spans tenants and
      // administering one is not grounds for reading the rest — the same rule the API applies.
      path: '/runners',
      name: 'runners-all',
      component: () => import('@/features/runners/views/RunnersView.vue'),
      meta: { requiresAuth: true, requiresAdmin: true, requiresCapability: 'distributed-execution' },
    },
    {
      // One tenant's fleet, for that tenant's own administrators. requiresTenantAdmin checks the tenant
      // named in the route rather than merely that the caller administers something, so this cannot be
      // reached for somebody else's tenant by editing the URL.
      path: '/tenants/:tenantId/runners',
      name: 'runners',
      component: () => import('@/features/runners/views/RunnersView.vue'),
      props: true,
      meta: { requiresAuth: true, requiresTenantAdmin: true, requiresCapability: 'distributed-execution' },
    },
    {
      path: '/clients/:id/providers',
      name: 'client-detail-providers',
      redirect: (to) => ({
        name: 'client-detail',
        params: { id: to.params.id },
        query: { ...to.query, tab: 'providers' },
      }),
      meta: { requiresAuth: true, requiresClientAdmin: true, idParamIsClient: true },
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
      meta: { requiresAuth: true, requiresClientAccess: true, idParamIsClient: true },
    },
    {
      path: '/:id/procursor/sources/:sourceId/events',
      name: 'client-procursor-source-events',
      component: () => import('@/features/procursor/views/ProCursorSourceEventsView.vue'),
      meta: { requiresAuth: true, requiresClientAdmin: true, idParamIsClient: true },
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

/**
 * Denies a route reserved to operators. Satisfied by a platform administrator or by administering any tenant,
 * unlike the tenant-scoped guard below, which checks the tenant named in the route.
 */
function resolveAnyTenantAdminGuard(
  to: RouteLocationNormalizedGeneric,
  isAdmin: boolean,
  tenantRoles: Record<string, number>,
): RouteLocationRaw | undefined {
  if (!to.meta.requiresAnyTenantAdmin || isAdmin) {
    return undefined
  }
  return Object.values(tenantRoles).some((role) => role >= RoleLevel.Administrator) ? undefined : ACCESS_DENIED
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

/**
 * The client a route is about, when it names one at all.
 *
 * A route whose `:id` is a client says so with `idParamIsClient`. Without it the parameter is something
 * else (the protocol route's `:id` is a job), and reading that as a client denies the page to everyone
 * except a platform administrator, whose check short-circuits before this one. A route that names no
 * client falls through to the any-client-role check below, with the server as the real boundary.
 */
function resolveRouteClientId(to: RouteLocationNormalizedGeneric): string | undefined {
  if (typeof to.query.clientId === 'string') {
    return to.query.clientId
  }
  return to.meta.idParamIsClient === true && typeof to.params.id === 'string' ? to.params.id : undefined
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

/**
 * Denies a route whose licensed capability is unavailable. Applies to admins too: a licence is not a role, and
 * an administrator of an installation that has not bought a capability still cannot use it.
 */
function resolveCapabilityGuard(
  to: RouteLocationNormalizedGeneric,
  isCapabilityAvailable: (key: string) => boolean,
): RouteLocationRaw | undefined {
  const required = to.meta.requiresCapability
  if (typeof required !== 'string') {
    return undefined
  }
  return isCapabilityAvailable(required) ? undefined : ACCESS_DENIED
}

function resolveLoginRedirectGuard(
  to: RouteLocationNormalizedGeneric,
  isAuthenticated: boolean,
): RouteLocationRaw | undefined {
  return (to.name === 'login' || to.name === 'tenant-login') && isAuthenticated ? { name: 'home' } : undefined
}

router.beforeEach((to) => {
  const {
    isAuthenticated,
    isAdmin,
    hasClientRole,
    hasTenantRole,
    clientRoles,
    tenantRoles,
    edition,
    isCapabilityAvailable,
  } = useSession()

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
    resolveCapabilityGuard(to, isCapabilityAvailable) ??
    resolveAnyTenantAdminGuard(to, isAdmin.value, tenantRoles.value) ??
    resolveTenantDirectoryGuard(to, isAdmin.value, tenantRoles.value) ??
    resolveTenantAdminGuard(to, isAdmin.value, hasTenantRole) ??
    resolveClientAccessGuard(to, isAdmin.value, hasClientRole, clientRoles.value) ??
    resolveLoginRedirectGuard(to, isAuthenticated.value)
  )
})

export default router
