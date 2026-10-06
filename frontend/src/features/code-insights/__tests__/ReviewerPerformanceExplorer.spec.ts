// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { describe, expect, it, vi } from 'vitest'
import { mount } from '@vue/test-utils'
import { toRaw } from 'vue'
import type { PerformanceMatrixCell } from '@/services/reviewerPerformanceService'
import ReviewerPerformanceExplorer from '../components/ReviewerPerformanceExplorer.vue'

vi.mock('../components/ReviewerPerformanceSelect.vue', async () => {
  const { performanceSelectStub } = await import('./performanceControlStubs')
  return { default: performanceSelectStub }
})

const retainedCells: PerformanceMatrixCell[] = [
  {
    rowId: 'a',
    rowLabel: 'Type A',
    columnId: 'x',
    columnLabel: 'Kind X',
    measurement: {
      date: '2026-09-02',
      windowFrom: '2026-09-01',
      windowTo: '2026-09-02',
      score: {
        counts: { outcomes: { total: 7 } },
        summary: {
          f1: { minimum: 0.2, firstQuartile: 0.3, median: 0.5, thirdQuartile: 0.7, maximum: 0.8 },
        },
      },
    },
  },
  {
    rowId: 'b',
    rowLabel: 'Type B',
    columnId: 'y',
    columnLabel: 'Kind Y',
    measurement: {
      date: '2026-09-02',
      score: { counts: { outcomes: { total: 3 } }, summary: {} },
    },
  },
]

function mountExplorer(frozen = false) {
  return mount(ReviewerPerformanceExplorer, {
    props: {
      cells: retainedCells,
      metric: 'f1',
      rows: 'type',
      columns: 'qualifier',
      date: '2026-09-02',
      frozen,
    },
  })
}

describe('reviewer performance dimension exploration', () => {
  it.each([false, true])(
    'shows the dimension matrix without display or camera controls (frozen=%s)',
    (frozen) => {
      const wrapper = mountExplorer(frozen)
      expect(wrapper.find('table').exists()).toBe(true)
      expect(wrapper.get('caption').text()).toContain('F1 by Finding type and Kind')
      expect(wrapper.find('[data-test="explorer-display"]').exists()).toBe(false)
      expect(wrapper.find('input[type="range"]').exists()).toBe(false)
      expect(wrapper.find('svg').exists()).toBe(false)
      expect(wrapper.text()).not.toContain('3D range view')
      wrapper.unmount()
    },
  )

  it('prevents native cancellation only while a selector menu is open and keeps Escape bubbling', () => {
    const wrapper = mountExplorer()
    const dialog = wrapper.get('dialog').element
    const menu = document.createElement('div')
    menu.className = 'v-menu v-overlay--active'
    dialog.appendChild(menu)
    const bubbled = vi.fn()
    dialog.addEventListener('keydown', bubbled)

    const menuEscape = new KeyboardEvent('keydown', { key: 'Escape', bubbles: true, cancelable: true })
    wrapper.get('button').element.dispatchEvent(menuEscape)
    expect(menuEscape.defaultPrevented).toBe(true)
    expect(bubbled).toHaveBeenCalledTimes(1)
    expect(wrapper.emitted('close')).toBeUndefined()

    menu.remove()
    const dialogEscape = new KeyboardEvent('keydown', { key: 'Escape', bubbles: true, cancelable: true })
    wrapper.get('button').element.dispatchEvent(dialogEscape)
    expect(dialogEscape.defaultPrevented).toBe(false)
    wrapper.unmount()
  })

  it.each([false, true])(
    'exposes available measurements as static frozen text or an enabled live action (frozen=%s)',
    async (frozen) => {
      const wrapper = mountExplorer(frozen)
      const matrixCell = wrapper.get('tbody td')
      expect(matrixCell.find('button, [role="button"]').exists()).toBe(!frozen)
      const description = frozen
        ? matrixCell.text()
        : matrixCell.get('button').attributes('aria-label')
      expect(description).toContain('F1')
      expect(description).toContain('median 50.0%')
      expect(description).toContain('full range 20.0%–80.0%')
      expect(description).toContain('middle half 30.0%–70.0%')
      expect(description).toContain('7 published')
      expect(matrixCell.text()).toContain('50.0%')
      if (frozen) {
        expect(description).not.toContain('Filter timeline')
        expect(wrapper.findAll('th[scope="row"]').map((heading) => heading.text())).toContain(
          'Type A',
        )
        expect(wrapper.findAll('th[scope="col"]').map((heading) => heading.text())).toContain(
          'Kind X',
        )
        await matrixCell.trigger('click')
        expect(wrapper.emitted('drill')).toBeUndefined()
      } else {
        const button = matrixCell.get('button')
        expect(description).toContain('Filter timeline to Type A, Kind X')
        expect(button.attributes('disabled')).toBeUndefined()
        await button.trigger('click')
        expect(toRaw(wrapper.emitted('drill')?.[0]?.[0] as PerformanceMatrixCell)).toBe(
          retainedCells[0],
        )
      }

      await wrapper.setProps({ metric: 'precision' })
      const unavailable = frozen
        ? matrixCell.text()
        : matrixCell.get('button').attributes('aria-label')
      expect(unavailable).toContain('Precision unavailable')
      expect(unavailable).toContain('middle half unavailable')
      expect(unavailable).toContain('7 published')
      expect(unavailable).not.toContain('50.0%')
    },
  )

  it.each([false, true])(
    'exposes an unavailable metric and retained publications without a numeric score (frozen=%s)',
    (frozen) => {
      const wrapper = mountExplorer(frozen)
      const matrixCell = wrapper.findAll('tbody td')[3]!
      const description = frozen
        ? matrixCell.text()
        : matrixCell.get('button').attributes('aria-label')
      expect(description).toContain('F1 unavailable')
      expect(description).toContain('middle half unavailable')
      expect(description).toContain('3 published')
      expect(description).not.toMatch(/\d(?:\.\d+)?%/)
      expect(matrixCell.find('button, [role="button"]').exists()).toBe(!frozen)
      if (frozen) expect(description).not.toContain('Filter timeline')
      else {
        expect(description).toContain('Filter timeline to Type B, Kind Y')
        expect(matrixCell.get('button').text()).toContain('Evidence unavailable')
        expect(matrixCell.get('button').attributes('disabled')).toBeUndefined()
      }
    },
  )

  it.each(
    [false, true].flatMap((frozen) => [
      { frozen, missing: 'first', quartiles: { firstQuartile: undefined, thirdQuartile: 0.7 } },
      { frozen, missing: 'third', quartiles: { firstQuartile: 0.3, thirdQuartile: undefined } },
      {
        frozen,
        missing: 'both',
        quartiles: { firstQuartile: undefined, thirdQuartile: undefined },
      },
    ]),
  )(
    'reports an unavailable middle half when $missing quartiles are missing (frozen=$frozen)',
    async ({ frozen, quartiles }) => {
      const wrapper = mountExplorer(frozen)
      const cell: PerformanceMatrixCell = {
        ...retainedCells[0],
        measurement: {
          score: {
            counts: { outcomes: { total: 7 } },
            summary: { f1: { minimum: 0.2, median: 0.5, maximum: 0.8, ...quartiles } },
          },
        },
      }
      await wrapper.setProps({ cells: [cell] })
      const matrixCell = wrapper.get('tbody td')
      const description = frozen
        ? matrixCell.text()
        : matrixCell.get('button').attributes('aria-label')
      expect(description).toContain('middle half unavailable')
      expect(description).toContain('median 50.0%')
      expect(description).toContain('full range 20.0%–80.0%')
      expect(description).toContain('7 published')
      expect(description).not.toMatch(/middle half \d/)
      expect(matrixCell.find('button, [role="button"]').exists()).toBe(!frozen)

      if (frozen) {
        await matrixCell.trigger('click')
        expect(wrapper.emitted('drill')).toBeUndefined()
      } else {
        await matrixCell.get('button').trigger('click')
        expect(toRaw(wrapper.emitted('drill')?.[0]?.[0] as PerformanceMatrixCell)).toBe(cell)
      }
    },
  )

  it.each([false, true])(
    'renders retained zero scores and quartiles in matrix descriptions (frozen=%s)',
    async (frozen) => {
      const wrapper = mountExplorer(frozen)
      await wrapper.setProps({
        cells: [
          {
            ...retainedCells[0],
            measurement: {
              score: {
                counts: { outcomes: { total: 0 } },
                summary: {
                  f1: { minimum: 0, firstQuartile: 0, median: 0, thirdQuartile: 0, maximum: 0.8 },
                },
              },
            },
          },
        ],
      })
      const matrixCell = wrapper.get('tbody td')
      const description = frozen
        ? matrixCell.text()
        : matrixCell.get('button').attributes('aria-label')
      expect(description).toContain('median 0.0%')
      expect(description).toContain('full range 0.0%–80.0%')
      expect(description).toContain('middle half 0.0%–0.0%')
      expect(description).toContain('0 published')
      expect(matrixCell.find('button, [role="button"]').exists()).toBe(!frozen)
      expect(wrapper.find('table').exists()).toBe(true)
    },
  )

  it('prepares the matrix while preserving unavailable intersections and emitted drill identity', async () => {
    const wrapper = mountExplorer()

    expect(wrapper.get('.explorer-note').text()).toContain(
      'Select a cell to focus its filtered timeline',
    )
    expect(wrapper.findAll('thead th').map((heading) => heading.text())).toEqual([
      'Finding type / Kind (qualifier)',
      'Kind X',
      'Kind Y',
    ])
    expect(wrapper.findAll('tbody th').map((heading) => heading.text())).toEqual([
      'Type A',
      'Type B',
    ])
    expect(wrapper.findAll('tbody td').map((cell) => cell.text())).toEqual([
      '50.0%20.0%–80.0%7 published',
      'Unavailable',
      'Unavailable',
      'UnavailableEvidence unavailable3 published',
    ])
    await wrapper.get('td button').trigger('click')
    expect(toRaw(wrapper.emitted('drill')?.[0]?.[0] as PerformanceMatrixCell)).toBe(
      retainedCells[0],
    )
  })

  it('emits controlled axis changes and both close interactions', async () => {
    const wrapper = mountExplorer()
    const [rows, columns] = wrapper.findAll('.explorer-controls select')

    expect(rows!.find('option[value="qualifier"]').exists()).toBe(false)
    expect(columns!.find('option[value="type"]').exists()).toBe(false)
    await rows!.setValue('model')
    await columns!.setValue('client')
    expect(wrapper.emitted('axes')).toEqual([
      ['model', 'qualifier'],
      ['type', 'client'],
    ])
    await wrapper.get('header button').trigger('click')
    await wrapper.get('dialog').trigger('close')
    expect(wrapper.emitted('close')).toHaveLength(2)
  })

  it('keeps frozen axes disabled and captured matrix cells static', async () => {
    const wrapper = mountExplorer(true)
    expect(wrapper.get('.explorer-note').text()).toContain('Saved filters cannot change')
    expect(wrapper.get('.explorer-note').text()).toContain('captured ranges')
    expect(wrapper.get('.explorer-note').text()).not.toContain('Select a cell')
    expect(
      wrapper
        .findAll('.explorer-controls select')
        .slice(0, 2)
        .every((select) => select.attributes('disabled') !== undefined),
    ).toBe(true)
    expect(wrapper.findAll('td button, td [role="button"]')).toHaveLength(0)
    await wrapper.get('tbody td').trigger('click')
    expect(wrapper.emitted('drill')).toBeUndefined()
  })

  it('renders loading and failed reads without displaying previous intersections', async () => {
    const wrapper = mountExplorer()
    await wrapper.setProps({ loading: true, error: 'The retained read failed.' })
    expect(wrapper.get('[role="status"]').text()).toContain('Loading retained count intersections')
    expect(wrapper.find('table').exists()).toBe(false)
    expect(wrapper.find('[role="alert"]').exists()).toBe(false)

    await wrapper.setProps({ loading: false })
    expect(wrapper.get('[role="alert"]').text()).toBe('The retained read failed.')
    expect(wrapper.find('table').exists()).toBe(false)
  })

  it.each([false, true])(
    'keeps forward and reverse keyboard focus inside an empty dialog (frozen=%s)',
    async (frozen) => {
      const wrapper = mount(ReviewerPerformanceExplorer, {
        attachTo: document.body,
        props: {
          cells: [],
          metric: 'f1',
          rows: 'type',
          columns: 'qualifier',
          date: '2026-09-01',
          frozen,
        },
      })
      const first = wrapper.get('button').element as HTMLButtonElement
      const lastControl = frozen ? wrapper.get('button') : wrapper.findAll('.explorer-controls select')[1]!
      const last = lastControl.element as HTMLElement
      last.focus()
      await lastControl.trigger('keydown', { key: 'Tab' })
      expect(document.activeElement).toBe(first)
      await wrapper.get('button').trigger('keydown', { key: 'Tab', shiftKey: true })
      expect(document.activeElement).toBe(last)
      wrapper.unmount()
    },
  )
})
