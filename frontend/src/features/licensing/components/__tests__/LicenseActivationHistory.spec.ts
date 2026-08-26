// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { flushPromises, mount } from '@vue/test-utils'
import { ref } from 'vue'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import type { LicenseActivationEvent } from '@/services/licensingService'

const history = ref<LicenseActivationEvent[]>([])
const historyStale = ref(false)
const loadHistoryMock = vi.fn()

vi.mock('@/composables/useLicensing', () => ({
  useLicensing: () => ({
    history,
    historyStale,
    loadHistory: loadHistoryMock,
  }),
}))

async function mountHistory() {
  const { default: LicenseActivationHistory } =
    await import('@/features/licensing/components/LicenseActivationHistory.vue')

  const wrapper = mount(LicenseActivationHistory)
  await flushPromises()

  return wrapper
}

describe('LicenseActivationHistory', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    history.value = []
    historyStale.value = false
    loadHistoryMock.mockResolvedValue(undefined)
  })

  it('reads the recorded changes when it mounts', async () => {
    await mountHistory()

    expect(loadHistoryMock).toHaveBeenCalledTimes(1)
  })

  // Every record names what was done, when, to which license and by whom, because that is what a support
  // conversation about a licensing change asks for.
  it('names the action, the license and the actor for each record', async () => {
    history.value = [
      {
        action: 'replaced',
        occurredAt: '2026-02-01T08:00:00Z',
        actorUserId: '2f7c4d3e-93a1-4a55-9a2f-6d9e0b1c2a34',
        licenseId: 'second-license',
        licensee: 'Contoso Engineering',
      },
    ]

    const wrapper = await mountHistory()
    const entry = wrapper.get('[data-testid="license-history-entry-0"]')

    expect(entry.text()).toContain('License replaced')
    expect(entry.text()).toContain('Contoso Engineering')
    expect(entry.text()).toContain('second-license')
    expect(entry.text()).toContain('2f7c4d3e-93a1-4a55-9a2f-6d9e0b1c2a34')
  })

  // The list is served newest first by the backend and rendered in the order it arrives, so the most recent
  // change is the first one read.
  it('renders the records in the order they arrive', async () => {
    history.value = [
      { action: 'removed', occurredAt: '2026-03-01T00:00:00Z', actorUserId: null, licenseId: 'b', licensee: null },
      { action: 'activated', occurredAt: '2026-01-01T00:00:00Z', actorUserId: null, licenseId: 'a', licensee: null },
    ]

    const wrapper = await mountHistory()

    expect(wrapper.get('[data-testid="license-history-entry-0"]').text()).toContain('License removed')
    expect(wrapper.get('[data-testid="license-history-entry-1"]').text()).toContain('License activated')
  })

  it('says so when a record carries no licensee or actor', async () => {
    history.value = [
      { action: 'activated', occurredAt: '2026-01-01T00:00:00Z', actorUserId: null, licenseId: null, licensee: null },
    ]

    const wrapper = await mountHistory()
    const entry = wrapper.get('[data-testid="license-history-entry-0"]')

    expect(entry.text()).toContain('Licensee not recorded')
    expect(entry.text()).toContain('License id not recorded')
    expect(entry.text()).toContain('No signed-in user recorded')
  })

  // The records outlive the license they describe, so the list renders on an installation that currently has
  // none rather than being hidden with the license card.
  it('renders an empty state rather than nothing', async () => {
    const wrapper = await mountHistory()

    expect(wrapper.get('[data-testid="license-history-empty"]').text())
      .toContain('No license changes recorded')
  })

  it('reports a failed read and offers to try again', async () => {
    loadHistoryMock.mockRejectedValue(new Error('Failed to load the license history.'))

    const wrapper = await mountHistory()

    expect(wrapper.get('[data-testid="license-history-error"]').text())
      .toBe('Failed to load the license history.')

    loadHistoryMock.mockResolvedValue(undefined)
    await wrapper.get('[data-testid="license-history-refresh"]').trigger('click')
    await flushPromises()

    expect(wrapper.find('[data-testid="license-history-error"]').exists()).toBe(false)
  })

  it('does not offer another refresh while the current refresh is in flight', async () => {
    let release: () => void = () => {}
    loadHistoryMock.mockReturnValue(new Promise<void>((resolve) => { release = resolve }))

    const wrapper = await mountHistory()
    const refresh = wrapper.get('[data-testid="license-history-refresh"]')

    expect(refresh.attributes('disabled')).toBeDefined()
    release()
    await flushPromises()
    expect(refresh.attributes('disabled')).toBeUndefined()
  })

  // A mutation-triggered refresh that fails leaves the list showing records that may no longer match what
  // was just written. The list has to say so rather than presenting stale data as current.
  it('marks the list stale when a background refresh fails', async () => {
    history.value = [
      { action: 'activated', occurredAt: '2026-01-01T00:00:00Z', actorUserId: null, licenseId: 'a', licensee: null },
    ]
    const wrapper = await mountHistory()

    // The stale marker comes from a background refresh that failed; set it after mount to simulate that.
    historyStale.value = true
    await wrapper.vm.$nextTick()

    expect(wrapper.get('[data-testid="license-history-stale"]').text())
      .toContain('could not be refreshed')
  })

  // An installation whose first activation could not be re-read has no records to show, and the empty state
  // states that no change was made. The warning has to reach that case too, or the list asserts the opposite
  // of what the failed refresh leaves known.
  it('marks the list stale even when it has no records to show', async () => {
    const wrapper = await mountHistory()

    historyStale.value = true
    await wrapper.vm.$nextTick()

    expect(wrapper.get('[data-testid="license-history-empty"]').text())
      .toContain('No license changes recorded')
    expect(wrapper.get('[data-testid="license-history-stale"]').text())
      .toContain('could not be refreshed')
  })

  // The stale marker comes from a background refresh that failed; a retry from the list itself clears it
  // once the read succeeds, so the warning does not outlive the condition it describes.
  it('clears the stale marker when the user retries', async () => {
    history.value = [
      { action: 'activated', occurredAt: '2026-01-01T00:00:00Z', actorUserId: null, licenseId: 'a', licensee: null },
    ]
    const wrapper = await mountHistory()

    // Set after mounting, because the load the mount runs clears the marker: setting it first would leave the
    // retry with nothing to clear and the assertion below would hold whether or not the retry clears it.
    historyStale.value = true
    await wrapper.vm.$nextTick()
    expect(wrapper.find('[data-testid="license-history-stale"]').exists()).toBe(true)

    await wrapper.get('[data-testid="license-history-refresh"]').trigger('click')
    await flushPromises()

    expect(wrapper.find('[data-testid="license-history-stale"]').exists()).toBe(false)
  })

})
