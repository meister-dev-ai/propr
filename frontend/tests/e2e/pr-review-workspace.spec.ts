// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { expect, test, type Page } from '@playwright/test'

const filePath = 'src/auth/middleware.ts'
const files = [{ filePath, changeType: 'modified', revisionKey: 'rev-2' },
  ...Array.from({ length: 450 }, (_, index) => ({ filePath: 'src/auth/file-' + String(index).padStart(3, '0') + '.ts', changeType: 'modified', revisionKey: 'rev-2' }))]
const message = 'Validate the authorization token before reading claims.'
const threads = [{ threadId: 'thread-1', filePath, line: 12, status: 'Closed', comments: [
  { commentId: 'ai-1', isAiAuthored: true, authorIdentity: 'ProPR', originatingJobId: 'review-1', body: message },
  { commentId: 'human-1', isAiAuthored: false, authorIdentity: 'Jane', body: 'Already tracked in the previous thread.' },
] }]
const findings = [{ id: 'finding-1', jobId: 'review-1', filePath, providerThreadId: 'thread-1', severity: 'Warning', coreTags: ['security'], disposition: 'Dismissed', rejectionReason: 'Redundant', message }]
const unifiedDiff = ['diff --git a/' + filePath + ' b/' + filePath, '--- a/' + filePath, '+++ b/' + filePath,
  '@@ -1,400 +1,400 @@', ...Array.from({ length: 400 }, (_, index) => ' export const value' + index + ' = ' + index)].join('\n')

async function openWorkspace(page: Page, overrides: Partial<{ threads: typeof threads; findings: typeof findings; unifiedDiff: string }> = {}): Promise<void> {
  await page.addInitScript(({ files, threads, findings, unifiedDiff, filePath }) => {
    const original = window.fetch.bind(window)
    window.fetch = async (input, init) => {
      const url = new URL(typeof input === 'string' ? input : input instanceof URL ? input.href : input.url, location.href)
      let data: unknown
      if (url.pathname.endsWith('/review-archive/pull-requests/files')) data = files
      else if (url.pathname.endsWith('/review-archive/pull-requests/threads')) data = threads
      else if (url.pathname.endsWith('/review-archive/pull-requests/file-diff')) data = { filePath, unifiedDiff, isBinary: false, changeType: 'modified' }
      else if (url.pathname.endsWith('/code-quality/findings')) data = findings
      else return original(input, init)
      const method = init?.method ?? (input instanceof Request ? input.method : 'GET')
      if (method.toUpperCase() !== 'GET'
        || url.searchParams.get('repositoryId') !== 'backend-service'
        || url.searchParams.get('pullRequestId') !== '42') throw new Error('Unexpected retained PR read scope.')
      if (url.pathname.endsWith('/code-quality/findings')) {
        if (url.searchParams.get('clientId') !== '1' || url.searchParams.get('offset') !== '0') throw new Error('Unexpected finding read scope.')
      } else if (!url.pathname.includes('/clients/1/review-archive/')) throw new Error('Unexpected retained client scope.')
      if (url.pathname.endsWith('/file-diff')
        && (url.searchParams.get('filePath') !== filePath || url.searchParams.get('revisionKey') !== 'rev-2')) throw new Error('Unexpected retained diff revision.')
      return new Response(JSON.stringify(data), { headers: { 'Content-Type': 'application/json' } })
    }
  }, { files, threads, findings, unifiedDiff, filePath, ...overrides })
  await page.goto('/login')
  await page.getByLabel('Username').fill('admin')
  await page.getByLabel('Password').fill('admin')
  await page.locator('form.login-form button[type="submit"]').click()
  await page.waitForURL('**/clients')
  const query = new URLSearchParams({ clientId: '1', providerScopePath: 'https://dev.azure.com/acme', providerProjectKey: 'proj-x', repositoryId: 'backend-service', pullRequestId: '42' })
  await page.goto('/pr-review?' + query)
  await page.getByTestId('pr-tab-browser').click()
}

test('aligns the back link with the title and keeps tab borders inside the toolbar', async ({ page }, testInfo) => {
  test.skip(testInfo.project.name !== 'mock', 'Uses the mock authentication runtime.')
  await page.setViewportSize({ width: 1920, height: 1080 })
  await openWorkspace(page)
  await page.evaluate(() => document.fonts.ready)

  for (const width of [1920, 1280, 390]) {
    await page.setViewportSize({ width, height: 1080 })
    for (const section of ['stats', 'browser']) {
      await page.getByTestId('pr-tab-' + section).click()
      const geometry = await page.locator('.header-row').evaluate(toolbar => {
        const back = toolbar.querySelector('.back-link')!.getBoundingClientRect()
        const title = toolbar.querySelector('.pr-title-row')!.getBoundingClientRect()
        const tabsElement = toolbar.querySelector<HTMLElement>('.pr-tabs')!
        const tabs = tabsElement.getBoundingClientRect()
        const buttons = Array.from(tabsElement.querySelectorAll('button'), button => {
          const bounds = button.getBoundingClientRect()
          return { top: bounds.top, bottom: bounds.bottom, right: bounds.right }
        })
        return {
          centers: Math.abs((back.top + back.bottom) / 2 - (title.top + title.bottom) / 2),
          titleGap: title.left - back.right,
          rem: Number.parseFloat(getComputedStyle(document.documentElement).fontSize),
          tabTop: tabs.top,
          tabBottom: tabs.bottom,
          tabRight: tabs.right,
          buttons,
          tabClientHeight: tabsElement.clientHeight,
          tabScrollHeight: tabsElement.scrollHeight,
        }
      })
      await page.screenshot({ path: 'test-results/pr-review-toolbar-' + width + '-' + section + '.png' })
      expect.soft(geometry.centers, 'Back link and title share the vertical center').toBeLessThanOrEqual(1)
      if (width >= 700) {
        expect.soft(geometry.titleGap, 'Back link and title have room between them').toBeGreaterThanOrEqual(geometry.rem)
        expect.soft(geometry.buttons.at(-1)!.right, 'The last tab fits inside the desktop toolbar').toBeLessThanOrEqual(geometry.tabRight)
      }
      for (const button of geometry.buttons) {
        expect.soft(button.top, 'The top tab border is visible').toBeGreaterThanOrEqual(geometry.tabTop)
        expect.soft(button.bottom, 'The bottom tab border is visible').toBeLessThanOrEqual(geometry.tabBottom)
      }
      expect.soft(geometry.tabScrollHeight, 'Tabs need no vertical scrolling').toBeLessThanOrEqual(geometry.tabClientHeight)
    }
  }
})

test('fills the viewport with independently scrolling panes and preserves comments and finding filters', async ({ page }, testInfo) => {
  test.skip(testInfo.project.name !== 'mock', 'Uses the mock authentication runtime.')
  await page.setViewportSize({ width: 1920, height: 1080 })
  const errors: string[] = []
  page.on('pageerror', error => errors.push(error.message))
  await openWorkspace(page)
  const browser = page.getByTestId('pr-panel-browser')
  await expect(browser.getByTestId('retained-file-item')).toHaveCount(451)
  await browser.getByLabel('Search files').fill('middleware')
  await browser.locator('[data-file-path="' + filePath + '"]').click()
  await expect(browser.getByTestId('retained-finding-badges')).toContainText('Security')
  await expect(browser.getByTestId('retained-finding-badges')).toContainText('Dismissed')
  await expect(browser.getByTitle('Recorded rejection reason: Redundant')).toHaveText('Duplicate')
  await expect(browser.getByTestId('inline-comment-human')).toContainText('Already tracked')
  await browser.getByTestId('browser-clear-filters').click()
  await expect(browser.getByLabel('Search files')).toHaveValue('')
  await expect(browser.getByTestId('retained-file-item')).toHaveCount(451)

  await page.getByTestId('installation-notices-trigger').click()
  await expect(page.getByTestId('usage-statistics-notice')).toBeVisible()
  await page.keyboard.press('Escape')
  await expect(page.getByTestId('usage-statistics-notice')).toBeHidden()

  await page.getByTestId('license-expiry-trigger').click()
  await expect(page.getByTestId('license-expiry-notice')).toBeVisible()
  await page.keyboard.press('Escape')
  await expect(page.getByTestId('license-expiry-notice')).toBeHidden()

  await expect(browser.getByTestId('browser-file-scroll')).toBeVisible()
  await expect(browser.getByTestId('browser-diff-scroll')).toBeVisible()
  await expect(page.locator('.header-row')).toBeVisible()
  await expect(page.locator('.pr-tabs')).toBeVisible()
  const geometry = await page.evaluate(() => {
    const files = document.querySelector<HTMLElement>('[data-testid="browser-file-scroll"]')!
    const diff = document.querySelector<HTMLElement>('[data-testid="browser-diff-scroll"]')!
    const toolbar = document.querySelector('.header-row')!.getBoundingClientRect()
    const tabs = document.querySelector('.pr-tabs')!.getBoundingClientRect()
    return { documentHeight: document.documentElement.scrollHeight, height: innerHeight, documentWidth: document.documentElement.scrollWidth, width: innerWidth, fileBottom: files.getBoundingClientRect().bottom,
      diffBottom: diff.getBoundingClientRect().bottom, toolbarTop: toolbar.top, tabsTop: tabs.top, fileScroll: files.scrollHeight > files.clientHeight, diffScroll: diff.scrollHeight > diff.clientHeight }
  })
  expect(geometry.documentHeight).toBeLessThanOrEqual(geometry.height + 1)
  expect(geometry.documentWidth).toBeLessThanOrEqual(geometry.width + 1)
  expect(Math.abs(geometry.fileBottom - geometry.diffBottom)).toBeLessThan(2)
  expect(geometry.height - geometry.diffBottom).toBeLessThan(24)
  expect(Math.abs(geometry.toolbarTop - geometry.tabsTop)).toBeLessThan(12)
  expect(geometry.toolbarTop).toBeLessThan(160)
  expect(geometry.fileScroll).toBe(true)
  expect(geometry.diffScroll).toBe(true)

  const filePane = browser.getByTestId('browser-file-scroll')
  const diffPane = browser.getByTestId('browser-diff-scroll')
  const documentPosition = () => page.evaluate(() => ({ x: scrollX, y: scrollY }))
  const initialDocumentPosition = await documentPosition()
  await filePane.hover()
  await page.mouse.wheel(0, 300)
  await expect.poll(() => filePane.evaluate(element => element.scrollTop)).toBe(300)
  expect(await diffPane.evaluate(element => element.scrollTop)).toBe(0)
  expect(await documentPosition()).toEqual(initialDocumentPosition)
  const fileTop = await filePane.evaluate(element => element.scrollTop)
  await diffPane.hover()
  await page.mouse.wheel(0, 300)
  await expect.poll(() => diffPane.evaluate(element => element.scrollTop)).toBe(300)
  expect(await filePane.evaluate(element => element.scrollTop)).toBe(fileTop)
  expect(await documentPosition()).toEqual(initialDocumentPosition)
  const diffTop = await diffPane.evaluate(element => element.scrollTop)
  await filePane.hover()
  await page.mouse.wheel(0, -(await filePane.evaluate(element => element.scrollHeight)))
  await expect.poll(() => filePane.evaluate(element => element.scrollTop)).toBe(0)
  expect(await diffPane.evaluate(element => element.scrollTop)).toBe(diffTop)
  expect(await documentPosition()).toEqual(initialDocumentPosition)
  await diffPane.hover()
  await page.mouse.wheel(0, -(await diffPane.evaluate(element => element.scrollHeight)))
  await expect.poll(() => diffPane.evaluate(element => element.scrollTop)).toBe(0)
  expect(await filePane.evaluate(element => element.scrollTop)).toBe(0)
  expect(await documentPosition()).toEqual(initialDocumentPosition)

  await browser.getByTestId('diff-mode-side-by-side').click()
  await expect(browser.getByTestId('diff-viewer').getByTestId('inline-thread')).toBeVisible()
  await expect(browser.getByTestId('diff-viewer').getByTestId('inline-comment-human')).toBeVisible()
  await browser.getByLabel('Filter finding kinds').selectOption('security')
  await browser.getByLabel('Filter finding outcomes').selectOption('duplicate')
  await expect(browser.getByTestId('retained-file-item')).toHaveCount(1)
  await expect(browser.getByTestId('diff-file-path')).toContainText(filePath)
  await browser.getByTestId('browser-clear-filters').click()
  await expect(browser.getByLabel('Filter finding kinds')).toHaveValue('')
  await expect(browser.getByLabel('Filter finding outcomes')).toHaveValue('')
  await expect(browser.getByTestId('retained-file-item')).toHaveCount(451)
  await browser.getByTestId('diff-mode-line-by-line').click()
  await page.evaluate(() => document.fonts.ready)
  await page.screenshot({ path: 'test-results/pr-review-workspace-desktop.png' })
  expect(errors).toEqual([])
})

test('keeps file selection and the diff reachable on a narrow screen without document scrolling', async ({ page }, testInfo) => {
  test.skip(testInfo.project.name !== 'mock', 'Uses the mock authentication runtime.')
  await page.setViewportSize({ width: 390, height: 844 })
  await openWorkspace(page)
  const browser = page.getByTestId('pr-panel-browser')
  await browser.getByRole('button', { name: /Files \(/ }).click()
  await browser.getByLabel('Search files').fill('middleware')
  await browser.locator('[data-file-path="' + filePath + '"]').click()
  await expect(browser.getByTestId('diff-viewer')).toBeVisible()
  await expect(browser.getByTestId('inline-comment-human')).toBeVisible()
  const threadBounds = await browser.getByTestId('inline-thread').boundingBox()
  expect(threadBounds).not.toBeNull()
  expect(threadBounds!.x + threadBounds!.width).toBeLessThanOrEqual(390)
  await page.getByTestId('installation-notices-trigger').click()
  await expect(page.getByTestId('usage-statistics-notice')).toBeVisible()
  await page.keyboard.press('Escape')
  await expect(page.getByTestId('usage-statistics-notice')).toBeHidden()
  await page.getByTestId('license-expiry-trigger').click()
  await expect(page.getByTestId('license-expiry-notice')).toBeVisible()
  const noticeBounds = await page.getByTestId('license-expiry-notice').boundingBox()
  expect(noticeBounds).not.toBeNull()
  expect(noticeBounds!.x).toBeGreaterThanOrEqual(0)
  expect(noticeBounds!.x + noticeBounds!.width).toBeLessThanOrEqual(390)
  await page.keyboard.press('Escape')
  await expect(page.getByTestId('license-expiry-notice')).toBeHidden()
  expect(await page.evaluate(() => document.documentElement.scrollHeight)).toBeLessThanOrEqual(844)
  expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(390)
  await page.evaluate(() => document.fonts.ready)
  await page.screenshot({ path: 'test-results/pr-review-workspace-mobile.png' })
})

test('aligns Split rows across unequal changes and multiple threads after resizing the panes', async ({ page }, testInfo) => {
  test.skip(testInfo.project.name !== 'mock', 'Uses the mock authentication runtime.')
  await page.setViewportSize({ width: 1600, height: 1000 })
  const lines = [2, 4, 5, 8, 8, 32, 33]
  const expectedPairs = [[2, 2], [4, null], [5, null], [8, 8], [8, 8], [32, 32], [33, 33]]
  const mixedThreads = lines.map((line, index) => ({
    threadId: 'mixed-thread-' + index, filePath, line, status: 'Active', comments: [{
      commentId: 'mixed-comment-' + index, isAiAuthored: index % 2 === 0,
      authorIdentity: index % 2 === 0 ? 'ProPR' : 'Jane', originatingJobId: 'review-1',
      body: ('Check the retained comment alignment after resizing. '.repeat(index + 1))
        + '\n\n' + 'UnbrokenText'.repeat(30),
    }],
  }))
  const mixedDiff = ['--- a/' + filePath, '+++ b/' + filePath,
    '@@ -1,8 +1,9 @@', ' context1', '-old2', '-old3', '+new2', ' context4', '+new4', '+new5',
    ' context5', '-old6', ' context7', ' context8', '+new9',
    '@@ -30,4 +31,3 @@', ' context30', '-old31', '-old32', '+new32', ' context33'].join('\n')
  await openWorkspace(page, { threads: mixedThreads, findings: [], unifiedDiff: mixedDiff })
  const browser = page.getByTestId('pr-panel-browser')
  await browser.getByLabel('Search files').fill('middleware')
  await browser.locator('[data-file-path="' + filePath + '"]').click()
  await browser.getByTestId('diff-mode-side-by-side').click()
  await expect(browser.getByTestId('diff-viewer').getByTestId('inline-thread')).toHaveCount(lines.length)

  const geometry = () => browser.getByTestId('diff-viewer').evaluate(element => {
    const sides = element.querySelectorAll<HTMLElement>('.d2h-file-side-diff')
    const oldRows = [...sides[0]!.querySelectorAll('tr')]
    const newRows = [...sides[1]!.querySelectorAll('tr')]
    const widgets = [...element.querySelectorAll<HTMLElement>('[data-testid="inline-thread"]')]
    function anchorLine(row: Element | null | undefined): number | null {
      let previous = row?.previousElementSibling
      while (previous?.classList.contains('d2h-inline-thread-row')) previous = previous.previousElementSibling
      const number = previous?.querySelector('.d2h-code-side-linenumber')?.textContent?.trim()
      return number ? Number(number) : null
    }
    return {
      oldCount: oldRows.length, newCount: newRows.length,
      alignmentDelta: Math.max(...newRows.map((row, index) => Math.abs(row.getBoundingClientRect().top - (oldRows[index]?.getBoundingClientRect().top ?? -10000)))),
      heightDelta: Math.max(...newRows.map((row, index) => Math.abs(row.getBoundingClientRect().height - (oldRows[index]?.getBoundingClientRect().height ?? -10000)))),
      widthOverflow: Math.max(...widgets.map(widget => widget.getBoundingClientRect().right - widget.closest('.d2h-file-side-diff')!.getBoundingClientRect().right)),
      spacerPairs: widgets.every(widget => oldRows[newRows.indexOf(widget.closest('tr')!)]?.classList.contains('d2h-inline-thread-spacer')),
      anchorPairs: widgets.map(widget => {
        const row = widget.closest('tr')!
        return [anchorLine(row), anchorLine(oldRows[newRows.indexOf(row)])]
      }),
    }
  })

  for (const width of [1600, 1000]) {
    await page.setViewportSize({ width, height: 1000 })
    await expect.poll(async () => {
      const bounds = await geometry()
      return {
        rowCountsMatch: bounds.oldCount === bounds.newCount,
        rowTopsMatch: bounds.alignmentDelta < 1,
        rowHeightsMatch: bounds.heightDelta < 1,
        commentsFit: bounds.widthOverflow <= 1,
        spacerPairs: bounds.spacerPairs,
        anchorPairs: bounds.anchorPairs,
      }
    }).toEqual({
      rowCountsMatch: true, rowTopsMatch: true, rowHeightsMatch: true, commentsFit: true,
      spacerPairs: true, anchorPairs: expectedPairs,
    })
  }
})
