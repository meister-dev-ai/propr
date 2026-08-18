import { RouterLinkStub, flushPromises, mount } from '@vue/test-utils'
import { computed, ref } from 'vue'
import { beforeEach, describe, expect, it, vi } from 'vitest'

const isAdmin = ref(true)
const isAuthenticated = ref(true)
interface NoticeSettings {
  consentGateSatisfied: boolean
  communityOptIn: boolean
}

const settings = ref<NoticeSettings | null>(null)
const noticeRequired = ref(true)

// Loading is what makes the settings appear, as it does at runtime. Seeding them before mount would hide the
// ordering the acknowledgement guard has to handle.
const loadMock = vi.fn(async () => {
  settings.value ??= { consentGateSatisfied: false, communityOptIn: true }
})
const recordNoticeShownMock = vi.fn().mockResolvedValue(undefined)
const dismissNoticeMock = vi.fn().mockResolvedValue(undefined)

vi.mock('@/composables/useSession', () => ({
  useSession: () => ({
    isAdmin: computed(() => isAdmin.value),
    isAuthenticated: computed(() => isAuthenticated.value),
  }),
}))

vi.mock('@/composables/useUsageStatistics', () => ({
  useUsageStatistics: () => ({
    settings,
    noticeRequired: computed(() => noticeRequired.value),
    load: loadMock,
    recordNoticeShown: recordNoticeShownMock,
    dismissNotice: dismissNoticeMock,
  }),
}))

async function mountNotice() {
  const { default: UsageStatisticsNotice } =
    await import('@/features/usage-statistics/components/UsageStatisticsNotice.vue')

  return mount(UsageStatisticsNotice, {
    global: { stubs: { RouterLink: RouterLinkStub } },
  })
}

describe('UsageStatisticsNotice', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    isAdmin.value = true
    isAuthenticated.value = true
    settings.value = null
    noticeRequired.value = true
  })

  it('states what is sent, how often, and where it goes', async () => {
    const wrapper = await mountNotice()
    await flushPromises()

    const text = wrapper.get('[data-testid="usage-statistics-notice"]').text()
    expect(text).toContain('Once a day')
    expect(text).toContain('meister-dev.ai')
    expect(text).toContain('No code')
  })

  it('links to the payload and the control', async () => {
    const wrapper = await mountNotice()
    await flushPromises()

    const targets = wrapper.findAllComponents(RouterLinkStub)
      .map((component) => component.props('to'))
      .filter((to): to is Record<string, unknown> => typeof to === 'object' && to !== null)

    expect(targets).toContainEqual({ name: 'usage-statistics' })
  })

  // Rendering opens the send gate: an administrator who reads the notice and navigates away has still been
  // informed.
  it('records that the notice was shown as soon as it renders', async () => {
    await mountNotice()
    await flushPromises()

    expect(recordNoticeShownMock).toHaveBeenCalledTimes(1)
  })

  // The settings arrive after mount, so both the post-await path and the watcher on the now-visible banner
  // reach the acknowledgement in the same tick. Exactly one of them may send it.
  it('records it exactly once even though two paths reach it', async () => {
    await mountNotice()
    await flushPromises()
    await flushPromises()

    expect(recordNoticeShownMock).toHaveBeenCalledTimes(1)
  })

  it('does not record it again once the gate is already open', async () => {
    settings.value = { consentGateSatisfied: true, communityOptIn: true }

    await mountNotice()
    await flushPromises()

    expect(recordNoticeShownMock).not.toHaveBeenCalled()
  })

  // Nothing is sent once the operator switches it off, so a banner describing a daily snapshot would be
  // inaccurate.
  it('stops describing a daily snapshot once sending is switched off', async () => {
    settings.value = { consentGateSatisfied: true, communityOptIn: false }

    const wrapper = await mountNotice()
    await flushPromises()

    expect(wrapper.find('[data-testid="usage-statistics-notice"]').exists()).toBe(false)
  })

  it('dismisses on request', async () => {
    const wrapper = await mountNotice()
    await flushPromises()

    await wrapper.get('[data-testid="usage-statistics-notice-dismiss"]').trigger('click')
    await flushPromises()

    expect(dismissNoticeMock).toHaveBeenCalledTimes(1)
  })

  // A commercial installation does not see the notice; the license relationship covers it. The backend
  // reports that through noticeRequired.
  it('renders nothing when the notice is not required', async () => {
    noticeRequired.value = false

    const wrapper = await mountNotice()
    await flushPromises()

    expect(wrapper.find('[data-testid="usage-statistics-notice"]').exists()).toBe(false)
    expect(recordNoticeShownMock).not.toHaveBeenCalled()
  })

  it('is not shown to someone who is not a platform administrator', async () => {
    isAdmin.value = false

    const wrapper = await mountNotice()
    await flushPromises()

    expect(wrapper.find('[data-testid="usage-statistics-notice"]').exists()).toBe(false)
    expect(loadMock).not.toHaveBeenCalled()
  })

  // A gate that stays shut because a call failed means nothing is sent, which is the safe direction. The
  // component retries rather than latching on the failed attempt.
  it('tries again after a failure to record the notice', async () => {
    recordNoticeShownMock.mockRejectedValueOnce(new Error('offline'))

    const wrapper = await mountNotice()
    await flushPromises()

    expect(wrapper.find('[data-testid="usage-statistics-notice"]').exists()).toBe(true)
    const afterFirstMount = recordNoticeShownMock.mock.calls.length

    settings.value = null
    const second = await mountNotice()
    await flushPromises()

    expect(second.find('[data-testid="usage-statistics-notice"]').exists()).toBe(true)
    expect(recordNoticeShownMock.mock.calls.length).toBeGreaterThan(afterFirstMount)
  })
})
