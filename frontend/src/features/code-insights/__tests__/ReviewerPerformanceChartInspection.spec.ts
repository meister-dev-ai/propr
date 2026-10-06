// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { afterEach, describe, expect, it, vi } from 'vitest'
import { mount, type VueWrapper } from '@vue/test-utils'
import type { PerformanceSeries } from '@/services/reviewerPerformanceService'
import ReviewerPerformanceRangeChart from '../components/ReviewerPerformanceRangeChart.vue'

const measured = {
  date: '2026-10-03',
  windowFrom: '2026-09-06',
  windowTo: '2026-10-03',
  score: {
    summary: {
      precision: { minimum: 0.8, maximum: 0.9, median: 0.85 },
      recall: { minimum: 0.5, maximum: 0.7, median: 0.6 },
      f1: { minimum: 0.6, maximum: 0.8, median: 0.7, firstQuartile: 0.65, thirdQuartile: 0.75 },
    },
  },
  unavailableReasons: ['duplicate-verification-incomplete'],
}
const data: PerformanceSeries[] = [
  {
    id: 'payments',
    label: 'Payments API',
    points: [
      measured,
      {
        date: '2026-10-04',
        windowFrom: '2026-09-06',
        windowTo: '2026-10-04',
        score: { summary: { precision: measured.score.summary.precision } },
        unavailableReasons: ['miss-judgement-failed'],
      },
      { ...measured, date: '2026-10-05', windowTo: '2026-10-05' },
    ],
  },
  {
    id: 'libraries',
    label: 'Shared libraries',
    points: [
      { ...measured, date: '2026-10-03' },
      { ...measured, date: '2026-10-04', windowTo: '2026-10-04' },
    ],
  },
]

describe('direct chart measurement inspection', () => {
  let wrapper: VueWrapper | undefined
  afterEach(() => {
    wrapper?.unmount()
    wrapper = undefined
    document.body.innerHTML = ''
    vi.useRealTimers()
  })
  function mountChart(viewLabel?: string): VueWrapper {
    return mount(ReviewerPerformanceRangeChart, {
      attachTo: document.body,
      props: {
        data,
        metric: 'f1',
        selectedDate: measured.date,
        focusSeries: 'payments',
        viewLabel,
      },
      global: { stubs: { teleport: true } },
    })
  }
  it('hovers the stored point window and all three ranges without using the latest annotation', async () => {
    wrapper = mountChart()
    await wrapper.get('[data-date="2026-10-03"][data-series-id="payments"]').trigger('pointerenter', { pointerType: 'mouse' })
    const details = wrapper.get('[role="tooltip"]')
    expect(details.get('.performance-chart-tooltip__series').text()).toBe('Payments API')
    expect(details.text()).toContain('2026-09-06 → 2026-10-03')
    expect(details.text()).toContain('Precision 80.0%–90.0%')
    expect(details.text()).toContain('Observed recall 50.0%–70.0%')
    expect(details.text()).toContain('F1 60.0%–80.0%')
    expect(details.text()).toContain('Verification coverage is incomplete.')
    expect(details.text()).not.toContain('2026-10-05')
    expect(wrapper.emitted('select-point')).toBeUndefined()
  })
  it('includes the comparison view in the visible and announced measurement header', async () => {
    wrapper = mountChart('View B')
    wrapper.get<SVGElement>('svg').element.focus()
    await wrapper.vm.$nextTick()
    const details = wrapper.get('[role="tooltip"]')

    expect(details.attributes('aria-label')).toBe('View B measurement details')
    expect(details.attributes('aria-live')).toBe('polite')
    expect(details.get('.performance-chart-tooltip__series').text()).toBe('View B · Payments API')
  })
  it.each([false, true])('keeps the actual hit series when point coordinates coincide: unavailable=%s', async (unavailable) => {
    wrapper = mountChart()
    const coincidentPoint = {
      ...measured,
      score: unavailable ? undefined : measured.score,
      unavailableReasons: unavailable ? ['no-series-observation-in-period'] : [],
    }
    await wrapper.setProps({
      data: data.map((series) => ({ ...series, points: [coincidentPoint] })),
    })
    const svg = wrapper.get('svg')
    vi.spyOn(svg.element, 'getBoundingClientRect').mockReturnValue({
      left: 0,
      top: 0,
      width: 940,
      height: 360,
    } as DOMRect)
    const hit = wrapper.get('[data-date="2026-10-03"][data-series-id="libraries"]')
    await hit.trigger('pointerenter', { pointerType: 'mouse' })
    expect(wrapper.get('[role="tooltip"]').text()).toContain('Shared libraries')
    const movement = new MouseEvent('pointermove', {
      bubbles: true,
      clientX: 48,
      clientY: unavailable ? 317 : 105,
    })
    Object.defineProperty(movement, 'pointerType', { value: 'mouse' })
    hit.element.dispatchEvent(movement)
    await wrapper.vm.$nextTick()
    expect(wrapper.get('[role="tooltip"]').text()).toContain('Shared libraries')
    expect(wrapper.get('[role="tooltip"]').text()).not.toContain('Payments API')
  })
  it('keeps Escape-dismissed pointer details closed for the same hovered point', async () => {
    wrapper = mountChart()
    const point = wrapper.get('[data-date="2026-10-03"][data-series-id="payments"]')
    await point.trigger('pointerenter', { pointerType: 'mouse' })
    await wrapper.get('svg').trigger('keydown', { key: 'Escape' })
    expect(wrapper.find('[role="tooltip"]').exists()).toBe(false)

    await point.trigger('pointerenter', { pointerType: 'mouse' })
    expect(wrapper.find('[role="tooltip"]').exists()).toBe(false)

    await wrapper.get('svg').trigger('pointerleave')
    await point.trigger('pointerenter', { pointerType: 'mouse' })
    expect(wrapper.find('[role="tooltip"]').exists()).toBe(true)
  })
  it('announces the plotted metric through the chart description', async () => {
    wrapper = mountChart()
    const chart = wrapper.get('svg')
    const descriptions = () => (chart.attributes('aria-describedby') ?? '').split(' ')
      .map((id) => document.getElementById(id)?.textContent).join(' ')

    expect(descriptions()).toContain('Plotted metric: F1.')
    await wrapper.setProps({ metric: 'precision' })
    expect(descriptions()).toContain('Plotted metric: Precision.')
  })
  it('dismisses mouse-selected details on pointer exit even when the chart gains focus', async () => {
    vi.useFakeTimers()
    wrapper = mountChart()
    const chart = wrapper.get<SVGElement>('svg')
    chart.element.focus()
    await wrapper.vm.$nextTick()
    await wrapper.get('[data-date="2026-10-03"][data-series-id="payments"]').trigger('click', { pointerType: 'mouse' })
    await chart.trigger('pointerleave')
    await vi.advanceTimersByTimeAsync(300)

    expect(wrapper.find('[role="tooltip"]').exists()).toBe(false)
    expect(document.activeElement).toBe(chart.element)
    expect(wrapper.emitted('select-point')).toEqual([[{ seriesId: 'payments', date: '2026-10-03' }]])
  })
  it('reveals keyboard-inspected points within a horizontally scrolled canvas', async () => {
    wrapper = mountChart()
    const chart = wrapper.get<SVGElement>('svg')
    const canvas = wrapper.get<HTMLElement>('.performance-chart__canvas')
    vi.spyOn(canvas.element, 'getBoundingClientRect').mockReturnValue({ left: 0, right: 300, width: 300 } as DOMRect)
    vi.spyOn(chart.element, 'getBoundingClientRect').mockImplementation(() => ({
      left: -canvas.element.scrollLeft,
      top: 0,
      width: 580,
      height: 360,
    } as DOMRect))
    chart.element.focus()
    await wrapper.vm.$nextTick()
    await chart.trigger('keydown', { key: 'End' })
    const endScroll = canvas.element.scrollLeft
    expect(endScroll).toBeGreaterThan(0)
    expect(wrapper.get('[role="tooltip"]').text()).toContain('2026-10-05')

    await chart.trigger('keydown', { key: 'Home' })
    expect(canvas.element.scrollLeft).toBeLessThan(endScroll)
    expect(wrapper.get('[role="tooltip"]').text()).toContain('2026-10-03')
    expect(document.activeElement).toBe(chart.element)
    expect(wrapper.emitted('select-point')).toBeUndefined()
  })
  it('keeps details readable while the pointer moves into them and dismisses after leaving', async () => {
    vi.useFakeTimers()
    wrapper = mountChart()
    await wrapper.get('[data-date="2026-10-03"][data-series-id="payments"]').trigger('pointerenter', { pointerType: 'mouse' })
    await wrapper.get('svg').trigger('pointerleave')
    await wrapper.get('[role="tooltip"]').trigger('pointerenter')
    await vi.advanceTimersByTimeAsync(300)
    expect(wrapper.find('[role="tooltip"]').exists()).toBe(true)
    await wrapper.get('[role="tooltip"]').trigger('pointerleave')
    await vi.advanceTimersByTimeAsync(300)
    expect(wrapper.find('[role="tooltip"]').exists()).toBe(false)
  })
  it('uses one keyboard entry, visits unavailable periods and changes series before selection', async () => {
    wrapper = mountChart()
    const entry = wrapper.get<SVGElement>('[aria-label="Inspect reviewer performance chart"]')
    expect(wrapper.findAll('[tabindex="0"]')).toHaveLength(1)
    entry.element.focus()
    await entry.trigger('focus')
    await entry.trigger('keydown', { key: 'ArrowRight' })
    expect(wrapper.get('[role="tooltip"]').text()).toContain('2026-09-06 → 2026-10-04')
    expect(wrapper.get('[role="tooltip"]').text()).toContain('F1 Unavailable')
    expect(wrapper.get('[role="tooltip"]').text()).toContain('at least one human-thread judgement failed')
    await entry.trigger('keydown', { key: 'ArrowDown' })
    expect(wrapper.get('[role="tooltip"]').text()).toContain('Shared libraries')
    await entry.trigger('keydown', { key: 'Enter' })
    expect(wrapper.emitted('select-point')).toEqual([[{ seriesId: 'libraries', date: '2026-10-04' }]])
    await entry.trigger('keydown', { key: 'Escape' })
    expect(wrapper.find('[role="tooltip"]').exists()).toBe(false)
    expect(document.activeElement).toBe(entry.element)
  })
  it('keeps focused keyboard details positioned when focus scrolls the chart into view', async () => {
    wrapper = mountChart()
    const chart = wrapper.get<SVGElement>('svg')
    let top = 600
    vi.spyOn(chart.element, 'getBoundingClientRect').mockImplementation(() => ({
      left: 0,
      top,
      width: 940,
      height: 360,
    } as DOMRect))
    chart.element.focus()
    await wrapper.vm.$nextTick()
    const originalTop = parseFloat(wrapper.get<HTMLElement>('[role="tooltip"]').element.style.top)

    top = 300
    window.dispatchEvent(new Event('scroll'))
    await wrapper.vm.$nextTick()

    const details = wrapper.get<HTMLElement>('[role="tooltip"]')
    const updatedTop = parseFloat(details.element.style.top)
    expect(updatedTop).toBeLessThan(originalTop)
    expect(details.text()).toContain('2026-09-06 → 2026-10-03')
    expect(document.activeElement).toBe(chart.element)
    expect(wrapper.emitted('select-point')).toBeUndefined()
  })
  it('scrolls long keyboard details without selecting another measurement', async () => {
    wrapper = mountChart()
    const chart = wrapper.get<SVGElement>('svg')
    chart.element.focus()
    await wrapper.vm.$nextTick()
    const details = wrapper.get<HTMLElement>('[role="tooltip"]')
    Object.defineProperty(details.element, 'clientHeight', { value: 120 })

    await chart.trigger('keydown', { key: 'PageDown' })
    expect(details.element.scrollTop).toBe(120)
    await chart.trigger('keydown', { key: 'PageUp' })
    expect(details.element.scrollTop).toBe(0)
    expect(details.text()).toContain('2026-09-06 → 2026-10-03')
    expect(document.activeElement).toBe(chart.element)
    expect(wrapper.emitted('select-point')).toBeUndefined()
  })
  it.each(['PageUp', 'PageDown'])('preserves page scrolling after dismissing details: %s', async (key) => {
    wrapper = mountChart()
    const chart = wrapper.get<SVGElement>('svg')
    chart.element.focus()
    await wrapper.vm.$nextTick()
    await chart.trigger('keydown', { key: 'Escape' })
    expect(wrapper.find('[role="tooltip"]').exists()).toBe(false)

    const event = new KeyboardEvent('keydown', { key, bubbles: true, cancelable: true })
    chart.element.dispatchEvent(event)

    expect(event.defaultPrevented).toBe(false)
    expect(document.activeElement).toBe(chart.element)
  })
  it.each([false, true])('places the focused series above coincident markers: unavailable=%s', async (unavailable) => {
    wrapper = mountChart()
    const coincidentPoint = { ...measured, score: unavailable ? undefined : measured.score }
    await wrapper.setProps({ data: data.map((series) => ({ ...series, points: [coincidentPoint] })) })
    let hit = wrapper.findAll('.chart-point').at(-1)!
    expect(hit.attributes('data-series-id')).toBe('payments')
    await hit.trigger('pointerenter', { pointerType: 'mouse' })
    expect(wrapper.get('[role="tooltip"]').text()).toContain('Payments API')

    await wrapper.setProps({ focusSeries: 'libraries' })
    hit = wrapper.findAll('.chart-point').at(-1)!
    expect(hit.attributes('data-series-id')).toBe('libraries')
    await hit.trigger('pointerenter', { pointerType: 'mouse' })
    expect(wrapper.get('[role="tooltip"]').text()).toContain('Shared libraries')
  })
  it('selects a touched point and can dismiss without changing its inspection selection', async () => {
    wrapper = mountChart()
    await wrapper.get('[data-date="2026-10-04"][data-series-id="payments"]').trigger('click', { pointerType: 'touch' })
    expect(wrapper.get('[role="tooltip"]').text()).toContain('F1 Unavailable')
    expect(wrapper.emitted('select-point')).toEqual([[{ seriesId: 'payments', date: '2026-10-04' }]])
    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' }))
    await wrapper.vm.$nextTick()
    expect(wrapper.find('[role="tooltip"]').exists()).toBe(false)
    expect(wrapper.emitted('select-point')).toHaveLength(1)
  })
  it('closes replaced details and handles an empty chart without moving focus', async () => {
    wrapper = mountChart()
    const entry = wrapper.get<SVGElement>('[aria-label="Inspect reviewer performance chart"]')
    entry.element.focus()
    await entry.trigger('focus')
    expect(wrapper.find('[role="tooltip"]').exists()).toBe(true)
    await wrapper.setProps({ data: [] })
    expect(wrapper.find('[role="tooltip"]').exists()).toBe(false)
    await entry.trigger('keydown', { key: 'ArrowRight' })
    await entry.trigger('keydown', { key: 'Enter' })
    expect(wrapper.emitted('select-point')).toBeUndefined()
    expect(document.activeElement).toBe(entry.element)
  })
})
