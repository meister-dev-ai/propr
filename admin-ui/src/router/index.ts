// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { createRouter, createWebHistory } from 'vue-router'
import { useSession } from '@/composables/useSession'

const router = createRouter({
  history: createWebHistory(import.meta.env.BASE_URL),
  routes: [
    {
      path: '/',
      name: 'home',
      redirect: () => {
        const { isAuthenticated, isAdmin, clientRoles } = useSession()
        if (!isAuthenticated.value) {
          return { name: 'login' }
        }

        const hasAnyAdminRole = isAdmin.value || Object.values(clientRoles.value).some((role) => role >= 1)
        return hasAnyAdminRole ? { name: 'clients' } : { name: 'reviews' }
      },
    },
    {
      path: '/login',
      name: 'login',
      component: () => import('@/views/LoginView.vue'),
    },
    {
      path: '/clients',
      name: 'clients',
      component: () => import('@/views/ClientsView.vue'),
      meta: { requiresAuth: true, requiresClientAdmin: true },
    },
    {
      path: '/reviews',
      name: 'reviews',
      component: () => import('@/views/ReviewHistoryView.vue'),
      meta: { requiresAuth: true },
    },
    {
      path: '/jobs/:id/protocol',
      name: 'job-protocol',
      component: () => import('@/views/JobProtocolView.vue'),
      meta: { requiresAuth: true, requiresClientAccess: true },
    },
    {
      path: '/pats',
      redirect: '/settings',
    },
    {
      path: '/settings',
      name: 'settings',
      component: () => import('@/views/SettingsView.vue'),
      meta: { requiresAuth: true },
    },
    {
      path: '/users',
      name: 'users',
      component: () => import('@/views/UsersView.vue'),
      meta: { requiresAuth: true, requiresAdmin: true },
    },
    {
      path: '/thread-memory',
      name: 'thread-memory',
      component: () => import('@/views/ThreadMemoryView.vue'),
      meta: { requiresAuth: true, requiresAdmin: true },
    },
    {
      path: '/provider-settings',
      name: 'provider-settings',
      component: () => import('@/views/ProviderSettingsView.vue'),
      meta: { requiresAuth: true, requiresAdmin: true },
    },
    {
      path: '/licensing',
      name: 'licensing',
      component: () => import('@/views/LicensingView.vue'),
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
      component: () => import('@/views/PrReviewView.vue'),
      meta: { requiresAuth: true, requiresClientAccess: true },
    },
    {
      path: '/403',
      name: 'access-denied',
      component: () => import('@/views/AccessDeniedView.vue'),
    },
    {
      path: '/:id',
      name: 'client-detail',
      component: () => import('@/views/ClientDetailView.vue'),
      meta: { requiresAuth: true, requiresClientAdmin: true },
    },
    {
      path: '/:id/procursor/sources/:sourceId/events',
      name: 'client-procursor-source-events',
      component: () => import('@/views/ProCursorSourceEventsView.vue'),
      meta: { requiresAuth: true, requiresClientAdmin: true },
    },
  ],
})

router.beforeEach((to) => {
  const { isAuthenticated, isAdmin, hasClientRole, clientRoles } = useSession()
  if (to.meta.requiresAuth && !isAuthenticated.value) {
    return { name: 'login' }
  }
  if (to.meta.requiresAdmin && !isAdmin.value) {
    return { name: 'access-denied' }
  }
  const requiredClientRole = to.meta.requiresClientAdmin ? 1 : to.meta.requiresClientAccess ? 0 : null
  if (requiredClientRole !== null && !isAdmin.value) {
    const routeClientId = typeof to.query.clientId === 'string'
      ? to.query.clientId
      : typeof to.params.id === 'string'
        ? to.params.id
        : undefined

    if (routeClientId) {
      if (!hasClientRole(routeClientId, requiredClientRole as 0 | 1)) {
        return { name: 'access-denied' }
      }
    } else {
      const hasAnyMatchingRole = Object.values(clientRoles.value).some((role) => role >= requiredClientRole)
      if (!hasAnyMatchingRole) {
        return { name: 'access-denied' }
      }
    }
  }
  if (to.name === 'login' && isAuthenticated.value) {
    return { name: 'home' }
  }
})

export default router
