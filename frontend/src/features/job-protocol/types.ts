// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import type { components } from '@/services/generated/openapi'

export type ReviewJobProtocolDto = components['schemas']['ReviewJobProtocolDto']
export type ProtocolEventDto = components['schemas']['ProtocolEventDto']
export type ProtocolEventPhaseTimingDto = components['schemas']['ProtocolEventPhaseTimingDto']
export type ReviewJobResultDto = components['schemas']['ReviewJobResultDto']
export type ReviewStrategy = components['schemas']['ReviewStrategy']
export type ReviewJobProtocolPassResponse = components['schemas']['ReviewJobProtocolDto']

export interface ProtocolFileOutcome {
    filePath: string
    isComplete?: boolean
    isFailed?: boolean
    isExcluded?: boolean
    isCarriedForward?: boolean
    exclusionReason?: string | null
    errorMessage?: string | null
    isDegraded?: boolean
}

export interface ProtocolFollowUp {
    used?: boolean
    triggerFamily?: string | null
    completedSuccessfully?: boolean
    dependencyRecorded?: boolean
}

export interface ProtocolRepeatedJudgment {
    findingId?: string | null
    evidenceSetId?: string | null
    agreementState?: string | null
    recommendedDisposition?: string | null
    usedSameEvidenceSet?: boolean
    reasonCodes?: string[] | null
}

export interface ProtocolInheritance {
    sourceJobId: string
    sourceFileResultId?: string | null
    sourceProtocolId: string
    sourceCompletedAt?: string | null
}

export interface TokenBreakdownEntry {
    connectionCategory: number | string | null
    modelId: string | null
    totalInputTokens: number
    totalOutputTokens: number
    totalCachedInputTokens?: number | null
}

export interface JobDetail {
    aiModel: string | null
    reviewTemperature: number | null
    tokenBreakdown: TokenBreakdownEntry[]
    breakdownConsistent: boolean | null
    submittedAt?: string | null
    processingStartedAt?: string | null
    completedAt?: string | null
}

export interface CommentRelevanceEventDetails {
    implementationId?: string
    implementationVersion?: string
    filePath?: string
    originalCommentCount?: number
    keptCount?: number
    discardedCount?: number
    degradedComponents?: string[]
    fallbackChecks?: string[]
    degradedCause?: string | null
}

export interface CommentRelevanceDiscardedComment {
    filePath?: string
    lineNumber?: number | null
    severity?: string
    message?: string
    reasonCodes?: string[]
    decisionSource?: string
}

export interface CommentRelevanceAiTokenUsage {
    implementationId?: string
    filePath?: string
    inputTokens?: number
    outputTokens?: number
    modelCategory?: number | string | null
    modelId?: string | null
}

export interface CommentRelevanceOutputRecord extends CommentRelevanceEventDetails {
    reasonBuckets?: Record<string, number>
    decisionSources?: Record<string, number>
    discarded?: CommentRelevanceDiscardedComment[]
    aiTokenUsage?: CommentRelevanceAiTokenUsage | null
}

export interface FinalGateSummaryRecord {
    candidateCount?: number
    publishCount?: number
    summaryOnlyCount?: number
    dropCount?: number
    categoryCounts?: Record<string, number>
    invariantBlockedCount?: number
    originalSummary?: string | null
    finalSummary?: string | null
    summaryRewritePerformed?: boolean
    droppedFindingIds?: string[]
    summaryOnlyFindingIds?: string[]
    summaryRuleSource?: string | null
}

export interface VerificationRecord {
    findingId?: string
    claimId?: string
    filePath?: string | null
    stage?: string | null
    coverageState?: string | null
    claimCount?: number
    droppedCount?: number
    summaryOnlyCount?: number
    degradedComponent?: string | null
}

export interface VerificationEvidenceAttemptRecord {
    attemptId?: string
    claimId?: string
    sourceFamily?: string
    attemptOrder?: number
    status?: string
    scopeSummary?: string
    coverageImpact?: string
    payloadReference?: string | null
    failureReason?: string | null
}

export interface VerificationEvidenceItemRecord {
    kind?: string
    sourceId?: string | null
    summary?: string
    payloadReference?: string | null
    freshnessState?: string | null
}

export interface VerificationEvidenceOutputRecord {
    claimId?: string
    evidenceItems?: VerificationEvidenceItemRecord[]
    coverageState?: string
    retrievalNotes?: string | null
    evidenceAttempts?: VerificationEvidenceAttemptRecord[]
    hasProCursorAttempt?: boolean
    proCursorResultStatus?: string | null
}

export interface FinalGateProvenance {
    originKind?: string
    generatedByStage?: string
    sourceFilePath?: string | null
    sourceFileResultId?: string | null
    sourceCommentOrdinal?: number | null
}

export interface FinalGateEvidence {
    supportingFindingIds?: string[]
    supportingFiles?: string[]
    evidenceResolutionState?: string
    evidenceSource?: string
}

export interface FinalGateDecisionRecord {
    findingId?: string
    disposition?: string
    category?: string
    provenance?: FinalGateProvenance | null
    evidence?: FinalGateEvidence | null
    reasonCodes?: string[]
    blockedInvariantIds?: string[]
    ruleSource?: string
    summaryText?: string | null
    includedInFinalSummary?: boolean | null
}

export interface AgenticToolUsageRecord {
    ToolName?: string
    toolName?: string
    Status?: string
    status?: string
    Target?: string | null
    target?: string | null
}

export interface AgenticInvestigationOutputRecord {
    Status?: string
    status?: string
    ToolUsage?: AgenticToolUsageRecord[]
    toolUsage?: AgenticToolUsageRecord[]
    Degraded?: boolean
    degraded?: boolean
    candidateCount?: number | null
    CandidateCount?: number | null
    evidenceCount?: number | null
    EvidenceCount?: number | null
}

export type ReviewCommentRecord = {
    filePath?: string | null
    lineNumber?: number | null
    severity?: string | null
    message: string
}

export interface MergedEvent {
    id: string
    time: string
    name: string
    callDetails: ProtocolEventDto
    resultDetails: ProtocolEventDto | null
}

export interface EventDisplayRow {
    id: string
    merged: MergedEvent
    protocolId: string | null
    depth: number
    parentId: string | null
    parentName: string | null
    isToolChild: boolean
    childCount: number
    isExpanded: boolean
    timingSummary: string | null
    timingDetail: string | null
    traceFilePath: string | null
    traceEventCategory: string
    traceModelId: string | null
    traceMatchedField: string | null
    traceMatchSnippet: string | null
    traceContextSnippet: string | null
    traceHasLimitedMetadata: boolean
    traceIsRedacted: boolean
}

export interface PendingToolRow {
    merged: MergedEvent
}

export interface TimingInsight {
    rank: number
    protocolId: string | null
    eventId: string
    passLabel: string
    toolName: string
    durationMs: number
    waitDurationMs: number | null
    activeDurationMs: number | null
    hasPhaseDetail: boolean
}

export interface ToolPhaseGroup {
    key: string
    title: string
    count: number
    totalDurationMs: number | null
    availability: string | null
    outcome: string | null
    startedAt: string | null
    completedAt: string | null
    summary: string | null
    phases: ProtocolEventPhaseTimingDto[]
}

export interface EventTimingPresentation {
    phaseTimings: ProtocolEventPhaseTimingDto[]
    phaseGroupCount: number
    summary: string | null
    detail: string | null
}

export type ReviewProtocolPass = ReviewJobProtocolDto & {
    id?: string
    attemptNumber?: number
    label?: string | null
    outcome?: string | null
    resolvedReviewStrategy?: ReviewStrategy | null
    strategySelectionSource?: string | null
    fileOutcome?: ProtocolFileOutcome | null
    followUp?: ProtocolFollowUp | null
    repeatedJudgment?: ProtocolRepeatedJudgment | null
    inheritance?: ProtocolInheritance | null
    isInherited?: boolean
    events?: ProtocolEventDto[]
    startedAt?: string
    completedAt?: string | null
    totalInputTokens?: number | null
    totalOutputTokens?: number | null
    totalCachedInputTokens?: number | null
    iterationCount?: number | null
    toolCallCount?: number | null
    finalSummary?: string | null
    finalComments?: ReviewCommentRecord[] | null
}

export type TraceSearchableRow = {
    filePath: string | null
    protocolLabel: string | null
    eventKind: string
    eventCategory: string
    eventName: string
    modelId: string | null
    matchedField: string | null
    matchSnippet: string | null
    contextSnippet: string | null
    hasLimitedMetadata: boolean
    isRedacted: boolean
}
