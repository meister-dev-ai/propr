// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { describe, expect, it } from 'vitest'
import { filterBrowserFiles, findingCountForFile, findingForComment, findingOutcome } from '@/features/reviews/components/reviewBrowserFindings'
import type { CodeInsightFinding } from '@/services/codeInsightsAnalyticsService'
import type { RetainedThread } from '@/features/reviews/composables/useRetainedPrData'

const files = [{ filePath: 'src/Auth.cs' }, { filePath: 'src/Cache.cs' }, { filePath: 'README.md' }]
const finding = (overrides: Partial<CodeInsightFinding> = {}): CodeInsightFinding => ({
  id: 'f1', clientId: 'c1', repositoryId: 'r1', pullRequestId: 42, jobId: 'job-1',
  filePath: '/src/Auth.cs', lineNumber: 7, severity: 'Warning', message: 'Validate the token.',
  coreTags: ['security'], disposition: 'Dismissed', rejectionReason: 'Redundant',
  providerThreadId: 'thread-1', observedAt: '2026-01-01T00:00:00Z', ...overrides,
})
const original = { commentId: 'comment-1', isAiAuthored: true, originatingJobId: 'job-1', body: '**Warning** Validate the token.' }
const human = { commentId: 'comment-2', isAiAuthored: false, body: 'This duplicates another comment.' }
const reply = { commentId: 'comment-3', isAiAuthored: true, originatingJobId: 'reply-job', body: 'Thanks for confirming.' }
const thread: RetainedThread = { threadId: 'thread-1', filePath: 'src/Auth.cs', line: 7, comments: [original, human, reply] }
const filters = { query: '', presence: 'all', kind: '', severity: '', outcome: '' }

describe('review browser findings', () => {
  it('joins finding identity to the original AI comment without classifying replies', () => {
    const metadata = finding()
    expect(findingForComment(thread, original, [metadata])).toBe(metadata)
    expect(findingForComment(thread, human, [metadata])).toBeNull()
    expect(findingForComment(thread, reply, [metadata])).toBeNull()
  })

  it('does not classify AI replies even when they repeat the finding text', () => {
    const repeatedReply = { ...reply, originatingJobId: 'job-1', body: 'Validate the token.' }
    const retained = { ...thread, comments: [original, human, repeatedReply] }
    expect(findingForComment(retained, repeatedReply, [finding()])).toBeNull()
    const humanThread = { ...thread, comments: [human, repeatedReply] }
    expect(findingForComment(humanThread, repeatedReply, [finding()])).toBeNull()
  })

  it('uses an unambiguous exact text match when provider identity was not recorded', () => {
    const comment = { ...original, originatingJobId: null, body: 'Validate the token.' }
    const retained = { ...thread, threadId: null, comments: [comment] }
    expect(findingForComment(retained, comment, [finding({ providerThreadId: null })])?.id).toBe('f1')
    expect(findingForComment(retained, comment, [finding(), finding({ id: 'f2' })])).toBeNull()
    expect(findingForComment(retained, { ...comment, body: 'Validate a different token.' }, [finding()])).toBeNull()
  })

  it('does not use text to disambiguate conflicting recorded thread identities', () => {
    const comment = { ...original, body: 'Validate the token.' }
    const retained = { ...thread, comments: [comment] }
    expect(findingForComment(retained, comment, [
      finding(), finding({ id: 'f2', message: 'Check a different token.' }),
    ])).toBeNull()
  })

  it('does not associate an ambiguous thread with another thread by matching text', () => {
    const comment = { ...original, body: 'Validate another token.' }
    const retained = { ...thread, comments: [comment] }
    expect(findingForComment(retained, comment, [
      finding(), finding({ id: 'f2' }),
      finding({ id: 'f3', providerThreadId: 'thread-3', message: comment.body }),
    ])).toBeNull()
  })

  it('does not attach another recorded thread finding by matching its message', () => {
    const comment = { ...original, body: 'Validate the token.' }
    const retained = { ...thread, comments: [comment] }
    const otherFinding = finding({ providerThreadId: 'thread-2' })
    expect(findingForComment(retained, comment, [otherFinding])).toBeNull()
    expect(findingCountForFile('src/Auth.cs', [retained], [otherFinding])).toBe(2)
  })

  it('uses text for legacy metadata without a recorded provider thread identity', () => {
    const comment = { ...original, body: 'Validate the token.' }
    const retained = { ...thread, comments: [comment] }
    const metadata = finding({ providerThreadId: null })
    expect(findingForComment(retained, comment, [metadata])).toBe(metadata)
  })

  it('recognizes a separately materialized root comment by its stable identity', () => {
    expect(findingForComment(thread, { ...original }, [finding()])?.id).toBe('f1')
    expect(findingForComment(thread, { ...reply }, [finding()])).toBeNull()
  })

  it('counts ambiguous recorded findings without counting their thread again', () => {
    expect(findingCountForFile('src/Auth.cs', [thread], [finding(), finding({ id: 'f2' })])).toBe(2)
  })

  it('counts each unclassified retained finding thread once', () => {
    expect(findingCountForFile('src/Auth.cs', [thread, { ...thread }], [])).toBe(1)
    const withoutThreadId = { ...thread, threadId: null }
    expect(findingCountForFile('src/Auth.cs', [withoutThreadId, { ...withoutThreadId }], [])).toBe(1)
  })

  it('keeps distinct retained findings even when their root comments have the same text', () => {
    const otherLine = { ...thread, threadId: 'thread-2', line: 12 }
    expect(findingCountForFile('src/Auth.cs', [thread, otherLine], [])).toBe(2)
  })

  it('deduplicates identical unidentified snapshots without merging different locations or messages', () => {
    const unidentified = { ...thread, threadId: null, comments: [{ ...original, commentId: null }] }
    const otherLine = { ...unidentified, line: 12 }
    const otherMessage = { ...unidentified, comments: [{ ...unidentified.comments[0]!, body: 'Check the expiry.' }] }
    expect(findingCountForFile('src/Auth.cs', [unidentified, { ...unidentified }], [])).toBe(1)
    expect(findingCountForFile('src/Auth.cs', [unidentified, otherLine, otherMessage], [])).toBe(3)
  })

  it('requires recorded metadata for finding kinds, severities and outcomes', () => {
    const unclassified = { ...thread, threadId: 'thread-2', filePath: 'src/Cache.cs' }
    expect(filterBrowserFiles(files, [thread, unclassified], [finding()], { ...filters, presence: 'findings' })).toEqual(files.slice(0, 2))
    for (const detail of [{ kind: 'security' }, { severity: 'warning' }, { outcome: 'dismissed' }]) {
      expect(filterBrowserFiles(files, [thread, unclassified], [finding()], { ...filters, ...detail })).toEqual([files[0]])
    }
  })

  it('requires recorded evidence for a duplicate outcome', () => {
    expect(findingOutcome(finding())).toBe('duplicate')
    expect(findingOutcome(finding({ rejectionReason: null }))).toBe('dismissed')
    expect(findingOutcome(finding({ disposition: null, rejectionReason: null }))).toBe('pending')
  })

  it('filters recorded dispositions and duplicate reasons independently', () => {
    for (const outcome of ['dismissed', 'duplicate']) {
      expect(filterBrowserFiles(files, [thread], [finding()], { ...filters, outcome })).toEqual([files[0]])
    }
    expect(filterBrowserFiles(files, [thread], [finding({ rejectionReason: null })], { ...filters, outcome: 'duplicate' })).toEqual([])
  })

  it('searches names and paths case insensitively', () => {
    expect(filterBrowserFiles(files, [thread], [finding()], { ...filters, query: ' AUTH ' }).map(f => f.filePath)).toEqual(['src/Auth.cs'])
  })

  it('filters finding presence using retained AI threads when classifications are unavailable', () => {
    expect(filterBrowserFiles(files, [thread], [], { ...filters, presence: 'findings' }).map(f => f.filePath)).toEqual(['src/Auth.cs'])
    expect(filterBrowserFiles(files, [thread], [], { ...filters, presence: 'withoutFindings' }).map(f => f.filePath)).toEqual(['src/Cache.cs', 'README.md'])
  })

  it('combines kind, severity and outcome on the same finding', () => {
    const metadata = [finding(), finding({ id: 'f2', severity: 'Error', coreTags: ['concurrency'], disposition: 'Addressed', rejectionReason: null })]
    expect(filterBrowserFiles(files, [thread], metadata, { ...filters, kind: 'security', severity: 'error' })).toEqual([])
    expect(filterBrowserFiles(files, [thread], metadata, { ...filters, kind: 'security', severity: 'warning', outcome: 'duplicate' })).toEqual([files[0]])
  })

  it('includes human-only threads when filtering files with comments', () => {
    const humanThread = { filePath: '/src/Cache.cs', comments: [human] }
    expect(filterBrowserFiles(files, [humanThread], [], { ...filters, presence: 'comments' })).toEqual([files[1]])
  })

  it('does not count an AI answer to a human thread as a finding', () => {
    const humanThread = { filePath: 'src/Cache.cs', comments: [human, reply] }
    expect(filterBrowserFiles(files, [humanThread], [], { ...filters, presence: 'findings' })).toEqual([])
  })
})
