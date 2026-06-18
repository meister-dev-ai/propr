// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import MarkdownIt from 'markdown-it'
import DOMPurify from 'dompurify'
import type {
    AgenticInvestigationOutputRecord,
    AgenticToolUsageRecord,
    CommentRelevanceAiTokenUsage,
    CommentRelevanceEventDetails,
    FinalGateEvidence,
    FinalGateProvenance,
    FinalGateSummaryRecord,
    ProtocolEventDto,
    ProtocolEventPhaseTimingDto,
    ProtocolFileOutcome,
    ProtocolFollowUp,
    ProtocolRepeatedJudgment,
    ReviewProtocolPass,
    ReviewStrategy,
    ToolPhaseGroup,
    VerificationEvidenceAttemptRecord,
    VerificationEvidenceItemRecord,
    VerificationEvidenceOutputRecord,
} from '../types'

const md = new MarkdownIt({
    html: false,
    linkify: true,
    breaks: true,
})

export function renderMarkdown(content: string | null | undefined): string {
    if (!content) return ''
    return DOMPurify.sanitize(md.render(content))
}

// === Generic helpers ===

export function isPlainObject(value: unknown): value is Record<string, unknown> {
    return typeof value === 'object' && value !== null && !Array.isArray(value)
}

// === Event-name classification ===

const commentRelevanceEventNames = new Set([
    'comment_relevance_filter_output',
    'comment_relevance_filter_degraded',
    'comment_relevance_evaluator_degraded',
    'comment_relevance_filter_selection_fallback',
])

const finalGateEventNames = new Set([
    'review_finding_gate_summary',
    'review_finding_gate_decision',
])

const verificationEventNames = new Set([
    'verification_claims_extracted',
    'verification_local_decision',
    'verification_evidence_collected',
    'verification_pr_decision',
    'verification_degraded',
    'summary_reconciliation',
])

const agenticInvestigationEventNames = new Set([
    'agentic_file_investigation_result',
    'agentic_file_degraded',
    'agentic_file_evidence_collected',
])

export function isCommentRelevanceEvent(name: string | null | undefined): boolean {
    return !!name && commentRelevanceEventNames.has(name)
}

export function isFinalGateEvent(name: string | null | undefined): boolean {
    return !!name && finalGateEventNames.has(name)
}

export function isVerificationEvent(name: string | null | undefined): boolean {
    return !!name && verificationEventNames.has(name)
}

export function isAgenticInvestigationEvent(name: string | null | undefined): boolean {
    return !!name && agenticInvestigationEventNames.has(name)
}

export function isAgenticDegradedEvent(name: string | null | undefined): boolean {
    return name === 'agentic_file_degraded'
}

// === Text decoding ===

export function decodeHtmlEntities(value: string): string {
    if (!value.includes('&') || typeof document === 'undefined') {
        return value
    }

    const textarea = document.createElement('textarea')
    textarea.innerHTML = value
    return textarea.value
}

export function decodeMergedEventEscapes(value: string): string {
    return value
        .replace(/\\u([0-9a-fA-F]{4})/g, (_, hex: string) => String.fromCharCode(parseInt(hex, 16)))
        .replace(/\\x([0-9a-fA-F]{2})/g, (_, hex: string) => String.fromCharCode(parseInt(hex, 16)))
        .replace(/\\r\\n/g, '\n')
        .replace(/\\n/g, '\n')
        .replace(/\\r/g, '\r')
        .replace(/\\t/g, '\t')
        .replace(/\\f/g, '\f')
        .replace(/\\"/g, '"')
        .replace(/\\'/g, "'")
        .replace(/\\\//g, '/')
        .replace(/\\\\/g, '\\')
}

export function renderMergedEventText(value: string | null | undefined): string {
    if (!value) {
        return ''
    }

    const trimmed = value.trim()
    if (trimmed.startsWith('"') && trimmed.endsWith('"')) {
        try {
            const parsed = JSON.parse(trimmed)
            if (typeof parsed === 'string') {
                return decodeHtmlEntities(parsed)
            }
        } catch {
            // Fall through to escape decoding for non-JSON raw strings.
        }
    }

    return decodeHtmlEntities(decodeMergedEventEscapes(value))
}

// === Number / date / duration formatting ===

export function formatDate(iso: string | null | undefined): string {
    if (!iso) return '—'
    return new Date(iso).toLocaleString()
}

export function formatTokens(n: number | null | undefined): string {
    if (n == null) return '—'
    return n.toLocaleString()
}

export function formatCacheStatus(status: unknown): string {
    if (status == null || status === 'notApplicable') return 'not captured'
    return String(status).replace(/([a-z])([A-Z])/g, '$1 $2').toLowerCase()
}

export function shortGuid(value: string | null | undefined): string {
    if (!value) return '—'
    return value.slice(0, 8)
}

export function formatTemperature(value: number | null | undefined): string {
    if (value == null) return 'Default'
    return value.toFixed(2)
}

export function formatDurationMs(ms: number): string {
    if (ms < 0) return '0s'
    const seconds = Math.floor(ms / 1000)
    const minutes = Math.floor(seconds / 60)
    const hours = Math.floor(minutes / 60)
    if (hours > 0) return `${hours}h ${minutes % 60}m`
    if (minutes > 0) return `${minutes}m ${seconds % 60}s`
    return `${seconds}s`
}

export function formatDurationWithMs(ms: number | null | undefined): string {
    if (ms == null) return '—'
    if (ms < 1000) return `${ms} ms`
    return formatDurationMs(ms)
}

export function humanizeStatusValue(value: string): string {
    return value
        .replace(/_/g, ' ')
        .replace(/\b\w/g, character => character.toUpperCase())
}

export function formatTimingAvailability(value: string | null | undefined): string {
    if (!value) return 'Not recorded'
    switch (value.toLowerCase()) {
        case 'captured': return 'Captured'
        case 'partial': return 'Partial'
        case 'missing': return 'Missing'
        case 'not_applicable':
        case 'notapplicable':
            return 'Not applicable'
        default:
            return humanizeStatusValue(value)
    }
}

export function formatToolOutcome(value: string | null | undefined): string {
    if (!value) return 'Unknown'
    switch (value.toLowerCase()) {
        case 'succeeded': return 'Succeeded'
        case 'failed': return 'Failed'
        case 'degraded': return 'Degraded'
        case 'cancelled': return 'Cancelled'
        default:
            return humanizeStatusValue(value)
    }
}

export function computePassDuration(pass: ReviewProtocolPass): string {
    if (!pass.startedAt) return '—'
    const start = new Date(pass.startedAt).getTime()
    const end = pass.completedAt ? new Date(pass.completedAt).getTime() : Date.now()
    return formatDurationMs(end - start)
}

// === Status / badge class helpers ===

export function statusIconClass(status: string | undefined | null): string {
    switch (status?.toLowerCase()) {
        case 'completed':
        case 'success':
            return 'icon-success'
        case 'processing':
            return 'icon-processing'
        case 'failed':
            return 'icon-failed'
        default:
            return 'icon-pending'
    }
}

export function statusBadgeClass(status: string | undefined | null): string {
    switch (status?.toLowerCase()) {
        case 'completed':
        case 'success':
            return 'status-badge status-completed'
        case 'processing':
            return 'status-badge status-processing'
        case 'failed':
            return 'status-badge status-failed'
        default:
            return 'status-badge status-pending'
    }
}

export function kindBadgeClass(kind: string | null | undefined): string {
    if (kind === 'aiCall') return 'kind-badge--suggestion'
    if (kind === 'toolCall') return 'kind-badge--accent'
    if (kind === 'memoryOperation') return 'kind-badge--success'
    if (kind === 'operational') return 'kind-badge--muted'
    return 'kind-badge--muted'
}

export function severityVariant(severity: string | null | undefined): string {
    switch ((severity ?? '').toLowerCase()) {
        case 'error':
        case 'warning':
        case 'info':
        case 'suggestion':
            return severity!.toLowerCase()
        default:
            return 'note'
    }
}

// === Strategy / outcome formatting ===

export function formatReviewStrategy(strategy: ReviewStrategy | null | undefined): string {
    switch (strategy) {
        case 'fileByFile':
            return 'File-by-File'
        case 'agenticFileByFile':
            return 'Agentic File-by-File'
        case 'prWideAgentic':
            return 'PR-wide Agentic'
        default:
            return strategy ?? 'Not recorded'
    }
}

export function formatFileOutcomeStatus(fileOutcome: ProtocolFileOutcome | null | undefined): string {
    if (!fileOutcome) return 'Not recorded'
    if (fileOutcome.isFailed) return 'Failed'
    if (fileOutcome.isExcluded) return 'Excluded'
    if (fileOutcome.isCarriedForward) return 'Carried Forward'
    if (fileOutcome.isDegraded) return 'Degraded'
    if (fileOutcome.isComplete) return 'Completed'
    return 'In Progress'
}

export function formatFollowUpStatus(followUp: ProtocolFollowUp | null | undefined): string {
    if (!followUp?.used) return 'Not used'
    if (followUp.completedSuccessfully) return 'Completed successfully'
    return 'Used'
}

export function formatRepeatedJudgmentStatus(repeatedJudgment: ProtocolRepeatedJudgment | null | undefined): string {
    if (!repeatedJudgment) return 'Not used'
    if (repeatedJudgment.agreementState === 'Agreed') return 'Agreement reached'
    if (repeatedJudgment.agreementState === 'Disagreed') return 'Disagreed'
    return repeatedJudgment.agreementState ?? 'Recorded'
}

// === Phase timing ===

export function getPhaseTimings(event: ProtocolEventDto | null | undefined): ProtocolEventPhaseTimingDto[] {
    const raw = event?.phaseTimings
    if (Array.isArray(raw)) {
        return raw.filter((phase): phase is ProtocolEventPhaseTimingDto => !!phase && typeof phase === 'object')
    }

    return []
}

export function getPhaseTimingDurationMs(phase: ProtocolEventPhaseTimingDto): number | null {
    if (phase.durationMs != null) {
        return phase.durationMs
    }

    if (phase.startedAt && phase.completedAt) {
        const duration = new Date(phase.completedAt).getTime() - new Date(phase.startedAt).getTime()
        if (!Number.isNaN(duration) && duration >= 0) {
            return duration
        }
    }

    return null
}

export function formatPhaseCountSummary(phaseCount: number, groupCount: number, compact = false): string {
    if (phaseCount <= 0) {
        return 'No phases'
    }

    if (phaseCount === groupCount) {
        return `${phaseCount} phase${phaseCount === 1 ? '' : 's'}`
    }

    if (compact) {
        return `${groupCount} group${groupCount === 1 ? '' : 's'} / ${phaseCount} occurrence${phaseCount === 1 ? '' : 's'}`
    }

    return `${phaseCount} phase${phaseCount === 1 ? '' : 's'} across ${groupCount} group${groupCount === 1 ? '' : 's'}`
}

export function formatPhaseTitle(phase: ProtocolEventPhaseTimingDto): string {
    const baseName = phase.displayName ?? phase.name ?? 'Unnamed phase'
    return phase.occurrence != null ? `${baseName} #${phase.occurrence}` : baseName
}

export function formatPhaseDuration(phase: ProtocolEventPhaseTimingDto): string {
    const duration = getPhaseTimingDurationMs(phase)
    return duration == null ? 'Duration unavailable' : formatDurationWithMs(duration)
}

export function formatToolPhaseGroupDuration(group: ToolPhaseGroup): string {
    if (group.totalDurationMs == null) {
        return group.count === 1 ? 'Duration unavailable' : `${group.count} occurrences`
    }

    return group.count === 1
        ? formatDurationWithMs(group.totalDurationMs)
        : `${formatDurationWithMs(group.totalDurationMs)} total`
}

export function slowestToolDurationLabel(protocol: ReviewProtocolPass): string | null {
    const slowestDuration = (protocol.events ?? [])
        .filter(event => (event.kind ?? '').toLowerCase() === 'toolcall' && event.durationMs != null)
        .reduce<number | null>((current, event) => {
            if (event.durationMs == null) {
                return current
            }

            if (current == null || event.durationMs > current) {
                return event.durationMs
            }

            return current
        }, null)

    return slowestDuration == null ? null : formatDurationWithMs(slowestDuration)
}

// === Event flag predicates ===

export function hasToolTiming(event: ProtocolEventDto | null | undefined): boolean {
    return event?.durationMs != null
        || event?.waitDurationMs != null
        || event?.activeDurationMs != null
        || !!event?.startedAt
        || !!event?.completedAt
        || !!event?.timingAvailability
        || !!event?.toolOutcome
        || !!event?.phaseTimings?.length
}

export function hasEventTokens(event: ProtocolEventDto | null | undefined): boolean {
    return event?.inputTokens != null
        || event?.outputTokens != null
        || event?.cachedInputTokens != null
}

export function hasEventError(event: ProtocolEventDto | null | undefined): boolean {
    return !!event?.error
        || !!event?.finalizationAttemptKind
        || !!event?.finalizationOutcome
        || !!event?.finalizationReason
        || !!event?.toolEvidence
}

// === List / count formatting ===

export function formatStringList(values: string[] | null | undefined): string {
    return values?.length ? values.join('\n') : 'None'
}

export function hasEntries(value: Record<string, number> | null | undefined): boolean {
    return !!value && Object.keys(value).length > 0
}

export function formatNamedCounts(value: Record<string, number> | null | undefined): string {
    if (!hasEntries(value)) {
        return 'None'
    }

    return Object.entries(value!)
        .sort(([left], [right]) => left.localeCompare(right))
        .map(([key, count]) => `${key}: ${count}`)
        .join('\n')
}

export function commentLocation(filePath: string | undefined, lineNumber: number | null | undefined): string {
    if (!filePath) {
        return lineNumber ? `L${lineNumber}` : 'Unknown location'
    }

    return lineNumber ? `${filePath}:L${lineNumber}` : filePath
}

// === Comment-relevance formatting ===

export function formatCommentRelevanceImplementation(details: CommentRelevanceEventDetails): string {
    if (details.implementationId && details.implementationVersion) {
        return `${details.implementationId} @ ${details.implementationVersion}`
    }

    return details.implementationId ?? details.implementationVersion ?? 'Unknown'
}

export function formatCommentRelevanceCounts(details: CommentRelevanceEventDetails): string {
    return `${details.originalCommentCount ?? 0} original -> ${details.keptCount ?? 0} kept / ${details.discardedCount ?? 0} discarded`
}

export function formatCommentRelevanceAiUsage(usage: CommentRelevanceAiTokenUsage): string {
    const lines = [
        `Input tokens: ${formatTokens(usage.inputTokens)}`,
        `Output tokens: ${formatTokens(usage.outputTokens)}`,
    ]

    if (usage.modelId) {
        lines.push(`Model: ${usage.modelId}`)
    }

    if (usage.filePath) {
        lines.push(`File: ${usage.filePath}`)
    }

    return lines.join('\n')
}

// === Final-gate formatting ===

export function formatFinalGateCounts(details: FinalGateSummaryRecord): string {
    return `${details.candidateCount ?? 0} candidates -> ${details.publishCount ?? 0} publish / ${details.summaryOnlyCount ?? 0} summary-only / ${details.dropCount ?? 0} drop`
}

export function formatFinalGateProvenance(provenance: FinalGateProvenance | null | undefined): string {
    if (!provenance) {
        return 'None'
    }

    const lines = [
        `Origin: ${provenance.originKind ?? 'Unknown'}`,
        `Stage: ${provenance.generatedByStage ?? 'Unknown'}`,
    ]

    if (provenance.sourceFilePath) {
        lines.push(`Source file: ${provenance.sourceFilePath}`)
    }

    if (provenance.sourceFileResultId) {
        lines.push(`Source file result: ${provenance.sourceFileResultId}`)
    }

    if (provenance.sourceCommentOrdinal != null) {
        lines.push(`Source comment ordinal: ${provenance.sourceCommentOrdinal}`)
    }

    return lines.join('\n')
}

export function formatFinalGateEvidence(evidence: FinalGateEvidence | null | undefined): string {
    if (!evidence) {
        return 'None'
    }

    const lines = [
        `Resolution: ${evidence.evidenceResolutionState ?? 'Unknown'}`,
        `Source: ${evidence.evidenceSource ?? 'Unknown'}`,
        `Supporting files: ${evidence.supportingFiles?.length ? evidence.supportingFiles.join(', ') : 'None'}`,
        `Supporting finding IDs: ${evidence.supportingFindingIds?.length ? evidence.supportingFindingIds.join(', ') : 'None'}`,
    ]

    return lines.join('\n')
}

// === Verification formatting ===

export function formatVerificationProCursorStatus(output: VerificationEvidenceOutputRecord): string {
    if (!output.hasProCursorAttempt) {
        return 'Not attempted'
    }

    return output.proCursorResultStatus ?? 'Unknown'
}

export function formatEvidenceAttempts(attempts: VerificationEvidenceAttemptRecord[] | null | undefined): string {
    if (!attempts?.length) {
        return 'None'
    }

    return attempts
        .map(attempt => {
            const lines = [
                `${attempt.attemptOrder ?? '?'}: ${attempt.sourceFamily ?? 'Unknown'} -> ${attempt.status ?? 'Unknown'}`,
                `Impact: ${attempt.coverageImpact ?? 'Unknown'}`,
                `Scope: ${attempt.scopeSummary ?? 'Unknown'}`,
            ]

            if (attempt.failureReason) {
                lines.push(`Failure: ${attempt.failureReason}`)
            }

            return lines.join('\n')
        })
        .join('\n\n')
}

export function formatEvidenceItems(items: VerificationEvidenceItemRecord[] | null | undefined): string {
    if (!items?.length) {
        return 'None'
    }

    return items
        .map(item => {
            const lines = [
                `Kind: ${item.kind ?? 'Unknown'}`,
                `Summary: ${item.summary ?? 'Unknown'}`,
            ]

            if (item.sourceId) {
                lines.push(`Source: ${item.sourceId}`)
            }

            if (item.freshnessState) {
                lines.push(`Freshness: ${item.freshnessState}`)
            }

            return lines.join('\n')
        })
        .join('\n\n')
}

// === Agentic investigation formatting ===

export function normalizeAgenticToolUsage(output: AgenticInvestigationOutputRecord | null | undefined): AgenticToolUsageRecord[] {
    if (!output) {
        return []
    }

    return output.ToolUsage ?? output.toolUsage ?? []
}

export function describeAgenticToolStatus(status: string | null | undefined): string {
    switch ((status ?? '').toLowerCase()) {
        case 'success':
            return 'Runtime attempt succeeded.'
        case 'blocked_not_allowed':
            return 'Runtime blocked this attempt because the tool was not allowed for the investigation.'
        case 'blocked_budget_exhausted':
            return 'Runtime blocked this attempt because the investigation exhausted its tool budget.'
        case 'blocked_scope_violation':
            return 'Runtime blocked this attempt because the requested target was outside the approved file scope.'
        case 'failed':
            return 'Runtime attempted the lookup, but the repository/provider fetch failed.'
        default:
            return status || 'Unknown runtime status.'
    }
}

export function formatAgenticToolUsage(output: AgenticInvestigationOutputRecord | null | undefined): string {
    const usage = normalizeAgenticToolUsage(output)
    if (!usage.length) {
        return 'No runtime tool attempts were recorded.'
    }

    return usage.map(item => {
        const toolName = item.ToolName ?? item.toolName ?? 'unknown_tool'
        const status = item.Status ?? item.status ?? 'unknown'
        const target = item.Target ?? item.target
        const lines = [
            `${toolName} -> ${status}`,
            describeAgenticToolStatus(status),
        ]

        if (target) {
            lines.splice(1, 0, `Target: ${target}`)
        }

        return lines.join('\n')
    }).join('\n\n')
}

export function formatAgenticInvestigationStatus(output: AgenticInvestigationOutputRecord | null | undefined): string {
    const status = output?.Status ?? output?.status ?? 'unknown'
    const degraded = output?.Degraded ?? output?.degraded ?? false
    return degraded ? `${status} (non-validated degraded intermediate outcome)` : status
}

export function agenticInvestigationCandidateCount(output: AgenticInvestigationOutputRecord | null | undefined): number | null {
    return output?.candidateCount ?? output?.CandidateCount ?? null
}

export function agenticInvestigationEvidenceCount(output: AgenticInvestigationOutputRecord | null | undefined): number | null {
    return output?.evidenceCount ?? output?.EvidenceCount ?? null
}
