// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { expect, test, type Page } from '@playwright/test'

// The retained-archive endpoints resolve the owning connection server-side, so reads are keyed on
// repository + pull request alone. The mock serves data for repository `backend-service`, PR #42.
const SCOPE_PATH = 'https://dev.azure.com/acme'
const ARCHIVE_REPOSITORY_ID = 'backend-service'
const ARCHIVE_PR_ID = '42'

// The mock retains provenance on the file-anchored AI comment: this is the review run that
// produced it, surfaced as the "View trace" link's target (`/jobs/{id}/protocol?clientId=1`).
const TRACE_JOB_ID = '11111111-2222-3333-4444-555555555555'
const EXPECTED_TRACE_HREF = `/jobs/${TRACE_JOB_ID}/protocol?clientId=1`

async function login(page: Page): Promise<void> {
  await page.goto('/login')
  await page.getByLabel('Username').fill('admin')
  await page.getByLabel('Password').fill('admin')
  await page.locator('form.login-form button[type="submit"]').click()
  await page.waitForURL('**/clients')
}

function prReviewUrl(overrides: Record<string, string> = {}): string {
  const query = new URLSearchParams({
    clientId: '1',
    providerScopePath: SCOPE_PATH,
    providerProjectKey: 'proj-x',
    repositoryId: ARCHIVE_REPOSITORY_ID,
    pullRequestId: ARCHIVE_PR_ID,
    ...overrides,
  })
  return `/pr-review?${query.toString()}`
}

test('renders retained threads, files, badges, and the selected file diff across the tabs', async ({ page }) => {
  const pageErrors: string[] = []
  page.on('pageerror', (error) => pageErrors.push(error.message))

  await login(page)
  await page.goto(prReviewUrl())

  await expect(page.getByRole('heading', { name: 'PR Review View' })).toBeVisible()

  // Stats is the default tab.
  await expect(page.getByTestId('pr-tab-stats')).toHaveClass(/tab-active/)
  await expect(page.getByText('Review Jobs')).toBeVisible()

  // --- Conversation tab: PR-level discussion + file-anchored threads shown together. ---
  await page.getByTestId('pr-tab-conversation').click()

  // Assertions are scoped to the active panel: both retained panels stay mounted (v-show), so
  // querying the page globally would match the inactive panel's elements too.
  const conversation = page.getByTestId('pr-panel-conversation')
  await expect(conversation.getByTestId('retained-archive-section')).toBeVisible()

  // Data loaded: no empty or error notice shows.
  await expect(conversation.getByTestId('retained-empty')).toHaveCount(0)
  await expect(conversation.getByTestId('retained-error')).toHaveCount(0)

  // The pull-request-level discussion is part of the conversation.
  await expect(conversation.getByText('Can we double-check the token refresh path before merging?')).toBeVisible()

  // The file-anchored AI comment (distinct AI marker) and the human comment and status are shown.
  const aiComment = conversation.getByTestId('retained-comment-ai').first()
  await expect(aiComment).toBeVisible()
  await expect(aiComment).toContainText('This handler swallows the rejected token error')
  await expect(aiComment.getByText('AI', { exact: true })).toBeVisible()
  await expect(conversation.getByTestId('retained-comment-human').first()).toBeVisible()
  await expect(conversation.getByTestId('retained-thread-status').first()).toBeVisible()

  // The AI comment carries provenance, so it links to its originating review run's trace.
  const aiTraceLink = aiComment.getByTestId('comment-trace-link')
  await expect(aiTraceLink).toBeVisible()
  await expect(aiTraceLink).toHaveAttribute('href', EXPECTED_TRACE_HREF)

  // The PR-level human comment recorded no origin, so it shows no trace link.
  const humanComment = conversation.getByTestId('retained-comment-human').first()
  await expect(humanComment.getByTestId('comment-trace-link')).toHaveCount(0)

  // --- Browser tab: file list + change-type/comment badges + diff-on-select. ---
  await page.getByTestId('pr-tab-browser').click()

  const browser = page.getByTestId('pr-panel-browser')
  await expect(browser.getByTestId('retained-archive-section')).toBeVisible()

  const fileItems = browser.getByTestId('retained-file-item')
  await expect(fileItems).toHaveCount(2)
  await expect(browser.locator('[data-file-path="src/auth/middleware.ts"]')).toBeVisible()
  await expect(browser.locator('[data-file-path="src/auth/tokens.ts"]')).toBeVisible()
  await expect(browser.getByTestId('retained-file-change-type').first()).toBeVisible()
  await expect(browser.getByTestId('retained-file-comment-badge')).toBeVisible()

  // Before selecting, the detail pane prompts for a file selection.
  await expect(browser.getByTestId('retained-no-selection')).toBeVisible()

  // Selecting a file loads its diff.
  await browser.locator('[data-file-path="src/auth/middleware.ts"]').click()

  await expect(browser.getByTestId('diff-viewer')).toBeVisible()
  await expect(browser.getByTestId('diff-file-path')).toContainText('src/auth/middleware.ts')
  await expect(browser.getByTestId('diff-viewer')).toContainText('UnauthorizedError')

  // The file-anchored thread (line 42) renders INLINE, inside the diff, at its line.
  const inlineThread = browser.getByTestId('diff-viewer').getByTestId('inline-thread')
  await expect(inlineThread).toBeVisible()
  await expect(inlineThread).toHaveAttribute('data-line', '42')
  await expect(inlineThread).toContainText('This handler swallows the rejected token error')
  await expect(inlineThread.getByTestId('inline-comment-ai')).toBeVisible()

  // The inline AI comment surfaces the same "View trace" link to its originating run.
  const inlineTraceLink = inlineThread.getByTestId('inline-comment-ai').getByTestId('comment-trace-link')
  await expect(inlineTraceLink).toBeVisible()
  await expect(inlineTraceLink).toHaveAttribute('href', EXPECTED_TRACE_HREF)

  // The inline human reply has no recorded origin, so it shows no trace link.
  await expect(inlineThread.getByTestId('inline-comment-human').getByTestId('comment-trace-link')).toHaveCount(0)

  // Every thread for this file anchored, so the below-diff unanchored fallback is absent.
  await expect(browser.getByTestId('retained-thread-panel')).toHaveCount(0)

  expect(pageErrors).toEqual([])
})

test('shows the empty notice within a retained tab when no data is retained for the pull request', async ({ page }) => {
  const pageErrors: string[] = []
  page.on('pageerror', (error) => pageErrors.push(error.message))

  await login(page)

  // A repository + pull request with nothing retained: the read endpoints return empty arrays
  // (and 404 for diffs), so the tab degrades to a calm empty notice.
  await page.goto(prReviewUrl({ repositoryId: 'unknown-repo' }))

  await expect(page.getByRole('heading', { name: 'PR Review View' })).toBeVisible()

  // The degrade is visible within the Conversation tab.
  await page.getByTestId('pr-tab-conversation').click()

  const conversation = page.getByTestId('pr-panel-conversation')
  await expect(conversation.getByTestId('retained-archive-section')).toBeVisible()
  await expect(conversation.getByTestId('retained-empty')).toBeVisible()

  // No error state, no file list, no diff: a clean degrade, not a failure.
  await expect(conversation.getByTestId('retained-error')).toHaveCount(0)
  await expect(conversation.getByTestId('retained-file-item')).toHaveCount(0)
  await expect(conversation.getByTestId('diff-viewer')).toHaveCount(0)

  // The Browser tab degrades the same way.
  await page.getByTestId('pr-tab-browser').click()
  const browser = page.getByTestId('pr-panel-browser')
  await expect(browser.getByTestId('retained-empty')).toBeVisible()

  expect(pageErrors).toEqual([])
})
