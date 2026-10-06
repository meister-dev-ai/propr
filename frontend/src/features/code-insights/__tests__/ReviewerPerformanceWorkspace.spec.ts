// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import ReviewerPerformanceWorkspace from '../components/ReviewerPerformanceWorkspace.vue'
import ReviewerPerformanceScopeFilters from '../components/ReviewerPerformanceScopeFilters.vue'
import ReviewerPerformanceExplorer from '../components/ReviewerPerformanceExplorer.vue'
import ReviewerPerformanceRangeChart from '../components/ReviewerPerformanceRangeChart.vue'
import type { PerformancePoint } from '@/services/reviewerPerformanceService'

vi.mock('../components/ReviewerPerformanceSelect.vue', async () => {
  const { performanceSelectStub } = await import('./performanceControlStubs')
  return { default: performanceSelectStub }
})

const query = vi.fn()
const list = vi.fn()
const save = vi.fn()
const open = vi.fn()
const remove = vi.fn()
const clients = vi.fn()
vi.mock('@/services/reviewerPerformanceService', () => ({
  queryPerformance: (...args: unknown[]) => query(...args),
  listPerformanceReports: () => list(),
  savePerformanceReport: (...args: unknown[]) => save(...args),
  openPerformanceReport: (...args: unknown[]) => open(...args),
  deletePerformanceReport: (...args: unknown[]) => remove(...args),
  listPerformanceClients: () => clients(),
}))
const point = {
  date: '2026-09-01',
  score: {
    counts: { outcomes: { positive: 8, wrong: 2 }, confirmedDuplicates: {} },
    scenarios: [],
    summary: {
      f1: { minimum: 0.5, firstQuartile: 0.6, median: 0.7, thirdQuartile: 0.8, maximum: 0.9 },
    },
  },
  unavailableReasons: [],
}
const view = {
  scope: { from: '2026-09-01', to: '2026-09-02' },
  facets: { clients: [], repositories: [], models: [], types: [], qualifiers: [] },
  series: [{ id: 'all', label: 'All selected evidence', points: [point] }],
  breakdown: [],
  evidence: { pendingSourceAggregates: 0 },
}
const response = {
  calculationVersion: 'reviewer-performance-v1',
  capturedAt: '2026-09-03T10:00:00Z',
  query: { bucket: 'day', aggregation: 'cumulative', grouping: 'none', views: [view.scope] },
  views: [view],
  premiseIds: [],
}
function inspectedPoint(wrapper: VueWrapper, index = 0): PerformancePoint | undefined {
  const chart = wrapper.findAllComponents(ReviewerPerformanceRangeChart)[index]
  if (!chart) {
    return undefined
  }
  const series = chart.props('data').find((item) => item.id === chart.props('focusSeries')) ?? chart.props('data')[0]
  return series?.points?.find((point) => point.date === chart.props('selectedDate'))
}

async function selectChartPoint(wrapper: VueWrapper, index: number, date: string): Promise<void> {
  const chart = wrapper.findAllComponents(ReviewerPerformanceRangeChart)[index]!
  chart.vm.$emit('select-point', { seriesId: chart.props('focusSeries'), date })
  await wrapper.vm.$nextTick()
}

describe('reviewer performance workspace', () => {
  beforeEach(() => {
    query.mockReset()
    list.mockReset()
    save.mockReset()
    open.mockReset()
    remove.mockReset()
    clients.mockReset()
    clients.mockResolvedValue([])
    query.mockImplementation(async (q) => ({
      ...response,
      query: q,
      views: q.views.map(() => view),
    }))
    list.mockResolvedValue([])
  })
  it('offers authorized clients when the selected window has no measured evidence', async () => {
    clients.mockResolvedValue([{ id: 'demo-client', label: 'Reviewer Performance Demo' }])
    const wrapper = mount(ReviewerPerformanceWorkspace)
    await flushPromises()

    const filters = wrapper.getComponent(ReviewerPerformanceScopeFilters)
    expect(filters.props('facets').clients).toEqual([
      { id: 'demo-client', label: 'Reviewer Performance Demo' },
    ])

    filters.vm.$emit('update:scope', { ...view.scope, clientIds: ['demo-client'] })
    filters.vm.$emit('apply')
    await flushPromises()
    expect(query.mock.calls.at(-1)![0].views[0].clientIds).toEqual(['demo-client'])
  })
  it('keeps authorized client choices available when the initial evidence request fails', async () => {
    clients.mockResolvedValue([{ id: 'demo-client', label: 'Reviewer Performance Demo' }])
    query.mockRejectedValueOnce(new Error('Narrow the selection'))
    const wrapper = mount(ReviewerPerformanceWorkspace)
    await flushPromises()

    expect(wrapper.getComponent(ReviewerPerformanceScopeFilters).props('facets').clients).toEqual([
      { id: 'demo-client', label: 'Reviewer Performance Demo' },
    ])
    expect(wrapper.text()).toContain('Narrow the selection')
  })
  it('uses captured client facets when opening a saved report', async () => {
    clients.mockResolvedValue([{ id: 'current-client', label: 'Current client' }])
    list.mockResolvedValue([{ id: 'stored', name: 'Captured clients' }])
    open.mockResolvedValue({
      report: { id: 'stored', name: 'Captured clients' },
      response: {
        ...response,
        views: [{ ...view, facets: { ...view.facets, clients: [{ id: 'captured-client', label: 'Captured client' }] } }],
      },
      compatibleVersion: true,
    })
    const wrapper = mount(ReviewerPerformanceWorkspace)
    await flushPromises()
    await wrapper.get('[data-test="report-select"]').setValue('stored')
    await flushPromises()

    expect(wrapper.getComponent(ReviewerPerformanceScopeFilters).props('facets').clients).toEqual([
      { id: 'captured-client', label: 'Captured client' },
    ])
  })
  it('keeps measured evidence visible when the independent client catalogue fails', async () => {
    clients.mockRejectedValueOnce(new Error('Client catalogue unavailable'))
    const wrapper = mount(ReviewerPerformanceWorkspace)
    await flushPromises()

    expect(wrapper.text()).toContain('Client catalogue unavailable')
    expect(wrapper.getComponent({ name: 'ReviewerPerformanceRangeChart' }).props('data')).toEqual(view.series)
    expect(query).toHaveBeenCalledTimes(1)
  })
  it('retries its client catalogue without changing scopes or reloading measured evidence', async () => {
    clients.mockRejectedValueOnce(new Error('Client catalogue unavailable'))
    clients.mockResolvedValueOnce([{ id: 'demo-client', label: 'Demo client' }])
    const wrapper = mount(ReviewerPerformanceWorkspace)
    await flushPromises()
    const filters = wrapper.getComponent(ReviewerPerformanceScopeFilters)
    filters.vm.$emit('update:scope', { ...filters.props('scope'), clientIds: ['demo-client'] })

    await wrapper.get('[data-test="retry-clients"]').trigger('click')
    await flushPromises()

    expect(clients).toHaveBeenCalledTimes(2)
    expect(wrapper.text()).not.toContain('Client catalogue unavailable')
    expect(filters.props('facets').clients).toEqual([{ id: 'demo-client', label: 'Demo client' }])
    expect(filters.props('scope').clientIds).toEqual(['demo-client'])
    expect(query).toHaveBeenCalledTimes(1)
  })
  it('delegates retrying a parent-provided client catalogue to its owner', async () => {
    const wrapper = mount(ReviewerPerformanceWorkspace, {
      props: { clients: [], clientsError: 'Client catalogue unavailable' },
    })
    await flushPromises()

    await wrapper.get('[data-test="retry-clients"]').trigger('click')
    expect(wrapper.emitted('retry-clients')).toEqual([[]])
    expect(clients).not.toHaveBeenCalled()
    expect(query).toHaveBeenCalledTimes(1)
  })
  it('passes independent client catalogue loading through to only the client facet', async () => {
    let finishClients!: (value: { id: string; label: string }[]) => void
    clients.mockReturnValueOnce(new Promise((resolve) => { finishClients = resolve }))
    const wrapper = mount(ReviewerPerformanceWorkspace)
    await flushPromises()
    const filters = wrapper.getComponent(ReviewerPerformanceScopeFilters)
    const facets = filters.findAllComponents({ name: 'VAutocomplete' })

    expect(filters.props('clientsLoading')).toBe(true)
    expect(facets.map((facet) => facet.props('loading'))).toEqual([true, false, false, false, false])
    finishClients([{ id: 'demo-client', label: 'Demo client' }])
    await flushPromises()
    expect(filters.props('clientsLoading')).toBe(false)
  })
  it('passes parent catalogue loading into live filters and excludes it from captured reports', async () => {
    list.mockResolvedValue([{ id: 'stored', name: 'Captured report' }])
    open.mockResolvedValue({ report: { id: 'stored', name: 'Captured report' }, response, compatibleVersion: true })
    const wrapper = mount(ReviewerPerformanceWorkspace, {
      props: { clients: [], clientsLoading: true },
    })
    await flushPromises()
    const filters = wrapper.getComponent(ReviewerPerformanceScopeFilters)
    expect(filters.props('clientsLoading')).toBe(true)
    expect(clients).not.toHaveBeenCalled()

    await wrapper.get('[data-test="report-select"]').setValue('stored')
    await flushPromises()
    expect(filters.props('clientsLoading')).toBe(false)
    expect(filters.props('facets')).toEqual(view.facets)
    expect(query).toHaveBeenCalledTimes(1)
  })
  it('starts with one cumulative F1 view and queries comparison only after activation', async () => {
    const wrapper = mount(ReviewerPerformanceWorkspace)
    await flushPromises()
    expect(query.mock.calls[0]![0].views).toHaveLength(1)
    expect(wrapper.find('[data-test="metric"]').element).toHaveProperty('value', 'f1')
    expect(wrapper.find('[data-test="aggregation"]').element).toHaveProperty('value', 'cumulative')
    expect(wrapper.findAll('[data-test="range-view"]')).toHaveLength(1)
    expect(wrapper.find('.performance-layout--compared').exists()).toBe(false)
    await wrapper.get('[data-test="comparison"] input[type="checkbox"]').setValue(true)
    await flushPromises()
    expect(query.mock.calls.at(-1)![0].views).toHaveLength(2)
    expect(wrapper.findAll('[data-test="range-view"]')).toHaveLength(2)
    expect(wrapper.find('.performance-layout--compared').exists()).toBe(true)
  })
  it('uses chart point selection for linked inspection without a period dropdown or duplicate summary', async () => {
    const second = { ...point, date: '2026-09-02' }
    query.mockImplementation(async (request) => ({
      ...response,
      query: request,
      views: request.views.map(() => ({ ...view, series: [{ ...view.series[0], points: [point, second] }] })),
    }))
    const wrapper = mount(ReviewerPerformanceWorkspace)
    await flushPromises()
    await wrapper.get('[data-test="comparison"] input[type="checkbox"]').setValue(true)
    await flushPromises()
    const calls = query.mock.calls.length
    expect(wrapper.find('[aria-label="Inspect period"]').exists()).toBe(false)
    expect(wrapper.find('.performance-view > .performance-evidence').exists()).toBe(false)
    const charts = wrapper.findAllComponents(ReviewerPerformanceRangeChart)
    charts[0]!.vm.$emit('select-point', { seriesId: 'all', date: point.date })
    await flushPromises()
    expect(charts.map((chart) => chart.props('selectedDate'))).toEqual([point.date, point.date])
    expect(query).toHaveBeenCalledTimes(calls)
    const explore = wrapper.findAll('button').filter((button) => button.text() === 'Explore dimensions')[0]!
    await explore.trigger('click')
    await flushPromises()
    expect(query.mock.calls.at(-1)![0].breakdown.date).toBe(point.date)
  })
  it('opens saved responses and freezes query controls without a live recalculation', async () => {
    list.mockResolvedValue([{ id: 'stored', name: 'Saved sample' }])
    open.mockResolvedValue({
      report: { id: 'stored', name: 'Saved sample', calculationVersion: 'reviewer-performance-v1' },
      response,
      compatibleVersion: true,
    })
    const wrapper = mount(ReviewerPerformanceWorkspace)
    await flushPromises()
    const calls = query.mock.calls.length
    await wrapper.get('[data-test="report-select"]').setValue('stored')
    await flushPromises()
    expect(open).toHaveBeenCalledWith('stored')
    expect(query.mock.calls).toHaveLength(calls)
    expect(wrapper.get('[data-test="aggregation"]').attributes('disabled')).toBeDefined()
    expect(wrapper.text()).toContain('Saved sample')
  })
  it('preserves the independently configured comparison scope after disabling and enabling comparison', async () => {
    const wrapper = mount(ReviewerPerformanceWorkspace)
    await flushPromises()
    await wrapper.get('[data-test="comparison"] input[type="checkbox"]').setValue(true)
    await flushPromises()
    await wrapper.get('[data-test="scope-view"]').setValue('1')
    wrapper.getComponent(ReviewerPerformanceScopeFilters).vm.$emit('update:scope', {
      ...view.scope,
      from: '2026-08-01',
      to: '2026-08-30',
      models: ['model-b'],
    })
    wrapper.getComponent(ReviewerPerformanceScopeFilters).vm.$emit('apply')
    await flushPromises()
    await wrapper.get('[data-test="comparison"] input[type="checkbox"]').setValue(false)
    await flushPromises()
    await wrapper.get('[data-test="comparison"] input[type="checkbox"]').setValue(true)
    await flushPromises()
    expect(query.mock.calls.at(-1)![0].views[1]).toMatchObject({
      from: '2026-08-01',
      to: '2026-08-30',
      models: ['model-b'],
    })
  })
  it('edits one comparison scope at a time without requesting evidence and applies both drafts', async () => {
    const wrapper = mount(ReviewerPerformanceWorkspace)
    await flushPromises()
    await wrapper.get('[data-test="comparison"] input[type="checkbox"]').setValue(true)
    await flushPromises()
    const originalA = { ...wrapper.getComponent(ReviewerPerformanceScopeFilters).props('scope') }
    const calls = query.mock.calls.length

    expect(wrapper.findAllComponents(ReviewerPerformanceScopeFilters)).toHaveLength(1)
    await wrapper.get('[data-test="scope-view"]').setValue('1')
    wrapper.getComponent(ReviewerPerformanceScopeFilters).vm.$emit('update:scope', {
      ...originalA,
      repositories: ['repository-b'],
    })
    await flushPromises()
    await wrapper.get('[data-test="scope-view"]').setValue('0')
    expect(wrapper.getComponent(ReviewerPerformanceScopeFilters).props('scope')).toEqual(originalA)
    wrapper.getComponent(ReviewerPerformanceScopeFilters).vm.$emit('update:scope', {
      ...originalA,
      models: ['model-a'],
    })
    await flushPromises()
    await wrapper.get('[data-test="scope-view"]').setValue('1')
    expect(wrapper.getComponent(ReviewerPerformanceScopeFilters).props('scope').repositories).toEqual([
      'repository-b',
    ])
    expect(query).toHaveBeenCalledTimes(calls)

    wrapper.getComponent(ReviewerPerformanceScopeFilters).vm.$emit('apply')
    await flushPromises()
    expect(query.mock.calls.at(-1)![0].views).toEqual([
      { ...originalA, models: ['model-a'] },
      { ...originalA, repositories: ['repository-b'] },
    ])
    wrapper.unmount()
  })
  it('preserves pending scope edits and displayed evidence while the filter pane is collapsed', async () => {
    const wrapper = mount(ReviewerPerformanceWorkspace)
    await flushPromises()
    const original = wrapper.getComponent(ReviewerPerformanceScopeFilters).props('scope')
    wrapper.getComponent(ReviewerPerformanceScopeFilters).vm.$emit('update:scope', {
      ...original,
      clientIds: ['pending-client'],
    })
    await flushPromises()
    const calls = query.mock.calls.length

    await wrapper.get('[data-test="filter-pane-toggle"]').trigger('click')
    expect(wrapper.findComponent(ReviewerPerformanceScopeFilters).exists()).toBe(false)
    expect(wrapper.get('[data-test="filter-pane-toggle"]').attributes('aria-expanded')).toBe('false')
    expect(wrapper.text()).toContain('Scope edits are pending')
    expect(wrapper.getComponent({ name: 'ReviewerPerformanceRangeChart' }).props('data')).toEqual(view.series)
    await wrapper.get('[data-test="filter-pane-toggle"]').trigger('click')
    expect(wrapper.getComponent(ReviewerPerformanceScopeFilters).props('scope').clientIds).toEqual([
      'pending-client',
    ])
    expect(query).toHaveBeenCalledTimes(calls)
    wrapper.unmount()
  })
  it('retains the B editor and submits both displayed drafts after disabling comparison fails', async () => {
    const wrapper = mount(ReviewerPerformanceWorkspace)
    await flushPromises()
    await wrapper.get('[data-test="comparison"] input[type="checkbox"]').setValue(true)
    await flushPromises()
    await wrapper.get('[data-test="scope-view"]').setValue('1')
    query.mockRejectedValueOnce(new Error('Comparison replacement failed'))
    await wrapper.get('[data-test="comparison"] input[type="checkbox"]').setValue(false)
    await flushPromises()

    expect(wrapper.get('[data-test="scope-view"]').element).toHaveProperty('value', '1')
    expect(wrapper.findAll('[data-test="range-view"]')).toHaveLength(2)
    expect(wrapper.find('.performance-layout--compared').exists()).toBe(true)
    const filters = wrapper.getComponent(ReviewerPerformanceScopeFilters)
    filters.vm.$emit('update:scope', { ...filters.props('scope'), repositories: ['retained-b'] })
    await flushPromises()
    const callsBeforeApply = query.mock.calls.length
    filters.vm.$emit('apply')
    await flushPromises()

    expect(query).toHaveBeenCalledTimes(callsBeforeApply + 1)
    expect(query.mock.calls.at(-1)![0].views).toHaveLength(2)
    expect(query.mock.calls.at(-1)![0].views[0].repositories).toBeNull()
    expect(query.mock.calls.at(-1)![0].views[1].repositories).toEqual(['retained-b'])
    expect(wrapper.get('[data-test="comparison"] input[type="checkbox"]').element).toHaveProperty('checked', true)
    expect(wrapper.text()).not.toContain('Scope edits are pending')
    expect(wrapper.get('[data-test="scope-view"]').element).toHaveProperty('value', '1')

    await wrapper.get('[data-test="aggregation"]').setValue('period')
    await flushPromises()
    expect(query).toHaveBeenCalledTimes(callsBeforeApply + 2)
    expect(query.mock.calls.at(-1)![0].views).toHaveLength(2)
    expect(query.mock.calls.at(-1)![0].views[1].repositories).toEqual(['retained-b'])
    expect(wrapper.get('[data-test="scope-view"]').element).toHaveProperty('value', '1')
    wrapper.unmount()
  })
  it('restores single-view mode after a failed comparison activation and successful Apply', async () => {
    const wrapper = mount(ReviewerPerformanceWorkspace)
    await flushPromises()
    query.mockRejectedValueOnce(new Error('Comparison activation failed'))
    await wrapper.get('[data-test="comparison"] input[type="checkbox"]').setValue(true)
    await flushPromises()
    expect(wrapper.findAll('[data-test="range-view"]')).toHaveLength(1)
    const filters = wrapper.getComponent(ReviewerPerformanceScopeFilters)
    filters.vm.$emit('update:scope', { ...filters.props('scope'), repositories: ['retained-a'] })
    await flushPromises()
    const callsBeforeApply = query.mock.calls.length
    filters.vm.$emit('apply')
    await flushPromises()

    expect(query).toHaveBeenCalledTimes(callsBeforeApply + 1)
    expect(wrapper.get('[data-test="comparison"] input[type="checkbox"]').element).toHaveProperty('checked', false)
    expect(wrapper.text()).not.toContain('Scope edits are pending')
    await wrapper.get('[data-test="aggregation"]').setValue('period')
    await flushPromises()
    expect(query).toHaveBeenCalledTimes(callsBeforeApply + 2)
    expect(query.mock.calls.at(-1)![0].views).toHaveLength(1)
    expect(query.mock.calls.at(-1)![0].views[0].repositories).toEqual(['retained-a'])
    wrapper.unmount()
  })
  it('replaces a pending comparison-disable request when comparison is enabled again', async () => {
    const wrapper = mount(ReviewerPerformanceWorkspace)
    await flushPromises()
    await wrapper.get('[data-test="comparison"] input[type="checkbox"]').setValue(true)
    await flushPromises()
    let finishDisable!: (value: typeof response) => void
    query.mockReturnValueOnce(new Promise((resolve) => { finishDisable = resolve }))
    await wrapper.get('[data-test="comparison"] input[type="checkbox"]').setValue(false)
    await flushPromises()
    const disabledQuery = query.mock.calls.at(-1)![0]
    const calls = query.mock.calls.length
    await wrapper.get('[data-test="comparison"] input[type="checkbox"]').setValue(true)
    await flushPromises()
    expect(query).toHaveBeenCalledTimes(calls + 1)
    expect(query.mock.calls.at(-1)![0].views).toHaveLength(2)
    finishDisable({ ...response, query: disabledQuery, views: [view] })
    await flushPromises()

    expect(wrapper.findAll('[data-test="range-view"]')).toHaveLength(2)
    expect(wrapper.get('[data-test="comparison"] input[type="checkbox"]').element).toHaveProperty('checked', true)
    expect(wrapper.text()).not.toContain('Scope edits are pending')
    wrapper.unmount()
  })
  describe('narrow filter pane', () => {
    let wrapper: VueWrapper | undefined
    let restoreMedia = () => {}

    beforeEach(() => {
      const original = window.matchMedia
      window.matchMedia = (query) => ({
        ...original(query),
        matches: query === '(max-width: 960px)',
      })
      restoreMedia = () => {
        window.matchMedia = original
      }
    })

    afterEach(() => {
      wrapper?.unmount()
      wrapper = undefined
      restoreMedia()
    })

    it('starts collapsed and returns focus to Filters when the pane is hidden', async () => {
      wrapper = mount(ReviewerPerformanceWorkspace, { attachTo: document.body })
      await flushPromises()
      expect(wrapper.findComponent(ReviewerPerformanceScopeFilters).exists()).toBe(false)
      const toggle = wrapper.get('[data-test="filter-pane-toggle"]')
      expect(toggle.attributes('aria-expanded')).toBe('false')
      expect(toggle.attributes('aria-controls')).toBeUndefined()
      await toggle.trigger('click')
      expect(wrapper.findComponent(ReviewerPerformanceScopeFilters).exists()).toBe(true)
      expect(toggle.attributes('aria-controls')).toBe(wrapper.get('aside').attributes('id'))
      const close = wrapper.get('.performance-filter-pane__header button')
      ;(close.element as HTMLButtonElement).focus()
      await close.trigger('click')
      await flushPromises()
      expect(document.activeElement).toBe(toggle.element)
      expect(wrapper.findComponent(ReviewerPerformanceScopeFilters).exists()).toBe(false)
      expect(toggle.attributes('aria-controls')).toBeUndefined()
      expect(query).toHaveBeenCalledTimes(1)
    })

    it('reveals and focuses the chart after successfully applying the selected client', async () => {
      wrapper = mount(ReviewerPerformanceWorkspace, { attachTo: document.body })
      await flushPromises()
      await wrapper.get('[data-test="filter-pane-toggle"]').trigger('click')
      const filters = wrapper.getComponent(ReviewerPerformanceScopeFilters)
      filters.vm.$emit('update:scope', { ...filters.props('scope'), clientIds: ['selected-client'] })
      await flushPromises()
      filters.vm.$emit('apply')
      await flushPromises()

      expect(query.mock.calls.at(-1)![0].views[0].clientIds).toEqual(['selected-client'])
      expect(wrapper.findComponent(ReviewerPerformanceScopeFilters).exists()).toBe(false)
      expect(document.activeElement).toBe(wrapper.get('[aria-label="View A filtered timeline"]').element)
    })

    it('keeps the editor, draft and retained results available after Apply fails', async () => {
      wrapper = mount(ReviewerPerformanceWorkspace)
      await flushPromises()
      await wrapper.get('[data-test="filter-pane-toggle"]').trigger('click')
      const filters = wrapper.getComponent(ReviewerPerformanceScopeFilters)
      filters.vm.$emit('update:scope', { ...filters.props('scope'), clientIds: ['pending-client'] })
      await flushPromises()
      query.mockRejectedValueOnce(new Error('Evidence replacement failed'))
      filters.vm.$emit('apply')
      await flushPromises()

      expect(wrapper.get('[data-test="filter-pane-toggle"]').attributes('aria-expanded')).toBe('true')
      expect(filters.props('scope').clientIds).toEqual(['pending-client'])
      expect(wrapper.get('.performance-content [role="alert"]').text()).toBe('Evidence replacement failed')
      expect(wrapper.getComponent({ name: 'ReviewerPerformanceRangeChart' }).props('data')).toEqual(view.series)
    })

    it('preserves a pane explicitly hidden and reopened while Apply is pending', async () => {
      wrapper = mount(ReviewerPerformanceWorkspace, { attachTo: document.body })
      await flushPromises()
      const toggle = wrapper.get('[data-test="filter-pane-toggle"]')
      await toggle.trigger('click')
      let finishApply!: (value: typeof response) => void
      query.mockReturnValueOnce(new Promise((resolve) => { finishApply = resolve }))
      wrapper.getComponent(ReviewerPerformanceScopeFilters).vm.$emit('apply')
      await flushPromises()
      const applied = query.mock.calls.at(-1)![0]
      await wrapper.get('.performance-filter-pane__header button').trigger('click')
      await flushPromises()
      expect(document.activeElement).toBe(toggle.element)
      await toggle.trigger('click')
      finishApply({ ...response, query: applied, views: [view] })
      await flushPromises()

      expect(wrapper.get('[data-test="filter-pane-toggle"]').attributes('aria-expanded')).toBe('true')
      expect(wrapper.getComponent(ReviewerPerformanceScopeFilters).props('disabled')).toBe(false)
      expect(document.activeElement).toBe(toggle.element)
      expect(query).toHaveBeenCalledTimes(2)
      expect(wrapper.text()).not.toContain('Scope edits are pending')
    })

    it('keeps the pane and focus when an earlier Apply finishes after saved-report navigation', async () => {
      list.mockResolvedValue([{ id: 'stored', name: 'Saved selection' }])
      open.mockResolvedValue({
        report: { id: 'stored', name: 'Saved selection' },
        response: {
          ...response,
          query: { ...response.query, views: [view.scope, view.scope] },
          views: [view, view],
        },
        compatibleVersion: true,
      })
      wrapper = mount(ReviewerPerformanceWorkspace, { attachTo: document.body })
      await flushPromises()
      await wrapper.get('[data-test="filter-pane-toggle"]').trigger('click')
      let finishApply!: (value: typeof response) => void
      query.mockReturnValueOnce(new Promise((resolve) => { finishApply = resolve }))
      wrapper.getComponent(ReviewerPerformanceScopeFilters).vm.$emit('apply')
      await flushPromises()
      await wrapper.get('[data-test="report-select"]').setValue('stored')
      await flushPromises()
      const toggle = wrapper.get('[data-test="filter-pane-toggle"]')
      ;(toggle.element as HTMLButtonElement).focus()
      finishApply(response)
      await flushPromises()

      expect(wrapper.get('[data-test="filter-pane-toggle"]').attributes('aria-expanded')).toBe('true')
      expect(wrapper.getComponent(ReviewerPerformanceScopeFilters).props('disabled')).toBe(true)
      expect(wrapper.get('[data-test="comparison"] input[type="checkbox"]').element).toHaveProperty('checked', true)
      expect(wrapper.get('.performance-content .saved-badge').text()).toContain('Saved selection')
      expect(document.activeElement).toBe(toggle.element)
    })

    it('keeps captured A/B facets and saved identity when the pane is hidden and reopened', async () => {
      const capturedA = { id: 'captured-a', label: 'Captured A' }
      const capturedB = { id: 'captured-b', label: 'Captured B' }
      const views = [capturedA, capturedB].map((client) => ({
        ...view,
        scope: { ...view.scope, clientIds: [client.id] },
        facets: { ...view.facets, clients: [client] },
      }))
      list.mockResolvedValue([{ id: 'stored', name: 'Captured comparison' }])
      open.mockResolvedValue({
        report: { id: 'stored', name: 'Captured comparison', calculationVersion: 'captured-version' },
        response: { ...response, query: { ...response.query, views: views.map((item) => item.scope) }, views },
        compatibleVersion: false,
      })
      wrapper = mount(ReviewerPerformanceWorkspace, {
        props: { clients: [{ id: 'current-client', label: 'Current client' }] },
      })
      await flushPromises()
      await wrapper.get('[data-test="filter-pane-toggle"]').trigger('click')
      await wrapper.get('[data-test="report-select"]').setValue('stored')
      await flushPromises()
      expect(wrapper.find('.performance-layout--compared').exists()).toBe(true)
      expect(wrapper.getComponent(ReviewerPerformanceScopeFilters).props('facets').clients).toEqual([capturedA])
      await wrapper.get('[data-test="scope-view"]').setValue('1')
      expect(wrapper.getComponent(ReviewerPerformanceScopeFilters).props('facets').clients).toEqual([capturedB])
      expect(wrapper.getComponent(ReviewerPerformanceScopeFilters).props('disabled')).toBe(true)
      await wrapper.get('[data-test="filter-pane-toggle"]').trigger('click')
      expect(wrapper.get('.performance-content .saved-badge').text()).toContain('Captured comparison')
      expect(wrapper.get('.performance-content').text()).toContain('This report uses captured-version')
      await wrapper.get('[data-test="filter-pane-toggle"]').trigger('click')
      expect(wrapper.get('[data-test="scope-view"]').element).toHaveProperty('value', '1')
      expect(wrapper.getComponent(ReviewerPerformanceScopeFilters).props('facets').clients).toEqual([capturedB])
      expect(query).toHaveBeenCalledTimes(1)
    })
  })
  it('keeps the newer live navigation when an earlier saved-report request finishes later', async () => {
    list.mockResolvedValue([{ id: 'stored', name: 'Older selection' }])
    let finish!: (value: unknown) => void
    open.mockImplementation(
      () =>
        new Promise((resolve) => {
          finish = resolve
        }),
    )
    const wrapper = mount(ReviewerPerformanceWorkspace)
    await flushPromises()
    await wrapper.get('[data-test="report-select"]').setValue('stored')
    await wrapper.get('[data-test="report-select"]').setValue('')
    await flushPromises()
    finish({ report: { id: 'stored', name: 'Older selection' }, response, compatibleVersion: true })
    await flushPromises()
    expect(wrapper.get('[data-test="report-select"]').element).toHaveProperty('value', '')
    expect(wrapper.get('[data-test="aggregation"]').attributes('disabled')).toBeUndefined()
    expect(wrapper.text()).not.toContain('Saved: Older selection')
  })
  it('identifies an incompatible saved scoring version without exposing the premise catalogue', async () => {
    list.mockResolvedValue([{ id: 'stored', name: 'Older report' }])
    open.mockResolvedValue({
      report: { id: 'stored', name: 'Older report', calculationVersion: 'earlier-version' },
      response: {
        ...response,
        calculationVersion: 'earlier-version',
        premiseIds: ['first', 'second'],
      },
      compatibleVersion: false,
    })
    const wrapper = mount(ReviewerPerformanceWorkspace)
    await flushPromises()
    await wrapper.get('[data-test="report-select"]').setValue('stored')
    await flushPromises()
    expect(wrapper.text()).not.toContain('describe 2 outcome')
    expect(wrapper.text()).toContain('earlier-version')
  })
  it('selects a new series last available point when grouping changes', async () => {
    const last = { ...point, date: '2026-09-02' }
    query.mockImplementation(async (q) => ({
      ...response,
      query: q,
      views: [
        {
          ...view,
          series:
            q.grouping === 'model'
              ? [
                  {
                    id: 'old-model',
                    label: 'Old model',
                    points: [point, { ...last, score: { ...last.score, summary: {} } }],
                  },
                ]
              : [{ ...view.series[0], points: [point, last] }],
        },
      ],
    }))
    const wrapper = mount(ReviewerPerformanceWorkspace)
    await flushPromises()
    const grouping = wrapper
      .findAll('select')
      .find((select) => select.find('option[value="model"]').exists())!
    await grouping.setValue('model')
    await flushPromises()
    expect(wrapper.findComponent(ReviewerPerformanceRangeChart).props('selectedDate')).toBe('2026-09-01')
  })
  it('inspects the last supported selected metric before a newer precision-only point', async () => {
    const later = {
      ...point,
      date: '2026-09-02',
      score: { ...point.score, summary: { precision: point.score.summary.f1 } },
    }
    query.mockResolvedValue({
      ...response,
      views: [{ ...view, series: [{ ...view.series[0], points: [point, later] }] }],
    })
    const wrapper = mount(ReviewerPerformanceWorkspace)
    await flushPromises()
    expect(inspectedPoint(wrapper)?.date).toBe(
      '2026-09-01',
    )
    const chart = wrapper.findComponent({ name: 'ReviewerPerformanceRangeChart' })
    chart.vm.$emit('select-series', 'all')
    await flushPromises()
    expect(inspectedPoint(wrapper)?.date).toBe(
      '2026-09-01',
    )
  })
  describe('inspection ownership with the dialog fallback', () => {
    let showModal: PropertyDescriptor | undefined
    beforeEach(() => {
      showModal = Object.getOwnPropertyDescriptor(HTMLDialogElement.prototype, 'showModal')
      Object.defineProperty(HTMLDialogElement.prototype, 'showModal', {
        configurable: true,
        value: undefined,
      })
    })
    afterEach(() => {
      if (showModal) Object.defineProperty(HTMLDialogElement.prototype, 'showModal', showModal)
      else Reflect.deleteProperty(HTMLDialogElement.prototype, 'showModal')
    })
    it.each([
      { selection: 'period', stale: 'response', sameDate: false },
      { selection: 'period', stale: 'error', sameDate: false },
      { selection: 'series', stale: 'response', sameDate: false },
      { selection: 'series', stale: 'error', sameDate: false },
      { selection: 'chart-series', stale: 'response', sameDate: false },
      { selection: 'chart-series', stale: 'error', sameDate: false },
      { selection: 'series', stale: 'response', sameDate: true },
      { selection: 'series', stale: 'error', sameDate: true },
      { selection: 'chart-series', stale: 'response', sameDate: true },
      { selection: 'chart-series', stale: 'error', sameDate: true },
    ])(
      'preserves a newer $selection inspection after a late explorer $stale with shared date=$sameDate',
      async ({ selection, stale, sameDate }) => {
        const later = {
          ...point,
          date: sameDate ? point.date : '2026-09-02',
          score: { ...point.score, summary: { precision: point.score.summary.f1 } },
        }
        const supported = {
          ...point,
          date: later.date,
          score: {
            ...point.score,
            counts: { ...point.score.counts, outcomes: { positive: 20, wrong: 1 } },
          },
        }
        const inspectedView = {
          ...view,
          series: [
            { ...view.series[0], points: sameDate ? [point] : [point, later] },
            { id: 'later-series', label: 'Later supported series', points: [supported] },
          ],
        }
        query.mockImplementation(async (request) => ({
          ...response,
          query: request,
          views: [inspectedView],
        }))
        const wrapper = mount(ReviewerPerformanceWorkspace, { attachTo: document.body })
        try {
          await flushPromises()
          query.mockRejectedValueOnce(new Error('Primary replacement failed'))
          await wrapper.get('[data-test="aggregation"]').setValue('period')
          await flushPromises()
          let finish!: (value: unknown) => void
          let fail!: (exception: Error) => void
          query.mockImplementationOnce(
            () =>
              new Promise((resolve, reject) => {
                finish = resolve
                fail = reject
              }),
          )
          const explore = wrapper
            .findAll('button')
            .find((button) => button.text() === 'Explore dimensions')!
          await explore.trigger('click')
          const originalRequest = query.mock.calls.at(-1)![0]
          expect(originalRequest.breakdown.date).toBe(point.date)
          expect(wrapper.get('dialog').attributes('open')).toBe('')
          expect(wrapper.findComponent(ReviewerPerformanceExplorer).props('loading')).toBe(true)

          const control =
            selection === 'chart-series'
              ? wrapper
                  .findAll('.chart-series button')
                  .find((button) => button.text().includes('Later supported series'))!
              : selection === 'period'
                ? wrapper.findComponent(ReviewerPerformanceRangeChart).get('svg')
                : wrapper.get('.performance-inspect select')
          const element = control.element as HTMLElement
          expect(element.matches(':disabled')).toBe(false)
          element.focus()
          expect(document.activeElement).toBe(element)
          if (selection === 'chart-series') await control.trigger('click')
          else if (selection === 'period') await selectChartPoint(wrapper, 0, later.date)
          else await control.setValue('later-series')
          const expectedPoint = selection === 'period' ? later : supported
          expect.soft(wrapper.findComponent(ReviewerPerformanceExplorer).exists()).toBe(false)
          expect.soft(wrapper.text()).not.toContain('Loading retained count intersections')

          if (stale === 'error') fail(new Error('Outdated explorer failed'))
          else
            finish({
              ...response,
              capturedAt: '2026-09-05T10:00:00Z',
              query: originalRequest,
              views: [inspectedView],
            })
          await flushPromises()
          expect
            .soft(wrapper.findComponent(ReviewerPerformanceRangeChart).props('selectedDate'))
            .toBe(later.date)
          expect
            .soft(inspectedPoint(wrapper))
            .toEqual(expectedPoint)
          expect.soft(wrapper.text()).toContain(response.capturedAt)
          expect.soft(wrapper.text()).not.toContain('2026-09-05T10:00:00Z')
          expect
            .soft(wrapper.findAll('[role="alert"]').map((alert) => alert.text()))
            .toEqual(['Primary replacement failed'])
          expect.soft(wrapper.findComponent(ReviewerPerformanceExplorer).exists()).toBe(false)

          const cell = { rowId: 'logic-error', columnId: 'Missing', measurement: expectedPoint }
          query.mockImplementationOnce(async (request) => ({
            ...response,
            query: request,
            views: [{ ...inspectedView, breakdown: [cell] }],
          }))
          await explore.trigger('click')
          await flushPromises()
          expect.soft(query.mock.calls.at(-1)![0].breakdown).toEqual({
            rows: 'type',
            columns: 'qualifier',
            date: later.date,
            viewIndex: 0,
          })
          const reopened = wrapper.findComponent(ReviewerPerformanceExplorer)
          expect(reopened.props('loading')).toBe(false)
          expect(reopened.props('error')).toBe('')
          expect(reopened.props('cells')).toEqual([cell])
          expect(reopened.find('table').exists()).toBe(true)
        } finally {
          wrapper.unmount()
        }
      },
    )
    it.each(['response', 'error'])(
      'invalidates a late explorer %s when alignment changes only the paired inspection date',
      async (stale) => {
        const second = { ...point, date: '2026-09-02' }
        const third = { ...point, date: '2026-09-03' }
        const views = [
          { ...view, series: [{ ...view.series[0], points: [point, second] }] },
          { ...view, series: [{ ...view.series[0], points: [second, third] }] },
        ]
        query.mockImplementation(async (request) => ({
          ...response,
          query: request,
          views: views.slice(0, request.views.length),
        }))
        const wrapper = mount(ReviewerPerformanceWorkspace, { attachTo: document.body })
        try {
          await flushPromises()
          await wrapper.get('[data-test="comparison"] input[type="checkbox"]').setValue(true)
          await flushPromises()
          const charts = wrapper.findAllComponents(ReviewerPerformanceRangeChart)
          expect(charts.map((chart) => chart.props('selectedDate'))).toEqual([
            second.date,
            second.date,
          ])
          let finish!: (value: unknown) => void
          let fail!: (exception: Error) => void
          query.mockImplementationOnce(
            () =>
              new Promise((resolve, reject) => {
                finish = resolve
                fail = reject
              }),
          )
          const explore = wrapper
            .findAll('button')
            .filter((button) => button.text() === 'Explore dimensions')[1]!
          await explore.trigger('click')
          const originalRequest = query.mock.calls.at(-1)![0]
          expect(originalRequest.breakdown).toMatchObject({ date: second.date, viewIndex: 1 })
          expect(wrapper.get('dialog').attributes('open')).toBe('')
          const alignment = wrapper
            .findAll('select')
            .find((select) => select.find('option[value="elapsed"]').exists())!
          await alignment.setValue('elapsed')
          expect(charts.map((chart) => chart.props('selectedDate'))).toEqual([
            second.date,
            third.date,
          ])
          expect.soft(wrapper.findComponent(ReviewerPerformanceExplorer).exists()).toBe(false)
          expect(query).toHaveBeenCalledTimes(3)

          if (stale === 'error') fail(new Error('Outdated paired explorer failed'))
          else
            finish({
              ...response,
              capturedAt: '2026-09-05T10:00:00Z',
              query: originalRequest,
              views,
            })
          await flushPromises()
          expect
            .soft(charts.map((chart) => chart.props('selectedDate')))
            .toEqual([second.date, third.date])
          expect
            .soft(
              wrapper
                .findAllComponents(ReviewerPerformanceRangeChart)
                .map((_, index) => inspectedPoint(wrapper, index)),
            )
            .toEqual([second, third])
          expect.soft(wrapper.text()).toContain(response.capturedAt)
          expect.soft(wrapper.text()).not.toContain('2026-09-05T10:00:00Z')
          expect.soft(wrapper.findAll('[role="alert"]')).toHaveLength(0)
          expect.soft(wrapper.findComponent(ReviewerPerformanceExplorer).exists()).toBe(false)

          const cell = { rowId: 'logic-error', columnId: 'Missing', measurement: third }
          query.mockImplementationOnce(async (request) => ({
            ...response,
            query: request,
            views: [views[0], { ...views[1], breakdown: [cell] }],
          }))
          await explore.trigger('click')
          await flushPromises()
          expect.soft(query.mock.calls.at(-1)![0].breakdown.date).toBe(third.date)
          const reopened = wrapper.findComponent(ReviewerPerformanceExplorer)
          expect(reopened.props('loading')).toBe(false)
          expect(reopened.props('error')).toBe('')
          expect(reopened.props('cells')).toEqual([cell])
          expect(reopened.find('table').exists()).toBe(true)
        } finally {
          wrapper.unmount()
        }
      },
    )
    it('retains the inspected period on a metric switch and completes aligned explorer and axes requests', async () => {
      const later = {
        ...point,
        date: '2026-09-02',
        score: { ...point.score, summary: { precision: point.score.summary.f1 } },
      }
      const paired = { ...point, date: '2026-08-01' }
      const pairedLater = { ...later, date: '2026-08-02' }
      const views = [
        { ...view, series: [{ ...view.series[0], points: [point, later] }] },
        { ...view, series: [{ ...view.series[0], points: [paired, pairedLater] }] },
      ]
      query.mockImplementation(async (request) => ({
        ...response,
        query: request,
        views: views.slice(0, request.views.length),
      }))
      const wrapper = mount(ReviewerPerformanceWorkspace, { attachTo: document.body })
      try {
        await flushPromises()
        await wrapper.get('[data-test="comparison"] input[type="checkbox"]').setValue(true)
        await flushPromises()
        await wrapper
          .findAll('select')
          .find((select) => select.find('option[value="elapsed"]').exists())!
          .setValue('elapsed')
        let finish!: (value: unknown) => void
        query.mockImplementationOnce(
          () =>
            new Promise((resolve) => {
              finish = resolve
            }),
        )
        await wrapper
          .findAll('button')
          .filter((button) => button.text() === 'Explore dimensions')[1]!
          .trigger('click')
        const originalRequest = query.mock.calls.at(-1)![0]
        await wrapper.get('[data-test="metric"]').setValue('precision')
        expect(wrapper.findComponent(ReviewerPerformanceExplorer).props('loading')).toBe(true)
        expect(
          wrapper
            .findAllComponents(ReviewerPerformanceRangeChart)
            .map((chart) => chart.props('selectedDate')),
        ).toEqual([point.date, paired.date])
        const cell = { rowId: 'logic-error', columnId: 'Missing', measurement: paired }
        const completed = {
          ...response,
          query: originalRequest,
          views: [views[0], { ...views[1], breakdown: [cell] }],
        }
        finish(completed)
        await flushPromises()
        let explorer = wrapper.findComponent(ReviewerPerformanceExplorer)
        expect(explorer.props('loading')).toBe(false)
        expect(explorer.props('cells')).toEqual([cell])
        expect(explorer.find('table').exists()).toBe(true)

        query.mockImplementationOnce(async (request) => ({ ...completed, query: request }))
        await explorer.findAll('.explorer-controls select')[0]!.setValue('model')
        await flushPromises()
        expect(query.mock.calls.at(-1)![0].breakdown).toEqual({
          rows: 'model',
          columns: 'qualifier',
          date: paired.date,
          viewIndex: 1,
        })
        explorer = wrapper.findComponent(ReviewerPerformanceExplorer)
        expect(explorer.props('loading')).toBe(false)
        expect(explorer.props('error')).toBe('')
        expect(explorer.props('cells')).toEqual([cell])
        expect(explorer.find('table').exists()).toBe(true)
        expect(
          wrapper
            .findAllComponents(ReviewerPerformanceRangeChart)
            .map((chart) => chart.props('selectedDate')),
        ).toEqual([point.date, paired.date])
      } finally {
        wrapper.unmount()
      }
    })
  })
  it('clears a cancelled live explorer before opening a stored matrix', async () => {
    const matrix = {
      rowId: 'logic-error',
      columnId: 'Missing',
      rowLabel: 'Logical error',
      columnLabel: 'Missing',
      measurement: point,
    }
    list.mockResolvedValue([{ id: 'stored', name: 'Captured matrix' }])
    open.mockResolvedValue({
      report: { id: 'stored', name: 'Captured matrix' },
      response: {
        ...response,
        query: {
          ...response.query,
          breakdown: { rows: 'type', columns: 'qualifier', date: point.date },
        },
        views: [{ ...view, breakdown: [matrix] }],
      },
      compatibleVersion: true,
    })
    const wrapper = mount(ReviewerPerformanceWorkspace)
    await flushPromises()
    let finish!: (value: unknown) => void
    query.mockImplementationOnce(
      () =>
        new Promise((resolve) => {
          finish = resolve
        }),
    )
    await wrapper
      .findAll('button')
      .find((button) => button.text() === 'Explore dimensions')!
      .trigger('click')
    expect(wrapper.findComponent(ReviewerPerformanceExplorer).props('loading')).toBe(true)
    wrapper.findComponent(ReviewerPerformanceExplorer).vm.$emit('close')
    await flushPromises()
    await wrapper.get('[data-test="report-select"]').setValue('stored')
    await flushPromises()
    await wrapper
      .findAll('button')
      .find((button) => button.text() === 'Explore dimensions')!
      .trigger('click')
    await flushPromises()
    expect(wrapper.findComponent(ReviewerPerformanceExplorer).props('loading')).toBe(false)
    expect(wrapper.findComponent(ReviewerPerformanceExplorer).props('cells')).toEqual([matrix])
    finish(response)
    await flushPromises()
    expect(wrapper.findComponent(ReviewerPerformanceExplorer).props('cells')).toEqual([matrix])
  })
  it.each([
    { setting: 'aggregation', primaryFails: false, stale: 'response' },
    { setting: 'aggregation', primaryFails: true, stale: 'error' },
    { setting: 'bucket', primaryFails: false, stale: 'error' },
    { setting: 'bucket', primaryFails: true, stale: 'response' },
    { setting: 'grouping', primaryFails: false, stale: 'response' },
    { setting: 'grouping', primaryFails: true, stale: 'error' },
  ])(
    'invalidates pending exploration for $setting and rejects its late $stale when primary fails: $primaryFails',
    async ({ setting, primaryFails, stale }) => {
      const wrapper = mount(ReviewerPerformanceWorkspace)
      await flushPromises()
      const originalQuery = query.mock.calls[0]![0]
      let finishExplorer!: (value: unknown) => void
      let failExplorer!: (exception: Error) => void
      query.mockImplementationOnce(
        () =>
          new Promise((resolve, reject) => {
            finishExplorer = resolve
            failExplorer = reject
          }),
      )
      const explore = wrapper
        .findAll('button')
        .find((button) => button.text() === 'Explore dimensions')!
      await explore.trigger('click')
      expect(wrapper.findComponent(ReviewerPerformanceExplorer).props('loading')).toBe(true)

      let finishPrimary!: (value: unknown) => void
      let failPrimary!: (exception: Error) => void
      query.mockImplementationOnce(
        () =>
          new Promise((resolve, reject) => {
            finishPrimary = resolve
            failPrimary = reject
          }),
      )
      const control =
        setting === 'aggregation'
          ? wrapper.get('[data-test="aggregation"]')
          : wrapper
              .findAll('select')
              .find((select) =>
                select.find(`option[value="${setting === 'bucket' ? 'week' : 'model'}"]`).exists(),
              )!
      await control.setValue(
        setting === 'aggregation' ? 'period' : setting === 'bucket' ? 'week' : 'model',
      )
      expect.soft(wrapper.findComponent(ReviewerPerformanceExplorer).exists()).toBe(false)
      expect(wrapper.get('.performance-status').text()).toContain('Loading retained evidence')
      const primaryQuery = query.mock.calls[2]![0]
      const winningPoint = { ...point, date: '2026-09-02' }
      const winningView = { ...view, series: [{ ...view.series[0], points: [winningPoint] }] }
      const expectedPoint = primaryFails ? point : winningPoint
      const expectedView = primaryFails ? view : winningView
      const expectedQuery = primaryFails ? originalQuery : primaryQuery
      const capturedAt = primaryFails ? response.capturedAt : '2026-09-04T10:00:00Z'
      if (primaryFails) failPrimary(new Error('Primary replacement failed'))
      else finishPrimary({ ...response, capturedAt, query: primaryQuery, views: [winningView] })
      await flushPromises()
      expect(wrapper.find('.performance-status').exists()).toBe(false)
      expect.soft(wrapper.text()).not.toContain('Loading retained count intersections')

      if (stale === 'error') failExplorer(new Error('Outdated explorer failed'))
      else finishExplorer({ ...response, capturedAt: '2026-09-05T10:00:00Z' })
      await flushPromises()
      expect(wrapper.text()).toContain(capturedAt)
      expect(wrapper.text()).not.toContain('2026-09-05T10:00:00Z')
      expect(wrapper.text()).not.toContain('Outdated explorer failed')
      expect(inspectedPoint(wrapper)).toEqual(
        expectedPoint,
      )
      expect(wrapper.findAll('[role="alert"]').map((alert) => alert.text())).toEqual(
        primaryFails ? ['Primary replacement failed'] : [],
      )
      expect(wrapper.findComponent(ReviewerPerformanceExplorer).exists()).toBe(false)

      const cell = {
        rowId: 'logic-error',
        columnId: 'Missing',
        rowLabel: 'Logical error',
        columnLabel: 'Missing',
        measurement: expectedPoint,
      }
      query.mockImplementationOnce(async (request) => ({
        ...response,
        capturedAt,
        query: request,
        views: [{ ...expectedView, breakdown: [cell] }],
      }))
      await explore.trigger('click')
      await flushPromises()
      expect(query.mock.calls.at(-1)![0]).toEqual({
        ...expectedQuery,
        breakdown: { rows: 'type', columns: 'qualifier', date: expectedPoint.date, viewIndex: 0 },
      })
      const reopened = wrapper.findComponent(ReviewerPerformanceExplorer)
      expect(reopened.props('loading')).toBe(false)
      expect(reopened.props('error')).toBe('')
      expect(reopened.props('cells')).toEqual([cell])
      expect(reopened.find('table').exists()).toBe(true)
    },
  )
  it('keeps stored exploration open when a frozen primary request does nothing', async () => {
    const cell = { rowId: 'a', columnId: 'x', measurement: point }
    list.mockResolvedValue([{ id: 'stored', name: 'Stored matrix' }])
    open.mockResolvedValue({
      report: { id: 'stored' },
      response: { ...response, views: [{ ...view, breakdown: [cell] }] },
      compatibleVersion: true,
    })
    const wrapper = mount(ReviewerPerformanceWorkspace)
    await flushPromises()
    await wrapper.get('[data-test="report-select"]').setValue('stored')
    await flushPromises()
    await wrapper
      .findAll('button')
      .find((button) => button.text() === 'Explore dimensions')!
      .trigger('click')
    await flushPromises()
    const calls = query.mock.calls.length
    wrapper.findComponent(ReviewerPerformanceScopeFilters).vm.$emit('apply')
    await flushPromises()
    expect(query).toHaveBeenCalledTimes(calls)
    const explorer = wrapper.findComponent(ReviewerPerformanceExplorer)
    expect(explorer.exists()).toBe(true)
    expect(explorer.props('loading')).toBe(false)
    expect(explorer.props('cells')).toEqual([cell])
    expect(explorer.props('frozen')).toBe(true)
  })
  it('returns the single chart to calendar alignment while retaining the comparison preference', async () => {
    const wrapper = mount(ReviewerPerformanceWorkspace)
    await flushPromises()
    await wrapper.get('[data-test="comparison"] input[type="checkbox"]').setValue(true)
    await flushPromises()
    const alignment = wrapper
      .findAll('select')
      .find((select) => select.find('option[value="elapsed"]').exists())!
    await alignment.setValue('elapsed')
    await wrapper.get('[data-test="comparison"] input[type="checkbox"]').setValue(false)
    await flushPromises()
    expect(
      wrapper.findComponent({ name: 'ReviewerPerformanceRangeChart' }).props('alignment'),
    ).toBe('calendar')
    await wrapper.get('[data-test="comparison"] input[type="checkbox"]').setValue(true)
    await flushPromises()
    expect(
      wrapper.findComponent({ name: 'ReviewerPerformanceRangeChart' }).props('alignment'),
    ).toBe('elapsed')
  })
  it('retains scope controls after the initial request fails', async () => {
    query.mockRejectedValueOnce(new Error('Narrow the selection'))
    const wrapper = mount(ReviewerPerformanceWorkspace)
    await flushPromises()
    expect(wrapper.findComponent(ReviewerPerformanceScopeFilters).exists()).toBe(true)
    wrapper.findComponent(ReviewerPerformanceScopeFilters).vm.$emit('apply')
    await flushPromises()
    expect(query).toHaveBeenCalledTimes(2)
  })
  it('captures and explores the displayed query when scope edits are still pending', async () => {
    const wrapper = mount(ReviewerPerformanceWorkspace)
    await flushPromises()
    const applied = query.mock.calls[0]![0]
    wrapper
      .findComponent(ReviewerPerformanceScopeFilters)
      .vm.$emit('update:scope', { ...applied.views[0], from: '2026-09-23' })
    save.mockRejectedValue(new Error('Capture failed'))
    await wrapper
      .findAll('button')
      .find((button) => button.text() === 'Save snapshot')!
      .trigger('click')
    await wrapper.get('.performance-save input').setValue('Applied evidence')
    await wrapper.get('.performance-save').trigger('submit')
    await flushPromises()
    expect(save.mock.calls[0]![2].views).toEqual(applied.views)
    await wrapper
      .findAll('button')
      .find((button) => button.text() === 'Explore dimensions')!
      .trigger('click')
    await flushPromises()
    expect(query.mock.calls.at(-1)![0].views).toEqual(applied.views)
  })
  it.each(['Edited name', '', '   '])(
    'uses the original capture identifier, name and displayed query for a retry after name edit %j',
    async (draftName) => {
      save.mockRejectedValue(new Error('Response lost'))
      const wrapper = mount(ReviewerPerformanceWorkspace, { attachTo: document.body })
      try {
        await flushPromises()
        await wrapper
          .findAll('button')
          .find((button) => button.text() === 'Save snapshot')!
          .trigger('click')
        await wrapper.get('.performance-save input').setValue('Original name')
        await wrapper.get('.performance-save').trigger('submit')
        await flushPromises()
        const first = save.mock.calls[0]
        await wrapper.get('[data-test="aggregation"]').setValue('period')
        await flushPromises()
        await wrapper.get('.performance-save input').setValue(draftName)
        const retry = wrapper.get('.performance-save button[type="submit"]')
        expect(retry.text()).toBe('Retry capture')
        expect(retry.attributes('disabled')).toBeUndefined()
        const retryButton = retry.element as HTMLButtonElement
        retryButton.click()
        await flushPromises()
        expect(save.mock.calls).toHaveLength(2)
        expect(save.mock.calls[1]).toEqual(first)
      } finally {
        wrapper.unmount()
      }
    },
  )
  it('keeps native form validation valid for an empty name draft on a stored retry', async () => {
    save.mockRejectedValue(new Error('Response lost'))
    const wrapper = mount(ReviewerPerformanceWorkspace)
    await flushPromises()
    await wrapper
      .findAll('button')
      .find((button) => button.text() === 'Save snapshot')!
      .trigger('click')
    await wrapper.get('.performance-save input').setValue('Original name')
    await wrapper.get('.performance-save').trigger('submit')
    await flushPromises()
    await wrapper.get('.performance-save input').setValue('')
    const input = wrapper.get('.performance-save input').element as HTMLInputElement
    const form = wrapper.get('.performance-save').element as HTMLFormElement
    expect(input.checkValidity()).toBe(true)
    expect(form.checkValidity()).toBe(true)
  })
  it.each(['', '   '])('rejects a new capture with name %j before saving', async (draftName) => {
    save.mockRejectedValue(new Error('Capture failed'))
    const wrapper = mount(ReviewerPerformanceWorkspace)
    await flushPromises()
    await wrapper
      .findAll('button')
      .find((button) => button.text() === 'Save snapshot')!
      .trigger('click')
    await wrapper.get('.performance-save input').setValue(draftName)
    const input = wrapper.get('.performance-save input').element as HTMLInputElement
    expect(input.required).toBe(true)
    expect(input.checkValidity()).toBe(draftName.length > 0)
    expect(
      wrapper.get('.performance-save button[type="submit"]').attributes('disabled'),
    ).toBeDefined()
    await wrapper.get('.performance-save').trigger('submit')
    await flushPromises()
    expect(save).not.toHaveBeenCalled()
  })
  it('restores editable-name validation after cancelling a failed capture', async () => {
    save.mockRejectedValue(new Error('Response lost'))
    const wrapper = mount(ReviewerPerformanceWorkspace)
    await flushPromises()
    const begin = wrapper.findAll('button').find((button) => button.text() === 'Save snapshot')!
    await begin.trigger('click')
    await wrapper.get('.performance-save input').setValue('Original name')
    await wrapper.get('.performance-save').trigger('submit')
    await flushPromises()
    const first = save.mock.calls[0]!
    await wrapper.get('.performance-save input').setValue('')
    await wrapper.get('.performance-save button[type="button"]').trigger('click')
    expect(wrapper.find('.performance-save').exists()).toBe(false)
    await begin.trigger('click')
    const input = wrapper.get('.performance-save input').element as HTMLInputElement
    expect(input.required).toBe(true)
    expect(input.checkValidity()).toBe(false)
    expect(wrapper.get('.performance-save button[type="submit"]').text()).toBe('Capture and save')
    await wrapper.get('.performance-save').trigger('submit')
    await flushPromises()
    expect(save).toHaveBeenCalledTimes(1)

    await wrapper.get('.performance-save input').setValue(' New capture ')
    await wrapper.get('.performance-save').trigger('submit')
    await flushPromises()
    expect(save).toHaveBeenCalledTimes(2)
    expect(save.mock.calls[1]![0]).not.toBe(first[0])
    expect(save.mock.calls[1]![1]).toBe('New capture')
  })
  it.each([false, true])(
    'selects the displayed captured snapshot exactly once when catalogue refresh fails: %s',
    async (catalogueFails) => {
      const report = {
        id: 'captured',
        name: 'Stored snapshot name',
        capturedAt: response.capturedAt,
      }
      save.mockResolvedValue({ report: { id: report.id, name: 'Capture response name' } })
      open.mockResolvedValue({ report, response, compatibleVersion: true })
      const wrapper = mount(ReviewerPerformanceWorkspace)
      await flushPromises()
      if (catalogueFails) list.mockRejectedValueOnce(new Error('Catalogue refresh failed'))
      else list.mockResolvedValueOnce([report])
      await wrapper
        .findAll('button')
        .find((button) => button.text() === 'Save snapshot')!
        .trigger('click')
      await wrapper.get('.performance-save input').setValue('Capture draft name')
      await wrapper.get('.performance-save').trigger('submit')
      await flushPromises()

      const select = wrapper.get('[data-test="report-select"]')
      expect(select.findAll(`option[value="${report.id}"]`)).toHaveLength(1)
      expect(select.get(`option[value="${report.id}"]`).text()).toContain(report.name)
      expect(select.element).toHaveProperty('value', report.id)
      expect(wrapper.text()).toContain(`Saved: ${report.name}`)
      expect(wrapper.text()).not.toContain('Capture response name')
      expect(wrapper.get('[data-test="aggregation"]').attributes('disabled')).toBeDefined()
      expect(inspectedPoint(wrapper)).toEqual(point)
      expect(wrapper.findAll('[role="alert"]').map((alert) => alert.text())).toEqual(
        catalogueFails ? ['Catalogue refresh failed'] : [],
      )
      expect(query).toHaveBeenCalledTimes(1)
    },
  )
  it('does not navigate to a captured report after a newer report selection', async () => {
    list.mockResolvedValue([{ id: 'newer', name: 'Chosen report' }])
    open.mockImplementation(async (id) => ({
      report: { id, name: id === 'newer' ? 'Chosen report' : 'Late capture' },
      response,
      compatibleVersion: true,
    }))
    let finish!: (value: unknown) => void
    save.mockImplementation(
      () =>
        new Promise((resolve) => {
          finish = resolve
        }),
    )
    const wrapper = mount(ReviewerPerformanceWorkspace)
    await flushPromises()
    await wrapper
      .findAll('button')
      .find((button) => button.text() === 'Save snapshot')!
      .trigger('click')
    await wrapper.get('.performance-save input').setValue('Capture')
    await wrapper.get('.performance-save').trigger('submit')
    await wrapper.get('[data-test="report-select"]').setValue('newer')
    await flushPromises()
    list.mockRejectedValueOnce(new Error('Catalogue refresh failed'))
    finish({ report: { id: 'late' } })
    await flushPromises()
    const select = wrapper.get('[data-test="report-select"]')
    expect(select.element).toHaveProperty('value', 'newer')
    expect(select.findAll('option[value="newer"]')).toHaveLength(1)
    expect(select.get('option[value="newer"]').text()).toContain('Chosen report')
    expect(select.find('option[value="late"]').exists()).toBe(false)
    expect(wrapper.text()).not.toContain('Late capture')
    expect(wrapper.get('[role="alert"]').text()).toBe('Catalogue refresh failed')
  })
  it('keeps the current view after cancelling a pending capture', async () => {
    let finish!: (value: unknown) => void
    save.mockImplementation(
      () =>
        new Promise((resolve) => {
          finish = resolve
        }),
    )
    open.mockResolvedValue({ report: { id: 'late' }, response, compatibleVersion: true })
    const wrapper = mount(ReviewerPerformanceWorkspace)
    await flushPromises()
    await wrapper
      .findAll('button')
      .find((button) => button.text() === 'Save snapshot')!
      .trigger('click')
    await wrapper.get('.performance-save input').setValue('Pending capture')
    await wrapper.get('.performance-save').trigger('submit')
    await wrapper
      .findAll('button')
      .find((button) => button.text() === 'Cancel')!
      .trigger('click')
    list.mockResolvedValue([{ id: 'late', name: 'Committed capture' }])
    finish({ report: { id: 'late' } })
    await flushPromises()
    expect(open).not.toHaveBeenCalled()
    expect(wrapper.get('[data-test="report-select"]').element).toHaveProperty('value', '')
    expect(
      wrapper.get('[data-test="report-select"]').find('option[value="late"]').text(),
    ).toContain('Committed capture')
  })
  it('preserves a saved independent comparison scope through live comparison toggles', async () => {
    const b = { ...view, scope: { ...view.scope, models: ['captured-b'] } }
    list.mockResolvedValue([{ id: 'stored', name: 'Compared' }])
    open.mockResolvedValue({
      report: { id: 'stored' },
      response: {
        ...response,
        query: { ...response.query, views: [view.scope, b.scope] },
        views: [view, b],
      },
      compatibleVersion: true,
    })
    const wrapper = mount(ReviewerPerformanceWorkspace)
    await flushPromises()
    await wrapper.get('[data-test="report-select"]').setValue('stored')
    await flushPromises()
    await wrapper.get('[data-test="report-select"]').setValue('')
    await flushPromises()
    await wrapper.get('[data-test="comparison"] input[type="checkbox"]').setValue(false)
    await flushPromises()
    await wrapper.get('[data-test="comparison"] input[type="checkbox"]').setValue(true)
    await flushPromises()
    expect(query.mock.calls.at(-1)![0].views[1].models).toEqual(['captured-b'])
  })
  it('keeps the originating B date when exploring nonoverlapping calendar windows', async () => {
    query.mockImplementation(async (q) => ({
      ...response,
      query: q,
      views: q.views.map((scope: unknown, index: number) => ({
        ...view,
        scope,
        series: [
          {
            ...view.series[0],
            points: [{ ...point, date: index === 0 ? '2026-01-01' : '2026-09-01' }],
          },
        ],
      })),
    }))
    const wrapper = mount(ReviewerPerformanceWorkspace)
    await flushPromises()
    await wrapper.get('[data-test="comparison"] input[type="checkbox"]').setValue(true)
    await flushPromises()
    await selectChartPoint(wrapper, 1, '2026-09-01')
    await wrapper
      .findAll('button')
      .filter((button) => button.text() === 'Explore dimensions')[1]!
      .trigger('click')
    await flushPromises()
    expect(inspectedPoint(wrapper, 1)?.date).toBe(
      '2026-09-01',
    )
    expect(
      inspectedPoint(wrapper, 0),
    ).toBeUndefined()
  })
  it('preserves the matrix measurement date after drilling into a different series', async () => {
    query.mockImplementation(async (q) => ({
      ...response,
      query: q,
      views: q.views.map((scope: unknown) => ({
        ...view,
        scope,
        series: [
          {
            id: q.views[0].types?.[0] ?? 'all',
            label: 'Selected',
            points: [point, { ...point, date: '2026-09-02' }],
          },
        ],
      })),
    }))
    const wrapper = mount(ReviewerPerformanceWorkspace)
    await flushPromises()
    await selectChartPoint(wrapper, 0, '2026-09-01')
    await wrapper
      .findAll('button')
      .find((button) => button.text() === 'Explore dimensions')!
      .trigger('click')
    await flushPromises()
    wrapper
      .findComponent(ReviewerPerformanceExplorer)
      .vm.$emit('drill', { rowId: 'logic-error', columnId: 'Missing', measurement: point })
    await flushPromises()
    expect(inspectedPoint(wrapper)?.date).toBe(
      '2026-09-01',
    )
  })
  it('omits the measurement date for an empty grouped explorer', async () => {
    query.mockImplementation(async (q) => ({
      ...response,
      query: q,
      views: [{ ...view, series: [] }],
    }))
    const wrapper = mount(ReviewerPerformanceWorkspace)
    await flushPromises()
    await wrapper
      .findAll('button')
      .find((button) => button.text() === 'Explore dimensions')!
      .trigger('click')
    await flushPromises()
    expect(query.mock.calls.at(-1)![0].breakdown.date).toBeUndefined()
  })
  it('aligns saved exploration to its captured bucket without issuing a live query', async () => {
    const matrix = [{ rowId: 'logic-error', columnId: 'Missing', measurement: point }]
    const captured = {
      ...response,
      query: {
        ...response.query,
        breakdown: { rows: 'type', columns: 'qualifier', date: '2026-09-01', viewIndex: 0 },
      },
      views: [
        {
          ...view,
          series: [{ ...view.series[0], points: [point, { ...point, date: '2026-09-02' }] }],
          breakdown: matrix,
        },
      ],
    }
    list.mockResolvedValue([{ id: 'stored', name: 'Captured matrix' }])
    open.mockResolvedValue({
      report: { id: 'stored' },
      response: captured,
      compatibleVersion: true,
    })
    const wrapper = mount(ReviewerPerformanceWorkspace)
    await flushPromises()
    await wrapper.get('[data-test="report-select"]').setValue('stored')
    await flushPromises()
    const calls = query.mock.calls.length
    await selectChartPoint(wrapper, 0, '2026-09-02')
    await wrapper
      .findAll('button')
      .find((button) => button.text() === 'Explore dimensions')!
      .trigger('click')
    await flushPromises()
    expect(wrapper.findComponent(ReviewerPerformanceExplorer).props('date')).toBe('2026-09-01')
    expect(inspectedPoint(wrapper)?.date).toBe(
      '2026-09-01',
    )
    expect(query).toHaveBeenCalledTimes(calls)
  })
  it('keeps a newer report selection after an earlier deletion finishes', async () => {
    list.mockResolvedValue([
      { id: 'a', name: 'First report' },
      { id: 'b', name: 'Second report' },
    ])
    open.mockImplementation(async (id) => ({
      report: { id, name: id },
      response,
      compatibleVersion: true,
    }))
    let finish!: (value: unknown) => void
    remove.mockImplementation(
      () =>
        new Promise((resolve) => {
          finish = resolve
        }),
    )
    const wrapper = mount(ReviewerPerformanceWorkspace)
    await flushPromises()
    await wrapper.get('[data-test="report-select"]').setValue('a')
    await flushPromises()
    await wrapper
      .findAll('button')
      .find((button) => button.text() === 'Delete snapshot')!
      .trigger('click')
    await wrapper.get('[data-test="report-select"]').setValue('b')
    await flushPromises()
    list.mockResolvedValue([{ id: 'b', name: 'Second report' }])
    finish(undefined)
    await flushPromises()
    expect(wrapper.get('[data-test="report-select"]').element).toHaveProperty('value', 'b')
    expect(wrapper.text()).toContain('Saved: b')
    expect(wrapper.get('[data-test="report-select"]').find('option[value="a"]').exists()).toBe(
      false,
    )
  })
  it('ignores repeated deletion until the return to current evidence completes', async () => {
    list.mockResolvedValue([{ id: 'stored', name: 'Stored report' }])
    open.mockResolvedValue({
      report: { id: 'stored', name: 'Stored report' },
      response,
      compatibleVersion: true,
    })
    remove
      .mockResolvedValueOnce(undefined)
      .mockRejectedValueOnce(new Error('The report could not be deleted.'))
    const wrapper = mount(ReviewerPerformanceWorkspace)
    await flushPromises()
    await wrapper.get('[data-test="report-select"]').setValue('stored')
    await flushPromises()
    let finishLive!: (value: unknown) => void
    query.mockImplementationOnce(
      () =>
        new Promise((resolve) => {
          finishLive = resolve
        }),
    )
    list.mockResolvedValue([])
    const deleteButton = wrapper
      .findAll('button')
      .find((button) => button.text() === 'Delete snapshot')!
    await deleteButton.trigger('click')
    await flushPromises()
    const disabledDuringReturn = deleteButton.attributes('disabled')
    expect(wrapper.text()).toContain('Loading retained evidence')
    await deleteButton.trigger('click')
    await flushPromises()
    finishLive(response)
    await flushPromises()
    expect(wrapper.find('[role="alert"]').exists()).toBe(false)
    expect(remove).toHaveBeenCalledTimes(1)
    expect(disabledDuringReturn).toBeDefined()
    expect(wrapper.get('[data-test="report-select"]').element).toHaveProperty('value', '')
  })
  it('ignores another same-report click before the pending deletion updates its control', async () => {
    list.mockResolvedValue([{ id: 'stored', name: 'Stored report' }])
    open.mockResolvedValue({
      report: { id: 'stored', name: 'Stored report' },
      response,
      compatibleVersion: true,
    })
    let finishDeletion!: () => void
    remove
      .mockImplementationOnce(
        () =>
          new Promise<void>((resolve) => {
            finishDeletion = resolve
          }),
      )
      .mockRejectedValueOnce(new Error('The report could not be deleted.'))
    const wrapper = mount(ReviewerPerformanceWorkspace)
    await flushPromises()
    await wrapper.get('[data-test="report-select"]').setValue('stored')
    await flushPromises()
    const deleteButton = wrapper
      .findAll('button')
      .find((button) => button.text() === 'Delete snapshot')!
    const firstClick = deleteButton.trigger('click')
    deleteButton.element.click()
    await firstClick
    await flushPromises()
    expect(remove).toHaveBeenCalledTimes(1)
    expect(deleteButton.attributes('disabled')).toBeDefined()
    list.mockResolvedValue([])
    finishDeletion()
    await flushPromises()
    expect(wrapper.get('[data-test="report-select"]').element).toHaveProperty('value', '')
    expect(wrapper.find('[role="alert"]').exists()).toBe(false)
  })
  it('allows retrying the same report after its deletion fails', async () => {
    list.mockResolvedValue([{ id: 'stored', name: 'Stored report' }])
    open.mockResolvedValue({
      report: { id: 'stored', name: 'Stored report' },
      response,
      compatibleVersion: true,
    })
    remove.mockRejectedValueOnce(new Error('Deletion failed')).mockResolvedValueOnce(undefined)
    const wrapper = mount(ReviewerPerformanceWorkspace)
    await flushPromises()
    await wrapper.get('[data-test="report-select"]').setValue('stored')
    await flushPromises()
    const deleteButton = wrapper
      .findAll('button')
      .find((button) => button.text() === 'Delete snapshot')!
    await deleteButton.trigger('click')
    await flushPromises()
    expect(wrapper.find('[role="alert"]').text()).toBe('Deletion failed')
    expect(deleteButton.attributes('disabled')).toBeUndefined()
    list.mockResolvedValue([])
    await deleteButton.trigger('click')
    await flushPromises()
    expect(remove).toHaveBeenCalledTimes(2)
    expect(wrapper.get('[data-test="report-select"]').element).toHaveProperty('value', '')
    expect(wrapper.find('[role="alert"]').exists()).toBe(false)
  })
  it.each([
    { earlier: 'response', newerFails: false },
    { earlier: 'error', newerFails: false },
    { earlier: 'response', newerFails: true },
  ])(
    'keeps the newer catalogue after an older $earlier when newer fails: $newerFails',
    async ({ earlier, newerFails }) => {
      list.mockResolvedValue([
        { id: 'a', name: 'First report' },
        { id: 'b', name: 'Second report' },
      ])
      open.mockImplementation(async (id) => ({
        report: { id, name: id },
        response,
        compatibleVersion: true,
      }))
      let finishFirstDeletion!: () => void
      let finishSecondDeletion!: () => void
      remove
        .mockImplementationOnce(
          () =>
            new Promise<void>((resolve) => {
              finishFirstDeletion = resolve
            }),
        )
        .mockImplementationOnce(
          () =>
            new Promise<void>((resolve) => {
              finishSecondDeletion = resolve
            }),
        )
      const wrapper = mount(ReviewerPerformanceWorkspace)
      await flushPromises()
      await wrapper.get('[data-test="report-select"]').setValue('a')
      await flushPromises()
      await wrapper
        .findAll('button')
        .find((button) => button.text() === 'Delete snapshot')!
        .trigger('click')
      await wrapper.get('[data-test="report-select"]').setValue('b')
      await flushPromises()
      await wrapper
        .findAll('button')
        .find((button) => button.text() === 'Delete snapshot')!
        .trigger('click')

      let finishEarlierList!: (value: unknown) => void
      let failEarlierList!: (exception: Error) => void
      list.mockImplementationOnce(
        () =>
          new Promise((resolve, reject) => {
            finishEarlierList = resolve
            failEarlierList = reject
          }),
      )
      if (newerFails) list.mockRejectedValueOnce(new Error('Latest catalogue read failed'))
      else list.mockResolvedValueOnce([])
      finishFirstDeletion()
      await flushPromises()
      finishSecondDeletion()
      await flushPromises()
      const options = () =>
        wrapper
          .get('[data-test="report-select"]')
          .findAll('option')
          .map((option) => option.attributes('value'))
      const latestOptions = newerFails ? ['', 'a', 'b'] : ['']
      expect(options()).toEqual(latestOptions)
      expect(wrapper.get('[data-test="report-select"]').element).toHaveProperty('value', '')
      if (newerFails) expect(wrapper.text()).toContain('Latest catalogue read failed')

      if (earlier === 'response') finishEarlierList([{ id: 'b', name: 'Second report' }])
      else failEarlierList(new Error('Earlier catalogue read failed'))
      await flushPromises()
      expect(options()).toEqual(latestOptions)
      expect(wrapper.get('[data-test="report-select"]').element).toHaveProperty('value', '')
      expect(wrapper.text()).not.toContain('Earlier catalogue read failed')
      if (newerFails) expect(wrapper.text()).toContain('Latest catalogue read failed')
    },
  )
  it.each(['aggregation', 'bucket', 'grouping'])(
    'keeps pending filters out of an automatic %s reload',
    async (setting) => {
      const wrapper = mount(ReviewerPerformanceWorkspace)
      await flushPromises()
      const applied = query.mock.calls[0]![0]
      wrapper
        .findComponent(ReviewerPerformanceScopeFilters)
        .vm.$emit('update:scope', { ...applied.views[0], models: ['pending-model'] })
      const select =
        setting === 'aggregation'
          ? wrapper.get('[data-test="aggregation"]')
          : wrapper
              .findAll('select')
              .find((item) =>
                item.find(`option[value="${setting === 'bucket' ? 'week' : 'model'}"]`).exists(),
              )!
      await select.setValue(
        setting === 'aggregation' ? 'period' : setting === 'bucket' ? 'week' : 'model',
      )
      await flushPromises()
      expect(query.mock.calls.at(-1)![0].views).toEqual(applied.views)
      expect(wrapper.text()).toContain('Scope edits are pending')
      expect(wrapper.findComponent(ReviewerPerformanceScopeFilters).props('scope').models).toEqual([
        'pending-model',
      ])
    },
  )
  it('retains the applied aggregation label after its replacement fails', async () => {
    const wrapper = mount(ReviewerPerformanceWorkspace)
    await flushPromises()
    query.mockRejectedValueOnce(new Error('Read failed'))
    await wrapper.get('[data-test="aggregation"]').setValue('period')
    await flushPromises()
    expect(wrapper.find('.performance-view__header').text()).toContain(
      'Cumulative counts from the window start',
    )
    expect(wrapper.find('.performance-view__header').text()).not.toContain(
      'Independent period counts',
    )
  })
  it.each(['aggregation', 'bucket', 'grouping'])(
    'retains submitted scopes across overlapping %s requests and leaves later edits pending',
    async (setting) => {
      const wrapper = mount(ReviewerPerformanceWorkspace)
      await flushPromises()
      const original = query.mock.calls[0]![0]
      const submitted = { ...original.views[0], models: ['submitted-model'] }
      const draft = { ...submitted, models: ['later-draft'] }
      let finishApply!: (value: unknown) => void
      query.mockImplementationOnce(
        () =>
          new Promise((resolve) => {
            finishApply = resolve
          }),
      )
      const filters = wrapper.findComponent(ReviewerPerformanceScopeFilters)
      filters.vm.$emit('update:scope', submitted)
      filters.vm.$emit('apply')
      await flushPromises()
      const control =
        setting === 'aggregation'
          ? wrapper.get('[data-test="aggregation"]')
          : wrapper
              .findAll('select')
              .find((item) =>
                item.find(`option[value="${setting === 'bucket' ? 'week' : 'model'}"]`).exists(),
              )!
      await control.setValue(
        setting === 'aggregation' ? 'period' : setting === 'bucket' ? 'week' : 'model',
      )
      await flushPromises()
      expect(query.mock.calls.at(-1)![0].views).toEqual([submitted])
      expect(filters.props('disabled')).toBe(false)
      filters.vm.$emit('update:scope', draft)
      await control.setValue(
        setting === 'aggregation' ? 'cumulative' : setting === 'bucket' ? 'day' : 'none',
      )
      await flushPromises()
      const replacement = query.mock.calls.at(-1)![0]
      expect(replacement.views).toEqual([submitted])
      finishApply({
        ...response,
        capturedAt: '2026-09-03T09:00:00Z',
        query: { ...original, views: [submitted] },
        views: [view],
      })
      await flushPromises()
      expect(wrapper.text()).toContain(response.capturedAt)
      expect(wrapper.text()).not.toContain('2026-09-03T09:00:00Z')
      expect(filters.props('scope')).toEqual(draft)
      expect(wrapper.text()).toContain('Scope edits are pending')
      save.mockRejectedValueOnce(new Error('Capture failed'))
      await wrapper
        .findAll('button')
        .find((button) => button.text() === 'Save snapshot')!
        .trigger('click')
      await wrapper.get('.performance-save input').setValue('Latest evidence')
      await wrapper.get('.performance-save').trigger('submit')
      await flushPromises()
      expect(save.mock.calls[0]![2]).toEqual(replacement)
    },
  )
  it('retains both displayed comparison views and their capture scope after disabling comparison fails', async () => {
    const wrapper = mount(ReviewerPerformanceWorkspace)
    await flushPromises()
    await wrapper.get('[data-test="comparison"] input[type="checkbox"]').setValue(true)
    await flushPromises()
    const displayed = query.mock.calls.at(-1)![0]
    query.mockRejectedValueOnce(new Error('Comparison replacement failed'))
    await wrapper.get('[data-test="comparison"] input[type="checkbox"]').setValue(false)
    await flushPromises()
    expect(wrapper.findAll('[data-test="range-view"]')).toHaveLength(2)
    expect(wrapper.find('.performance-views--compared').exists()).toBe(true)
    expect(wrapper.find('[aria-label="Paired comparison"]').exists()).toBe(true)
    expect(wrapper.findAll('.performance-view__header h2').map((title) => title.text())).toEqual([
      'View A',
      'View B',
    ])
    expect(wrapper.find('[role="alert"]').text()).toBe('Comparison replacement failed')
    save.mockRejectedValueOnce(new Error('Capture failed'))
    await wrapper
      .findAll('button')
      .find((button) => button.text() === 'Save snapshot')!
      .trigger('click')
    await wrapper.get('.performance-save input').setValue('Displayed comparison')
    await wrapper.get('.performance-save').trigger('submit')
    await flushPromises()
    expect(save.mock.calls[0]![2]).toEqual(displayed)
  })
  it('keeps deleted snapshot evidence and prevents repeat deletion after current loading fails', async () => {
    list.mockResolvedValue([{ id: 'stored', name: 'Retained report' }])
    open.mockResolvedValue({
      report: { id: 'stored', name: 'Retained report' },
      response,
      compatibleVersion: true,
    })
    remove.mockResolvedValue(undefined)
    const wrapper = mount(ReviewerPerformanceWorkspace)
    await flushPromises()
    await wrapper.get('[data-test="report-select"]').setValue('stored')
    await flushPromises()
    query.mockRejectedValueOnce(new Error('Current evidence unavailable'))
    list.mockResolvedValue([])
    const deletion = wrapper
      .findAll('button')
      .find((button) => button.text() === 'Delete snapshot')!
    await deletion.trigger('click')
    await flushPromises()
    expect(wrapper.find('[role="alert"]').text()).toBe('Current evidence unavailable')
    expect(wrapper.findAll('[data-test="range-view"]')).toHaveLength(1)
    expect(inspectedPoint(wrapper)).toEqual(point)
    expect(deletion.attributes('disabled')).toBeDefined()
    expect(wrapper.text()).toContain('Snapshot deleted')
    expect(wrapper.get('[data-test="report-select"]').element).toHaveProperty('value', 'stored')
    const deletedOption = wrapper
      .get('[data-test="report-select"]')
      .findAll('option[value="stored"]')
    expect(deletedOption).toHaveLength(1)
    expect(deletedOption[0]!.text()).toBe('Deleted: Retained report (cached evidence)')
    expect(wrapper.get('[data-test="aggregation"]').attributes('disabled')).toBeDefined()
    await deletion.trigger('click')
    expect(remove).toHaveBeenCalledTimes(1)
    await wrapper
      .findAll('button')
      .find((button) => button.text() === 'Return to current')!
      .trigger('click')
    await flushPromises()
    expect(wrapper.get('[data-test="aggregation"]').attributes('disabled')).toBeUndefined()
    expect(wrapper.text()).not.toContain('Snapshot deleted')
  })
  it('keeps newer report navigation when the current load after confirmed deletion fails later', async () => {
    list.mockResolvedValue([
      { id: 'a', name: 'First' },
      { id: 'b', name: 'Second' },
    ])
    open.mockImplementation(async (id) => ({
      report: { id, name: id },
      response,
      compatibleVersion: true,
    }))
    remove.mockResolvedValue(undefined)
    const wrapper = mount(ReviewerPerformanceWorkspace)
    await flushPromises()
    await wrapper.get('[data-test="report-select"]').setValue('a')
    await flushPromises()
    let failCurrent!: (exception: Error) => void
    query.mockImplementationOnce(
      () =>
        new Promise((_, reject) => {
          failCurrent = reject
        }),
    )
    list.mockResolvedValue([{ id: 'b', name: 'Second' }])
    await wrapper
      .findAll('button')
      .find((button) => button.text() === 'Delete snapshot')!
      .trigger('click')
    await flushPromises()
    await wrapper.get('[data-test="report-select"]').setValue('b')
    await flushPromises()
    failCurrent(new Error('Current evidence unavailable'))
    await flushPromises()
    expect(wrapper.get('[data-test="report-select"]').element).toHaveProperty('value', 'b')
    expect(wrapper.text()).toContain('Saved: b')
    expect(wrapper.text()).not.toContain('Snapshot deleted')
    expect(wrapper.find('[role="alert"]').exists()).toBe(false)
    expect(
      wrapper
        .findAll('button')
        .find((button) => button.text() === 'Delete snapshot')!
        .attributes('disabled'),
    ).toBeUndefined()
  })
  it('retains saved provenance when returning to current evidence fails', async () => {
    list.mockResolvedValue([{ id: 'stored', name: 'Retained report' }])
    open.mockResolvedValue({
      report: { id: 'stored', name: 'Retained report' },
      response,
      compatibleVersion: true,
    })
    const wrapper = mount(ReviewerPerformanceWorkspace)
    await flushPromises()
    await wrapper.get('[data-test="report-select"]').setValue('stored')
    await flushPromises()
    query.mockRejectedValueOnce(new Error('Current evidence unavailable'))
    await wrapper.get('[data-test="report-select"]').setValue('')
    await flushPromises()
    expect(wrapper.text()).toContain('Saved: Retained report')
    expect(wrapper.get('[data-test="report-select"]').element).toHaveProperty('value', 'stored')
    expect(wrapper.get('[data-test="aggregation"]').attributes('disabled')).toBeDefined()
  })
})
