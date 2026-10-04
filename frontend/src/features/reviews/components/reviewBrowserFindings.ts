// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import type { CodeInsightFinding } from '@/services/codeInsightsAnalyticsService'
import type { RetainedComment, RetainedFile, RetainedThread } from '@/features/reviews/composables/useRetainedPrData'

export interface BrowserFileFilters {
    query: string
    presence: string
    kind: string
    severity: string
    outcome: string
}

export function normalizeFilePath(path: string | null | undefined): string {
    return (path ?? '').replaceAll('\\', '/').replace(/^\/+/, '')
}

export function findingOutcome(finding: CodeInsightFinding): string {
    if (finding.rejectionReason?.toLowerCase() === 'redundant') return 'duplicate'
    return finding.disposition?.toLowerCase() ?? 'pending'
}

export function findingLabel(value: string): string {
    const words = value.replace(/([a-z])([A-Z])/g, '$1 $2').replaceAll('-', ' ').toLowerCase()
    return words.charAt(0).toUpperCase() + words.slice(1)
}

function matchingFindings(
    thread: RetainedThread,
    comment: RetainedComment,
    findings: CodeInsightFinding[],
): CodeInsightFinding[] {
    const root = thread.comments?.[0]
    if (!root?.isAiAuthored || !comment.isAiAuthored) return []
    if (root !== comment && (!root.commentId || root.commentId !== comment.commentId)) return []
    let candidates = findings.filter(finding =>
        normalizeFilePath(finding.filePath) === normalizeFilePath(thread.filePath)
        && (!root.originatingJobId || finding.jobId === root.originatingJobId),
    )
    if (thread.threadId) {
        const byThread = candidates.filter(finding => finding.providerThreadId === thread.threadId)
        if (byThread.length > 0) return byThread
        candidates = candidates.filter(finding => !finding.providerThreadId)
    }
    const text = (comment.body ?? '').trim().replace(/\s+/g, ' ')
    if (!text) return []
    return candidates.filter(finding => finding.message.trim().replace(/\s+/g, ' ') === text)
}

/** Match the original finding without assigning its metadata to human or AI replies. */
export function findingForComment(
    thread: RetainedThread,
    comment: RetainedComment,
    findings: CodeInsightFinding[],
): CodeInsightFinding | null {
    const matches = matchingFindings(thread, comment, findings)
    return matches.length === 1 ? matches[0]! : null
}

export function findingCountForFile(path: string, threads: RetainedThread[], findings: CodeInsightFinding[]): number {
    const normalized = normalizeFilePath(path)
    const own = findings.filter(finding => normalizeFilePath(finding.filePath) === normalized)
    const unmatched = new Set<string>()
    for (const thread of threads) {
        const root = thread.comments?.[0]
        if (normalizeFilePath(thread.filePath) !== normalized || !root?.isAiAuthored) continue
        if (matchingFindings(thread, root, own).length > 0) continue
        const identity = thread.threadId ? `thread:${thread.threadId}`
            : root.commentId ? `comment:${root.commentId}`
                : JSON.stringify([root.originatingJobId ?? null, thread.line ?? null, (root.body ?? '').trim().replace(/\s+/g, ' ')])
        unmatched.add(identity)
    }
    return new Set(own.map(finding => finding.id)).size + unmatched.size
}

export function filterBrowserFiles(
    files: RetainedFile[],
    threads: RetainedThread[],
    findings: CodeInsightFinding[],
    filters: BrowserFileFilters,
): RetainedFile[] {
    const query = filters.query.trim().toLowerCase()
    return files.filter(file => {
        const path = normalizeFilePath(file.filePath)
        if (query && !path.toLowerCase().includes(query)) return false
        const ownFindings = findings.filter(finding => normalizeFilePath(finding.filePath) === path)
        const ownThreads = threads.filter(thread => normalizeFilePath(thread.filePath) === path)
        const hasFindings = ownFindings.length > 0 || ownThreads.some(thread => thread.comments?.[0]?.isAiAuthored)
        if (filters.presence === 'findings' && !hasFindings) return false
        if (filters.presence === 'withoutFindings' && hasFindings) return false
        if (filters.presence === 'comments' && !ownThreads.some(thread => thread.comments?.length)) return false
        if (!filters.kind && !filters.severity && !filters.outcome) return true
        return ownFindings.some(finding =>
            (!filters.kind || finding.coreTags.includes(filters.kind))
            && (!filters.severity || finding.severity.toLowerCase() === filters.severity)
            && (!filters.outcome || findingOutcome(finding) === filters.outcome
                || finding.disposition?.toLowerCase() === filters.outcome),
        )
    })
}
