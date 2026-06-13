// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { computed, nextTick, onMounted, onUnmounted, reactive, ref, shallowRef, watch } from 'vue'
import { useRoute } from 'vue-router'
import { createAdminClient } from '@/services/api'
import { createDismissal } from '@/services/findingDismissalsService'
import { useSession } from '@/composables/useSession'
import { getActiveRuntime } from '@/app/runtime/runtimeContext'
import type {
    AgenticInvestigationOutputRecord,
    CommentRelevanceEventDetails,
    CommentRelevanceOutputRecord,
    EventDisplayRow,
    EventTimingPresentation,
    FinalGateDecisionRecord,
    FinalGateSummaryRecord,
    JobDetail,
    MergedEvent,
    PendingToolRow,
    ProtocolEventDto,
    ProtocolEventPhaseTimingDto,
    ProtocolFileOutcome,
    ProtocolFollowUp,
    ProtocolInheritance,
    ProtocolRepeatedJudgment,
    ReviewCommentRecord,
    ReviewJobProtocolPassResponse,
    ReviewJobResultDto,
    ReviewProtocolPass,
    TimingInsight,
    TokenBreakdownEntry,
    ToolPhaseGroup,
    TraceSearchableRow,
    VerificationEvidenceOutputRecord,
    VerificationRecord,
} from '../types'
import {
    agenticInvestigationCandidateCount,
    agenticInvestigationEvidenceCount,
    commentLocation,
    computePassDuration,
    decodeHtmlEntities,
    formatAgenticInvestigationStatus,
    formatAgenticToolUsage,
    formatCacheStatus,
    formatCommentRelevanceAiUsage,
    formatCommentRelevanceCounts,
    formatCommentRelevanceImplementation,
    formatDate,
    formatDurationMs,
    formatDurationWithMs,
    formatEvidenceAttempts,
    formatEvidenceItems,
    formatFileOutcomeStatus,
    formatFinalGateCounts,
    formatFinalGateEvidence,
    formatFinalGateProvenance,
    formatFollowUpStatus,
    formatNamedCounts,
    formatPhaseCountSummary,
    formatPhaseDuration,
    formatPhaseTitle,
    formatRepeatedJudgmentStatus,
    formatReviewStrategy,
    formatStringList,
    formatTemperature,
    formatTimingAvailability,
    formatTokens,
    formatToolOutcome,
    formatToolPhaseGroupDuration,
    formatVerificationProCursorStatus,
    getPhaseTimingDurationMs,
    getPhaseTimings,
    hasEventError,
    hasEventTokens,
    hasToolTiming,
    isAgenticDegradedEvent,
    isAgenticInvestigationEvent,
    isCommentRelevanceEvent,
    isFinalGateEvent,
    isPlainObject,
    isVerificationEvent,
    kindBadgeClass,
    renderMarkdown,
    renderMergedEventText,
    severityVariant,
    shortGuid,
    slowestToolDurationLabel,
    statusBadgeClass,
    statusIconClass,
} from '../utils/formatters'

export function useJobProtocolViewModel() {
    const route = useRoute()

    const loading = ref(false)
    const error = ref('')
    const activeTab = ref<'summary' | 'traces' | 'tokens'>('summary')
    const protocols = shallowRef<ReviewProtocolPass[]>([])
    const loadedProtocolIds = ref<Set<string>>(new Set())
    const loadingProtocolIds = ref<Set<string>>(new Set())
    const activePassId = ref<string | null>(null)
    const selectedMergedEvent = ref<MergedEvent | null>(null)
    const expandedPhaseGroups = ref<Set<string>>(new Set())
    const modalPhaseGroups = shallowRef<ToolPhaseGroup[]>([])
    const modalPhaseGroupsPending = ref(false)
    const reviewStatus = ref<ReviewJobResultDto | null>(null)
    const jobDetail = ref<JobDetail | null>(null)
    const collapsedFolders = ref<Set<string>>(new Set())
    const collapsedEventParents = ref<Set<string>>(new Set())
    const selectedCommentPath = ref<string | null>(null)
    const isSummaryModalOpen = ref(false)
    const focusedEventId = ref<string | null>(null)
    const isTraceSearchCollapsed = ref(true)
    const traceFindingsOnly = ref(false)
    const traceFilters = ref({
        queryText: '',
        filePath: '',
        modelId: '',
    })
    const detailTab = ref<'events' | 'diff'>('events')
    const fileDiff = shallowRef<FileDiffDto | null>(null)
    const diffLoading = ref(false)
    const diffError = ref<string | null>(null)
    const diffCache = new Map<string, FileDiffDto>()

    type TraceFilterKey = keyof typeof traceFilters.value

    const routeClientId = computed(() =>
        (route.query?.clientId as string | undefined) ?? reviewStatus.value?.clientId ?? undefined,
    )

    const jobShortId = computed(() => {
        const id = protocols.value[0]?.jobId
        if (!id) return '—'
        return id.substring(0, 8)
    })

    const prReviewLink = computed(() => {
        if (!protocols.value.length || !routeClientId.value) return null
        const protocol = protocols.value[0]
        if (!protocol.providerScopePath || !protocol.providerProjectKey || !protocol.repositoryId || !protocol.pullRequestId) return null

        return {
            name: 'pr-review',
            query: {
                clientId: routeClientId.value,
                providerScopePath: protocol.providerScopePath,
                providerProjectKey: protocol.providerProjectKey,
                repositoryId: protocol.repositoryId,
                pullRequestId: String(protocol.pullRequestId),
            },
        }
    })

    const backToReviewsLink = computed(() => ({
        name: 'reviews',
        query: route.query.clientId ? { clientId: route.query.clientId } : {},
    }))

    const dismissingIds = ref<Set<string>>(new Set())
    const dismissToast = ref<{ message: string; isError: boolean } | null>(null)

    function commentKey(comment: any): string {
        return `${comment.filePath ?? (comment as any).file_path ?? ''}:${comment.lineNumber ?? (comment as any).line_number ?? 0}:${String(comment.message ?? '').slice(0, 80)}`
    }

    async function dismissComment(comment: any) {
        const clientId = routeClientId.value
        if (!clientId) {
            dismissToast.value = { message: 'Cannot dismiss: client context not available.', isError: true }
            setTimeout(() => {
                dismissToast.value = null
            }, 3000)
            return
        }

        const key = commentKey(comment)
        dismissingIds.value = new Set([...dismissingIds.value, key])
        try {
            await createDismissal(clientId, { originalMessage: comment.message ?? '', label: '' })
            dismissToast.value = { message: 'Finding dismissed.', isError: false }
        } catch {
            dismissToast.value = { message: 'Failed to dismiss finding.', isError: true }
        } finally {
            const next = new Set(dismissingIds.value)
            next.delete(key)
            dismissingIds.value = next
            setTimeout(() => {
                dismissToast.value = null
            }, 3000)
        }
    }

    const globalSearchQuery = ref('')
    const localSearchQuery = ref('')
    const localSeverities = ref<Set<string>>(new Set())

    const severityCounts = computed(() => {
        const counts = { error: 0, warning: 0, info: 0, suggestion: 0 }
        const allComments = reviewStatus.value?.result?.comments || []
        allComments.forEach(comment => {
            const severity = (comment.severity ?? '').toLowerCase() as keyof typeof counts
            if (counts[severity] !== undefined) counts[severity] += 1
        })
        return counts
    })

    function toggleSeverity(sev: string) {
        if (localSeverities.value.has(sev)) {
            localSeverities.value.delete(sev)
        } else {
            localSeverities.value.add(sev)
        }
        localSeverities.value = new Set(localSeverities.value)
    }

    const filteredCommentsForTree = computed(() => {
        const allComments = reviewStatus.value?.result?.comments || []
        const qLabel = globalSearchQuery.value.trim().toLowerCase()

        if (!qLabel) return allComments

        return allComments.filter(comment => {
            const filePath = ((comment.filePath ?? (comment as any).file_path) ?? '').toLowerCase()
            return filePath.includes(qLabel)
        })
    })

    const filteredCommentsForDetail = computed(() => {
        const allComments = reviewStatus.value?.result?.comments || []
        const query = localSearchQuery.value.trim().toLowerCase()
        const severities = localSeverities.value

        return allComments.filter(comment => {
            const matchesSeverity = severities.size === 0 || severities.has((comment.severity ?? '').toLowerCase())
            if (!matchesSeverity) return false
            if (!query) return true

            const message = (comment.message ?? '').toLowerCase()
            const filePath = ((comment.filePath ?? (comment as any).file_path) ?? '').toLowerCase()
            return message.includes(query) || filePath.includes(query)
        })
    })

    function toggleFolder(dir: string) {
        if (collapsedFolders.value.has(dir)) {
            collapsedFolders.value.delete(dir)
        } else {
            collapsedFolders.value.add(dir)
        }
    }

    function toggleEventParent(eventId: string) {
        const next = new Set(collapsedEventParents.value)
        if (next.has(eventId)) {
            next.delete(eventId)
        } else {
            next.add(eventId)
        }

        collapsedEventParents.value = next
    }

    function parseFilePath(label: string | null | undefined) {
        if (!label) return { filename: 'Pass', directory: '' }
        const path = label.replace(/\\/g, '/')
        const parts = path.split('/')
        const filename = parts.pop() || path
        const directory = parts.join('/') || ''
        return { filename, directory }
    }

    const normalizedTraceFilters = computed(() => ({
        queryText: traceFilters.value.queryText.trim().toLowerCase(),
        filePath: traceFilters.value.filePath.trim().toLowerCase(),
        modelId: traceFilters.value.modelId.trim().toLowerCase(),
    }))

    const hasActiveTraceFilters = computed(() =>
        Object.values(normalizedTraceFilters.value).some(value => value.length > 0),
    )

    const traceSearchToggleLabel = computed(() => (isTraceSearchCollapsed.value ? 'Show filters' : 'Hide filters'))
    const traceSearchToggleIcon = computed(() => (isTraceSearchCollapsed.value ? 'mdi-chevron-down' : 'mdi-chevron-up'))

    function normalizeTraceFilterValue(value: unknown): string {
        if (typeof value === 'string') {
            return value
        }

        if (typeof value === 'number') {
            return String(value)
        }

        if (Array.isArray(value)) {
            return normalizeTraceFilterValue(value[0])
        }

        return ''
    }

    function traceAutocompleteValue(value: string): string | null {
        return value.trim().length > 0 ? value : null
    }

    function setTraceFilterValue(key: TraceFilterKey, value: unknown): void {
        traceFilters.value[key] = normalizeTraceFilterValue(value)
    }

    function protocolHasFinalFindings(protocol: ReviewProtocolPass): boolean {
        return (protocol.finalComments?.length ?? 0) > 0
    }

    function normalizeTraceCategory(kind: string | null | undefined, name: string | null | undefined, eventCategory?: string | null): string {
        const normalizedCategory = (eventCategory ?? '').trim().toLowerCase()
        if (normalizedCategory) return normalizedCategory

        const normalizedKind = (kind ?? '').trim().toLowerCase()
        const normalizedName = (name ?? '').trim().toLowerCase()

        if (normalizedKind === 'memoryoperation') return 'memory'
        if (normalizedName.startsWith('dedup_')) return 'duplicate-suppression'
        if (normalizedName.includes('comment_relevance')) return 'comment-relevance'
        if (normalizedName.includes('verification')) return 'verification'
        if (normalizedName.includes('review_finding_gate') || normalizedName.includes('summary_reconciliation') || normalizedName.includes('repeated_judgment')) return 'review-finding-gate'
        if (normalizedName.includes('prorv')) return 'prorv-prefilter'
        if (normalizedName.includes('pr_wide')) return 'pr-wide-review'
        if (normalizedName.includes('review_strategy') || normalizedName.includes('agentic_file') || normalizedName.includes('review_agent_session') || normalizedName.includes('prompt_stage_evidence') || normalizedName.includes('review_step_skipped')) return 'review-strategy'
        if (normalizedKind === 'aicall') return 'ai-call'
        if (normalizedKind === 'toolcall') return 'tool-call'
        return 'operational'
    }

    function buildTraceSnippet(value: string | null | undefined, queryText: string): string | null {
        const normalized = value?.trim()
        if (!normalized) {
            return null
        }

        const maxLength = 220
        if (!queryText) {
            return normalized.length <= maxLength ? normalized : `${normalized.slice(0, maxLength).trim()}...`
        }

        const index = normalized.toLowerCase().indexOf(queryText)
        if (index < 0) {
            return null
        }

        const start = Math.max(0, index - 80)
        const end = Math.min(normalized.length, start + maxLength)
        let snippet = normalized.slice(start, end).trim()
        if (start > 0) snippet = `...${snippet}`
        if (end < normalized.length) snippet = `${snippet}...`
        return snippet
    }

    function firstTraceValue(...values: Array<string | null | undefined>): string | null {
        return values.find(value => !!value && value.trim().length > 0)?.trim() ?? null
    }

    function detectTraceRedaction(...values: Array<string | null | undefined>): boolean {
        const markers = ['[REDACTED]', '***REDACTED***', '<redacted>']
        return values.some(value => !!value && markers.some(marker => value.includes(marker)))
    }

    function buildTraceSearchableRow(protocol: ReviewProtocolPass, event: ProtocolEventDto): TraceSearchableRow {
        const filters = normalizedTraceFilters.value
        const candidates: Array<{ field: string; value: string | null | undefined }> = [
            { field: 'eventName', value: event.name },
            { field: 'inputTextSample', value: event.inputTextSample },
            { field: 'systemPrompt', value: event.systemPrompt },
            { field: 'outputSummary', value: event.outputSummary },
            { field: 'error', value: event.error },
        ]

        const firstVisibleField = candidates.find(candidate => !!candidate.value && candidate.value.trim().length > 0) ?? null
        const matchingField = filters.queryText
            ? candidates.find(candidate => candidate.value?.toLowerCase().includes(filters.queryText)) ?? null
            : firstVisibleField

        const matchedField = matchingField?.field ?? null
        const matchSnippet = buildTraceSnippet(matchingField?.value, filters.queryText)
        const contextSource = matchedField === 'inputTextSample'
            ? firstTraceValue(event.outputSummary, event.systemPrompt, event.error)
            : matchedField === 'systemPrompt'
                ? firstTraceValue(event.inputTextSample, event.outputSummary, event.error)
                : matchedField === 'outputSummary'
                    ? firstTraceValue(event.inputTextSample, event.systemPrompt, event.error)
                    : matchedField === 'error'
                        ? firstTraceValue(event.outputSummary, event.inputTextSample, event.systemPrompt)
                        : firstTraceValue(event.outputSummary, event.inputTextSample, event.systemPrompt, event.error)

        return {
            filePath: protocol.fileOutcome?.filePath ?? protocol.label ?? null,
            protocolLabel: protocol.label ?? null,
            eventKind: String(event.kind ?? 'unknown'),
            eventCategory: normalizeTraceCategory(String(event.kind ?? ''), event.name, event.eventCategory ?? null),
            eventName: event.name ?? 'Unknown',
            modelId: protocol.modelId ?? null,
            matchedField,
            matchSnippet,
            contextSnippet: buildTraceSnippet(contextSource, ''),
            hasLimitedMetadata: !protocol.label || !protocol.modelId,
            isRedacted: detectTraceRedaction(matchSnippet, contextSource, event.inputTextSample, event.systemPrompt, event.outputSummary, event.error),
        }
    }

    function matchesTraceFilters(protocol: ReviewProtocolPass, event: ProtocolEventDto): boolean {
        const filters = normalizedTraceFilters.value
        const row = buildTraceSearchableRow(protocol, event)

        if (filters.queryText && !row.matchSnippet) return false
        if (filters.filePath && !(row.filePath ?? '').toLowerCase().includes(filters.filePath)) return false
        if (filters.modelId && !(row.modelId ?? '').toLowerCase().includes(filters.modelId)) return false
        return true
    }

    const traceSuggestions = computed(() => {
        const matchingRows = protocols.value.flatMap(protocol =>
            (protocol.events ?? [])
                .filter(event => matchesTraceFilters(protocol, event))
                .map(event => buildTraceSearchableRow(protocol, event)),
        )

        const collect = (values: Array<string | null | undefined>) => Array.from(new Set(values.filter((value): value is string => !!value && value.trim().length > 0))).sort((left, right) => left.localeCompare(right))

        return {
            filePaths: collect(matchingRows.map(row => row.filePath)),
            modelIds: collect(matchingRows.map(row => row.modelId)),
        }
    })

    function protocolHasVisibleTraceRows(protocolId: string | null | undefined): boolean {
        if (!protocolId) {
            return false
        }

        return protocols.value.some(protocol =>
            protocol.id === protocolId && (protocol.events ?? []).some(event => matchesTraceFilters(protocol, event)),
        )
    }

    function clearTraceFilters() {
        traceFilters.value = {
            queryText: '',
            filePath: '',
            modelId: '',
        }
    }

    const sidebarItems = computed(() => {
        const root: any = { children: {} }

        const visibleProtocols = protocols.value.filter(protocol => {
            if (traceFindingsOnly.value && !protocolHasFinalFindings(protocol)) {
                return false
            }

            if (!hasActiveTraceFilters.value) {
                return true
            }

            return (protocol.events ?? []).some(event => matchesTraceFilters(protocol, event))
        })

        visibleProtocols.forEach(protocol => {
            const { directory } = parseFilePath(protocol.label)

            let parts: string[]
            if (!directory || directory === '.' || directory === './') {
                parts = ['./']
            } else {
                parts = directory.split('/').filter(Boolean)
            }

            let current = root
            parts.forEach((part, index) => {
                if (!current.children[part]) {
                    const nodePath = parts.slice(0, index + 1).join('/')
                    current.children[part] = {
                        name: part,
                        path: nodePath,
                        children: {},
                        protocols: [],
                    }
                }
                current = current.children[part]
            })
            current.protocols.push(protocol)
        })

        const items: any[] = []
        const flatten = (node: any, depth: number) => {
            const sortedFolderKeys = Object.keys(node.children ?? {}).sort((left, right) => {
                if (left === './') return -1
                if (right === './') return 1
                return left.localeCompare(right)
            })

            const passes = node.protocols ?? []
            const sortedPasses = [...passes].sort((left, right) => {
                const nameA = parseFilePath(left.label).filename
                const nameB = parseFilePath(right.label).filename
                return nameA.localeCompare(nameB)
            })

            sortedFolderKeys.forEach((key, index) => {
                const childNode = node.children[key]
                const isCollapsed = collapsedFolders.value.has(childNode.path)
                const isLast = index === sortedFolderKeys.length - 1 && sortedPasses.length === 0

                items.push({
                    type: 'folder',
                    name: childNode.name,
                    path: childNode.path,
                    depth,
                    isCollapsed,
                    isLast,
                })

                if (!isCollapsed) {
                    flatten(childNode, depth + 1)
                }
            })

            sortedPasses.forEach((protocol: ReviewProtocolPass, index) => {
                const isLast = index === sortedPasses.length - 1
                items.push({
                    type: 'pass',
                    name: parseFilePath(protocol.label).filename,
                    depth,
                    protocol,
                    slowestToolLabel: slowestToolDurationLabel(protocol),
                    isLast,
                })
            })
        }

        flatten(root, 0)
        return items
    })

    const treeVisiblePasses = computed<ReviewProtocolPass[]>(() =>
        sidebarItems.value
            .filter((item): item is { type: 'pass'; protocol: ReviewProtocolPass } => item.type === 'pass')
            .map(item => item.protocol),
    )

    const commentSidebarItems = computed(() => {
        const comments = filteredCommentsForTree.value
        const root: any = { children: {}, files: {} }

        comments.forEach(comment => {
            const path = comment.filePath || (comment as any).file_path || ''
            const { filename, directory } = parseFilePath(path)

            let parts: string[]
            if (!directory || directory === '.' || directory === './') {
                parts = ['./']
            } else {
                parts = directory.split('/').filter(Boolean)
            }

            let current = root
            parts.forEach((part, index) => {
                if (!current.children[part]) {
                    current.children[part] = {
                        name: part,
                        path: parts.slice(0, index + 1).join('/'),
                        children: {},
                        files: {},
                    }
                }
                current = current.children[part]
            })

            if (!current.files[filename]) {
                current.files[filename] = { name: filename, path, commentCount: 0 }
            }
            current.files[filename].commentCount += 1
        })

        const items: any[] = []
        const flatten = (node: any, depth: number) => {
            const sortedFolderKeys = Object.keys(node.children ?? {}).sort((left, right) => {
                if (left === './') return -1
                if (right === './') return 1
                return left.localeCompare(right)
            })

            const sortedFileKeys = Object.keys(node.files ?? {}).sort()

            sortedFolderKeys.forEach((key, index) => {
                const childNode = node.children[key]
                const isCollapsed = collapsedFolders.value.has(`comments:${childNode.path}`)
                const isLast = index === sortedFolderKeys.length - 1 && sortedFileKeys.length === 0

                items.push({
                    type: 'folder',
                    name: childNode.name,
                    path: childNode.path,
                    depth,
                    isCollapsed,
                    isLast,
                })

                if (!isCollapsed) {
                    flatten(childNode, depth + 1)
                }
            })

            sortedFileKeys.forEach((key, index) => {
                const file = node.files[key]
                const isLast = index === sortedFileKeys.length - 1
                items.push({
                    type: 'file',
                    name: file.name,
                    path: file.path,
                    depth,
                    commentCount: file.commentCount,
                    isLast,
                })
            })
        }

        flatten(root, 0)
        return items
    })

    const groupedReviewComments = computed(() => {
        let comments = filteredCommentsForDetail.value

        if (selectedCommentPath.value) {
            comments = comments.filter(comment => {
                const path = comment.filePath || (comment as any).file_path || ''
                return path === selectedCommentPath.value || path.startsWith(`${selectedCommentPath.value}/`)
            })
        }

        const groups: Record<string, any[]> = {}

        comments.forEach(comment => {
            const path = comment.filePath || (comment as any).file_path || ''
            const { directory } = parseFilePath(path)
            const dirKey = directory || 'Root'
            if (!groups[dirKey]) groups[dirKey] = []
            groups[dirKey].push(comment)
        })

        const sortedDirKeys = Object.keys(groups).sort((left, right) => {
            if (left === 'Root') return -1
            if (right === 'Root') return 1
            return left.localeCompare(right)
        })

        return sortedDirKeys.map(dir => ({
            directory: dir,
            comments: [...groups[dir]].sort((left, right) => {
                const pathA = left.filePath || (left as any).file_path || ''
                const pathB = right.filePath || (right as any).file_path || ''
                if (pathA !== pathB) return pathA.localeCompare(pathB)
                return (left.lineNumber || (left as any).line_number || 0) - (right.lineNumber || (right as any).line_number || 0)
            }),
        }))
    })

    const activePass = computed<ReviewProtocolPass | null>(() => {
        if (!protocols.value.length || activeTab.value === 'summary') return null
        return protocols.value.find(protocol => protocol.id === activePassId.value) ?? protocols.value[0]
    })

    const activePassFinalComments = computed<ReviewCommentRecord[]>(() => activePass.value?.finalComments ?? [])

    watch(activePassId, protocolId => {
        if (!protocolId) {
            return
        }

        void ensureProtocolPassLoaded(protocolId)
    }, { flush: 'post' })

    watch(activePassId, () => {
        detailTab.value = 'events'
        fileDiff.value = null
        diffError.value = null
    })

    watch([treeVisiblePasses, activeTab, traceFindingsOnly], ([visiblePasses, tab, findingsOnly]) => {
        if (tab !== 'traces') {
            return
        }

        if (visiblePasses.length === 0 && findingsOnly) {
            activePassId.value = null
            return
        }

        if (findingsOnly && !visiblePasses.some(protocol => protocol.id === activePassId.value)) {
            activePassId.value = visiblePasses[0]?.id ?? null
        }
    }, { flush: 'post' })

    function processEvents(events: ProtocolEventDto[] | undefined | null): MergedEvent[] {
        if (!events) return []
        return events.map((event, index) => ({
            id: event.id ?? String(index),
            time: event.occurredAt ?? '',
            name: event.name ?? event.kind ?? 'Unknown',
            callDetails: event,
            resultDetails: event,
        }))
    }

    function isPrimaryAiTurnEvent(event: MergedEvent): boolean {
        return (event.callDetails.kind ?? '').toLowerCase() === 'aicall'
            && /^ai_call_iter_\d+$/.test(event.name)
    }

    function parentIterationLabel(name: string): string {
        const match = /^ai_call_iter_(\d+)$/.exec(name)
        if (!match) {
            return 'Child of AI turn'
        }

        return `AI ${match[1]}`
    }

    function isMergedEventProcessing(event: MergedEvent | null | undefined): boolean {
        return !event?.resultDetails
    }


    const eventTimingPresentationCache = new WeakMap<ProtocolEventDto, EventTimingPresentation>()


    function summarizeSharedValue(values: Array<string | null | undefined>): string | null {
        const distinct = [...new Set(values.filter((value): value is string => !!value && value.trim().length > 0))]
        if (distinct.length === 0) {
            return null
        }

        return distinct.length === 1 ? distinct[0] : 'mixed'
    }

    function summarizePhaseGroupSummary(phases: ProtocolEventPhaseTimingDto[]): string | null {
        const summaries = [...new Set(phases
            .map(phase => phase.summary?.trim())
            .filter((summary): summary is string => !!summary))]

        if (summaries.length === 0) {
            return null
        }

        if (summaries.length === 1) {
            return summaries[0]
        }

        return `${summaries.length} distinct summaries recorded`
    }

    function buildToolPhaseGroups(phases: ProtocolEventPhaseTimingDto[]): ToolPhaseGroup[] {
        const groups = new Map<string, ToolPhaseGroup>()

        for (const phase of phases) {
            const key = (phase.name ?? phase.displayName ?? 'unnamed-phase').trim().toLowerCase() || 'unnamed-phase'
            const existing = groups.get(key)

            if (existing) {
                existing.count += 1
                existing.phases.push(phase)
                const durationMs = getPhaseTimingDurationMs(phase)
                if (durationMs != null) {
                    existing.totalDurationMs = (existing.totalDurationMs ?? 0) + durationMs
                }

                if (!existing.startedAt || (phase.startedAt && new Date(phase.startedAt).getTime() < new Date(existing.startedAt).getTime())) {
                    existing.startedAt = phase.startedAt ?? existing.startedAt
                }

                if (!existing.completedAt || (phase.completedAt && new Date(phase.completedAt).getTime() > new Date(existing.completedAt).getTime())) {
                    existing.completedAt = phase.completedAt ?? existing.completedAt
                }

                existing.availability = summarizeSharedValue(existing.phases.map(entry => entry.availability))
                existing.outcome = summarizeSharedValue(existing.phases.map(entry => entry.outcome))
                existing.summary = summarizePhaseGroupSummary(existing.phases)
                continue
            }

            const durationMs = getPhaseTimingDurationMs(phase)
            groups.set(key, {
                key,
                title: phase.displayName ?? phase.name ?? 'Unnamed phase',
                count: 1,
                totalDurationMs: durationMs,
                availability: phase.availability ?? null,
                outcome: phase.outcome ?? null,
                startedAt: phase.startedAt ?? null,
                completedAt: phase.completedAt ?? null,
                summary: phase.summary?.trim() ?? null,
                phases: [phase],
            })
        }

        return [...groups.values()]
    }

    function getEventTimingPresentation(event: ProtocolEventDto | null | undefined): EventTimingPresentation {
        if (!event) {
            return {
                phaseTimings: [],
                phaseGroupCount: 0,
                summary: null,
                detail: null,
            }
        }

        const cached = eventTimingPresentationCache.get(event)
        if (cached) {
            return cached
        }

        const phaseTimings = getPhaseTimings(event)
        const phaseGroupCount = phaseTimings.length > 0 ? buildToolPhaseGroups(phaseTimings).length : 0

        let summary: string | null
        if (event.durationMs != null) {
            summary = formatDurationWithMs(event.durationMs)
        } else if (event.startedAt && event.completedAt) {
            const duration = new Date(event.completedAt).getTime() - new Date(event.startedAt).getTime()
            summary = !Number.isNaN(duration) && duration >= 0 ? formatDurationWithMs(duration) : 'Timing recorded'
        } else {
            summary = hasToolTiming(event) ? 'Timing recorded' : null
        }

        const parts: string[] = []
        if (event.activeDurationMs != null) {
            parts.push(`Active ${formatDurationWithMs(event.activeDurationMs)}`)
        }

        if (event.waitDurationMs != null) {
            parts.push(`Wait ${formatDurationWithMs(event.waitDurationMs)}`)
        }

        if (phaseTimings.length > 0) {
            parts.push(formatPhaseCountSummary(phaseTimings.length, phaseGroupCount))
        }

        const presentation: EventTimingPresentation = {
            phaseTimings,
            phaseGroupCount,
            summary,
            detail: parts.length > 0 ? parts.join(' · ') : null,
        }

        eventTimingPresentationCache.set(event, presentation)
        return presentation
    }

    // Cache keyed by (protocol object, collapsed-set identity, filter snapshot) so unchanged passes skip full recompute
    const eventRowsCache = new WeakMap<ReviewProtocolPass, { collapseKey: Set<string>, filterKey: string, rows: EventDisplayRow[] }>()

    function buildEventRows(protocol: ReviewProtocolPass | null | undefined): EventDisplayRow[] {
        if (protocol) {
            const collapsed = collapsedEventParents.value
            const filterKey = JSON.stringify(normalizedTraceFilters.value)
            const cached = eventRowsCache.get(protocol)
            if (cached && cached.collapseKey === collapsed && cached.filterKey === filterKey) {
                return cached.rows
            }
        }
        const mergedEvents = processEvents(protocol?.events)
        const filteredMergedEvents = protocol
            ? mergedEvents.filter(merged => matchesTraceFilters(protocol, merged.callDetails))
            : mergedEvents
        const rows: EventDisplayRow[] = []
        let activeAiTurnParentId: string | null = null
        let pendingToolRows: PendingToolRow[] = []
        const childEventsByParentId = new Map<string, MergedEvent[]>()
        const standaloneEvents: MergedEvent[] = []

        const createDisplayRow = (
            merged: MergedEvent,
            depth: number,
            parentId: string | null,
            parentName: string | null,
            isToolChild: boolean,
            childCount: number,
            isExpanded: boolean,
        ): EventDisplayRow => {
            const timing = getEventTimingPresentation(merged.callDetails)
            const traceRow = protocol ? buildTraceSearchableRow(protocol, merged.callDetails) : null

            return {
                id: merged.id,
                merged,
                protocolId: protocol?.id ?? null,
                depth,
                parentId,
                parentName,
                isToolChild,
                childCount,
                isExpanded,
                timingSummary: timing.summary,
                timingDetail: timing.detail,
                traceFilePath: traceRow?.filePath ?? null,
                traceEventCategory: traceRow?.eventCategory ?? 'operational',
                traceModelId: traceRow?.modelId ?? null,
                traceMatchedField: traceRow?.matchedField ?? null,
                traceMatchSnippet: traceRow?.matchSnippet ?? null,
                traceContextSnippet: traceRow?.contextSnippet ?? null,
                traceHasLimitedMetadata: traceRow?.hasLimitedMetadata ?? false,
                traceIsRedacted: traceRow?.isRedacted ?? false,
            }
        }

        const appendStandaloneRow = (merged: MergedEvent) => {
            rows.push(createDisplayRow(merged, 0, null, null, false, 0, false))
        }

        const flushPendingToolRows = () => {
            for (const pendingToolRow of pendingToolRows) {
                standaloneEvents.push(pendingToolRow.merged)
            }

            pendingToolRows = []
        }

        for (const merged of filteredMergedEvents) {
            const isToolCall = (merged.callDetails.kind ?? '').toLowerCase() === 'toolcall'

            if (isPrimaryAiTurnEvent(merged)) {
                activeAiTurnParentId = merged.id

                if (pendingToolRows.length > 0) {
                    childEventsByParentId.set(
                        merged.id,
                        pendingToolRows.map(pendingToolRow => pendingToolRow.merged),
                    )
                }

                pendingToolRows = []
                continue
            }

            const parentId = isToolCall ? activeAiTurnParentId : null

            if (isToolCall && !parentId) {
                pendingToolRows.push({ merged })
                continue
            }

            if (isToolCall && parentId) {
                const existingChildren = childEventsByParentId.get(parentId) ?? []
                existingChildren.push(merged)
                childEventsByParentId.set(parentId, existingChildren)
                continue
            }

            if (!isToolCall) {
                flushPendingToolRows()
                standaloneEvents.push(merged)
            }
        }

        flushPendingToolRows()

        const childEventIds = new Set<string>()
        for (const children of childEventsByParentId.values()) {
            for (const child of children) {
                childEventIds.add(child.id)
            }
        }

        for (const merged of filteredMergedEvents) {
            if (isPrimaryAiTurnEvent(merged)) {
                const children = childEventsByParentId.get(merged.id) ?? []
                const isExpanded = !collapsedEventParents.value.has(merged.id)
                rows.push(createDisplayRow(merged, 0, null, null, false, children.length, isExpanded))

                if (isExpanded) {
                    for (const child of children) {
                        rows.push(createDisplayRow(child, 1, merged.id, merged.name, true, 0, false))
                    }
                }
                continue
            }

            if (!childEventIds.has(merged.id)) {
                appendStandaloneRow(merged)
            }
        }

        if (protocol) {
            eventRowsCache.set(protocol, {
                collapseKey: collapsedEventParents.value,
                filterKey: JSON.stringify(normalizedTraceFilters.value),
                rows,
            })
        }

        return rows
    }

    const reviewTraceRows = computed<EventDisplayRow[]>(() =>
        protocols.value.flatMap(protocol => buildEventRows(protocol)),
    )

    const activePassEventRows = computed<EventDisplayRow[]>(() =>
        buildEventRows(activePass.value),
    )

    const visibleTraceRows = computed<EventDisplayRow[]>(() => reviewTraceRows.value)

    const traceTimingInsights = computed<TimingInsight[]>(() => {
        const protocol = activePass.value
        if (!protocol) {
            return []
        }

        const protocolId = protocol.id ?? null
        const passLabel = protocol.label ?? 'Unknown pass'

        return (protocol.events ?? [])
            .filter(event => (event.kind ?? '').toLowerCase() === 'toolcall' && event.durationMs != null)
            .map(event => ({
                protocolId,
                eventId: event.id ?? `${protocolId ?? 'protocol'}:${event.name ?? 'tool'}`,
                passLabel,
                toolName: event.name ?? 'Unknown tool',
                durationMs: event.durationMs ?? 0,
                waitDurationMs: event.waitDurationMs ?? null,
                activeDurationMs: event.activeDurationMs ?? null,
                hasPhaseDetail: getPhaseTimings(event).length > 0,
            }))
            .sort((left, right) => right.durationMs - left.durationMs)
            .slice(0, 3)
            .map((insight, index) => ({
                rank: index + 1,
                ...insight,
            }))
    })

    const totalInputTokens = computed(() =>
        protocols.value.reduce((sum, protocol) => sum + (protocol.totalInputTokens ?? 0), 0),
    )
    const totalOutputTokens = computed(() =>
        protocols.value.reduce((sum, protocol) => sum + (protocol.totalOutputTokens ?? 0), 0),
    )
    const totalCachedInputTokens = computed(() =>
        protocols.value.reduce((sum, protocol) => sum + (protocol.totalCachedInputTokens ?? 0), 0),
    )
    const totalEffectiveInputTokens = computed(() =>
        Math.max(0, totalInputTokens.value - totalCachedInputTokens.value),
    )

    const overallDuration = computed(() => {
        const start = jobDetail.value?.processingStartedAt ?? jobDetail.value?.submittedAt
        if (!start) return '—'

        const startMs = new Date(start).getTime()
        if (Number.isNaN(startMs)) return '—'

        const end = jobDetail.value?.completedAt ?? null
        const endMs = end ? new Date(end).getTime() : Date.now()
        if (Number.isNaN(endMs)) return '—'

        return formatDurationMs(endMs - startMs)
    })

    const reviewModelDisplay = computed(() => jobDetail.value?.aiModel?.trim() || 'Default')
    const reviewStrategyDisplay = computed(() =>
        formatReviewStrategy(protocols.value[0]?.resolvedReviewStrategy),
    )
    const activePassReviewStrategyDisplay = computed(() =>
        formatReviewStrategy(activePass.value?.resolvedReviewStrategy ?? protocols.value[0]?.resolvedReviewStrategy),
    )
    const activePassFileOutcome = computed<ProtocolFileOutcome | null>(() => activePass.value?.fileOutcome ?? null)
    const activePassFollowUp = computed<ProtocolFollowUp | null>(() => activePass.value?.followUp ?? null)
    const activePassRepeatedJudgment = computed<ProtocolRepeatedJudgment | null>(() => activePass.value?.repeatedJudgment ?? null)
    const activePassProRvPrefilter = computed(() => activePass.value?.proRvPrefilter ?? null)
    const reviewTemperatureDisplay = computed(() => formatTemperature(jobDetail.value?.reviewTemperature))
    const inheritedProtocolCount = computed(() => protocols.value.filter(protocol => protocol.isInherited).length)
    const activePassInheritance = computed<ProtocolInheritance | null>(() => activePass.value?.inheritance ?? null)

    const protocolTokenBreakdown = computed<TokenBreakdownEntry[]>(() => {
        const grouped = new Map<string, TokenBreakdownEntry>()

        protocols.value.forEach(protocol => {
            const input = protocol.totalInputTokens ?? 0
            const output = protocol.totalOutputTokens ?? 0
            const cached = protocol.totalCachedInputTokens ?? 0
            if (input === 0 && output === 0) {
                return
            }

            const connectionCategory = protocol.aiConnectionCategory ?? null
            const modelId = protocol.modelId ?? null
            const key = `${String(connectionCategory ?? 'unknown')}|${modelId ?? '(default)'}`
            const existing = grouped.get(key)
            if (existing) {
                existing.totalInputTokens += input
                existing.totalOutputTokens += output
                existing.totalCachedInputTokens = (existing.totalCachedInputTokens ?? 0) + cached
                return
            }

            grouped.set(key, {
                connectionCategory,
                modelId,
                totalInputTokens: input,
                totalOutputTokens: output,
                totalCachedInputTokens: cached,
            })
        })

        return Array.from(grouped.values()).sort((left, right) => {
            const leftTotal = left.totalInputTokens + left.totalOutputTokens
            const rightTotal = right.totalInputTokens + right.totalOutputTokens
            if (leftTotal !== rightTotal) {
                return rightTotal - leftTotal
            }

            return `${left.connectionCategory ?? ''}|${left.modelId ?? ''}`.localeCompare(`${right.connectionCategory ?? ''}|${right.modelId ?? ''}`)
        })
    })

    const protocolBreakdownConsistent = computed(() => {
        if (protocolTokenBreakdown.value.length === 0) {
            return jobDetail.value?.breakdownConsistent ?? null
        }

        const breakdownInput = protocolTokenBreakdown.value.reduce((sum, entry) => sum + entry.totalInputTokens, 0)
        const breakdownOutput = protocolTokenBreakdown.value.reduce((sum, entry) => sum + entry.totalOutputTokens, 0)
        return breakdownInput === totalInputTokens.value && breakdownOutput === totalOutputTokens.value
    })

    const parsedInputResult = computed(() => {
        if (!selectedMergedEvent.value?.callDetails.inputTextSample) return null
        try {
            const text = selectedMergedEvent.value.callDetails.inputTextSample
            if (text.startsWith('args=')) {
                return JSON.parse(text.substring(5))
            }
            return JSON.parse(text)
        } catch {
            return null
        }
    })

    const parsedOutputResult = computed(() => {
        if (!selectedMergedEvent.value?.resultDetails?.outputSummary) return null
        try {
            const parsed = JSON.parse(selectedMergedEvent.value.resultDetails.outputSummary)

            if (parsed && typeof parsed === 'object' && parsed.confidence_evaluations) {
                const evaluations = parsed.confidence_evaluations
                if (Array.isArray(evaluations)) {
                    parsed.confidence_evaluations = evaluations.map((evaluation: any) => {
                        if (typeof evaluation === 'object' && evaluation !== null) {
                            return {
                                concern: evaluation.concern || evaluation.category || evaluation.metric || evaluation.type || evaluation.name || 'Unknown',
                                confidence: evaluation.confidence || evaluation.level || evaluation.score || 'N/A',
                            }
                        }
                        return { concern: 'Unknown', confidence: String(evaluation) }
                    })
                } else if (typeof evaluations === 'object') {
                    parsed.confidence_evaluations = Object.entries(evaluations).map(([key, value]: [string, any]) => ({
                        concern: key,
                        confidence: typeof value === 'object' && value !== null ? (value.confidence || value.level || value.score || 'N/A') : value,
                    }))
                }
            }
            return parsed
        } catch {
            return null
        }
    })

    const selectedToolPhaseTimings = computed(() => getEventTimingPresentation(selectedMergedEvent.value?.callDetails).phaseTimings)

    function resetEventModalExpansionState(): void {
        expandedPhaseGroups.value = new Set()
        modalPhaseGroups.value = []
        modalPhaseGroupsPending.value = false
    }

    async function scheduleModalPhaseGrouping(): Promise<void> {
        const event = selectedMergedEvent.value?.callDetails
        const phaseTimings = getEventTimingPresentation(event).phaseTimings
        if (phaseTimings.length === 0) {
            modalPhaseGroups.value = []
            modalPhaseGroupsPending.value = false
            return
        }

        modalPhaseGroupsPending.value = true
        await nextTick()

        if (selectedMergedEvent.value?.callDetails !== event) {
            return
        }

        modalPhaseGroups.value = buildToolPhaseGroups(phaseTimings)
        modalPhaseGroupsPending.value = false
    }

    function togglePhaseGroup(key: string): void {
        const next = new Set(expandedPhaseGroups.value)
        if (next.has(key)) {
            next.delete(key)
        } else {
            next.add(key)
        }

        expandedPhaseGroups.value = next
    }

    function isPhaseGroupExpanded(key: string): boolean {
        return expandedPhaseGroups.value.has(key)
    }

    const selectedAiCallSessionTurn = computed(() => {
        if (!activePass.value?.events?.length || !selectedMergedEvent.value) {
            return null
        }

        const selectedIndex = activePass.value.events.findIndex(event => event.id === selectedMergedEvent.value?.callDetails.id)
        if (selectedIndex < 0) {
            return null
        }

        for (let index = selectedIndex + 1; index < activePass.value.events.length; index += 1) {
            const event = activePass.value.events[index]
            if (event.name === 'review_agent_session_turn') {
                return event
            }

            if (event.kind === 'AiCall') {
                break
            }
        }

        return null
    })

    const selectedAiCallProviderManagedNote = computed(() => {
        if ((selectedMergedEvent.value?.callDetails.kind ?? '').toLowerCase() !== 'aicall') {
            return null
        }

        const sessionTurn = selectedAiCallSessionTurn.value
        if (!sessionTurn?.inputTextSample) {
            return null
        }

        try {
            const parsed = JSON.parse(sessionTurn.inputTextSample)
            if (parsed?.sessionMode !== 'ProviderManagedSession') {
                return null
            }

            return 'Provider-managed session: this input/output panel shows only the local delta sent for this turn. Token counts may be higher because the provider accounts for the full continued conversation it retained server-side.'
        } catch {
            return null
        }
    })


    const selectedCommentRelevanceInput = computed<CommentRelevanceEventDetails | null>(() => {
        if (!isCommentRelevanceEvent(selectedMergedEvent.value?.callDetails.name)) {
            return null
        }

        return isPlainObject(parsedInputResult.value)
            ? parsedInputResult.value as CommentRelevanceEventDetails
            : null
    })

    const selectedCommentRelevanceOutput = computed<CommentRelevanceOutputRecord | null>(() => {
        if (!isCommentRelevanceEvent(selectedMergedEvent.value?.callDetails.name)) {
            return null
        }

        return isPlainObject(parsedOutputResult.value)
            ? parsedOutputResult.value as CommentRelevanceOutputRecord
            : null
    })

    const selectedFinalGateInput = computed<FinalGateSummaryRecord | null>(() => {
        if (!isFinalGateEvent(selectedMergedEvent.value?.callDetails.name)) {
            return null
        }

        return isPlainObject(parsedInputResult.value)
            ? parsedInputResult.value as FinalGateSummaryRecord
            : null
    })

    const selectedFinalGateSummaryOutput = computed<FinalGateSummaryRecord | null>(() => {
        if (selectedMergedEvent.value?.callDetails.name !== 'review_finding_gate_summary') {
            return null
        }

        return isPlainObject(parsedOutputResult.value)
            ? parsedOutputResult.value as FinalGateSummaryRecord
            : null
    })

    const selectedFinalGateDecisionOutput = computed<FinalGateDecisionRecord | null>(() => {
        if (selectedMergedEvent.value?.callDetails.name !== 'review_finding_gate_decision') {
            return null
        }

        return isPlainObject(parsedOutputResult.value)
            ? parsedOutputResult.value as FinalGateDecisionRecord
            : null
    })

    const selectedVerificationInput = computed<VerificationRecord | null>(() => {
        if (!isVerificationEvent(selectedMergedEvent.value?.callDetails.name)) {
            return null
        }

        return isPlainObject(parsedInputResult.value)
            ? parsedInputResult.value as VerificationRecord
            : null
    })

    const selectedVerificationOutput = computed<Record<string, unknown> | null>(() => {
        if (!isVerificationEvent(selectedMergedEvent.value?.callDetails.name)) {
            return null
        }

        return isPlainObject(parsedOutputResult.value)
            ? parsedOutputResult.value
            : null
    })

    const selectedVerificationEvidenceOutput = computed<VerificationEvidenceOutputRecord | null>(() => {
        if (selectedMergedEvent.value?.callDetails.name !== 'verification_evidence_collected') {
            return null
        }

        return isPlainObject(parsedOutputResult.value)
            ? parsedOutputResult.value as VerificationEvidenceOutputRecord
            : null
    })

    const selectedAgenticInvestigationOutput = computed<AgenticInvestigationOutputRecord | null>(() => {
        if (!isAgenticInvestigationEvent(selectedMergedEvent.value?.callDetails.name)) {
            return null
        }

        return isPlainObject(parsedOutputResult.value)
            ? parsedOutputResult.value as AgenticInvestigationOutputRecord
            : null
    })


    let pollInterval: ReturnType<typeof setInterval> | null = null

    function resetProtocolState() {
        error.value = ''
        protocols.value = []
        loadedProtocolIds.value = new Set()
        loadingProtocolIds.value = new Set()
        reviewStatus.value = null
        jobDetail.value = null
        activePassId.value = null
        focusedEventId.value = null
        clearTraceFilters()

        if (pollInterval) {
            clearInterval(pollInterval)
            pollInterval = null
        }
    }

    async function loadProtocol(showLoading = false) {
        if (showLoading) loading.value = true
        try {
            const jobId = route.params.id as string
            const [protocolRes, resultRes, detailRes] = await Promise.all([
                createAdminClient().GET('/jobs/{id}/protocol', { params: { path: { id: jobId }, query: { includeEvents: false } } }),
                createAdminClient().GET('/jobs/{id}/result', { params: { path: { id: jobId } } }),
                createAdminClient().GET('/jobs/{id}', { params: { path: { id: jobId } } }),
            ])

            const data = protocolRes.data as any
            const fetchError = protocolRes.error

            if (fetchError) {
                if (showLoading) error.value = 'Protocol not found for this job.'
            } else if (Array.isArray(data)) {
                const normalizedProtocols = [...data].sort(compareProtocols)
                protocols.value = normalizedProtocols
                loadedProtocolIds.value = new Set()
                if (resultRes.data) {
                    reviewStatus.value = resultRes.data
                }
                if (detailRes.data) {
                    const detail = detailRes.data as any
                    jobDetail.value = {
                        aiModel: detail.aiModel ?? null,
                        reviewTemperature: detail.reviewTemperature ?? null,
                        tokenBreakdown: detail.tokenBreakdown ?? [],
                        breakdownConsistent: detail.breakdownConsistent ?? null,
                        submittedAt: detail.submittedAt ?? null,
                        processingStartedAt: detail.processingStartedAt ?? null,
                        completedAt: detail.completedAt ?? null,
                    }
                }
                if (!activePassId.value && normalizedProtocols.length > 0 && normalizedProtocols[0].id) {
                    activePassId.value = normalizedProtocols[0].id
                }

                const routeProtocolId = typeof route.query.protocolId === 'string' ? route.query.protocolId : null
                const protocolIdToLoad = routeProtocolId && normalizedProtocols.some(protocol => protocol.id === routeProtocolId)
                    ? routeProtocolId
                    : activePassId.value && normalizedProtocols.some(protocol => protocol.id === activePassId.value)
                        ? activePassId.value
                        : normalizedProtocols[0]?.id

                if (protocolIdToLoad) {
                    if (activePassId.value !== protocolIdToLoad) {
                        activePassId.value = protocolIdToLoad
                    }

                    await ensureProtocolPassLoaded(protocolIdToLoad)
                    await focusRouteEventIfRequested()
                }
                const isProcessing = normalizedProtocols.some(protocol => !protocol.completedAt) || resultRes.data?.status === 'processing'
                if (isProcessing && !pollInterval) {
                    pollInterval = setInterval(() => {
                        void loadProtocol(false)
                    }, 3000)
                } else if (!isProcessing && pollInterval) {
                    clearInterval(pollInterval)
                    pollInterval = null
                }
            }
        } catch {
            if (showLoading) error.value = 'Failed to load protocol.'
        } finally {
            if (showLoading) loading.value = false
        }
    }

    async function ensureProtocolPassLoaded(protocolId: string) {
        if (loadedProtocolIds.value.has(protocolId) || loadingProtocolIds.value.has(protocolId)) {
            return
        }

        const jobId = route.params.id as string
        const nextLoading = new Set(loadingProtocolIds.value)
        nextLoading.add(protocolId)
        loadingProtocolIds.value = nextLoading

        try {
            const response = await createAdminClient().GET('/jobs/{id}/protocol/{protocolId}', {
                params: {
                    path: {
                        id: jobId,
                        protocolId,
                    },
                },
            })

            if (response.error || !response.data) {
                return
            }

            const detailedPass = response.data as ReviewJobProtocolPassResponse
            const index = protocols.value.findIndex(protocol => protocol.id === protocolId)
            if (index < 0) {
                return
            }

            const nextProtocols = [...protocols.value]
            nextProtocols[index] = {
                ...nextProtocols[index],
                ...detailedPass,
            }
            protocols.value = nextProtocols

            const nextLoaded = new Set(loadedProtocolIds.value)
            nextLoaded.add(protocolId)
            loadedProtocolIds.value = nextLoaded
        } catch {
            // Keep the lightweight overview visible when detail loading fails.
        } finally {
            const nextLoadingIds = new Set(loadingProtocolIds.value)
            nextLoadingIds.delete(protocolId)
            loadingProtocolIds.value = nextLoadingIds
        }
    }

    async function focusRouteEventIfRequested() {
        const routeEventId = typeof route.query.eventId === 'string' ? route.query.eventId : null
        focusedEventId.value = routeEventId
        if (!routeEventId) {
            return
        }

        await nextTick()
        const row = document.querySelector(`[data-event-id="${routeEventId}"]`)
        if (row instanceof HTMLElement && typeof row.scrollIntoView === 'function') {
            row.scrollIntoView({ behavior: 'smooth', block: 'center' })
        }
    }

    onMounted(() => {
        void loadProtocol(true)
    })

    watch(
        () => `${String(route.params.id ?? '')}|${String(route.query.clientId ?? '')}|${String(route.query.protocolId ?? '')}|${String(route.query.eventId ?? '')}`,
        (nextKey, previousKey) => {
            if (nextKey === previousKey) {
                return
            }

            resetProtocolState()
            void loadProtocol(true)
        },
    )

    watch(activeTab, async tab => {
        if (tab !== 'traces') {
            return
        }

        await Promise.all(protocols.value
            .map(protocol => protocol.id)
            .filter((protocolId): protocolId is string => !!protocolId)
            .map(protocolId => ensureProtocolPassLoaded(protocolId)))
    })

    watch(reviewTraceRows, rows => {
        if (activeTab.value !== 'traces' || !hasActiveTraceFilters.value || rows.length === 0) {
            return
        }

        if (activePassEventRows.value.length > 0) {
            return
        }

        const nextProtocolId = rows[0]?.protocolId
        if (nextProtocolId && activePassId.value !== nextProtocolId) {
            activePassId.value = nextProtocolId
        }
    }, { flush: 'post' })

    onUnmounted(() => {
        if (pollInterval) clearInterval(pollInterval)
    })

    function compareProtocols(left: ReviewProtocolPass, right: ReviewProtocolPass): number {
        const attemptDelta = (right.attemptNumber ?? 0) - (left.attemptNumber ?? 0)
        if (attemptDelta !== 0) return attemptDelta

        const rightStarted = right.startedAt ? new Date(right.startedAt).getTime() : 0
        const leftStarted = left.startedAt ? new Date(left.startedAt).getTime() : 0
        if (rightStarted !== leftStarted) return rightStarted - leftStarted

        return (left.label ?? '').localeCompare(right.label ?? '')
    }

    function emptyPassMessage(pass: ReviewProtocolPass): string {
        if ((pass.events?.length ?? 0) > 0) return 'No events recorded.'
        if (pass.isInherited) {
            return 'This inherited pass exposes the source run metadata even when no new events were recorded in the current job.'
        }
        if ((pass.outcome ?? '').toLowerCase() === 'failed') {
            return 'This pass failed before any protocol events were captured.'
        }

        return 'No events recorded.'
    }


    const isEventModalOpen = ref(false)

    async function openMergedModal(event: MergedEvent): Promise<void> {
        resetEventModalExpansionState()

        const protocolId = visibleTraceRows.value.find(row => row.id === event.id)?.protocolId ?? activePass.value?.id
        if (protocolId && !loadedProtocolIds.value.has(protocolId)) {
            activePassId.value = protocolId
            await ensureProtocolPassLoaded(protocolId)
            const refreshedEvent = buildEventRows(activePass.value)
                .find(row => row.id === event.id)?.merged
            selectedMergedEvent.value = refreshedEvent ?? event
            isEventModalOpen.value = true
            void scheduleModalPhaseGrouping()
            return
        }

        if (protocolId && activePassId.value !== protocolId) {
            activePassId.value = protocolId
        }

        selectedMergedEvent.value = event
        isEventModalOpen.value = true
        void scheduleModalPhaseGrouping()
    }

    async function openTimingInsight(insight: TimingInsight): Promise<void> {
        if (insight.protocolId && activePassId.value !== insight.protocolId) {
            activePassId.value = insight.protocolId
            await ensureProtocolPassLoaded(insight.protocolId)
        }

        const targetEvent = buildEventRows(activePass.value)
            .find(row => row.id === insight.eventId)?.merged

        if (targetEvent) {
            await openMergedModal(targetEvent)
        }
    }


    function sourceJobProtocolLink(sourceJobId: string) {
        return {
            name: 'job-protocol',
            params: { id: sourceJobId },
            query: route.query.clientId ? { clientId: route.query.clientId } : {},
        }
    }


    function buildDiffCacheKey(jobId: string, fileResultId: string): string {
        return `${jobId}::${fileResultId}`
    }

    async function loadFileDiff(jobId: string, fileResultId: string): Promise<void> {
        if (!jobId || !fileResultId) {
            fileDiff.value = null
            diffError.value = null
            return
        }

        const cacheKey = buildDiffCacheKey(jobId, fileResultId)
        const cached = diffCache.get(cacheKey)
        if (cached) {
            fileDiff.value = cached
            diffError.value = null
            diffLoading.value = false
            return
        }

        diffLoading.value = true
        diffError.value = null
        try {
            const { getAccessToken } = useSession()
            const token = getAccessToken()
            const headers: Record<string, string> = { Accept: 'application/json' }
            if (token) {
                headers.Authorization = `Bearer ${token}`
            }

            const res = await fetch(
                `${getActiveRuntime().apiBaseUrl}/reviewing/jobs/${jobId}/files/${fileResultId}/diff`,
                { headers },
            )
            if (!res.ok) {
                fileDiff.value = null
                diffError.value = `Diff unavailable (HTTP ${res.status})`
                return
            }

            const payload = (await res.json()) as FileDiffDto
            diffCache.set(cacheKey, payload)
            fileDiff.value = payload
            diffError.value = null
        } catch (error) {
            fileDiff.value = null
            diffError.value = `Diff unavailable: ${error instanceof Error ? error.message : 'unknown error'}`
        } finally {
            diffLoading.value = false
        }
    }

    function clearDiff(): void {
        detailTab.value = 'events'
        fileDiff.value = null
        diffError.value = null
    }

    return reactive({
        loading,
        error,
        activeTab,
        protocols,
        loadedProtocolIds,
        loadingProtocolIds,
        activePassId,
        selectedMergedEvent,
        expandedPhaseGroups,
        modalPhaseGroups,
        modalPhaseGroupsPending,
        reviewStatus,
        jobDetail,
        collapsedFolders,
        collapsedEventParents,
        selectedCommentPath,
        isSummaryModalOpen,
        focusedEventId,
        isTraceSearchCollapsed,
        traceFindingsOnly,
        traceFilters,
        detailTab,
        fileDiff,
        diffLoading,
        diffError,
        routeClientId,
        jobShortId,
        prReviewLink,
        backToReviewsLink,
        dismissingIds,
        dismissToast,
        globalSearchQuery,
        localSearchQuery,
        localSeverities,
        severityCounts,
        filteredCommentsForTree,
        filteredCommentsForDetail,
        normalizedTraceFilters,
        hasActiveTraceFilters,
        traceSearchToggleLabel,
        traceSearchToggleIcon,
        traceSuggestions,
        treeVisiblePasses,
        sidebarItems,
        commentSidebarItems,
        groupedReviewComments,
        activePassFinalComments,
        activePass,
        reviewTraceRows,
        activePassEventRows,
        visibleTraceRows,
        traceTimingInsights,
        totalInputTokens,
        totalOutputTokens,
        totalCachedInputTokens,
        totalEffectiveInputTokens,
        overallDuration,
        reviewModelDisplay,
        reviewStrategyDisplay,
        activePassReviewStrategyDisplay,
        activePassFileOutcome,
        activePassFollowUp,
        activePassRepeatedJudgment,
        activePassProRvPrefilter,
        reviewTemperatureDisplay,
        inheritedProtocolCount,
        activePassInheritance,
        protocolTokenBreakdown,
        protocolBreakdownConsistent,
        parsedInputResult,
        parsedOutputResult,
        selectedToolPhaseTimings,
        selectedAiCallSessionTurn,
        selectedAiCallProviderManagedNote,
        selectedCommentRelevanceInput,
        selectedCommentRelevanceOutput,
        selectedFinalGateInput,
        selectedFinalGateSummaryOutput,
        selectedFinalGateDecisionOutput,
        selectedVerificationInput,
        selectedVerificationOutput,
        selectedVerificationEvidenceOutput,
        selectedAgenticInvestigationOutput,
        isEventModalOpen,
        renderMarkdown,
        commentKey,
        dismissComment,
        toggleSeverity,
        toggleFolder,
        toggleEventParent,
        parseFilePath,
        traceAutocompleteValue,
        setTraceFilterValue,
        protocolHasVisibleTraceRows,
        clearTraceFilters,
        processEvents,
        buildEventRows,
        parentIterationLabel,
        isMergedEventProcessing,
        getPhaseTimings,
        getEventTimingPresentation,
        togglePhaseGroup,
        isPhaseGroupExpanded,
        isAgenticDegradedEvent,
        decodeHtmlEntities,
        renderMergedEventText,
        formatCommentRelevanceImplementation,
        formatCommentRelevanceCounts,
        formatFinalGateCounts,
        formatStringList,
        formatNamedCounts,
        commentLocation,
        severityVariant,
        formatCommentRelevanceAiUsage,
        formatFinalGateProvenance,
        formatFinalGateEvidence,
        formatVerificationProCursorStatus,
        formatEvidenceAttempts,
        formatEvidenceItems,
        formatAgenticToolUsage,
        formatAgenticInvestigationStatus,
        agenticInvestigationCandidateCount,
        agenticInvestigationEvidenceCount,
        openMergedModal,
        openTimingInsight,
        statusIconClass,
        statusBadgeClass,
        formatDate,
        formatReviewStrategy,
        formatFileOutcomeStatus,
        formatFollowUpStatus,
        formatRepeatedJudgmentStatus,
        formatTokens,
        formatCacheStatus,
        shortGuid,
        sourceJobProtocolLink,
        formatTemperature,
        formatDurationMs,
        hasToolTiming,
        hasEventTokens,
        hasEventError,
        formatDurationWithMs,
        formatTimingAvailability,
        formatToolOutcome,
        formatPhaseCountSummary,
        formatPhaseTitle,
        formatPhaseDuration,
        formatToolPhaseGroupDuration,
        slowestToolDurationLabel,
        computePassDuration,
        kindBadgeClass,
        emptyPassMessage,
        loadFileDiff,
        clearDiff,
    })
}

export type JobProtocolViewModel = ReturnType<typeof useJobProtocolViewModel>
