import { beforeEach, describe, expect, it, vi } from 'vitest'

const getMock = vi.fn()
const patchMock = vi.fn()
const postMock = vi.fn()

vi.mock('@/services/api', () => ({
  createAdminClient: () => ({ GET: getMock, PATCH: patchMock, POST: postMock }),
  getApiErrorMessage: (_error: unknown, fallback: string) => fallback,
}))

function ok(data: unknown) {
  return { data, error: undefined, response: { ok: true } }
}

function failed() {
  return { data: undefined, error: { error: 'nope' }, response: { ok: false } }
}

describe('usageStatisticsService', () => {
  beforeEach(() => {
    vi.clearAllMocks()
  })

  it('reads the current setting', async () => {
    const { getUsageStatisticsSettings } = await import('@/services/usageStatisticsService')
    getMock.mockResolvedValue(ok({ enabled: true }))

    const settings = await getUsageStatisticsSettings()

    expect(getMock).toHaveBeenCalledWith('/admin/usage-statistics', {})
    expect(settings.enabled).toBe(true)
  })

  it('sends the requested state when the toggle changes', async () => {
    const { setUsageStatisticsEnabled } = await import('@/services/usageStatisticsService')
    patchMock.mockResolvedValue(ok({ enabled: false }))

    await setUsageStatisticsEnabled(false)

    expect(patchMock).toHaveBeenCalledWith('/admin/usage-statistics', { body: { enabled: false } })
  })

  it('reads the payload preview from its own endpoint', async () => {
    const { getUsageStatisticsPreview } = await import('@/services/usageStatisticsService')
    getMock.mockResolvedValue(ok({ payload: '{}' }))

    await getUsageStatisticsPreview()

    expect(getMock).toHaveBeenCalledWith('/admin/usage-statistics/preview', {})
  })

  it('asks the backend to send now', async () => {
    const { sendUsageStatisticsNow } = await import('@/services/usageStatisticsService')
    postMock.mockResolvedValue(ok({ decision: 'sent', settings: { enabled: true } }))

    const result = await sendUsageStatisticsNow()

    expect(postMock).toHaveBeenCalledWith('/admin/usage-statistics/send', {})
    expect(result.decision).toBe('sent')
  })

  it('records the notice and its dismissal separately', async () => {
    const { dismissUsageStatisticsNotice, recordUsageStatisticsNoticeShown } =
      await import('@/services/usageStatisticsService')
    postMock.mockResolvedValue(ok({ noticeRequired: false }))

    await recordUsageStatisticsNoticeShown()
    await dismissUsageStatisticsNotice()

    expect(postMock).toHaveBeenNthCalledWith(1, '/admin/usage-statistics/notice/shown', {})
    expect(postMock).toHaveBeenNthCalledWith(2, '/admin/usage-statistics/notice/dismiss', {})
  })

  it('turns a refused request into a readable failure', async () => {
    const { getUsageStatisticsSettings } = await import('@/services/usageStatisticsService')
    getMock.mockResolvedValue(failed())

    await expect(getUsageStatisticsSettings()).rejects.toThrow(
      'Failed to load the anonymous usage statistics setting.',
    )
  })

  it('pretty-prints the payload for reading', async () => {
    const { formatPayloadForDisplay } = await import('@/services/usageStatisticsService')

    expect(formatPayloadForDisplay('{"a":1}')).toBe('{\n  "a": 1\n}')
  })

  // The preview is whatever the sender would post. If that is ever not JSON, it is shown verbatim rather than
  // replaced by an error.
  it('shows an unparseable payload as it arrived', async () => {
    const { formatPayloadForDisplay } = await import('@/services/usageStatisticsService')

    expect(formatPayloadForDisplay('not json')).toBe('not json')
  })
})
