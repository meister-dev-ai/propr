// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { createMemoryHistory, createRouter, type Router, type RouteRecordRaw } from 'vue-router'

const stubComponent = { template: '<div />' }

const defaultRoutes: RouteRecordRaw[] = [
  { path: '/', name: 'home', component: stubComponent },
  { path: '/login', name: 'login', component: stubComponent },
  { path: '/clients', name: 'clients', component: stubComponent },
  { path: '/reviews', name: 'reviews', component: stubComponent },
  { path: '/settings', name: 'settings', component: stubComponent },
  { path: '/403', name: 'access-denied', component: stubComponent },
  { path: '/:catchAll(.*)*', name: 'not-found', component: stubComponent },
]

export interface CreateTestRouterOptions {
  /** Override the default route table with the routes a specific test needs. */
  routes?: RouteRecordRaw[]
  /** Starting path. Defaults to `/`. */
  initialPath?: string
}

export async function createTestRouter(options: CreateTestRouterOptions = {}): Promise<Router> {
  const router = createRouter({
    history: createMemoryHistory(),
    routes: options.routes ?? defaultRoutes,
  })

  await router.push(options.initialPath ?? '/')
  await router.isReady()
  return router
}
