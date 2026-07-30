// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { describe, expect, it, vi, beforeEach } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import CodeQualityWorkspace from '@/features/code-insights/components/CodeQualityWorkspace.vue'
import type { CodeInsightScope } from '@/services/codeInsightsAnalyticsService'

vi.mock('vue-chartjs', () => ({
  Bar: { name: 'BarChartStub', template: '<div class="bar-chart-stub" />' },
  Line: { name: 'LineChartStub', template: '<div class="line-chart-stub" />' },
}))

vi.mock('chart.js', () => ({
  Chart: { register: () => {} },
  BarElement: {},
  CategoryScale: {},
  LinearScale: {},
  PointElement: {},
  LineElement: {},
  Title: {},
  Tooltip: {},
  Legend: {},
  Filler: {},
}))

const typesMock = vi.fn()
const concentrationMock = vi.fn()
const findingsMock = vi.fn()
const survivalMock = vi.fn()
const hotspotsMock = vi.fn()

vi.mock('@/services/codeInsightsAnalyticsService', () => ({
  fetchTypesOverTime: (...args: unknown[]) => typesMock(...args),
  fetchConcentration: (...args: unknown[]) => concentrationMock(...args),
  fetchFindings: (...args: unknown[]) => findingsMock(...args),
  fetchSurvival: (...args: unknown[]) => survivalMock(...args),
  fetchHotspots: (...args: unknown[]) => hotspotsMock(...args),
}))

const TYPES = {
  points: [{ bucketStart: '2026-06-01', key: 'logic-error', count: 2 }],
  totalFindings: 2,
  keys: ['logic-error'],
}

const FILES = [
  {
    clientId: 'client-a',
    clientName: 'Client A',
    repositoryId: '4',
    repositoryName: 'payments-api',
    pullRequestId: null,
    filePath: 'src/Service.cs',
    count: 2,
  },
]

async function mountPinned() {
  const wrapper = mount(CodeQualityWorkspace, {
    props: { clientId: 'client-a', repositoryId: '4', pullRequestId: 4821 },
  })
  await flushPromises()
  return wrapper
}

describe('CodeQualityWorkspace pinned to one pull request', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    typesMock.mockResolvedValue(TYPES)
    concentrationMock.mockResolvedValue(FILES)
    findingsMock.mockResolvedValue([])
    survivalMock.mockResolvedValue({
      total: { persisted: 2, fixed: 1, dropped: 1, total: 4, persistenceRate: 0.5, pullRequests: 1 },
      pullRequests: [],
    })
    hotspotsMock.mockResolvedValue({
      totalFindings: 31,
      pullRequests: 11,
      averagePerPullRequest: 31 / 11,
      fileCount: 1,
      unplacedFindings: 0,
      files: [{ filePath: 'src/Service.cs', symbolName: null, findings: 31, pullRequests: 11, averagePerPullRequest: 31 / 11 }],
    })
  })

  it('sends the pull request on every read', async () => {
    await mountPinned()

    for (const mock of [typesMock, concentrationMock, survivalMock]) {
      const scope = mock.mock.calls[0][0] as CodeInsightScope
      expect(scope.pullRequestId).toBe(4821)
      expect(scope.repositoryId).toBe('4')
      expect(scope.clientId).toBe('client-a')
    }
  })

  it('never ranks repositories, because the one being looked at is already known', async () => {
    // Ranking would land the view on the busiest repository of the whole client instead of this one.
    await mountPinned()

    expect(concentrationMock.mock.calls.every((call) => call[1] === 'file')).toBe(true)
  })

  it('asks the hotspots for these files across every pull request, not just this one', async () => {
    // The reason the tab is worth having inside a review: what have these files produced before today.
    await mountPinned()

    expect(hotspotsMock.mock.calls[0][1]).toMatchObject({ filesFromPullRequestId: 4821 })
  })

  it('offers no repository picker and no window, because it cannot honour either', async () => {
    const wrapper = await mountPinned()

    expect(wrapper.find('#quality-repository').exists()).toBe(false)
    expect(wrapper.find('#quality-from').exists()).toBe(false)
    expect(wrapper.find('form').exists()).toBe(false)
  })

  it('asks for a window wide enough that the pull request is the filter', async () => {
    // A review from last year must not read as a review that found nothing.
    await mountPinned()

    const scope = typesMock.mock.calls[0][0] as CodeInsightScope
    const years = (Date.parse(scope.to) - Date.parse(scope.from)) / (365 * 86_400_000)

    expect(years).toBeGreaterThan(5)
  })

  it('keeps a drill-through inside the pull request', async () => {
    const wrapper = await mountPinned()

    await wrapper.findAll('.type-drill')[0].trigger('click')
    await flushPromises()

    expect((findingsMock.mock.calls[0][0] as CodeInsightScope).pullRequestId).toBe(4821)
  })

  it('still answers every question about this pull request', async () => {
    const wrapper = await mountPinned()

    expect(wrapper.text()).toContain('Finding types over time')

    await wrapper.findAll('.section-tab')[1].trigger('click')
    expect(wrapper.text()).toContain('src/Service.cs')

    await wrapper.findAll('.section-tab')[2].trigger('click')
    expect(wrapper.text()).toContain('Hotspots')
    expect(wrapper.text()).toContain('before today')

    await wrapper.findAll('.section-tab')[3].trigger('click')
    expect(wrapper.text()).toContain('Finding persistence')
  })

  it('leaves the pull request behind when the drill is about a file\'s history', async () => {
    // The hotspot number counts every pull request, so the findings behind it have to as well: otherwise the
    // drill contradicts the number that was clicked.
    const wrapper = await mountPinned()
    await wrapper.findAll('.section-tab')[2].trigger('click')

    await wrapper.findAll('.flame-frame:not(.flame-frame--root)')[0].trigger('click')
    await flushPromises()

    const scope = findingsMock.mock.calls[0][0] as CodeInsightScope
    expect(scope.pullRequestId).toBeNull()
    expect(scope.filePath).toBe('src/Service.cs')
  })
})
