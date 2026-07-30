// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { describe, expect, it } from 'vitest'
import { mount } from '@vue/test-utils'
import CodeInsightsHotspotsPanel from '@/features/code-insights/components/CodeInsightsHotspotsPanel.vue'
import type {
  CodeInsightConcentrationRow,
  CodeInsightHotspotReport,
} from '@/services/codeInsightsAnalyticsService'

function report(overrides: Partial<CodeInsightHotspotReport> = {}): CodeInsightHotspotReport {
  return {
    totalFindings: 40,
    pullRequests: 12,
    averagePerPullRequest: 40 / 12,
    fileCount: 3,
    unplacedFindings: 0,
    files: [
      { filePath: 'src/Payments/RefundProcessor.cs', symbolName: null, findings: 31, pullRequests: 11, averagePerPullRequest: 31 / 11 },
      { filePath: 'src/Api/WebhookController.cs', symbolName: null, findings: 7, pullRequests: 4, averagePerPullRequest: 7 / 4 },
      { filePath: '', symbolName: null, findings: 2, pullRequests: 2, averagePerPullRequest: 1 },
    ],
    ...overrides,
  }
}

const CURRENT: CodeInsightConcentrationRow[] = [
  {
    clientId: 'client-a',
    clientName: 'Client A',
    repositoryId: '4',
    repositoryName: 'payments-api',
    pullRequestId: 4821,
    filePath: 'src/Payments/RefundProcessor.cs',
    count: 3,
  },
]

function mountPanel(props: Partial<Record<string, unknown>> = {}) {
  return mount(CodeInsightsHotspotsPanel, {
    props: { report: report(), grouping: 'file', ...props },
  })
}

/** Enough rows that one page cannot hold them, each identifiable by its own name. */
function manyFiles(count: number): CodeInsightHotspotReport['files'] {
  return Array.from({ length: count }, (_unused, index) => ({
    filePath: `src/Payments/File${String(index).padStart(2, '0')}.cs`,
    symbolName: null,
    findings: count - index,
    pullRequests: 2,
    averagePerPullRequest: (count - index) / 2,
  }))
}

function rowPaths(wrapper: ReturnType<typeof mountPanel>): string[] {
  return wrapper.findAll('.hotspot-table tbody th').map((cell) => cell.text())
}

describe('CodeInsightsHotspotsPanel', () => {
  it('leads with the average per pull request and the counts behind it', () => {
    const wrapper = mountPanel()

    expect(wrapper.text()).toContain('3.3')
    expect(wrapper.text()).toContain('40')
    expect(wrapper.text()).toContain('12')
    expect(wrapper.text()).toContain('across 3 files')
  })

  it('draws the file tree as frames, folded where a folder has one child', () => {
    const wrapper = mountPanel()

    const frames = wrapper.findAll('.flame-frame:not(.flame-frame--root)').map((frame) => frame.text())
    // src holds two folders, so it stays a frame; each folder holds one file, so it folds onto that file rather
    // than framing the same width twice.
    expect(frames).toContain('src')
    expect(frames.some((label) => label.includes('Payments/RefundProcessor.cs'))).toBe(true)
    expect(frames.some((label) => label === 'Payments')).toBe(false)
  })

  it('says a file\'s history and how much of it is in front of the reader', () => {
    const wrapper = mountPanel({ scopedToPullRequest: true, currentByFile: CURRENT })

    const leaf = wrapper
      .findAll('.flame-frame')
      .find((frame) => frame.attributes('aria-label')?.includes('RefundProcessor.cs'))

    expect(leaf?.attributes('aria-label')).toContain('31 findings')
    expect(leaf?.attributes('aria-label')).toContain('11 pull requests')
    expect(leaf?.attributes('aria-label')).toContain('3 here')
  })

  it('opens the findings behind a file when its frame is clicked', async () => {
    const wrapper = mountPanel()

    const leaf = wrapper
      .findAll('.flame-frame')
      .find((frame) => frame.text().includes('RefundProcessor.cs'))
    await leaf!.trigger('click')

    expect(wrapper.emitted('drill')?.[0]).toEqual([
      { filePath: 'src/Payments/RefundProcessor.cs', symbolName: null },
    ])
  })

  it('zooms into a folder rather than opening it', async () => {
    const wrapper = mount(CodeInsightsHotspotsPanel, {
      props: {
        grouping: 'file',
        report: report({
          files: [
            { filePath: 'src/Payments/A.cs', symbolName: null, findings: 5, pullRequests: 3, averagePerPullRequest: 5 / 3 },
            { filePath: 'src/Payments/B.cs', symbolName: null, findings: 4, pullRequests: 2, averagePerPullRequest: 2 },
            { filePath: 'src/Api/C.cs', symbolName: null, findings: 2, pullRequests: 1, averagePerPullRequest: 2 },
          ],
        }),
      },
    })

    const folder = wrapper.findAll('.flame-frame').find((frame) => frame.text() === 'Payments')
    await folder!.trigger('click')

    expect(wrapper.emitted('drill')).toBeUndefined()
    // Zoomed in, the trail offers the way back out.
    expect(wrapper.find('.flame-trail').exists()).toBe(true)
  })

  it('reports pull-request-level findings beside the graph, not as a file', () => {
    // They belong to no file, so a frame for them would be a lie about where they were found.
    const wrapper = mountPanel()

    expect(wrapper.text()).toContain('raised about')
    const frames = wrapper.findAll('.flame-frame').map((frame) => frame.text())
    expect(frames.some((label) => label.includes('(pull-request level)'))).toBe(false)
    // The table still lists them, and offers no drill for a row with no file.
    expect(wrapper.find('.hotspot-table table').text()).toContain('(pull-request level)')
    expect(wrapper.findAll('.hotspot-table .drill-button')).toHaveLength(2)
  })

  it('explains why folder frames carry no average', () => {
    const wrapper = mountPanel()

    expect(wrapper.text()).toContain('cannot be summed')
    expect(wrapper.text()).toContain('found nothing')
  })

  it('says nothing has been collected rather than drawing an empty graph', () => {
    const wrapper = mountPanel({
      report: report({ files: [], fileCount: 0, totalFindings: 0, pullRequests: 0, averagePerPullRequest: null }),
    })

    expect(wrapper.text()).toContain('No findings have been collected')
    expect(wrapper.find('.flame-canvas').exists()).toBe(false)
  })

  it('counts per definition when asked, nesting each under its own file', () => {
    // The reason findings record a symbol: which part of a file keeps producing findings, not just which file.
    const wrapper = mountPanel({
      grouping: 'symbol',
      report: report({
        fileCount: 2,
        totalFindings: 12,
        files: [
          { filePath: 'src/Payments/RefundProcessor.cs', symbolName: 'Process', findings: 9, pullRequests: 6, averagePerPullRequest: 1.5 },
          { filePath: 'src/Payments/RefundProcessor.cs', symbolName: 'Validate', findings: 3, pullRequests: 2, averagePerPullRequest: 1.5 },
        ],
      }),
    })

    expect(wrapper.text()).toContain('Worst definition')
    expect(wrapper.text()).toContain('across 2 definitions')
    expect(wrapper.find('.hotspot-table thead').text()).toContain('Definition')

    // The graph deepens by a level rather than becoming a different graph: the file, then its definitions.
    const frames = wrapper.findAll('.flame-frame:not(.flame-frame--root)').map((frame) => frame.text())
    expect(frames.some((label) => label.includes('RefundProcessor.cs'))).toBe(true)
    expect(frames.some((label) => label.includes('Process'))).toBe(true)
  })

  it('drills to the definition, not just its file', async () => {
    const wrapper = mountPanel({
      grouping: 'symbol',
      report: report({
        fileCount: 1,
        files: [
          { filePath: 'src/Payments/RefundProcessor.cs', symbolName: 'Process', findings: 9, pullRequests: 6, averagePerPullRequest: 1.5 },
        ],
      }),
    })

    await wrapper.get('.hotspot-table .drill-button').trigger('click')

    expect(wrapper.emitted('drill')?.[0]).toEqual([
      { filePath: 'src/Payments/RefundProcessor.cs', symbolName: 'Process' },
    ])
  })

  it('says how many findings it could not place instead of ranking them as a bucket', () => {
    // An "(unknown)" row would rank as if it were somewhere in the code.
    const wrapper = mountPanel({
      grouping: 'symbol',
      report: report({ unplacedFindings: 7 }),
    })

    const note = wrapper.get('[data-testid="unplaced-note"]').text()
    expect(note).toContain('7 findings')
    expect(note).toContain('not counted above')
  })

  it('asks its parent to reload when the grouping changes', async () => {
    const wrapper = mountPanel()

    await wrapper.get('#hotspot-grouping').setValue('symbol')

    expect(wrapper.emitted('update:grouping')?.[0]).toEqual(['symbol'])
  })

  it('changes what it says when it sits inside one pull request', () => {
    const wrapper = mountPanel({ scopedToPullRequest: true })

    expect(wrapper.text()).toContain('before today')
    expect(wrapper.find('.hotspot-table thead').text()).toContain('In this PR')
  })

  it('shows one page of rows at a time and steps through the rest', async () => {
    const wrapper = mountPanel({ report: report({ fileCount: 24, files: manyFiles(24) }) })

    expect(rowPaths(wrapper)).toHaveLength(10)
    expect(rowPaths(wrapper)[0]).toContain('File00.cs')
    expect(wrapper.get('[data-testid="hotspot-pager"]').text()).toContain('Page 1 of 3')

    await wrapper.findAll('[data-testid="hotspot-pager"] button')[1].trigger('click')

    expect(rowPaths(wrapper)[0]).toContain('File10.cs')
    expect(wrapper.get('[data-testid="hotspot-pager"]').text()).toContain('Page 2 of 3')
  })

  it('leaves the pager out when everything fits on one page', () => {
    const wrapper = mountPanel()

    expect(wrapper.find('[data-testid="hotspot-pager"]').exists()).toBe(false)
  })

  it('searches paths and definitions without minding their casing', async () => {
    const wrapper = mountPanel({
      grouping: 'symbol',
      report: report({
        fileCount: 2,
        files: [
          { filePath: 'src/Payments/RefundProcessor.cs', symbolName: 'ProcessRefund', findings: 9, pullRequests: 6, averagePerPullRequest: 1.5 },
          { filePath: 'src/Api/WebhookController.cs', symbolName: 'Post', findings: 3, pullRequests: 2, averagePerPullRequest: 1.5 },
        ],
      }),
    })

    // Lower case against a mixed-case definition: a reader searching for a method rarely reproduces its casing.
    await wrapper.get('[data-testid="hotspot-search"]').setValue('processrefund')

    const paths = rowPaths(wrapper)
    expect(paths).toHaveLength(1)
    expect(paths[0]).toContain('ProcessRefund')
    expect(wrapper.get('[data-testid="hotspot-count"]').text()).toContain('1 of 2')
  })

  it('goes back to the first page when the search narrows the list', async () => {
    const wrapper = mountPanel({ report: report({ fileCount: 24, files: manyFiles(24) }) })
    await wrapper.findAll('[data-testid="hotspot-pager"] button')[1].trigger('click')

    await wrapper.get('[data-testid="hotspot-search"]').setValue('File2')

    expect(rowPaths(wrapper)[0]).toContain('File20.cs')
  })

  it('says so when the search matches nothing, rather than showing an empty table', async () => {
    const wrapper = mountPanel()

    await wrapper.get('[data-testid="hotspot-search"]').setValue('nothing-like-this')

    expect(wrapper.text()).toContain('Nothing here matches')
    expect(wrapper.find('.hotspot-table table').exists()).toBe(false)
  })

  it('says the ranked rows are fewer than the scope holds', () => {
    // A searchable table invites the assumption that everything is in it, and the ranking is capped.
    const wrapper = mountPanel({ report: report({ fileCount: 940, files: manyFiles(200) }) })

    expect(wrapper.get('[data-testid="hotspot-count"]').text()).toBe('Top 200 of 940 files in scope')
  })
})
