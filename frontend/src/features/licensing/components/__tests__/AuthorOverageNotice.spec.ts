// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { RouterLinkStub, flushPromises, mount } from '@vue/test-utils'
import { computed, ref } from 'vue'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { AuthorOverage } from '@/services/licensingService'

const isAdmin = ref(true)
const isAuthenticated = ref(true)
const authorOverage = ref<AuthorOverage | null>(null)
const loadMock = vi.fn()
const resetMock = vi.fn()

vi.mock('@/composables/useSession', () => ({
  useSession: () => ({
    isAdmin: computed(() => isAdmin.value),
    isAuthenticated: computed(() => isAuthenticated.value),
  }),
}))

// The composable decides whether a notice is due, the same way it does for the expiry notice: it hands over the
// overage only while the month is above the number.
vi.mock('@/composables/useLicensing', () => ({
  useLicensing: () => ({
    authorOverageNotice: computed(() =>
      authorOverage.value !== null && authorOverage.value.isInOverage ? authorOverage.value : null,
    ),
    load: loadMock,
    reset: resetMock,
  }),
}))

// The component watches the session refs, so a wrapper left mounted would answer a later test's changes to
// them as well. Every mount is tracked and torn down between tests.
const mounted: { unmount: () => void }[] = []

async function mountNotice() {
  const { default: AuthorOverageNotice } =
    await import('@/features/licensing/components/AuthorOverageNotice.vue')

  const wrapper = mount(AuthorOverageNotice, {
    global: { stubs: { RouterLink: RouterLinkStub } },
  })
  mounted.push(wrapper)
  await flushPromises()

  return wrapper
}

describe('AuthorOverageNotice', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    isAdmin.value = true
    isAuthenticated.value = true
    authorOverage.value = null
    loadMock.mockResolvedValue(undefined)
  })

  afterEach(() => {
    mounted.splice(0).forEach((wrapper) => wrapper.unmount())
  })

  it('names both counts while the month is above the licensed number', async () => {
    authorOverage.value = { licensedCount: 5, observedCount: 12, isInOverage: true }

    const wrapper = await mountNotice()
    const text = wrapper.get('[data-testid="author-overage-notice"]').text()

    expect(text).toContain('12')
    expect(text).toContain('5')
  })

  // The allowance is metered, so an operator reading a warning has to be told the installation kept running.
  // Without that, the notice reads as something having stopped.
  it('states that nothing has been withheld', async () => {
    authorOverage.value = { licensedCount: 5, observedCount: 12, isInOverage: true }

    const wrapper = await mountNotice()
    const text = wrapper.get('[data-testid="author-overage-notice"]').text()

    expect(text).toContain('Nothing has been withheld, delayed or degraded')
    expect(text).toContain('not enforced')
  })

  it('links to the licensing page', async () => {
    authorOverage.value = { licensedCount: 5, observedCount: 12, isInOverage: true }

    const wrapper = await mountNotice()
    const targets = wrapper.findAllComponents(RouterLinkStub).map((component) => component.props('to'))

    expect(targets).toContainEqual({ name: 'licensing' })
  })

  it('renders nothing when the summary carries no overage', async () => {
    const wrapper = await mountNotice()

    expect(wrapper.find('[data-testid="author-overage-notice"]').exists()).toBe(false)
  })

  // A month at or below the number needs no notice, and the payload says so on the flag rather than by leaving
  // the object out.
  it('renders nothing once the month is at or below the number', async () => {
    authorOverage.value = { licensedCount: 5, observedCount: 3, isInOverage: false }

    const wrapper = await mountNotice()

    expect(wrapper.find('[data-testid="author-overage-notice"]').exists()).toBe(false)
  })

  // The endpoint behind it is administrator-only, so a non-administrator neither sees the notice nor causes a
  // request that would be refused.
  it('is not shown to someone who is not a platform administrator', async () => {
    isAdmin.value = false
    authorOverage.value = { licensedCount: 5, observedCount: 12, isInOverage: true }

    const wrapper = await mountNotice()

    expect(wrapper.find('[data-testid="author-overage-notice"]').exists()).toBe(false)
    expect(loadMock).not.toHaveBeenCalled()
  })

  it('is not shown or loaded without an authenticated session', async () => {
    isAuthenticated.value = false
    authorOverage.value = { licensedCount: 5, observedCount: 12, isInOverage: true }

    const wrapper = await mountNotice()

    expect(wrapper.find('[data-testid="author-overage-notice"]').exists()).toBe(false)
    expect(loadMock).not.toHaveBeenCalled()
  })

  it('reads the licensing state when it mounts for an administrator', async () => {
    authorOverage.value = { licensedCount: 5, observedCount: 12, isInOverage: true }

    await mountNotice()

    expect(loadMock).toHaveBeenCalledTimes(1)
  })

  it('clears the shared state when it unmounts', async () => {
    authorOverage.value = { licensedCount: 5, observedCount: 12, isInOverage: true }

    const wrapper = await mountNotice()
    wrapper.unmount()

    expect(resetMock).toHaveBeenCalledTimes(1)
  })
})
