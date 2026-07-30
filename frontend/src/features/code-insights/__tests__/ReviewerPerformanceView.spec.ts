// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { describe, expect, it, vi, beforeEach } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import ReviewerPerformanceView from '@/features/code-insights/views/ReviewerPerformanceView.vue'
import type { CodeInsightMiss, CodeInsightQuality, CodeInsightScope } from '@/services/codeInsightsAnalyticsService'

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

const qualityMock = vi.fn()
const missesMock = vi.fn()
const reviewerFindingsMock = vi.fn()
const coverageMock = vi.fn()
const rejectionReasonsMock = vi.fn()

/**
 * Half of what the reviews produced is collected, and only one of the two pull requests still has its threads.
 * That is the shape of an installation that switched collection on partway through the window.
 */
const COVERAGE = {
  reviewJobs: 4,
  jobsCollected: 2,
  producedFindings: 40,
  collectedFindings: 20,
  pullRequests: 2,
  pullRequestsRetained: 1,
  clientsWithCollectionOff: 1,
  rows: [
    {
      clientId: 'client-a',
      clientName: 'Client A',
      repositoryId: 'payments-api',
      repositoryName: 'payments-api',
      reviewJobs: 4,
      jobsCollected: 2,
      producedFindings: 40,
      collectedFindings: 20,
      pullRequests: 2,
      pullRequestsRetained: 1,
      retainedThreads: 9,
      dispositions: 6,
      misses: 3,
      pullRequestsSealed: 1,
    },
  ],
}
const byGrainMock = vi.fn()

vi.mock('@/services/codeInsightsAnalyticsService', () => ({
  fetchQuality: (...args: unknown[]) => qualityMock(...args),
  fetchMisses: (...args: unknown[]) => missesMock(...args),
  fetchCoverage: (...args: unknown[]) => coverageMock(...args),
  fetchRejectionReasons: (...args: unknown[]) => rejectionReasonsMock(...args),
  fetchReviewerFindings: (...args: unknown[]) => reviewerFindingsMock(...args),
  fetchReviewerPerformanceByGrain: (...args: unknown[]) => byGrainMock(...args),
  fetchTypesOverTime: vi.fn(),
  fetchConcentration: vi.fn(),
  fetchFindings: vi.fn(),
}))

const REJECTION_REASONS = {
  reasons: [
    { reason: 'Wrong' as const, count: 9 },
    { reason: 'DesignTradeOff' as const, count: 4 },
    { reason: 'OutOfScope' as const, count: 2 },
  ],
  unclassified: 5,
  rejections: 20,
  byConcernClass: [
    {
      concernClass: 'Functional' as const,
      reasons: [
        { reason: 'Wrong' as const, count: 8 },
        { reason: 'OutOfScope' as const, count: 2 },
      ],
      unclassified: 2,
      rejections: 12,
    },
    {
      concernClass: 'Evolvability' as const,
      reasons: [
        { reason: 'DesignTradeOff' as const, count: 4 },
        { reason: 'Wrong' as const, count: 1 },
      ],
      unclassified: 3,
      rejections: 8,
    },
  ],
}

const BY_MODEL = [
  {
    clientId: '',
    clientName: null,
    repositoryId: null,
    pullRequestId: null,
    modelId: 'gpt-5.4-mini',
    logicalModelName: 'thrifty-reviewer',
    repositoryName: null,
    metric: { ...metricDefaults(), precision: 0.61, acceptanceRate: 0.52, addressed: 34, falsePositive: 35, sampleSize: 90 },
  },
  {
    clientId: '',
    clientName: null,
    repositoryId: null,
    pullRequestId: null,
    modelId: null,
    logicalModelName: null,
    repositoryName: null,
    metric: { ...metricDefaults(), precision: 0.8, acceptanceRate: 0.7, addressed: 5, falsePositive: 2, sampleSize: 40 },
  },
]

const BY_SCOPE = [
  {
    clientId: 'client-a',
    clientName: 'Acme Corp',
    repositoryId: 'quiet-service',
    repositoryName: null,
    pullRequestId: null,
    metric: { ...metricDefaults(), precision: 0.5, recall: 0.31, f1: 0.38, falsePositive: 9, misses: 20, sampleSize: 11 },
    modelId: null,
    logicalModelName: null,
  },
  {
    clientId: 'client-a',
    clientName: 'Acme Corp',
    repositoryId: '7',
    repositoryName: 'internal-tools',
    pullRequestId: null,
    metric: { ...metricDefaults(), precision: 1, recall: 1, f1: 1, falsePositive: 0, misses: 0, sampleSize: 3 },
    modelId: null,
    logicalModelName: null,
  },
]

function metricDefaults() {
  return {
    precision: null as number | null,
    recall: null as number | null,
    f1: null as number | null,
    acceptanceRate: null as number | null,
    addressed: 0,
    acknowledged: 0,
    dismissed: 0,
    falsePositive: 0,
    misses: 0,
    sampleSize: 0,
    discussed: 0,
  }
}

function metric(overrides: Partial<CodeInsightQuality['correctnessTotal']> = {}) {
  return {
    precision: 0.8,
    recall: 0.6,
    f1: 0.686,
    acceptanceRate: 0.75,
    addressed: 6,
    acknowledged: 2,
    dismissed: 1,
    falsePositive: 1,
    misses: 3,
    sampleSize: 12,
    discussed: 0,
    ...overrides,
  }
}

function quality(overrides: Partial<CodeInsightQuality> = {}): CodeInsightQuality {
  return {
    correctness: [
      { bucketStart: '2026-06-01', metric: metric() },
      { bucketStart: '2026-06-08', metric: metric({ f1: 0.75 }) },
    ],
    acceptance: [{ bucketStart: '2026-06-01', metric: metric() }],
    correctnessTotal: metric(),
    acceptanceTotal: metric({ sampleSize: 40 }),
    correctnessTrend: { direction: 'improving', tau: 0.86, pValue: 0.004, slopePerPeriod: 0.021, periods: 9 },
    acceptanceTrend: { direction: 'flat', tau: 0.12, pValue: 0.66, slopePerPeriod: 0.002, periods: 9 },
    minimumSampleSize: 10,
    minimumTrendPeriods: 8,
    ...overrides,
  }
}

const MISSES: CodeInsightMiss[] = [
  {
    id: 'miss-1',
    clientId: 'client-a',
    repositoryId: 'busy-repo',
    pullRequestId: 7,
    providerThreadId: 'thread-1',
    filePath: 'src/Service.cs',
    lineNumber: 42,
    discussion: 'alice: this drops the retry count',
    isSubstantive: true,
    wasActedOn: true,
    isInScope: true,
    countsAsMiss: true,
    classifierConfidence: 0.9,
    harvestedAt: '2026-06-02T10:00:00Z',
  },
  {
    id: 'miss-2',
    clientId: 'client-a',
    repositoryId: 'busy-repo',
    pullRequestId: 7,
    providerThreadId: 'thread-2',
    filePath: 'src/Service.cs',
    lineNumber: 50,
    discussion: 'bob: nit, rename this',
    isSubstantive: false,
    wasActedOn: true,
    isInScope: false,
    countsAsMiss: false,
    classifierConfidence: 0.8,
    harvestedAt: '2026-06-02T11:00:00Z',
  },
]

async function mountView() {
  const wrapper = mount(ReviewerPerformanceView, {
    global: { stubs: { RouterLink: { template: '<a><slot /></a>' } } },
  })
  await flushPromises()
  return wrapper
}

describe('ReviewerPerformanceView', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    qualityMock.mockResolvedValue(quality())
    missesMock.mockResolvedValue(MISSES)
    reviewerFindingsMock.mockResolvedValue([])
    byGrainMock.mockResolvedValue(BY_SCOPE)
    coverageMock.mockResolvedValue(COVERAGE)
    rejectionReasonsMock.mockResolvedValue(REJECTION_REASONS)
  })

  it('says up front that these numbers measure the reviewer and are AI-estimated', async () => {
    // The framing is load-bearing on this surface: the evidence underneath is uncalibrated model judgement.
    const wrapper = await mountView()

    expect(wrapper.text()).toContain('measures the reviewer, not')
    expect(wrapper.text()).toContain('AI-estimated')
  })

  it('answers all four operator questions', async () => {
    const wrapper = await mountView()

    expect(wrapper.text()).toContain('Quality trend')
    expect(wrapper.text()).toContain('68.6%')

    await wrapper.findAll('.section-tab')[1].trigger('click')
    expect(wrapper.text()).toContain('Correctness by scope')

    await wrapper.findAll('.section-tab')[2].trigger('click')
    expect(wrapper.text()).toContain('Acceptance rate')

    await wrapper.findAll('.section-tab')[3].trigger('click')
    expect(wrapper.text()).toContain('Counts as a miss')
    expect(wrapper.text()).toContain('Excluded')
  })

  it('groups correctness by scope, and suppresses a scope below the sample floor', async () => {
    // A ranked table is the easiest place to read a thin number as a verdict, so the floor applies here too.
    const wrapper = await mountView()
    await wrapper.findAll('.section-tab')[1].trigger('click')

    const rows = wrapper.findAll('tbody tr')
    expect(rows[0].text()).toContain('quiet-service')
    expect(rows[0].text()).toContain('38.0%')
    // internal-tools rests on 3 sealed pull requests against a floor of 10. It shows its name rather than the
    // provider's "7", which beside a row of numbers would read as a count.
    expect(rows[1].text()).toContain('internal-tools')
    expect(rows[1].text()).not.toContain('100.0%')
    expect(rows[1].text()).toContain('of 10')
  })

  it('groups by the model that produced the findings, and says which columns stop applying', async () => {
    // The dimension that answers "would the cheap model have done". Recall and misses are left blank on purpose:
    // a miss is a problem no finding of ours described, so no model can be charged with it.
    byGrainMock.mockResolvedValue(BY_MODEL)

    const wrapper = await mountView()
    await wrapper.findAll('.section-tab')[1].trigger('click')

    await wrapper.find('#performance-grain').setValue('model')
    await flushPromises()

    expect(byGrainMock.mock.calls.at(-1)![1]).toBe('model')

    const rows = wrapper.findAll('tbody tr')
    expect(rows[0].text()).toContain('thrifty-reviewer')
    // Both identities, since a configured name can be repointed at another model.
    expect(rows[0].text()).toContain('gpt-5.4-mini')
    expect(rows[0].text()).toContain('61.0%')

    // Recall, F1, and the miss count are dashes rather than zeroes, and the reason is on the page.
    const cells = rows[0].findAll('td').map((cell) => cell.text())
    expect(cells[0]).toBe('—')
    expect(cells[2]).toBe('—')
    expect(cells[4]).toBe('—')
    expect(wrapper.get('[data-testid="model-grain-note"]').text()).toContain('no model to charge it to')

    // The sample means something different here, so the column says so.
    expect(wrapper.find('thead').text()).toContain('Findings')
  })

  it('names the unattributed row rather than dropping the findings behind it', async () => {
    byGrainMock.mockResolvedValue(BY_MODEL)

    const wrapper = await mountView()
    await wrapper.findAll('.section-tab')[1].trigger('click')
    await wrapper.find('#performance-grain').setValue('model')
    await flushPromises()

    const last = wrapper.findAll('tbody tr')[1]
    expect(last.text()).toContain('Not recorded')
    expect(last.text()).toContain('Reviewed before models were recorded')
  })

  it('re-groups without a full reload when only the grain changes', async () => {
    const wrapper = await mountView()
    await wrapper.findAll('.section-tab')[1].trigger('click')

    await wrapper.find('#performance-grain').setValue('pullRequest')
    await flushPromises()

    expect(byGrainMock.mock.calls.at(-1)![1]).toBe('pullRequest')
    // The other reads were not repeated: the window did not move.
    expect(qualityMock).toHaveBeenCalledTimes(1)
  })

  it('defaults to a wider window than the code-quality views', async () => {
    // Correctness only moves when pull requests close, and thirty days of closes rarely clears the sample floor.
    await mountView()

    const scope = qualityMock.mock.calls[0][0] as CodeInsightScope
    const days = (Date.parse(scope.to) - Date.parse(scope.from)) / 86_400_000

    expect(days).toBeGreaterThan(30)
  })

  it('suppresses the correctness ratios below the minimum sample and says why', async () => {
    qualityMock.mockResolvedValue(
      quality({
        correctness: [
          { bucketStart: '2026-06-01', metric: metric({ sampleSize: 1 }) },
          { bucketStart: '2026-06-08', metric: metric({ f1: 0.75, sampleSize: 1 }) },
        ],
        correctnessTotal: metric({ sampleSize: 2 }),
        minimumSampleSize: 10,
      }),
    )

    const wrapper = await mountView()

    expect(wrapper.text()).toContain('Not enough closed pull requests')
    expect(wrapper.text()).toContain('2 of 10 needed')
    expect(wrapper.text()).not.toContain('68.6%')

    const table = wrapper.find('.chart-table table').text()
    expect(table).toContain('—')
    expect(table).not.toContain('%')
  })

  it('shows the size and the confidence of the trend beside the arrow', async () => {
    // An arrow on its own invites a decision the data may not support, so the slope and the p-value travel with
    // it: a fifth of a point per week at p = 0.004 is a claim a reader can argue with.
    const wrapper = await mountView()

    expect(wrapper.text()).toContain('Improving')
    expect(wrapper.text()).toContain('+2.1 points per period across 9 periods')
    expect(wrapper.text()).toContain('p = 0.004')
    expect(wrapper.text()).toContain("Kendall's Tau 0.86")
  })

  it('says how far a window is from testable rather than only that it is not', async () => {
    qualityMock.mockResolvedValue(
      quality({
        correctnessTrend: { direction: 'insufficient', tau: null, pValue: null, slopePerPeriod: null, periods: 3 },
      }),
    )

    const wrapper = await mountView()

    expect(wrapper.text()).toContain('Not enough data')
    expect(wrapper.text()).toContain('3 of 8 periods')
  })

  it('drills from an outcome into the findings behind it, through the operator endpoint', async () => {
    const wrapper = await mountView()

    await wrapper.findAll('.drill-button')[0].trigger('click')
    await flushPromises()

    expect(reviewerFindingsMock).toHaveBeenCalledTimes(1)
    expect(reviewerFindingsMock.mock.calls[0][1]).toMatchObject({ disposition: 'falsePositive' })
    expect(wrapper.text()).toContain('Findings judged wrong')
  })

  it('reloads with the new window when a slice changes', async () => {
    const wrapper = await mountView()

    await wrapper.find('#performance-repository').setValue('busy-repo')
    await wrapper.find('form').trigger('submit')
    await flushPromises()

    expect((qualityMock.mock.calls.at(-1)![0] as CodeInsightScope).repositoryId).toBe('busy-repo')
  })

  it('surfaces a refusal rather than rendering an empty page', async () => {
    // A plain client user is refused this surface server-side. That must not look like "no data yet".
    qualityMock.mockRejectedValue(new Error('Reviewer performance requires tenant administration.'))

    const wrapper = await mountView()

    expect(wrapper.find('[role="alert"]').text()).toContain('tenant administration')
  })

  it('says how much of the review history the collection is blind to', async () => {
    // Without this, an empty correctness reading and a reviewer that found nothing look identical.
    const wrapper = await mountView()

    await wrapper.findAll('.section-tab')[4].trigger('click')

    expect(wrapper.text()).toContain('What the collection knows about')
    expect(wrapper.text()).toContain('50%')
    expect(wrapper.text()).toContain('1 / 2')
    // Clients with collection switched off are a setting, not missing data, and are named as such.
    expect(wrapper.text()).toContain('switched off')
  })

  it('keeps every metric readable when the coverage read fails', async () => {
    // A backend that predates the coverage endpoint answers 404, and a shared load would have taken the whole
    // surface down with it: correctness, acceptance and the misses list all went blank behind one failed read.
    coverageMock.mockRejectedValue(new Error('Failed to load the collection coverage.'))

    const wrapper = await mountView()

    expect(wrapper.text()).toContain('Quality trend')
    expect(wrapper.text()).toContain('68.6%')
    expect(wrapper.find('.page-error').exists()).toBe(false)
  })

  it('counts findings left unresolved apart from the outcomes that are verdicts', async () => {
    // Neither accepted nor rejected. The count is visible so the volume of undetermined threads is known, and
    // the acceptance rate it sits beside does not move because of it.
    qualityMock.mockResolvedValue(
      quality({ acceptanceTotal: metric({ sampleSize: 40, discussed: 7, acceptanceRate: 0.75 }) }),
    )
    const wrapper = await mountView()

    await wrapper.findAll('.section-tab')[2].trigger('click')

    expect(wrapper.text()).toContain('Left unresolved')
    expect(wrapper.text()).toContain('75.0%')
    expect(wrapper.text()).toContain('40 resolved')
  })

  it('drills into the findings a human left unresolved', async () => {
    qualityMock.mockResolvedValue(
      quality({ acceptanceTotal: metric({ sampleSize: 40, discussed: 7 }) }),
    )
    const wrapper = await mountView()
    await wrapper.findAll('.section-tab')[2].trigger('click')

    const chips = wrapper.findAll('.outcome-chip')
    await chips[chips.length - 1].trigger('click')
    await flushPromises()

    const [, options] = reviewerFindingsMock.mock.calls[0] as [unknown, Record<string, unknown>]
    expect(options.disposition).toBe('discussed')
    expect(wrapper.text()).toContain('Findings a human left unresolved')
  })

  it('does not invite a drill into an outcome nothing reached', async () => {
    // An outcome recorded once and never re-judged starts at zero on every existing installation, so the chip
    // for it is the first thing a reader clicks. Opening a panel that says nothing matches reads like a
    // failure rather than like an empty set.
    qualityMock.mockResolvedValue(
      quality({ acceptanceTotal: metric({ sampleSize: 40, discussed: 0 }) }),
    )
    const wrapper = await mountView()
    await wrapper.findAll('.section-tab')[2].trigger('click')

    const chips = wrapper.findAll('.outcome-chip')
    const empty = chips[chips.length - 1]

    expect(empty.attributes('disabled')).toBeDefined()
    expect(empty.attributes('title')).toContain('Nothing in this window')

    await empty.trigger('click')
    await flushPromises()

    expect(reviewerFindingsMock).not.toHaveBeenCalled()
  })

  it('shows why findings were turned down, with the unexplained rejections kept apart', async () => {
    const wrapper = await mountView()

    await wrapper.findAll('.section-tab')[2].trigger('click')

    expect(wrapper.text()).toContain('Why findings were turned down')
    expect(wrapper.text()).toContain('Reviewer was wrong')
    expect(wrapper.text()).toContain('Deliberate trade-off')
    // Shares are of the rejections that carry a reason, so 9 of the 15 classified reads as 60 per cent rather
    // than as 45 per cent of all twenty.
    expect(wrapper.text()).toContain('60%')
    expect(wrapper.text()).toContain('5 of 20 rejections carry no reason')
  })

  it('reads the reasons within one kind of concern rather than across the whole set', async () => {
    // The published finding this follows: functional and evolvability findings are rejected at similar rates for
    // entirely different reasons, so a combined distribution averages the difference away.
    const wrapper = await mountView()
    await wrapper.findAll('.section-tab')[2].trigger('click')

    const tabs = wrapper.findAll('.class-tab')
    expect(tabs.map((tab) => tab.text())).toEqual(['All rejections 20', 'Functional 12', 'Evolvability 8'])

    await tabs[2].trigger('click')

    // Evolvability is mostly the team not wanting the advice: four of its five explained rejections.
    expect(wrapper.text()).toContain('Deliberate trade-off')
    expect(wrapper.text()).toContain('80%')
    expect(wrapper.text()).toContain('3 of 8 rejections carry no reason')
  })

  it('drills from a rejection reason into the findings behind it', async () => {
    const wrapper = await mountView()
    await wrapper.findAll('.section-tab')[2].trigger('click')

    await wrapper.findAll('.reason-row')[0].trigger('click')
    await flushPromises()

    // The reason implies its outcome, so no disposition travels with it.
    const [, options] = reviewerFindingsMock.mock.calls[0] as [unknown, Record<string, unknown>]
    expect(options.rejectionReason).toBe('Wrong')
    expect(options.disposition).toBeUndefined()
    expect(wrapper.text()).toContain('Findings turned down: reviewer was wrong')
  })

  it('keeps every metric readable when the rejection-reason read fails', async () => {
    // The endpoint is newer than the surface around it. A backend that predates it answers 404, and a shared
    // load would have taken correctness, acceptance and the misses list down with it.
    rejectionReasonsMock.mockRejectedValue(new Error('Failed to load the rejection reasons.'))

    const wrapper = await mountView()

    expect(wrapper.text()).toContain('Quality trend')
    expect(wrapper.find('.page-error').exists()).toBe(false)

    await wrapper.findAll('.section-tab')[2].trigger('click')
    expect(wrapper.find('.panel-error').text()).toContain('Failed to load the rejection reasons.')
  })

  it('reports a failed coverage read inside its own section, with a way to retry', async () => {
    coverageMock.mockRejectedValue(new Error('Failed to load the collection coverage.'))
    const wrapper = await mountView()

    await wrapper.findAll('.section-tab')[4].trigger('click')

    expect(wrapper.find('.panel-error').text()).toContain('Failed to load the collection coverage.')
    coverageMock.mockResolvedValue(COVERAGE)

    await wrapper.find('.retry-button').trigger('click')
    await flushPromises()

    expect(wrapper.find('.panel-error').exists()).toBe(false)
    expect(wrapper.text()).toContain('50%')
  })

  it('reads coverage even when the metrics themselves refuse to load', async () => {
    // The section exists to explain an empty surface, so it cannot be hidden behind the surface loading.
    qualityMock.mockRejectedValue(new Error('the projection is unavailable'))

    const wrapper = await mountView()
    await wrapper.findAll('.section-tab')[4].trigger('click')

    expect(wrapper.text()).toContain('What the collection knows about')
  })
})
