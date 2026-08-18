import { flushPromises, mount } from '@vue/test-utils'
import { computed, ref } from 'vue'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import type { UsageStatisticsSettings } from '@/services/usageStatisticsService'

const settings = ref<UsageStatisticsSettings | null>(null)
const loadMock = vi.fn().mockResolvedValue(undefined)
const setEnabledMock = vi.fn().mockResolvedValue(undefined)
const sendNowMock = vi.fn().mockResolvedValue('sent')
const notifyMock = vi.fn()
const getPreviewMock = vi.fn()

const loading = ref(false)

vi.mock('@/composables/useUsageStatistics', () => ({
  useUsageStatistics: () => ({
    settings,
    loading: computed(() => loading.value),
    load: loadMock,
    setEnabled: setEnabledMock,
    sendNow: sendNowMock,
  }),
}))

vi.mock('@/composables/useNotification', () => ({
  useNotification: () => ({ notify: notifyMock }),
}))

vi.mock('@/services/usageStatisticsService', async () => {
  const actual = await vi.importActual<typeof import('@/services/usageStatisticsService')>(
    '@/services/usageStatisticsService',
  )

  return {
    ...actual,
    getUsageStatisticsPreview: getPreviewMock,
  }
})

function buildSettings(overrides: Partial<UsageStatisticsSettings> = {}): UsageStatisticsSettings {
  return {
    edition: 'community',
    enabled: true,
    communityOptIn: true,
    managedByLicense: false,
    consentGateSatisfied: true,
    noticeRequired: false,
    lastAttemptAt: '2026-08-16T06:00:00Z',
    lastAttemptSucceeded: true,
    lastAttemptDetail: 'Delivered.',
    lastSuccessAt: '2026-08-16T06:00:00Z',
    pingEndpoint: 'https://telemetry.meister-dev.ai/v1/ping',
    payloadDocumentationUrl: 'https://example.invalid/payload',
    privacyContact: 'privacy@meister-dev.ai',
    update: {
      currentVersion: '1.0.0.alpha.0049',
      latestVersion: null,
      updateAvailable: false,
      advisories: [],
      receivedAt: null,
    },
    ...overrides,
  }
}

async function mountView() {
  const { default: UsageStatisticsView } =
    await import('@/features/usage-statistics/views/UsageStatisticsView.vue')

  return mount(UsageStatisticsView)
}

describe('UsageStatisticsView', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    settings.value = buildSettings()
    getPreviewMock.mockResolvedValue({
      endpoint: 'https://telemetry.meister-dev.ai/v1/ping',
      contentType: 'application/json',
      payload: '{"schemaVersion":1,"edition":"community"}',
      payloadDocumentationUrl: 'https://example.invalid/payload',
    })
  })

  it('shows the endpoint, the documentation link and the privacy contact', async () => {
    const wrapper = await mountView()
    await flushPromises()

    const text = wrapper.text()
    expect(text).toContain('https://telemetry.meister-dev.ai/v1/ping')
    expect(text).toContain('privacy@meister-dev.ai')
    expect(text).toContain('Payload documentation')
  })

  it('turns the setting off on request', async () => {
    const wrapper = await mountView()
    await flushPromises()

    await wrapper.get('[data-testid="usage-statistics-toggle"] input').setValue(false)
    await flushPromises()

    expect(setEnabledMock).toHaveBeenCalledWith(false)
  })

  // The control stays visible under a license rather than disappearing, so administrators can see the
  // current state.
  it('renders the control visible but locked under a commercial license', async () => {
    settings.value = buildSettings({ edition: 'commercial', managedByLicense: true, communityOptIn: false })

    const wrapper = await mountView()
    await flushPromises()

    const input = wrapper.get('[data-testid="usage-statistics-toggle"] input')
    expect(input.attributes('disabled')).toBeDefined()
    expect(wrapper.find('[data-testid="usage-statistics-locked-note"]').exists()).toBe(true)
    expect(wrapper.get('[data-testid="usage-statistics-control"]').text()).toContain('commercial license')
  })

  // A disabled input cannot be triggered, so this dispatches the change event directly at the element to
  // exercise the guard.
  it('does not attempt to change a locked setting even when the event is dispatched directly', async () => {
    settings.value = buildSettings({ edition: 'commercial', managedByLicense: true })

    const wrapper = await mountView()
    await flushPromises()

    wrapper.get('[data-testid="usage-statistics-toggle"] input').element
      .dispatchEvent(new Event('change'))
    await flushPromises()

    expect(setEnabledMock).not.toHaveBeenCalled()
  })

  // The browser flips the box itself on click. The bound value has not changed after a failure, so Vue sees
  // nothing to patch and the box would otherwise sit checked next to the word "Off".
  it('puts the box back when the change is refused', async () => {
    setEnabledMock.mockRejectedValueOnce(new Error('The setting could not be changed.'))

    const wrapper = await mountView()
    await flushPromises()

    const input = wrapper.get('[data-testid="usage-statistics-toggle"] input')
    const element = input.element as HTMLInputElement
    element.checked = false
    await input.trigger('change')
    await flushPromises()

    expect(element.checked).toBe(true)
    expect(wrapper.get('[data-testid="usage-statistics-error"]').text())
      .toContain('The setting could not be changed.')
  })

  // The page reports that the state is unknown rather than rendering its defaults as an off-and-unlocked
  // state.
  it('reports that the setting could not be read', async () => {
    settings.value = null

    const wrapper = await mountView()
    await flushPromises()

    expect(wrapper.find('[data-testid="usage-statistics-unavailable"]').exists()).toBe(true)
    expect(wrapper.find('[data-testid="usage-statistics-toggle"]').exists()).toBe(false)
    expect(wrapper.find('[data-testid="usage-statistics-last-attempt"]').exists()).toBe(false)
  })

  // The link comes from the receiver and is stored as it was sent, so a compromised receiver must not be able
  // to supply a script URL.
  it('refuses to link an advisory whose target is not a web address', async () => {
    settings.value = buildSettings({
      update: {
        currentVersion: '1.0.0.alpha.0049',
        latestVersion: null,
        updateAvailable: false,
        advisories: [
          // eslint-disable-next-line no-script-url
          { id: 'PROPR-2026-0002', severity: 'high', title: 'Hostile', link: 'javascript:alert(1)' },
        ],
        receivedAt: '2026-08-16T06:00:00Z',
      },
    })

    const wrapper = await mountView()
    await flushPromises()

    const advisories = wrapper.get('[data-testid="usage-statistics-advisories"]')
    expect(advisories.text()).toContain('Hostile')
    expect(advisories.find('a').exists()).toBe(false)
  })

  it('reports the last send attempt', async () => {
    const wrapper = await mountView()
    await flushPromises()

    expect(wrapper.get('[data-testid="usage-statistics-last-attempt"]').text())
      .toContain('Last snapshot delivered')
  })

  // The outcome detail is only shown when the attempt failed; on a success it repeated the word "delivered".
  it('reports a failed attempt with the detail behind it', async () => {
    settings.value = buildSettings({
      lastAttemptSucceeded: false,
      lastAttemptDetail: 'The receiver could not be reached.',
      lastSuccessAt: null,
    })

    const wrapper = await mountView()
    await flushPromises()

    const text = wrapper.get('[data-testid="usage-statistics-last-attempt"]').text()
    expect(text).toContain('did not reach the receiver')
    expect(text).toContain('The receiver could not be reached.')
  })

  it('reports that nothing has been sent yet', async () => {
    settings.value = buildSettings({ lastAttemptAt: null, lastAttemptSucceeded: null, lastAttemptDetail: null })

    const wrapper = await mountView()
    await flushPromises()

    expect(wrapper.get('[data-testid="usage-statistics-last-attempt"]').text())
      .toContain('No snapshot has been sent yet')
  })

  it('sends a snapshot on request and reports it', async () => {
    const wrapper = await mountView()
    await flushPromises()

    await wrapper.get('[data-testid="usage-statistics-send-now"]').trigger('click')
    await flushPromises()

    expect(sendNowMock).toHaveBeenCalledTimes(1)
    expect(notifyMock).toHaveBeenCalledWith('Snapshot sent.')
  })

  // Each outcome in which nothing was sent is reported separately rather than as a single "done".
  it.each([
    ['disabled', 'switched off'],
    ['awaitingConsent', 'notice has not been shown'],
    ['notDue', 'already went out today'],
  ])('reports the %s outcome separately', async (decision, expected) => {
    sendNowMock.mockResolvedValueOnce(decision)

    const wrapper = await mountView()
    await flushPromises()

    await wrapper.get('[data-testid="usage-statistics-send-now"]').trigger('click')
    await flushPromises()

    expect(notifyMock).toHaveBeenCalledWith(expect.stringContaining(expected))
  })

  it('reports a failed send without breaking the page', async () => {
    sendNowMock.mockRejectedValueOnce(new Error('The snapshot could not be sent.'))

    const wrapper = await mountView()
    await flushPromises()

    await wrapper.get('[data-testid="usage-statistics-send-now"]').trigger('click')
    await flushPromises()

    expect(wrapper.get('[data-testid="usage-statistics-error"]').text())
      .toContain('The snapshot could not be sent.')
  })

  it('renders the payload preview it was given', async () => {
    const wrapper = await mountView()
    await flushPromises()

    const payload = wrapper.get('[data-testid="usage-statistics-preview-payload"]').text()
    expect(payload).toContain('"schemaVersion": 1')
    expect(payload).toContain('"edition": "community"')
  })

  it('lists an advisory the receiver reported', async () => {
    settings.value = buildSettings({
      update: {
        currentVersion: '1.0.0.alpha.0049',
        latestVersion: '1.0.0.alpha.0050',
        updateAvailable: true,
        advisories: [
          { id: 'PROPR-2026-0001', severity: 'high', title: 'A thing', link: 'https://example.invalid/a' },
        ],
        receivedAt: '2026-08-16T06:00:00Z',
      },
    })

    const wrapper = await mountView()
    await flushPromises()

    const advisories = wrapper.get('[data-testid="usage-statistics-advisories"]').text()
    expect(advisories).toContain('A thing')
    expect(advisories).toContain('high')
  })

  it('reports a failed preview without breaking the page', async () => {
    getPreviewMock.mockRejectedValueOnce(new Error('The payload preview could not be built.'))

    const wrapper = await mountView()
    await flushPromises()

    expect(wrapper.get('[data-testid="usage-statistics-preview-error"]').text())
      .toContain('The payload preview could not be built.')
    expect(wrapper.find('[data-testid="usage-statistics-control"]').exists()).toBe(true)
  })
})
