// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { describe, expect, it } from 'vitest'
import { mount } from '@vue/test-utils'

describe('feedback system primitives', () => {
  it('renders loading, empty, error, and success states', async () => {
    const modules = await Promise.all([
      import('@/components/feedback/LoadingState.vue'),
      import('@/components/feedback/EmptyState.vue'),
      import('@/components/feedback/ErrorState.vue'),
      import('@/components/feedback/SuccessState.vue'),
    ])

    const [LoadingState, EmptyState, ErrorState, SuccessState] = modules.map((module) => module.default)
    expect(mount(LoadingState, { slots: { default: 'Loading data' } }).text()).toContain('Loading data')
    expect(mount(EmptyState, { slots: { default: 'Nothing here' } }).text()).toContain('Nothing here')
    expect(mount(ErrorState, { slots: { default: 'Failure state' } }).text()).toContain('Failure state')
    expect(mount(SuccessState, { slots: { default: 'Success state' } }).text()).toContain('Success state')
  })

  it('keeps the feedback notification surface mounted from the shared path', async () => {
    const { default: AppNotification } = await import('@/components/feedback/AppNotification.vue')
    const wrapper = mount(AppNotification)
    expect(wrapper.exists()).toBe(true)
  })
})
