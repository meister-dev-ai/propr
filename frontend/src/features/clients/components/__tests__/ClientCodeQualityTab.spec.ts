// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { describe, expect, it, vi, beforeEach } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import ClientCodeQualityTab from '@/features/clients/components/ClientCodeQualityTab.vue'
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
const survivalMock = vi.fn()
const hotspotsMock = vi.fn()
const directoryMock = vi.fn()

vi.mock('@/services/codeInsightsAnalyticsService', () => ({
  fetchTypesOverTime: (...args: unknown[]) => typesMock(...args),
  fetchConcentration: (...args: unknown[]) => concentrationMock(...args),
  fetchSurvival: (...args: unknown[]) => survivalMock(...args),
  fetchHotspots: (...args: unknown[]) => hotspotsMock(...args),
  fetchRepositoryDirectory: (...args: unknown[]) => directoryMock(...args),
  fetchFindings: vi.fn(),
}))

const DIRECTORY = {
  totalFindings: 5,
  repositories: 1,
  pullRequests: 3,
  averagePerPullRequest: 5 / 3,
  rows: [
    {
      clientId: 'client-a',
      clientName: 'Client A',
      repositoryId: 'only-repo',
      repositoryName: 'only-repo',
      findings: 5,
      pullRequests: 3,
      files: 4,
      averagePerPullRequest: 5 / 3,
      lastActivityOn: '2026-07-20',
    },
  ],
}

describe('ClientCodeQualityTab', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    typesMock.mockResolvedValue({ points: [], totalFindings: 0, keys: [] })
    // The one repository the client has, so the tab reads it straight away rather than asking for a choice of one.
    concentrationMock.mockResolvedValue([
      {
        clientId: 'client-a',
        clientName: 'Client A',
        repositoryId: 'only-repo',
        repositoryName: 'only-repo',
        pullRequestId: null,
        filePath: null,
        count: 5,
      },
    ])
    survivalMock.mockResolvedValue({
      total: { persisted: 0, fixed: 0, dropped: 0, total: 0, persistenceRate: null, pullRequests: 0 },
      pullRequests: [],
    })
    hotspotsMock.mockResolvedValue({
      totalFindings: 0,
      pullRequests: 0,
      averagePerPullRequest: null,
      fileCount: 0,
      files: [],
      unplacedFindings: 0,
    })
    directoryMock.mockResolvedValue(DIRECTORY)
  })

  it('pins every read to the client whose page it is on', async () => {
    // The whole point of the per-client tab: the same workspace, one scope narrower, without the operator
    // having to set a filter that the page already knows.
    const wrapper = mount(ClientCodeQualityTab, {
      props: { clientId: 'client-a' },
      global: { stubs: { RouterLink: { template: '<a><slot /></a>' } } },
    })
    await flushPromises()

    expect((typesMock.mock.calls[0][0] as CodeInsightScope).clientId).toBe('client-a')
    expect((concentrationMock.mock.calls[0][0] as CodeInsightScope).clientId).toBe('client-a')
    expect((survivalMock.mock.calls[0][0] as CodeInsightScope).clientId).toBe('client-a')
    expect(wrapper.text()).toContain('pinned to this client')
  })

  it('shares one implementation with the top-level area', async () => {
    // Asserted structurally: the tab renders the same workspace, so a fix to either lands on both.
    const wrapper = mount(ClientCodeQualityTab, {
      props: { clientId: 'client-a' },
      global: { stubs: { RouterLink: { template: '<a><slot /></a>' } } },
    })
    await flushPromises()

    expect(wrapper.find('.code-quality-workspace').exists()).toBe(true)
    // A client with a single repository has no choice to make, so the reading starts immediately and the switcher
    // (which only exists to move between several) is absent.
    expect(wrapper.find('.section-tabs').exists()).toBe(true)
    expect(wrapper.find('#quality-repository').exists()).toBe(false)
  })
})
