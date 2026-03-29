import { createRouter, createWebHistory } from 'vue-router'
import { useSession } from '@/composables/useSession'

const router = createRouter({
  history: createWebHistory('/admin/'),
  routes: [
    {
      path: '/login',
      name: 'login',
      component: () => import('@/views/LoginView.vue'),
    },
    {
      path: '/',
      name: 'clients',
      component: () => import('@/views/ClientsView.vue'),
      meta: { requiresAuth: true },
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
      meta: { requiresAuth: true },
    },
    {
      path: '/pats',
      name: 'pats',
      component: () => import('@/views/PatsView.vue'),
      meta: { requiresAuth: true },
    },
    {
      path: '/users',
      name: 'users',
      component: () => import('@/views/UsersView.vue'),
      meta: { requiresAuth: true, requiresAdmin: true },
    },
    {
      path: '/crawl-configs',
      name: 'crawl-configs',
      component: () => import('@/views/CrawlConfigsView.vue'),
      meta: { requiresAuth: true },
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
      meta: { requiresAuth: true },
    },
  ],
})

router.beforeEach((to) => {
  const { isAuthenticated, isAdmin } = useSession()
  if (to.meta.requiresAuth && !isAuthenticated.value) {
    return { name: 'login' }
  }
  if (to.meta.requiresAdmin && !isAdmin.value) {
    return { name: 'access-denied' }
  }
  if (to.name === 'login' && isAuthenticated.value) {
    return { name: 'clients' }
  }
})

export default router
