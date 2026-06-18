// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { mount, type VueWrapper } from '@vue/test-utils'
import { createVuetify } from 'vuetify'
import * as vuetifyComponents from 'vuetify/components'
import * as vuetifyDirectives from 'vuetify/directives'
import type { Component } from 'vue'
import type { Router } from 'vue-router'
import { createTestRouter, type CreateTestRouterOptions } from './createTestRouter'

export interface RenderWithAppOptions {
  /** Props supplied to the component under test. */
  props?: Record<string, unknown>
  /** Slot content keyed by slot name. */
  slots?: Record<string, string | Component>
  /** Custom router, or options forwarded to createTestRouter when omitted. */
  router?: Router
  routerOptions?: CreateTestRouterOptions
  /** Extra Vue plugins to install. */
  plugins?: unknown[]
  /** Component stubs (forwarded to @vue/test-utils). */
  stubs?: Record<string, unknown>
}

export interface RenderResult<TComponent extends Component> {
  wrapper: VueWrapper<InstanceType<TComponent extends abstract new (...args: never) => infer R ? new (...args: never) => R : never>>
  router: Router
}

/**
 * Mount a component inside a Vuetify-aware test app with an in-memory router.
 * Use this helper for view and dumb-component tests that need the real Vuetify plugin
 * (per Decision 6 in research.md and the constitution's Test First principle).
 */
export async function renderWithApp<TComponent extends Component>(
  component: TComponent,
  options: RenderWithAppOptions = {},
): Promise<RenderResult<TComponent>> {
  const router = options.router ?? (await createTestRouter(options.routerOptions))
  const vuetify = createVuetify({
    components: vuetifyComponents,
    directives: vuetifyDirectives,
  })

  const wrapper = mount(component, {
    props: options.props,
    slots: options.slots,
    global: {
      plugins: [router, vuetify, ...(options.plugins ?? [])],
      stubs: options.stubs,
    },
  } as Parameters<typeof mount>[1]) as unknown as RenderResult<TComponent>['wrapper']

  return { wrapper, router }
}
