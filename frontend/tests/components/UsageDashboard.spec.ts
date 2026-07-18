// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'

const getClientTokenUsageMock = vi.fn()
const getProCursorClientTokenUsageMock = vi.fn()
const getProCursorTopSourcesMock = vi.fn()
const exportProCursorTokenUsageCsvMock = vi.fn()
const anchorClickMock = vi.fn()

vi.mock('@/composables/useSession', () => ({
  useSession: () => ({
    getAccessToken: () => 'test-token',
  }),
}))

vi.mock('@/services/clientTokenUsageService', () => ({
  getClientTokenUsage: getClientTokenUsageMock,
}))

vi.mock('@/services/proCursorService', () => ({
  getProCursorClientTokenUsage: getProCursorClientTokenUsageMock,
  getProCursorTopSources: getProCursorTopSourcesMock,
  exportProCursorTokenUsageCsv: exportProCursorTokenUsageCsvMock,
}))

vi.mock('vue-chartjs', () => ({
  Line: {
    name: 'LineChartStub',
    template: '<div class="line-chart-stub" />',
  },
}))

vi.mock('chart.js', () => ({
  Chart: { register: vi.fn() },
  CategoryScale: {},
  LinearScale: {},
  PointElement: {},
  LineElement: {},
  Title: {},
  Tooltip: {},
  Legend: {},
  Filler: {},
}))

describe('UsageDashboard', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(anchorClickMock)
    getClientTokenUsageMock.mockResolvedValue({
      clientId: 'client-1',
      from: '2026-03-01',
      to: '2026-03-30',
      totalInputTokens: 900,
      totalOutputTokens: 150,
      totalCachedInputTokens: 300,
      totalCacheWriteTokens: 0,
      totalReasoningTokens: 60,
      samples: [
        {
          connectionCategory: 0,
          modelId: 'gpt-4o',
          date: '2026-03-01',
          inputTokens: 900,
          outputTokens: 150,
          cachedInputTokens: 300,
          cacheWriteTokens: 0,
          reasoningTokens: 60,
        },
      ],
    })
    getProCursorTopSourcesMock.mockResolvedValue({
      clientId: 'client-1',
      period: '30d',
      items: [
        {
          rank: 1,
          sourceId: 'source-1',
          sourceDisplayName: 'Platform Wiki',
          totalTokens: 93800,
          estimatedCostUsd: 0.55,
          estimatedEventCount: 4,
        },
      ],
    })
    getProCursorClientTokenUsageMock.mockResolvedValue({
      clientId: 'client-1',
      from: '2026-03-01',
      to: '2026-03-30',
      granularity: 'daily',
      groupBy: 'source',
      totals: {
        promptTokens: 161204,
        completionTokens: 12854,
        totalTokens: 174058,
        estimatedCostUsd: 1.104322,
        eventCount: 148,
        estimatedEventCount: 19,
      },
      series: [
        {
          bucketStart: '2026-03-01',
          promptTokens: 12000,
          completionTokens: 0,
          totalTokens: 12000,
          estimatedCostUsd: 0.024,
          breakdown: [
            {
              sourceId: 'source-1',
              sourceDisplayName: 'Platform Wiki',
              modelName: 'text-embedding-3-small',
              promptTokens: 12000,
              completionTokens: 0,
              totalTokens: 12000,
              estimatedCostUsd: 0.024,
              estimated: false,
            },
          ],
        },
      ],
      topSources: [
        {
          rank: 1,
          sourceId: 'source-1',
          sourceDisplayName: 'Platform Wiki',
          totalTokens: 93800,
          estimatedCostUsd: 0.55,
          estimatedEventCount: 4,
        },
      ],
      includesEstimatedUsage: true,
      lastRollupCompletedAtUtc: '2026-03-30T12:00:00Z',
    })
    exportProCursorTokenUsageCsvMock.mockResolvedValue('date,sourceId,sourceDisplayName\n2026-03-01,source-1,Platform Wiki')
    // Keep URL a real constructor (vite 8/jsdom uses `new URL` during render); only stub the static blob helpers.
    vi.stubGlobal('URL', Object.assign(globalThis.URL, {
      createObjectURL: vi.fn(() => 'blob:test'),
      revokeObjectURL: vi.fn(),
    }))
  })

  afterEach(() => {
    vi.restoreAllMocks()
  })

  it('renders ProCursor totals, the estimated-usage banner, and top sources', async () => {
    const { default: UsageDashboard } = await import('@/components/UsageDashboard.vue')
    const wrapper = mount(UsageDashboard, {
      props: { clientId: 'client-1' },
      global: {
        stubs: {
          ProgressOrb: { template: '<div class="orb-stub" />' },
        },
      },
    })

    await flushPromises()

    expect(getProCursorClientTokenUsageMock).toHaveBeenCalledWith(
      'client-1',
      expect.objectContaining({ granularity: 'daily', groupBy: 'source' }),
    )
    expect(getProCursorTopSourcesMock).toHaveBeenCalledWith('client-1', '30d', 5)
    expect(wrapper.text()).toContain('ProCursor Usage')
    expect(wrapper.text()).toContain('174,058')
    expect(wrapper.text()).toContain('$1.10')
    expect(wrapper.text()).toContain('19 events used estimated token counts')
    expect(wrapper.text()).toContain('Platform Wiki')
  })

  it('renders reviewer input, output, cached, and reasoning token totals', async () => {
    const { default: UsageDashboard } = await import('@/components/UsageDashboard.vue')
    const wrapper = mount(UsageDashboard, {
      props: { clientId: 'client-1' },
      global: {
        stubs: {
          ProgressOrb: { template: '<div class="orb-stub" />' },
        },
      },
    })

    await flushPromises()

    const text = wrapper.text()
    expect(text).toContain('Input Tokens')
    expect(text).toContain('Output Tokens')
    expect(text).toContain('Cached Input Tokens')
    expect(text).toContain('Reasoning Tokens')
    expect(text).toContain('900')
    expect(text).toContain('300')
    expect(text).toContain('60')
  })

  it('exports the selected ProCursor range as CSV', async () => {
    const { default: UsageDashboard } = await import('@/components/UsageDashboard.vue')
    const wrapper = mount(UsageDashboard, {
      props: { clientId: 'client-1' },
      global: {
        stubs: {
          ProgressOrb: { template: '<div class="orb-stub" />' },
        },
      },
    })

    await flushPromises()

    const exportButton = wrapper.findAll('button').find((candidate) => candidate.text().includes('Export CSV'))
    expect(exportButton).toBeDefined()
    await exportButton!.trigger('click')
    await flushPromises()

    expect(exportProCursorTokenUsageCsvMock).toHaveBeenCalledWith(
      'client-1',
      expect.objectContaining({
        from: expect.any(String),
        to: expect.any(String),
      }),
    )
  })
})
