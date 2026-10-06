// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { expect, test, type Locator, type Page } from '@playwright/test'
import type { PerformanceResponse } from '../../src/services/reviewerPerformanceService'

async function waitForEvidence(page: Page): Promise<void> {
  await expect(page.locator('.performance-chart__canvas svg').first()).toBeVisible()
  await expect(page.locator('.chart-point').first()).toHaveAttribute('data-date', /^\d{4}-\d{2}-\d{2}$/)
  await expect(page.getByRole('status').filter({ hasText: 'Loading retained evidence' })).toHaveCount(0)
  await page.evaluate(() => document.fonts.ready)
}

async function expectDirectFieldInsets(input: Locator): Promise<void> {
  const geometry = await input.evaluate((element) => {
    const style = getComputedStyle(element)
    return {
      leadingInset: parseFloat(style.paddingInlineStart),
      trailingInset: parseFloat(style.paddingInlineEnd),
      height: element.closest('.v-field')!.getBoundingClientRect().height,
    }
  })
  expect.soft(geometry.leadingInset).toBeGreaterThanOrEqual(12)
  expect.soft(geometry.trailingInset).toBeGreaterThanOrEqual(12)
  expect.soft(geometry.height).toBeGreaterThanOrEqual(44)
  expect.soft(geometry.height).toBeLessThanOrEqual(46)
}

async function expectCenteredComparison(pane: Locator): Promise<void> {
  const geometry = await pane.locator('.performance-filter-pane__compare').evaluate((control) => {
    const label = control.querySelector('.v-label')!
    const text = document.createRange()
    text.selectNodeContents(label)
    const textBounds = text.getBoundingClientRect()
    const iconBounds = control.querySelector('.v-selection-control__input .v-icon')!.getBoundingClientRect()
    return {
      textCenter: textBounds.y + textBounds.height / 2,
      iconCenter: iconBounds.y + iconBounds.height / 2,
      height: control.getBoundingClientRect().height,
    }
  })
  expect.soft(Math.abs(geometry.textCenter - geometry.iconCenter)).toBeLessThanOrEqual(2)
  expect.soft(geometry.height).toBeGreaterThanOrEqual(44)
  expect.soft(geometry.height).toBeLessThanOrEqual(46)
}

async function expectControlsInside(panel: Locator): Promise<void> {
  const panelBounds = (await panel.boundingBox())!
  const fields = await panel.locator('.v-field').evaluateAll((elements) =>
    elements.map((element) => {
      const bounds = element.getBoundingClientRect()
      const selection = element.querySelector('.v-select__selection-text, .performance-facet__selection')?.getBoundingClientRect()
      const icon = element.querySelector('.v-field__append-inner .v-icon')?.getBoundingClientRect()
      return {
        left: bounds.left,
        right: bounds.right,
        height: bounds.height,
        selection: selection ? { left: selection.left, right: selection.right } : null,
        icon: icon ? { left: icon.left, right: icon.right } : null,
      }
    }),
  )
  expect(fields.length).toBeGreaterThan(0)
  for (const field of fields) {
    expect(field.left).toBeGreaterThanOrEqual(panelBounds.x + 16)
    expect(field.right).toBeLessThanOrEqual(panelBounds.x + panelBounds.width - 16)
    expect(field.height).toBeGreaterThanOrEqual(44)
    expect(field.height).toBeLessThanOrEqual(46)
    if (field.icon) {
      expect(field.right - field.icon.right).toBeGreaterThanOrEqual(10)
    }
    if (field.selection) {
      expect(field.selection.left - field.left).toBeGreaterThanOrEqual(12)
      expect(field.selection.right).toBeLessThanOrEqual(field.icon?.left ?? field.right - 12)
    }
  }
  const actions = await panel.locator('button.performance-action').evaluateAll((elements) =>
    elements.map((element) => {
      const bounds = element.getBoundingClientRect()
      return { left: bounds.left, right: bounds.right, height: bounds.height }
    }),
  )
  for (const action of actions) {
    expect(action.left).toBeGreaterThanOrEqual(panelBounds.x + 16)
    expect(action.right).toBeLessThanOrEqual(panelBounds.x + panelBounds.width - 16)
    expect(action.height).toBeGreaterThanOrEqual(44)
    expect(action.height).toBeLessThanOrEqual(46)
  }
}

async function expectBoundedMenu(page: Page): Promise<void> {
  const menu = page.locator('.v-overlay--active .v-overlay__content').last()
  await expect(menu).toBeVisible()
  const viewport = page.viewportSize()!
  await expect.poll(async () => {
    const bounds = await menu.boundingBox()
    return bounds ? bounds.y + bounds.height : Infinity
  }).toBeLessThanOrEqual(viewport.height)
  const bounds = await menu.boundingBox()

  expect(bounds).not.toBeNull()
  expect(bounds!.height).toBeLessThanOrEqual(302)
  expect(bounds!.x).toBeGreaterThanOrEqual(0)
  expect(bounds!.y).toBeGreaterThanOrEqual(0)
  expect(bounds!.x + bounds!.width).toBeLessThanOrEqual(viewport.width)
  expect(bounds!.y + bounds!.height).toBeLessThanOrEqual(viewport.height)
}

async function openPerformance(page: Page): Promise<void> {
  await page.goto('/login')
  await page.getByLabel('Username').fill('admin')
  await page.getByLabel('Password').fill('admin')
  await page.locator('form.login-form button[type="submit"]').click()
  await page.waitForURL('**/clients')
  await page.goto('/reviewer-performance')
}

test('inspects hovered chart measurements without a period dropdown or evidence query', async ({ page }, testInfo) => {
  test.skip(testInfo.project.name !== 'mock', 'Uses deterministic mock performance evidence.')
  await page.setViewportSize({ width: 1440, height: 1080 })
  const returned = page.waitForResponse((response) => response.request().method() === 'POST' && response.url().includes('/reviewer-performance/ranges/query'))
  await openPerformance(page)
  await waitForEvidence(page)
  const result = await (await returned).json() as PerformanceResponse
  const series = result.views![0]!.series![0]!
  const point = series.points!.find((point) => point.score?.summary?.f1)!
  let reads = 0
  page.on('request', (request) => {
    if (request.url().includes('/reviewer-performance/ranges/query')) {
      reads++
    }
  })
  await page.locator(`.chart-point[data-date="${point.date}"][data-series-id="${series.id}"] circle`).hover()
  const details = page.getByRole('tooltip', { name: 'Measurement details', exact: true })
  await expect(details).toBeVisible()
  await expect(details).toContainText(series.label!)
  await expect(details).toContainText(`${point.windowFrom ?? point.date} → ${point.windowTo ?? point.date}`)
  for (const label of ['Precision', 'Observed recall', 'F1']) {
    await expect(details).toContainText(label)
  }
  await expect(page.getByRole('combobox', { name: 'Inspect period', exact: true })).toHaveCount(0)
  await details.hover()
  await expect(details).toBeVisible()
  await page.keyboard.press('Escape')
  await expect(details).toHaveCount(0)
  expect(reads).toBe(0)
})

test('inspects and selects chart measurements with one keyboard entry', async ({ page }, testInfo) => {
  test.skip(testInfo.project.name !== 'mock', 'Uses deterministic mock performance evidence.')
  await openPerformance(page)
  await waitForEvidence(page)
  const chart = page.getByRole('group', { name: 'Inspect reviewer performance chart', exact: true })
  await chart.focus()
  const details = page.getByRole('tooltip', { name: 'Measurement details', exact: true })
  await expect(details).toBeVisible()
  await chart.press('Home')
  const firstWindow = await details.locator('header strong').innerText()
  await chart.press('ArrowRight')
  await expect(details.locator('header strong')).not.toHaveText(firstWindow)
  await chart.press('Enter')
  await chart.press('Escape')
  await expect(details).toHaveCount(0)
  await expect(chart).toBeFocused()
  await expect(page.locator('.performance-chart [tabindex="0"]')).toHaveCount(1)
})

test('keeps selection overlays from intercepting measurement markers', async ({ page }, testInfo) => {
  test.skip(testInfo.project.name !== 'mock', 'Uses deterministic mock performance evidence.')
  await openPerformance(page)
  await waitForEvidence(page)
  const marker = page.locator('.chart-point:has(circle)').last()
  await marker.locator('circle').click()
  const selected = page.locator('.chart-selected line')
  await expect(selected).toHaveCount(1)
  expect(await selected.evaluate((element) => getComputedStyle(element).pointerEvents)).toBe('none')
  const hitDate = await marker.locator('circle').evaluate((element) => {
    const bounds = element.getBoundingClientRect()
    const hit = document.elementFromPoint(bounds.x + bounds.width / 2, bounds.y + bounds.height / 2)
    return hit?.closest('.chart-point')?.getAttribute('data-date')
  })
  expect(hitDate).toBe(await marker.getAttribute('data-date'))
})

test('inspects the focused model when unavailable markers overlap', async ({ page }, testInfo) => {
  test.skip(testInfo.project.name !== 'mock', 'Uses deterministic mock performance evidence.')
  await openPerformance(page)
  await waitForEvidence(page)
  const returned = page.waitForResponse((response) => response.request().method() === 'POST' && response.url().includes('/reviewer-performance/ranges/query'))
  await page.getByRole('combobox', { name: 'Separate series', exact: true }).press('ArrowDown')
  await page.getByRole('option', { name: 'Models', exact: true }).click()
  const result = await (await returned).json() as PerformanceResponse
  await waitForEvidence(page)
  const series = result.views![0]!.series![0]!
  await page.getByRole('combobox', { name: 'Inspect series', exact: true }).press('ArrowDown')
  await page.getByRole('option', { name: series.label!, exact: true }).click()
  const marker = page.locator('.chart-point').filter({ has: page.locator('.chart-unavailable') }).last()
  expect(await marker.getAttribute('data-series-id')).toBe(series.id)
  await marker.hover()
  await expect(page.getByRole('tooltip', { name: 'Measurement details', exact: true })).toContainText(series.label!)
})

test('keeps keyboard-inspected chart points visible on a narrow screen', async ({ page }, testInfo) => {
  test.skip(testInfo.project.name !== 'mock', 'Uses deterministic mock performance evidence.')
  await page.setViewportSize({ width: 390, height: 844 })
  await openPerformance(page)
  await waitForEvidence(page)
  const chart = page.getByRole('group', { name: 'Inspect reviewer performance chart', exact: true })
  const canvas = page.locator('.performance-chart__canvas')
  await chart.focus()
  for (const [key, marker] of [
    ['Home', page.locator('.chart-point:has(circle)').first()],
    ['End', page.locator('.chart-point:has(circle)').last()],
  ] as const) {
    await chart.press(key)
    await expect.poll(async () => {
      const bounds = await marker.boundingBox()
      const visible = await canvas.boundingBox()
      if (!bounds || !visible) {
        return false
      }
      const center = bounds.x + bounds.width / 2
      return center >= visible.x && center <= visible.x + visible.width
    }).toBe(true)
    await expect(page.getByRole('tooltip', { name: 'Measurement details', exact: true })).toBeVisible()
    await expect(chart).toBeFocused()
  }
})

test.describe('touch chart inspection', () => {
  test.use({ hasTouch: true })
  for (const action of ['click', 'tap'] as const) {
    test(`keeps the pressed date during narrow ${action} selection`, async ({ page }, testInfo) => {
      test.skip(testInfo.project.name !== 'mock', 'Uses deterministic mock performance evidence.')
      await page.setViewportSize({ width: 390, height: 844 })
      await openPerformance(page)
      await waitForEvidence(page)
      const point = page.locator('.chart-point:has(circle)').first()
      const date = await point.getAttribute('data-date')
      const x = await point.locator('circle').getAttribute('cx')
      await point.locator('circle')[action]()
      await expect(page.locator('.chart-selected line')).toHaveAttribute('x1', x!)
      if (action === 'click') {
        await point.hover()
      }
      const details = page.getByRole('tooltip', { name: 'Measurement details', exact: true })
      await expect(details.locator('header strong')).toContainText(date!)
    })
  }
  test('bounds tapped measurement details within a narrow viewport', async ({ page }, testInfo) => {
    test.skip(testInfo.project.name !== 'mock', 'Uses deterministic mock performance evidence.')
    await page.setViewportSize({ width: 390, height: 844 })
    await openPerformance(page)
    await waitForEvidence(page)
    await page.locator('.performance-chart__canvas svg circle').first().tap()
    const details = page.getByRole('tooltip', { name: 'Measurement details', exact: true })
    await expect(details).toBeVisible()
    const bounds = (await details.boundingBox())!
    expect(bounds.x).toBeGreaterThanOrEqual(8)
    expect(bounds.y).toBeGreaterThanOrEqual(8)
    expect(bounds.x + bounds.width).toBeLessThanOrEqual(382)
    expect(bounds.y + bounds.height).toBeLessThanOrEqual(836)
    await page.getByRole('button', { name: 'Filters', exact: true }).tap()
    await expect(details).toHaveCount(0)
  })
})

async function selectKeyboardReport(page: Page, report: Locator, position: 'first' | 'second' | 'last'): Promise<void> {
  await report.press('ArrowDown')
  const menuId = await report.getAttribute('aria-controls')
  const options = page.locator('.v-overlay--active').getByRole('option')
  await expect(page.locator('.v-overlay--active [role="option"][aria-selected="true"]')).toBeFocused()
  await page.keyboard.press(position === 'last' ? 'End' : 'Home')
  if (position === 'second') await page.keyboard.press('ArrowDown')
  const index = position === 'last' ? (await options.count()) - 1 : position === 'second' ? 1 : 0
  await expect(options.nth(index)).toBeFocused()
  await page.keyboard.press('Enter')
  await expect(page.locator(`[id="${menuId}"] .v-overlay__content`)).toBeHidden()
}

async function prepareKeyboardReports(page: Page): Promise<void> {
  await page.setViewportSize({ width: 1440, height: 1080 })
  await openPerformance(page)
  await waitForEvidence(page)
  const pane = page.getByRole('complementary', { name: 'Performance filters and reports', exact: true })
  const report = pane.getByRole('combobox', { name: 'Report', exact: true })
  for (const name of ['Keyboard single', 'Keyboard comparison']) {
    if (name === 'Keyboard comparison') {
      await selectKeyboardReport(page, report, 'first')
      await expect(report).toHaveValue('Current evidence')
      await pane.getByRole('checkbox', { name: 'Compare side by side', exact: true }).check()
      await expect(page.locator('[data-test="range-view"]')).toHaveCount(2)
      await expect(page.getByRole('status').filter({ hasText: 'Loading retained evidence' })).toHaveCount(0)
    }
    await pane.getByRole('button', { name: 'Save snapshot', exact: true }).click()
    await pane.getByLabel('Snapshot name', { exact: true }).fill(name)
    await pane.getByRole('button', { name: 'Capture and save', exact: true }).click()
    await expect(page.getByRole('status').filter({ hasText: `Saved: ${name}` })).toBeVisible()
  }
  await selectKeyboardReport(page, report, 'second')
  await expect(page.locator('[data-test="range-view"]')).toHaveCount(1)
  await selectKeyboardReport(page, report, 'first')
  await expect(report).toHaveValue('Current evidence')
  await expect(page.getByRole('status').filter({ hasText: 'Loading retained evidence' })).toHaveCount(0)
}

test('keeps Report focus when keyboard navigation opens a saved comparison and returns to one view', async ({ page }, testInfo) => {
  test.skip(testInfo.project.name !== 'mock', 'Uses deterministic mock performance evidence.')
  await prepareKeyboardReports(page)
  const pane = page.getByRole('complementary', { name: 'Performance filters and reports', exact: true })
  const report = pane.getByRole('combobox', { name: 'Report', exact: true })
  await report.focus()
  await selectKeyboardReport(page, report, 'last')
  await expect(page.getByRole('status').filter({ hasText: 'Saved: Keyboard comparison' })).toBeVisible()
  await expect(page.locator('[data-test="range-view"]')).toHaveCount(2)
  await expect(report).toBeFocused()

  let currentEvidenceReads = 0
  page.on('request', (request) => {
    if (request.url().includes('/reviewer-performance/ranges/query')) {
      currentEvidenceReads++
    }
  })
  const capturedPoint = page.locator('[data-test="range-view"]').first().locator('.chart-point').first()
  const capturedDate = await capturedPoint.getAttribute('data-date')
  await capturedPoint.locator('circle').hover()
  const details = page.getByRole('tooltip', { name: 'View A measurement details', exact: true })
  await expect(details).toContainText(capturedDate!)
  await expect(details.locator('.performance-chart-tooltip__series')).toHaveText('View A · All selected evidence')
  expect(currentEvidenceReads).toBe(0)
  await page.keyboard.press('Escape')
  await expect(details).toHaveCount(0)
  await expect(report).toBeFocused()

  await selectKeyboardReport(page, report, 'second')
  await expect(page.getByRole('status').filter({ hasText: 'Saved: Keyboard single' })).toBeVisible()
  await expect(page.locator('[data-test="range-view"]')).toHaveCount(1)
  await expect(report).toBeFocused()

  await selectKeyboardReport(page, report, 'first')
  await expect(report).toHaveValue('Current evidence')
  await expect(page.getByRole('status').filter({ hasText: 'Loading retained evidence' })).toHaveCount(0)
  await expect(report).toBeFocused()
})

test('leaves operator focus unchanged when a delayed saved comparison finishes loading', async ({ page }, testInfo) => {
  test.skip(testInfo.project.name !== 'mock', 'Uses deterministic mock performance evidence.')
  await prepareKeyboardReports(page)
  await page.evaluate(() => {
    const apiFetch = window.fetch.bind(window)
    const testWindow = window as typeof window & { releaseReport?: () => void }
    window.fetch = (input, init) => {
      const url = typeof input === 'string' ? input : input instanceof Request ? input.url : input.href
      if (url.includes('/reviewer-performance/reports/')) {
        return new Promise<Response>((resolve, reject) => {
          testWindow.releaseReport = () => {
            delete testWindow.releaseReport
            window.fetch = apiFetch
            void apiFetch(input, init).then(resolve, reject)
          }
        })
      }
      return apiFetch(input, init)
    }
  })
  const pane = page.getByRole('complementary', { name: 'Performance filters and reports', exact: true })
  await selectKeyboardReport(page, pane.getByRole('combobox', { name: 'Report', exact: true }), 'last')
  await expect(page.getByRole('status').filter({ hasText: 'Loading retained evidence' })).toBeVisible()
  const filters = page.getByRole('button', { name: 'Filters', exact: true })
  await filters.focus()
  await page.evaluate(() => {
    const testWindow = window as typeof window & { releaseReport?: () => void }
    if (!testWindow.releaseReport) throw new Error('The saved report request was not held.')
    testWindow.releaseReport()
  })
  await expect(page.getByRole('status').filter({ hasText: 'Saved: Keyboard comparison' })).toBeVisible()
  await expect(page.locator('[data-test="range-view"]')).toHaveCount(2)
  await expect(filters).toBeFocused()
})

test('retains insets for direct date and snapshot text fields in current and frozen reports', async ({ page }, testInfo) => {
  test.skip(testInfo.project.name !== 'mock', 'Uses deterministic mock performance evidence.')
  await page.clock.setFixedTime(new Date('2026-10-05T12:00:00Z'))
  await page.setViewportSize({ width: 1440, height: 1080 })
  await openPerformance(page)
  await waitForEvidence(page)
  const pane = page.getByRole('complementary', { name: 'Performance filters and reports', exact: true })
  const from = pane.getByLabel('From', { exact: true })
  const to = pane.getByLabel('To', { exact: true })
  await expectDirectFieldInsets(from)
  await expectDirectFieldInsets(to)
  await expect(from).toHaveAttribute('type', 'date')
  await from.fill('2026-09-07')
  await expect(to).toHaveAttribute('min', '2026-09-07')
  await pane.getByRole('combobox', { name: 'Clients', exact: true }).fill('Payments')
  await page.getByRole('option', { name: 'Payments', exact: true }).click()
  await page.keyboard.press('Escape')
  const applied = page.waitForRequest((request) =>
    request.method() === 'POST' && request.url().includes('/reviewer-performance/ranges/query'),
  )
  await pane.getByRole('button', { name: 'Apply filters', exact: true }).click()
  expect((await applied).postDataJSON().views[0]).toMatchObject({
    from: '2026-09-07',
    clientIds: ['10000000-0000-0000-0000-000000000001'],
  })
  await waitForEvidence(page)
  await pane.getByRole('button', { name: 'Save snapshot', exact: true }).click()
  const name = pane.getByLabel('Snapshot name', { exact: true })
  await expectDirectFieldInsets(name)
  const longName = 'Payments reviewer performance with repository and model comparison '.repeat(2).trim()
  await name.fill(longName)
  await pane.getByRole('button', { name: 'Capture and save', exact: true }).click()
  await expect(page.getByRole('status').filter({ hasText: `Saved: ${longName}` })).toBeVisible()
  await expect(from).toBeDisabled()
  await expect(to).toBeDisabled()
  await expect(pane.getByRole('checkbox', { name: 'Compare side by side', exact: true })).toBeDisabled()
  await expectDirectFieldInsets(from)
  await expectDirectFieldInsets(to)
  await expect(pane.locator('[data-test="scope-clients"] .performance-facet__selection')).toHaveText('Payments')
  await expectControlsInside(pane)
})

test('aligns the Filters action with selectors inside one padded toolbar panel', async ({ page }, testInfo) => {
  test.skip(testInfo.project.name !== 'mock', 'Uses deterministic mock performance evidence.')
  await page.setViewportSize({ width: 1440, height: 1080 })
  await openPerformance(page)
  await waitForEvidence(page)
  const actionBounds = (await page.getByRole('button', { name: 'Filters', exact: true }).boundingBox())!
  const fieldBottoms = await page.locator('.performance-toolbar .v-field').evaluateAll((elements) =>
    elements.map((element) => element.getBoundingClientRect().bottom),
  )
  expect(fieldBottoms).toHaveLength(4)
  for (const bottom of fieldBottoms) {
    expect.soft(Math.abs(actionBounds.y + actionBounds.height - bottom)).toBeLessThanOrEqual(1)
  }
  await expectControlsInside(page.locator('.performance-controls-row'))
})

test('centers comparison text with its checkbox in current and frozen reports', async ({ page }, testInfo) => {
  test.skip(testInfo.project.name !== 'mock', 'Uses deterministic mock performance evidence.')
  await page.setViewportSize({ width: 1440, height: 1080 })
  await openPerformance(page)
  await waitForEvidence(page)
  const pane = page.getByRole('complementary', { name: 'Performance filters and reports', exact: true })
  await expectCenteredComparison(pane)
  await pane.getByRole('checkbox', { name: 'Compare side by side', exact: true }).check()
  await expect(page.locator('[data-test="range-view"]')).toHaveCount(2)
  await pane.getByRole('button', { name: 'Save snapshot', exact: true }).click()
  await pane.getByLabel('Snapshot name', { exact: true }).fill('Comparison spacing')
  await pane.getByRole('button', { name: 'Capture and save', exact: true }).click()
  await expect(page.getByRole('status').filter({ hasText: 'Saved: Comparison spacing' })).toBeVisible()
  await expect(pane.getByRole('checkbox', { name: 'Compare side by side', exact: true })).toBeDisabled()
  await expectCenteredComparison(pane)
})

for (const width of [1100, 1280, 1440]) {
  test(`tabs through comparison reports before scope filters at ${width}px`, async ({ page }, testInfo) => {
    test.skip(testInfo.project.name !== 'mock', 'Uses deterministic mock performance evidence.')
    await page.setViewportSize({ width, height: 1080 })
    await openPerformance(page)
    await waitForEvidence(page)
    const pane = page.getByRole('complementary', { name: 'Performance filters and reports', exact: true })
    await pane.getByRole('checkbox', { name: 'Compare side by side', exact: true }).check()
    await expect(page.locator('[data-test="range-view"]')).toHaveCount(2)
    await expect(page.getByRole('status').filter({ hasText: 'Loading retained evidence' })).toHaveCount(0)

    const alignment = pane.getByRole('combobox', { name: 'Align by', exact: true })
    const report = pane.getByRole('combobox', { name: 'Report', exact: true })
    const save = pane.getByRole('button', { name: 'Save snapshot', exact: true })
    const from = pane.getByLabel('From', { exact: true })
    await alignment.focus()
    await page.keyboard.press('Tab')
    await expect(report).toBeFocused()
    await page.keyboard.press('Tab')
    await expect(save).toBeFocused()
    await page.keyboard.press('Tab')
    await expect(from).toBeFocused()

    const reportBounds = (await report.boundingBox())!
    const fromBounds = (await from.boundingBox())!
    expect(reportBounds.y + reportBounds.height).toBeLessThanOrEqual(fromBounds.y + 1)
  })
}

test('keeps sidebar Tab order from scope actions to reports', async ({ page }, testInfo) => {
  test.skip(testInfo.project.name !== 'mock', 'Uses deterministic mock performance evidence.')
  await page.setViewportSize({ width: 1440, height: 1080 })
  await openPerformance(page)
  await waitForEvidence(page)
  const pane = page.getByRole('complementary', { name: 'Performance filters and reports', exact: true })
  await pane.getByRole('button', { name: 'Apply filters', exact: true }).focus()
  await page.keyboard.press('Tab')
  await expect(pane.getByRole('combobox', { name: 'Report', exact: true })).toBeFocused()
  await page.keyboard.press('Tab')
  await expect(pane.getByRole('button', { name: 'Save snapshot', exact: true })).toBeFocused()
})

for (const width of [320, 379, 380, 390, 600, 601, 960, 961, 1440]) {
  test(`keeps performance controls inside their panels at ${width}px`, async ({ page }, testInfo) => {
    test.skip(testInfo.project.name !== 'mock', 'Uses deterministic mock performance evidence.')
    await page.setViewportSize({ width, height: 1080 })
    await openPerformance(page)
    await waitForEvidence(page)
    const pane = page.getByRole('complementary', { name: 'Performance filters and reports', exact: true })
    const trigger = page.getByRole('button', { name: 'Filters', exact: true })
    if (width <= 960) {
      await trigger.click()
    }
    await expect(pane).toBeVisible()
    await expectControlsInside(pane)
    await expectCenteredComparison(pane)
    await expectDirectFieldInsets(pane.getByLabel('From', { exact: true }))
    await expectDirectFieldInsets(pane.getByLabel('To', { exact: true }))
    const controls = page.locator('.performance-controls-row')
    await expectControlsInside(controls)
    for (const panel of [pane, controls]) {
      const bounds = (await panel.boundingBox())!
      expect(bounds.x).toBeGreaterThanOrEqual(0)
      expect(bounds.x + bounds.width).toBeLessThanOrEqual(width)
    }
    const dates = await pane.locator('.scope-dates .v-field').evaluateAll((elements) =>
      elements.map((element) => {
        const bounds = element.getBoundingClientRect()
        return { x: bounds.x, y: bounds.y, height: bounds.height }
      }),
    )
    expect(dates[0]!.height).toBeCloseTo(dates[1]!.height, 0)
    if (width >= 380 && width <= 960) {
      expect(dates[0]!.y).toBeCloseTo(dates[1]!.y, 0)
    } else {
      expect(dates[0]!.x).toBeCloseTo(dates[1]!.x, 0)
    }
    await pane.getByRole('button', { name: 'Hide filters', exact: true }).click()
    await expect(pane).toHaveCount(0)
    await expect(trigger).toBeFocused()
    await expectControlsInside(controls)
  })
}

test('places filters beside the chart and collapses them without losing pending selections', async ({ page }, testInfo) => {
  test.skip(testInfo.project.name !== 'mock', 'Uses deterministic mock performance evidence.')
  await page.setViewportSize({ width: 1440, height: 1080 })
  await openPerformance(page)
  await waitForEvidence(page)
  await expect(page.getByRole('status').filter({ hasText: 'Loading retained evidence' })).toHaveCount(0)
  const chart = page.locator('.performance-chart__canvas svg').first()
  await expect(chart).toBeVisible()
  const chartBounds = (await chart.boundingBox())!
  const workspaceBounds = (await page.getByRole('region', { name: 'Reviewer performance score ranges', exact: true }).boundingBox())!
  await testInfo.attach('desktop-chart-geometry', {
    body: JSON.stringify({ chart: chartBounds, workspace: workspaceBounds }),
    contentType: 'application/json',
  })
  expect(chartBounds.y - workspaceBounds.y).toBeLessThan(220)
  const pane = page.getByRole('complementary', { name: 'Performance filters and reports', exact: true })
  await expect(pane).toBeVisible()
  const paneBounds = (await pane.boundingBox())!
  expect(paneBounds.x + paneBounds.width).toBeLessThan(chartBounds.x)
  await expect(pane.getByRole('combobox', { name: 'Report', exact: true })).toBeVisible()
  await page.getByRole('button', { name: 'Hide filters', exact: true }).click()
  await expect(pane).toHaveCount(0)
  expect((await chart.boundingBox())!.width).toBeGreaterThan(chartBounds.width)

  await page.setViewportSize({ width: 390, height: 844 })
  await page.evaluate(() => window.scrollTo(0, 0))
  const toggle = page.getByRole('button', { name: 'Filters', exact: true })
  await expect(toggle).toHaveAttribute('aria-expanded', 'false')
  await expect(pane).toHaveCount(0)
  const mobileWorkspace = (await page.getByRole('region', { name: 'Reviewer performance score ranges', exact: true }).boundingBox())!
  await testInfo.attach('mobile-chart-geometry', {
    body: JSON.stringify({ chart: await chart.boundingBox(), workspace: mobileWorkspace }),
    contentType: 'application/json',
  })
  expect((await chart.boundingBox())!.y - mobileWorkspace.y).toBeLessThan(400)
  await toggle.click()
  const clients = pane.getByRole('combobox', { name: 'Clients', exact: true })
  await clients.fill('Payments')
  await expectBoundedMenu(page)
  await page.getByRole('option', { name: 'Payments', exact: true }).click()
  await expect(page.getByRole('status').filter({ hasText: 'Scope edits are pending' })).toBeVisible()
  await expect(pane.getByRole('button', { name: 'Hide filters', exact: true })).toBeInViewport()
  await page.getByRole('button', { name: 'Hide filters', exact: true }).click()
  await expect(page.locator('.v-overlay--active')).toHaveCount(0)
  await expect(toggle).toBeFocused()
  await expect(page.getByRole('status').filter({ hasText: 'Scope edits are pending' })).toBeVisible()
  await toggle.click()
  await expect(pane.locator('.performance-facet__selection').first()).toHaveText('Payments')
  const appliedQuery = page.waitForRequest((request) =>
    request.method() === 'POST' && request.url().includes('/reviewer-performance/ranges/query'),
  )
  await pane.getByRole('button', { name: 'Apply filters', exact: true }).click()
  expect((await appliedQuery).postDataJSON().views[0].clientIds).toEqual([
    '10000000-0000-0000-0000-000000000001',
  ])
  await expect(pane).toHaveCount(0)
  await expect(page.getByLabel('View A filtered timeline', { exact: true })).toBeFocused()
})

test('keeps chart inspection interactive and control menus bounded at desktop and mobile widths', async ({ page }, testInfo) => {
  test.skip(testInfo.project.name !== 'mock', 'Uses deterministic mock performance evidence.')
  await page.setViewportSize({ width: 1440, height: 1080 })
  await openPerformance(page)

  await waitForEvidence(page)
  const fieldBounds = await page.locator('.performance-toolbar .v-field').evaluateAll((fields) =>
    fields.map((field) => ({
      height: field.getBoundingClientRect().height,
      bottom: field.getBoundingClientRect().bottom,
    })),
  )
  expect(fieldBounds).toHaveLength(4)
  for (const field of fieldBounds) {
    expect(field.height).toBeGreaterThanOrEqual(44)
    expect(field.height).toBeLessThanOrEqual(46)
    expect(field.bottom).toBeCloseTo(fieldBounds[0]!.bottom, 0)
  }

  const metric = page.getByRole('combobox', { name: 'Metric', exact: true })
  await metric.press('ArrowDown')
  await expectBoundedMenu(page)
  await page.keyboard.press('Escape')
  const chart = page.getByRole('group', { name: 'Inspect reviewer performance chart', exact: true })
  await chart.focus()
  await chart.press('Home')
  const firstDate = await page.locator('.chart-point').first().getAttribute('data-date')
  const details = page.getByRole('tooltip', { name: 'Measurement details', exact: true })
  await expect(details).toContainText(firstDate!)
  await chart.press('Enter')
  await chart.press('Escape')
  await expect(details).toHaveCount(0)
  await expect(chart).toBeFocused()

  await page.setViewportSize({ width: 390, height: 844 })
  await page.getByRole('button', { name: 'Filters', exact: true }).click()
  const clients = page.getByRole('combobox', { name: 'Clients', exact: true })
  await clients.fill('Payments')
  await expectBoundedMenu(page)
  await page.getByRole('option', { name: 'Payments', exact: true }).click()
  await expect(clients).toHaveValue('')
  await expect(page.locator('.performance-facet__selection').first()).toHaveText('Payments')
  await page.keyboard.press('Escape')
  const appliedQuery = page.waitForRequest((request) =>
    request.method() === 'POST' && request.url().includes('/reviewer-performance/ranges/query'),
  )
  await page.getByRole('button', { name: 'Apply filters', exact: true }).click()
  const request = await appliedQuery
  expect(request.postDataJSON().views[0].clientIds).toEqual(['10000000-0000-0000-0000-000000000001'])
  await expect(page.getByRole('complementary', { name: 'Performance filters and reports', exact: true })).toHaveCount(0)
  await expect(page.getByLabel('View A filtered timeline', { exact: true })).toBeFocused()

  await metric.press('ArrowDown')
  await expectBoundedMenu(page)
  await page.keyboard.press('Escape')
  await expect(metric).toBeFocused()
})

for (const width of [1366, 1440, 1920]) {
  test(`uses the available page width for adjacent comparison charts with filters open at ${width}px`, async ({ page }, testInfo) => {
    test.skip(testInfo.project.name !== 'mock', 'Uses deterministic mock performance evidence.')
    await page.clock.setFixedTime(new Date('2026-10-05T12:00:00Z'))
    await page.setViewportSize({ width, height: 1080 })
    await openPerformance(page)
    await waitForEvidence(page)
    const pane = page.getByRole('complementary', { name: 'Performance filters and reports', exact: true })
    await pane.getByRole('checkbox', { name: 'Compare side by side', exact: true }).check()
    const views = page.locator('[data-test="range-view"]')
    await expect(views).toHaveCount(2)
    await expect(page.getByRole('status').filter({ hasText: 'Loading retained evidence' })).toHaveCount(0)
    await expect(pane).toBeVisible()
    await expectControlsInside(pane)
    await expectCenteredComparison(pane)
    await expectDirectFieldInsets(pane.getByLabel('From', { exact: true }))
    await expectDirectFieldInsets(pane.getByLabel('To', { exact: true }))

    const workspace = (await page.getByRole('region', { name: 'Reviewer performance score ranges', exact: true }).boundingBox())!
    const filters = (await pane.boundingBox())!
    const a = (await views.nth(0).boundingBox())!
    const b = (await views.nth(1).boundingBox())!
    await testInfo.attach('open-comparison-geometry', {
      body: JSON.stringify({ workspace, filters, a, b }),
      contentType: 'application/json',
    })
    expect.soft(workspace.width).toBeGreaterThanOrEqual(width - 64)
    expect.soft(a.y).toBeCloseTo(b.y, 0)
    expect.soft(b.x).toBeGreaterThanOrEqual(a.x + a.width + 15)
    expect.soft(a.width).toBeGreaterThanOrEqual(618)
    expect.soft(b.width).toBeGreaterThanOrEqual(618)
    expect.soft(a.x).toBeCloseTo(workspace.x, 0)
    expect.soft(b.x + b.width).toBeCloseTo(workspace.x + workspace.width, 0)
    expect.soft(filters.width).toBeCloseTo(workspace.width, 0)
    expect.soft(filters.height).toBeLessThanOrEqual(200)
    expect.soft(a.y - workspace.y).toBeLessThan(380)
    expect.soft(filters.x).toBeGreaterThanOrEqual(0)
    expect.soft(filters.x + filters.width).toBeLessThanOrEqual(width)
    const charts = await views.locator('.performance-chart__canvas svg').evaluateAll((elements) =>
      elements.map((element) => element.getBoundingClientRect().width),
    )
    expect(charts).toHaveLength(2)
    for (const chartWidth of charts) {
      expect.soft(chartWidth).toBeGreaterThanOrEqual(580)
    }
  })
}

test('stacks narrow comparison charts while keeping open filters and menus within the workspace', async ({ page }, testInfo) => {
  test.skip(testInfo.project.name !== 'mock', 'Uses deterministic mock performance evidence.')
  await page.setViewportSize({ width: 390, height: 844 })
  await openPerformance(page)
  await waitForEvidence(page)
  await page.getByRole('button', { name: 'Filters', exact: true }).click()
  const pane = page.getByRole('complementary', { name: 'Performance filters and reports', exact: true })
  await pane.getByRole('checkbox', { name: 'Compare side by side', exact: true }).check()
  const views = page.locator('[data-test="range-view"]')
  await expect(views).toHaveCount(2)
  await expect(page.getByRole('status').filter({ hasText: 'Loading retained evidence' })).toHaveCount(0)
  await expectControlsInside(pane)
  await expectCenteredComparison(pane)
  const workspace = (await page.getByRole('region', { name: 'Reviewer performance score ranges', exact: true }).boundingBox())!
  const a = (await views.nth(0).boundingBox())!
  const b = (await views.nth(1).boundingBox())!
  expect(a.x).toBeCloseTo(b.x, 0)
  expect(b.y).toBeGreaterThan(a.y + a.height)
  expect(workspace.x).toBeGreaterThanOrEqual(0)
  expect(workspace.x + workspace.width).toBeLessThanOrEqual(390)
  for (const view of await views.all()) {
    const canvas = view.locator('.performance-chart__canvas')
    const geometry = await canvas.evaluate((element) => ({
      width: element.getBoundingClientRect().width,
      scrollWidth: element.scrollWidth,
      overflow: getComputedStyle(element).overflowX,
    }))
    expect(geometry.width).toBeLessThanOrEqual(workspace.width)
    expect(geometry.scrollWidth).toBeGreaterThanOrEqual(580)
    expect(geometry.overflow).toBe('auto')
  }
  await pane.getByRole('combobox', { name: 'Edit view', exact: true }).press('ArrowDown')
  await expectBoundedMenu(page)
  await page.getByRole('option', { name: 'View B', exact: true }).click()
  await pane.getByRole('button', { name: 'Hide filters', exact: true }).click()
  await expect(page.getByRole('button', { name: 'Filters', exact: true })).toBeFocused()
  await expect(views).toHaveCount(2)
})

test('edits comparison B independently and restores readable side-by-side charts when filters close', async ({ page }, testInfo) => {
  test.skip(testInfo.project.name !== 'mock', 'Uses deterministic mock performance evidence.')
  await page.setViewportSize({ width: 1440, height: 1080 })
  await openPerformance(page)
  await waitForEvidence(page)
  const pane = page.getByRole('complementary', { name: 'Performance filters and reports', exact: true })
  await pane.getByRole('checkbox', { name: 'Compare side by side', exact: true }).check()
  const views = page.locator('[data-test="range-view"]')
  await expect(views).toHaveCount(2)
  const editor = pane.locator('[data-test="scope-view"] .v-field')
  await editor.click()
  await page.getByRole('option', { name: 'View B', exact: true }).click()
  await pane.getByRole('combobox', { name: 'Repositories', exact: true }).fill('Payments API')
  await page.getByRole('option', { name: 'Payments API', exact: true }).click()
  await page.keyboard.press('Escape')
  const appliedRequest = page.waitForRequest((request) =>
    request.method() === 'POST' && request.url().includes('/reviewer-performance/ranges/query'),
  )
  const appliedResponse = page.waitForResponse((response) =>
    response.request().method() === 'POST' && response.url().includes('/reviewer-performance/ranges/query'),
  )
  await pane.getByRole('button', { name: 'Apply filters', exact: true }).click()
  const submitted = (await appliedRequest).postDataJSON()
  expect(submitted.views[0].repositories).toBeNull()
  expect(submitted.views[1].repositories).toHaveLength(1)
  const returned = await (await appliedResponse).json()
  expect(returned.views[0].scope.repositories).toBeNull()
  expect(returned.views[1].scope.repositories).toEqual(submitted.views[1].repositories)
  await expect(pane).toBeVisible()

  await editor.click()
  await page.getByRole('option', { name: 'View A', exact: true }).click()
  await expect(pane.getByRole('combobox', { name: 'Repositories', exact: true })).toHaveAttribute('placeholder', 'All')
  await editor.click()
  await page.getByRole('option', { name: 'View B', exact: true }).click()
  await expect(pane.locator('[data-test="scope-repositories"] .performance-facet__selection')).toHaveText('Payments API')
  await pane.getByRole('button', { name: 'Hide filters', exact: true }).click()
  await expect(pane).toHaveCount(0)
  const a = (await views.nth(0).boundingBox())!
  const b = (await views.nth(1).boundingBox())!
  expect(a.y).toBeCloseTo(b.y, 0)
  expect(b.x).toBeGreaterThan(a.x + a.width)
  expect(a.width).toBeGreaterThanOrEqual(618)
  expect(b.width).toBeGreaterThanOrEqual(618)
})

test('returns focus from a pane menu on resize and preserves focus in an unrelated menu', async ({ page }, testInfo) => {
  test.skip(testInfo.project.name !== 'mock', 'Uses deterministic mock performance evidence.')
  await page.setViewportSize({ width: 1440, height: 1080 })
  await openPerformance(page)
  await waitForEvidence(page)
  const pane = page.getByRole('complementary', { name: 'Performance filters and reports', exact: true })
  const trigger = page.getByRole('button', { name: 'Filters', exact: true })
  await pane.getByRole('combobox', { name: 'Report', exact: true }).press('ArrowDown')
  const reportOption = page.getByRole('option', { name: 'Current evidence', exact: true })
  await expect(reportOption).toBeFocused()
  await expect(pane.getByRole('option')).toHaveCount(0)

  await page.setViewportSize({ width: 960, height: 1080 })
  await expect(pane).toHaveCount(0)
  await expect(page.locator('.v-overlay--active')).toHaveCount(0)
  await expect(trigger).toBeFocused()

  await page.setViewportSize({ width: 1440, height: 1080 })
  await trigger.click()
  await expect(pane).toBeVisible()
  const metric = page.getByRole('combobox', { name: 'Metric', exact: true })
  await metric.press('ArrowDown')
  const metricOption = page.getByRole('option', { name: 'F1 score', exact: true })
  await expect(metricOption).toBeFocused()
  await page.setViewportSize({ width: 960, height: 1080 })
  await expect(pane).toHaveCount(0)
  await expect(metricOption).toBeFocused()
  await page.keyboard.press('Escape')
  await expect(metric).toBeFocused()
})

test('selects dimension options inside the matrix explorer', async ({ page }, testInfo) => {
  test.skip(testInfo.project.name !== 'mock', 'Uses deterministic mock performance evidence.')
  await openPerformance(page)
  await page.getByRole('button', { name: 'Explore dimensions', exact: true }).click()
  const dialog = page.getByRole('dialog', { name: 'Explore dimensions', exact: true })
  await expect(dialog.locator('tbody tr').first()).toBeVisible()

  await expect(dialog.getByRole('combobox', { name: /^(Rows|Columns)$/ })).toHaveCount(2)
  await expect(dialog.getByRole('combobox', { name: 'Display', exact: true })).toHaveCount(0)
  await expect(dialog.getByRole('slider')).toHaveCount(0)
  await expect(dialog.locator('table')).toBeVisible()

  await dialog.getByRole('combobox', { name: 'Rows', exact: true }).press('ArrowDown')
  await dialog.locator('.v-overlay--active').getByRole('option', { name: 'Client', exact: true }).click()
  await expect(dialog.getByRole('combobox', { name: 'Rows', exact: true })).toHaveValue('Client')
  await dialog.getByRole('combobox', { name: 'Columns', exact: true }).press('ArrowDown')
  await dialog.locator('.v-overlay--active').getByRole('option', { name: 'Repository', exact: true }).click()
  await expect(dialog.getByRole('combobox', { name: 'Columns', exact: true })).toHaveValue('Repository')
  await expect(dialog.locator('tbody tr').first()).toBeVisible()
})

test('uses keyboard selection and dismisses only an open menu with Escape in the native dialog', async ({ page }, testInfo) => {
  test.skip(testInfo.project.name !== 'mock', 'Uses deterministic mock performance evidence.')
  await openPerformance(page)
  await page.getByRole('button', { name: 'Explore dimensions', exact: true }).click()
  const dialog = page.getByRole('dialog', { name: 'Explore dimensions', exact: true })
  const rows = dialog.getByRole('combobox', { name: 'Rows', exact: true })
  await rows.press('ArrowDown')
  await expect(dialog.locator('.v-overlay--active [role="option"][aria-selected="true"]')).toBeFocused()
  await page.keyboard.press('ArrowDown')
  await expect(dialog.locator('.v-overlay--active').getByRole('option', { name: 'Client', exact: true })).toBeFocused()
  await page.keyboard.press('Enter')
  await expect(rows).toHaveValue('Client')
  await expect(dialog.locator('.v-overlay--active')).toHaveCount(0)
  await expect(dialog.locator('table')).toBeVisible()

  for (const label of ['Rows', 'Columns']) {
    const control = dialog.getByRole('combobox', { name: label, exact: true })
    await control.press('ArrowDown')
    await expect(dialog.locator('.v-overlay--active')).toHaveCount(1)
    await expect(dialog.locator('.v-overlay--active [role="option"][aria-selected="true"]')).toBeFocused()
    const menuId = await control.getAttribute('aria-controls')
    await page.keyboard.press('Escape')
    await expect(dialog.locator('.v-overlay--active')).toHaveCount(0)
    await expect(dialog.locator(`[id="${menuId}"] .v-overlay__content`)).toBeHidden()
    await expect(control).toBeFocused()
    await expect(dialog).toBeVisible()
    await expect(dialog.locator('table')).toBeVisible()
    await expect(rows).toHaveValue('Client')
  }
  await page.keyboard.press('Escape')
  await expect(dialog).toHaveCount(0)
  await expect(page.getByRole('button', { name: 'Explore dimensions', exact: true })).toBeFocused()
})
