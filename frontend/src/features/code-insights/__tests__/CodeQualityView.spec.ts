// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { describe, expect, it, vi, beforeEach } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import CodeQualityView from '@/features/code-insights/views/CodeQualityView.vue'
import type {
  CodeInsightConcentrationRow,
  CodeInsightScope,
  CodeInsightTypeSeries,
} from '@/services/codeInsightsAnalyticsService'

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
const directoryMock = vi.fn()
const qualityMock = vi.fn()
const missesMock = vi.fn()
const reviewerFindingsMock = vi.fn()

vi.mock('@/services/codeInsightsAnalyticsService', () => ({
  fetchTypesOverTime: (...args: unknown[]) => typesMock(...args),
  fetchConcentration: (...args: unknown[]) => concentrationMock(...args),
  fetchFindings: (...args: unknown[]) => findingsMock(...args),
  fetchSurvival: (...args: unknown[]) => survivalMock(...args),
  fetchHotspots: (...args: unknown[]) => hotspotsMock(...args),
  fetchRepositoryDirectory: (...args: unknown[]) => directoryMock(...args),
  fetchQuality: (...args: unknown[]) => qualityMock(...args),
  fetchMisses: (...args: unknown[]) => missesMock(...args),
  fetchReviewerFindings: (...args: unknown[]) => reviewerFindingsMock(...args),
}))

const TYPES: CodeInsightTypeSeries = {
  points: [
    { bucketStart: '2026-06-01', key: 'logic-error', count: 4 },
    { bucketStart: '2026-06-08', key: 'logic-error', count: 2 },
    { bucketStart: '2026-06-08', key: 'security', count: 1 },
  ],
  totalFindings: 7,
  keys: ['logic-error', 'security'],
}

const DIRECTORY = {
  totalFindings: 11,
  repositories: 2,
  pullRequests: 5,
  averagePerPullRequest: 11 / 5,
  rows: [
    { clientId: 'client-a', clientName: 'Client A', repositoryId: '4', repositoryName: 'busy-repo', findings: 9, pullRequests: 4, files: 6, averagePerPullRequest: 2.25, lastActivityOn: '2026-07-20' },
    { clientId: 'client-a', clientName: 'Client A', repositoryId: 'quiet-repo', repositoryName: null, findings: 2, pullRequests: 1, files: 2, averagePerPullRequest: 2, lastActivityOn: '2026-07-02' },
  ],
}

const REPOSITORIES: CodeInsightConcentrationRow[] = [
  // A provider identifier with a display name, and one repository the provider never named.
  { clientId: 'client-a', clientName: 'Client A', repositoryId: '4', repositoryName: 'busy-repo', pullRequestId: null, filePath: null, count: 9 },
  { clientId: 'client-a', clientName: 'Client A', repositoryId: 'quiet-repo', repositoryName: null, pullRequestId: null, filePath: null, count: 2 },
]

const FILES: CodeInsightConcentrationRow[] = [
  { clientId: 'client-a', clientName: 'Client A', repositoryId: '4', repositoryName: 'busy-repo', pullRequestId: null, filePath: 'src/Service.cs', count: 6 },
]

const SURVIVAL = {
  total: { persisted: 9, fixed: 3, dropped: 2, total: 14, persistenceRate: 9 / 14, pullRequests: 3 },
  pullRequests: [
    {
      clientId: 'client-a',
      repositoryId: '4',
      repositoryName: 'busy-repo',
      pullRequestId: 4790,
      revisions: 3,
      survival: { persisted: 2, fixed: 1, dropped: 2, total: 5, persistenceRate: 0.4, pullRequests: 1 },
    },
  ],
}

const NOTHING_MEASURED = {
  total: { persisted: 0, fixed: 0, dropped: 0, total: 0, persistenceRate: null, pullRequests: 0 },
  pullRequests: [],
}

async function mountView() {
  const wrapper = mount(CodeQualityView, {
    global: { stubs: { RouterLink: { template: '<a><slot /></a>' } } },
  })
  await flushPromises()
  return wrapper
}

/**
 * The page opens on the repository directory, so anything about one codebase's numbers starts by choosing one,
 * exactly as a reader does.
 */
async function mountInRepository() {
  const wrapper = await mountView()
  await wrapper.findAll('.directory-row')[0].trigger('click')
  await flushPromises()
  return wrapper
}

describe('CodeQualityView', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    typesMock.mockResolvedValue(TYPES)
    // The first call ranks repositories to pick a landing scope; later calls rank within it.
    concentrationMock.mockImplementation((_scope: CodeInsightScope, grain: string) =>
      Promise.resolve(grain === 'repository' ? REPOSITORIES : FILES),
    )
    findingsMock.mockResolvedValue([])
    survivalMock.mockResolvedValue(SURVIVAL)
    directoryMock.mockResolvedValue(DIRECTORY)
    hotspotsMock.mockResolvedValue({
      totalFindings: 11,
      pullRequests: 4,
      averagePerPullRequest: 11 / 4,
      fileCount: 1,
      unplacedFindings: 0,
      files: [{ filePath: 'src/Service.cs', symbolName: null, findings: 11, pullRequests: 4, averagePerPullRequest: 11 / 4 }],
    })
  })

  it('lands on the repository directory rather than on one codebase\'s numbers', async () => {
    // Nothing past the list is comparable between codebases, so choosing one is the first thing to do: landing on
    // the busiest would answer a question about all of them with one of them.
    const wrapper = await mountView()

    expect(wrapper.text()).toContain('Select a repository')
    expect(wrapper.text()).toContain('busy-repo')
    // Named where a name was recorded, and the provider's identifier where none was.
    expect(wrapper.text()).toContain('quiet-repo')
    expect(wrapper.find('.section-tabs').exists()).toBe(false)
  })

  it('reads nothing about one codebase until one is chosen', async () => {
    // The whole point: those numbers would mix codebases.
    await mountView()

    expect(directoryMock).toHaveBeenCalledTimes(1)
    expect(typesMock).not.toHaveBeenCalled()
    expect(survivalMock).not.toHaveBeenCalled()
    expect(hotspotsMock).not.toHaveBeenCalled()
  })

  it('leads with the volume across codebases and labels it as volume', async () => {
    const wrapper = await mountView()

    expect(wrapper.text()).toContain('11')
    expect(wrapper.text()).toContain('across 2 repositories')
    expect(wrapper.text()).toContain('as a measure of volume')
  })

  it('opening a repository scopes every read to it and offers the way back', async () => {
    const wrapper = await mountView()

    await wrapper.findAll('.directory-row')[0].trigger('click')
    await flushPromises()

    expect((typesMock.mock.calls.at(-1)![0] as CodeInsightScope).repositoryId).toBe('4')
    expect(wrapper.find('[data-testid="scope-repository"]').text()).toBe('busy-repo')
    expect(wrapper.find('[data-testid="back-to-repositories"]').exists()).toBe(true)
    expect(wrapper.find('.section-tabs').exists()).toBe(true)
  })

  it('going back to the directory stops reading one codebase', async () => {
    const wrapper = await mountView()
    await wrapper.findAll('.directory-row')[0].trigger('click')
    await flushPromises()

    const readsBefore = typesMock.mock.calls.length
    await wrapper.find('[data-testid="back-to-repositories"]').trigger('click')
    await flushPromises()

    expect(wrapper.text()).toContain('Select a repository')
    expect(typesMock.mock.calls.length).toBe(readsBefore)
  })

  it('leaves the client scope to the server', async () => {
    // The frontend half of the leakage rule: "all" must never be expressed as a list of client ids the page
    // invented, or the server would aggregate over a set the request supplied.
    await mountInRepository()

    expect((typesMock.mock.calls[0][0] as CodeInsightScope).clientId).toBeNull()
    expect((directoryMock.mock.calls[0][0] as CodeInsightScope).clientId).toBeNull()
  })

  it('answers both developer questions', async () => {
    const wrapper = await mountInRepository()

    expect(wrapper.text()).toContain('Finding types')
    expect(wrapper.text()).toContain('Finding types over time')

    await wrapper.findAll('.section-tab')[1].trigger('click')
    expect(wrapper.text()).toContain('Finding distribution')
    expect(wrapper.text()).toContain('src/Service.cs')
  })

  it('separates what stuck from what was fixed and what merely stopped', async () => {
    // A fix is the reviewer working; a silent disappearance is the code moving or the reviewer being
    // inconsistent. Merging the two would flatter it.
    const wrapper = await mountInRepository()
    await wrapper.findAll('.section-tab')[3].trigger('click')

    expect(wrapper.text()).toContain('Finding persistence')
    expect(wrapper.text()).toContain('64.3%')
    expect(wrapper.find('.survival-card--persisted').text()).toContain('9')
    expect(wrapper.find('.survival-card--fixed').text()).toContain('3')
    expect(wrapper.find('.survival-card--dropped').text()).toContain('2')
    // The pull request that shed the most is broken out, under the repository's name rather than its identifier.
    expect(wrapper.find('.survival-scroll').text()).toContain('4790')
    expect(wrapper.find('.survival-scroll').text()).toContain('busy-repo')
  })

  it('says nothing about persistence when no pull request was reviewed twice', async () => {
    // Undefined rather than zero: nothing was measured, which is not the same as nothing persisting.
    survivalMock.mockResolvedValue(NOTHING_MEASURED)

    const wrapper = await mountInRepository()
    await wrapper.findAll('.section-tab')[3].trigger('click')

    expect(wrapper.text()).toContain('no persistence to report yet')
    expect(wrapper.find('.survival-card--persisted').exists()).toBe(false)
  })

  it('never shows reviewer-performance material', async () => {
    // The whole point of the split: no precision, no recall, no F1, no harvested misses on this surface.
    const wrapper = await mountInRepository()
    await wrapper.findAll('.section-tab')[1].trigger('click')

    const text = wrapper.text()
    expect(text).not.toContain('F1')
    expect(text).not.toContain('Precision')
    expect(text).not.toContain('Recall')
    expect(text).not.toContain('Counts as a miss')
    expect(qualityMock).not.toHaveBeenCalled()
    expect(missesMock).not.toHaveBeenCalled()
  })

  it('switching codebases from inside one reloads in that scope', async () => {
    // The switcher is for staying in the reading; the directory is for choosing. Both scope every read.
    const wrapper = await mountInRepository()

    await wrapper.find('#quality-repository').setValue('quiet-repo')
    await flushPromises()

    expect((typesMock.mock.calls.at(-1)![0] as CodeInsightScope).repositoryId).toBe('quiet-repo')
    expect(wrapper.find('[data-testid="scope-repository"]').text()).toBe('quiet-repo')
  })

  it('changes the bucket size the series is asked for', async () => {
    const wrapper = await mountInRepository()

    await wrapper.find('#quality-bucket').setValue('month')
    await wrapper.find('form').trigger('submit')
    await flushPromises()

    expect(typesMock.mock.calls.at(-1)![1]).toBe('month')
  })

  it('drills from a type into the findings behind it', async () => {
    const wrapper = await mountInRepository()

    await wrapper.findAll('.type-drill')[0].trigger('click')
    await flushPromises()

    expect(findingsMock.mock.calls[0][1]).toMatchObject({ coreType: 'logic-error' })
    expect(wrapper.text()).toContain('Findings typed “logic-error”')
  })

  it('renders a finding as the markdown it was written in', async () => {
    // A finding is markdown, the same markdown the provider renders on the thread. Printed raw, the most
    // important text on the panel arrives with its own fences and backticks showing.
    findingsMock.mockResolvedValue([
      {
        id: 'finding-1',
        clientId: 'client-a',
        repositoryId: 'payments-api',
        pullRequestId: 53,
        jobId: 'job-1',
        filePath: 'Program.cs',
        lineNumber: 81,
        severity: 'Error',
        message:
          'The comparer is defective: it compares `x?.Title` to `x?.Title`.\n\n'
          + '```csharp\nreturn string.Compare(x?.Title, x?.Title, StringComparison.Ordinal);\n```\n',
        coreTags: ['logic-error'],
        disposition: null,
        providerThreadId: null,
        observedAt: '2026-06-02T10:00:00Z',
      },
    ])

    const wrapper = await mountInRepository()
    await wrapper.findAll('.type-drill')[0].trigger('click')
    await flushPromises()

    const rendered = wrapper.find('.finding-message')
    expect(rendered.find('pre code').text()).toContain('string.Compare')
    expect(rendered.findAll('code').length).toBeGreaterThan(1)
    // The syntax itself is gone: no stray fences, no stray backticks.
    expect(rendered.text()).not.toContain('```')
    expect(rendered.text()).not.toContain('`x?.Title`')
  })

  it('re-ranks without a full reload when only the grain changes', async () => {
    const wrapper = await mountInRepository()
    await wrapper.findAll('.section-tab')[1].trigger('click')

    await wrapper.find('#insights-grain').setValue('pullRequest')
    await flushPromises()

    expect(concentrationMock.mock.calls.at(-1)![1]).toBe('pullRequest')
    expect(typesMock).toHaveBeenCalledTimes(1)
  })

  it('surfaces a failed load instead of rendering an empty page silently', async () => {
    typesMock.mockRejectedValue(new Error('the projection is unavailable'))

    const wrapper = await mountInRepository()

    expect(wrapper.find('[role="alert"]').text()).toContain('the projection is unavailable')
  })

  it('opens on one line per type, and offers the stack for the volume question', async () => {
    // Which type is moving is the question this panel exists for, so that is the shape it starts in.
    const wrapper = await mountInRepository()

    expect(wrapper.find('.line-chart-stub').exists()).toBe(true)
    expect(wrapper.find('.bar-chart-stub').exists()).toBe(false)

    await wrapper.findAll('.shape-toggle button')[0].trigger('click')

    expect(wrapper.find('.bar-chart-stub').exists()).toBe(true)
    expect(wrapper.find('.line-chart-stub').exists()).toBe(false)
  })

  it('carries every chart\'s numbers as a table for assistive technology, without drawing one', async () => {
    const wrapper = await mountInRepository()

    const table = wrapper.find('.chart-table')
    // Present in the accessibility tree, absent from the page: a canvas says nothing to a screen reader.
    expect(table.classes()).toContain('visually-hidden')
    expect(table.find('table').text()).toContain('logic-error')
    expect(wrapper.find('details.chart-table').exists()).toBe(false)
    expect(wrapper.find('[role="img"]').attributes('aria-label')).toContain('Findings by core type')
  })
})
