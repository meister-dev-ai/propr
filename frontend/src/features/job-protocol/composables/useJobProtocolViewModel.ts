// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { computed, nextTick, onMounted, onUnmounted, reactive, ref, shallowRef, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import type { LocationQueryRaw } from 'vue-router'
import { createAdminClient } from '@/services/api'
import { createDismissal } from '@/services/findingDismissalsService'
import { restartJob } from '@/services/jobsService'
import { formatTriageDecision } from './formatTriageDecision'
import { originLabel, passKindLabel } from './passLabels'
import { parseUnionContributions, parseUnionPassIndex, type UnionPassContribution } from './multiPassUnionContribution'
import { useFileDiff } from './useFileDiff'
import { useTokenTotals } from './useTokenTotals'
import { useTraceSearch } from './useTraceSearch'
import { parseTraceChipParam, serializeTraceChips } from './traceQuickFilters'
import type {
    AgenticInvestigationOutputRecord,
    CommentGroupComment,
    CommentRelevanceEventDetails,
    CommentRelevanceOutputRecord,
    CommentSidebarItem,
    CommentTreeNode,
    EventDisplayRow,
    EventTimingPresentation,
    FileGroup,
    FinalGateDecisionRecord,
    FinalGateSummaryRecord,
    JobDetail,
    MergedEvent,
    PassTab,
    PendingToolRow,
    ProtocolEventDto,
    ProtocolEventPhaseTimingDto,
    ProtocolFileOutcome,
    ProtocolFollowUp,
    ProtocolInheritance,
    ProtocolRepeatedJudgment,
    ProtocolReviewComment,
    ProtocolSidebarItem,
    ProtocolTreeNode,
    ReviewCommentRecord,
    ReviewJobProtocolPassResponse,
    ReviewJobResultDto,
    ReviewProtocolPass,
    TimingInsight,
    ToolPhaseGroup,
    TriageDecisionEventDetails,
    TriageDecisionPresentation,
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

function commentKey(comment: CommentGroupComment): string {
    return `${comment.filePath ?? comment.file_path ?? ''}:${comment.lineNumber ?? comment.line_number ?? 0}:${String(comment.message ?? '').slice(0, 80)}`
}

function parseFilePath(label: string | null | undefined) {
    if (!label) return { filename: 'Pass', directory: '' }
    const path = label.replaceAll('\\', '/')
    const parts = path.split('/')
    const filename = parts.pop() || path
    const directory = parts.join('/') || ''
    return { filename, directory }
}

function commentOriginLabel(comment: {
    originPassKind?: string | null
    originPassIndex?: number | null
    originPassLens?: string | null
}): string | null {
    return originLabel(comment.originPassKind, comment.originPassIndex, comment.originPassLens)
}

function parentIterationLabel(name: string): string {
    const match = /^ai_call_iter_(\d+)$/.exec(name)
    if (!match) {
        return 'Child of AI turn'
    }

    return `AI ${match[1]}`
}

function phaseGroupKey(phase: ProtocolEventPhaseTimingDto): string {
    return (phase.name ?? phase.displayName ?? 'unnamed-phase').trim().toLowerCase() || 'unnamed-phase'
}

export function useJobProtocolViewModel() {
    const route = useRoute()
    const router = useRouter()

    const loading = ref(false)
    const error = ref('')
    const activeTab = ref<'summary' | 'traces' | 'tokens'>('summary')
    const protocols = shallowRef<ReviewProtocolPass[]>([])
    const loadedProtocolIds = ref<Set<string>>(new Set())
    const loadingProtocolIds = ref<Set<string>>(new Set())
    // Bumped to cancel an in-flight Traces-tab background backfill (tab leave, unmount, job change).
    let traceBackfillToken = 0
    const activePassId = ref<string | null>(null)
    const selectedMergedEvent = ref<MergedEvent | null>(null)
    const expandedPhaseGroups = ref<Set<string>>(new Set())
    const modalPhaseGroups = shallowRef<ToolPhaseGroup[]>([])
    const modalPhaseGroupsPending = ref(false)
    const reviewStatus = ref<ReviewJobResultDto | null>(null)
    const jobDetail = ref<JobDetail | null>(null)
    const jobStatus = ref<string | null>(null)
    const restarting = ref(false)
    const collapsedFolders = ref<Set<string>>(new Set())
    const expandedEventParents = ref<Set<string>>(new Set())
    const selectedCommentPath = ref<string | null>(null)
    const isSummaryModalOpen = ref(false)
    const focusedEventId = ref<string | null>(null)
    const detailTab = ref<'events' | 'diff'>('events')

    const {
        fileDiff,
        diffLoading,
        diffError,
        loadFileDiff,
        resetDiff,
    } = useFileDiff()

    const {
        isTraceSearchCollapsed,
        traceFindingsOnly,
        activeTraceChipIds,
        traceFilters,
        normalizedTraceFilters,
        hasActiveTraceFilters,
        traceSearchToggleLabel,
        traceSearchToggleIcon,
        traceSuggestions,
        traceAutocompleteValue,
        traceChips,
        traceChipGroups,
        setTraceFilterValue,
        buildTraceSearchableRow,
        matchesTraceFilters,
        protocolHasVisibleTraceRows,
        toggleTraceChip,
        setActiveTraceChips,
        clearTraceChips,
        clearTraceFilters,
    } = useTraceSearch(protocols)

    const {
        totalInputTokens,
        totalOutputTokens,
        totalCachedInputTokens,
        totalEffectiveInputTokens,
        protocolTokenBreakdown,
        protocolBreakdownConsistent,
    } = useTokenTotals(protocols, jobDetail)

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

    async function dismissComment(comment: CommentGroupComment) {
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

    const canRestart = computed(() => jobStatus.value === 'failed')

    async function restart() {
        const jobId = route.params.id as string
        if (!jobId || restarting.value || !canRestart.value) {
            return
        }

        restarting.value = true
        try {
            await restartJob(jobId)
            dismissToast.value = { message: 'Review restarted.', isError: false }
            await loadProtocol(true)
        } catch (err) {
            const message = err instanceof Error ? err.message : 'Failed to restart review.'
            dismissToast.value = { message, isError: true }
        } finally {
            restarting.value = false
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

    const filteredCommentsForTree = computed<ProtocolReviewComment[]>(() => {
        const allComments = (reviewStatus.value?.result?.comments ?? []) as ProtocolReviewComment[]
        const qLabel = globalSearchQuery.value.trim().toLowerCase()

        if (!qLabel) return allComments

        return allComments.filter(comment => {
            const filePath = ((comment.filePath ?? comment.file_path) ?? '').toLowerCase()
            return filePath.includes(qLabel)
        })
    })

    const filteredCommentsForDetail = computed<ProtocolReviewComment[]>(() => {
        const allComments = (reviewStatus.value?.result?.comments ?? []) as ProtocolReviewComment[]
        const query = localSearchQuery.value.trim().toLowerCase()
        const severities = localSeverities.value

        return allComments.filter(comment => {
            const matchesSeverity = severities.size === 0 || severities.has((comment.severity ?? '').toLowerCase())
            if (!matchesSeverity) return false
            if (!query) return true

            const message = (comment.message ?? '').toLowerCase()
            const filePath = ((comment.filePath ?? comment.file_path) ?? '').toLowerCase()
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
        const next = new Set(expandedEventParents.value)
        if (next.has(eventId)) {
            next.delete(eventId)
        } else {
            next.add(eventId)
        }

        expandedEventParents.value = next
    }

    function protocolHasFinalFindings(protocol: ReviewProtocolPass): boolean {
        return (protocol.finalComments?.length ?? 0) > 0
    }

    function passTokenTotal(protocol: ReviewProtocolPass): number {
        return (protocol.totalInputTokens ?? 0) + (protocol.totalOutputTokens ?? 0)
    }

    // PR-level passes belong to no single changed file (synthesis, the PR-wide
    // review, the posting/finalization bookkeeping pass). They group under the
    // synthetic "PR-level" file rather than a file path.
    const PR_LEVEL_LABELS = new Set(['synthesis', 'pr-wide-review', 'finalization', 'posting'])

    function isPrLevelPass(protocol: ReviewProtocolPass): boolean {
        if (protocol.fileResultId) return false
        const label = (protocol.label ?? '').trim().toLowerCase()
        return PR_LEVEL_LABELS.has(label)
    }

    // Bookkeeping passes carry no AI cost and no findings (e.g. "posting").
    // They are hidden from the selectable file/pass tree per the spec, but are
    // left untouched in the underlying protocols/trace-row pipelines. A PR-level
    // pass that has trace events or a failed outcome is NOT bookkeeping noise
    // (e.g. a synthesis pass that errored before any AI call) and stays visible.
    function isHiddenBookkeepingPass(protocol: ReviewProtocolPass): boolean {
        if (!isPrLevelPass(protocol)) return false
        const hasTokens = passTokenTotal(protocol) > 0
        const hasFindings = protocolHasFinalFindings(protocol)
        const hasEvents = (protocol.events?.length ?? 0) > 0
        const failed = (protocol.outcome ?? '').toLowerCase() === 'failed'
        return !hasTokens && !hasFindings && !hasEvents && !failed
    }

    function fileKeyForPass(protocol: ReviewProtocolPass): string {
        return isPrLevelPass(protocol) ? '' : (protocol.label ?? '')
    }

    function buildPassTab(protocol: ReviewProtocolPass): PassTab {
        const reason = (protocol.reason ?? '').trim()
        return {
            id: protocol.id ?? '',
            label: passKindLabel(protocol.passKind, protocol.label, protocol.reason),
            reason: reason.length > 0 ? reason : null,
            tokens: passTokenTotal(protocol),
            findingCount: protocol.finalComments?.length ?? 0,
            failed: (protocol.outcome ?? '').toLowerCase() === 'failed',
        }
    }

    function passChronologicalCompare(left: ReviewProtocolPass, right: ReviewProtocolPass): number {
        const leftStarted = left.startedAt ? new Date(left.startedAt).getTime() : 0
        const rightStarted = right.startedAt ? new Date(right.startedAt).getTime() : 0
        if (leftStarted !== rightStarted) return leftStarted - rightStarted
        // Baseline before augmentation when timestamps tie.
        const order = (kind: string | null | undefined) => (kind === 'Baseline' ? 0 : 1)
        return order(left.passKind) - order(right.passKind)
    }

    const sidebarItems = computed<ProtocolSidebarItem[]>(() => {
        const root: ProtocolTreeNode = { name: '', path: '', children: {}, protocols: [] }

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

        const items: ProtocolSidebarItem[] = []
        const flatten = (node: ProtocolTreeNode, depth: number) => {
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
            .filter((item): item is Extract<ProtocolSidebarItem, { type: 'pass' }> => item.type === 'pass')
            .map(item => item.protocol),
    )

    // File → passes selector model. Groups the visible passes by file PATH (not
    // fileResultId — augmentation passes have a null fileResultId but share the
    // file-path label with their base pass). Job-wide passes collect under the
    // synthetic "PR-level" group. Empty / 0-token bookkeeping passes are hidden.
    const fileGroups = computed<FileGroup[]>(() => {
        const order: string[] = []
        const byKey = new Map<string, ReviewProtocolPass[]>()

        for (const protocol of treeVisiblePasses.value) {
            if (isHiddenBookkeepingPass(protocol)) {
                continue
            }

            const key = fileKeyForPass(protocol)
            if (!byKey.has(key)) {
                byKey.set(key, [])
                order.push(key)
            }
            byKey.get(key)!.push(protocol)
        }

        // PR-level last; files alphabetical by path.
        order.sort((left, right) => {
            if (left === '') return 1
            if (right === '') return -1
            return left.localeCompare(right)
        })

        return order.map(key => {
            const passes = [...(byKey.get(key) ?? [])].sort(passChronologicalCompare)
            const isPrLevel = key === ''
            const { filename, directory } = parseFilePath(key)
            const totalTokens = passes.reduce((sum, pass) => sum + passTokenTotal(pass), 0)
            const totalFindings = passes.reduce((sum, pass) => sum + (pass.finalComments?.length ?? 0), 0)

            return {
                path: key,
                label: isPrLevel ? 'PR-level' : key,
                isPrLevel,
                directory,
                filename: isPrLevel ? 'PR-level' : filename,
                passes,
                tabs: passes.map(buildPassTab),
                totalTokens,
                totalFindings,
            }
        })
    })

    const activeFile = computed<FileGroup | null>(() => {
        const groups = fileGroups.value
        if (groups.length === 0) return null
        const fromActive = groups.find(group => group.passes.some(pass => pass.id === activePassId.value))
        return fromActive ?? groups[0]
    })

    const passesForActiveFile = computed<ReviewProtocolPass[]>(() => activeFile.value?.passes ?? [])

    const activeFilePassTabs = computed<PassTab[]>(() => activeFile.value?.tabs ?? [])

    function selectFile(filePath: string): void {
        const group = fileGroups.value.find(candidate => candidate.path === filePath)
        if (!group || group.passes.length === 0) return
        const nextId = group.passes[0]?.id ?? null
        if (nextId && activePassId.value !== nextId) {
            activePassId.value = nextId
        }
    }

    // Flat traversal order across every file group's passes — drives the prev/next pass stepper. Setting
    // activePassId is enough; activeFile is derived from it, so stepping into another file switches the selector.
    const orderedPassIds = computed<string[]>(() => fileGroups.value.flatMap(group => group.tabs.map(tab => tab.id)))
    const activePassFlatIndex = computed<number>(() => orderedPassIds.value.indexOf(activePassId.value ?? ''))
    const previousPassId = computed<string | null>(() => {
        const index = activePassFlatIndex.value
        return index > 0 ? orderedPassIds.value[index - 1] ?? null : null
    })
    const nextPassId = computed<string | null>(() => {
        const index = activePassFlatIndex.value
        return index >= 0 && index < orderedPassIds.value.length - 1 ? orderedPassIds.value[index + 1] ?? null : null
    })

    function goToPass(passId: string | null): void {
        if (passId && activePassId.value !== passId) {
            activePassId.value = passId
        }
    }

    // Pass count after the active trace filters and chips are applied; the stat
    // strip "Visible Passes" counter reflects this rather than the raw total.
    const visiblePassCount = computed<number>(() =>
        hasActiveTraceFilters.value || traceFindingsOnly.value
            ? treeVisiblePasses.value.length
            : protocols.value.length,
    )

    const commentSidebarItems = computed<CommentSidebarItem[]>(() => {
        const comments = filteredCommentsForTree.value
        const root: CommentTreeNode = { name: '', path: '', children: {}, files: {} }

        comments.forEach(comment => {
            const path = comment.filePath || comment.file_path || ''
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

        const items: CommentSidebarItem[] = []
        const flatten = (node: CommentTreeNode, depth: number) => {
            const sortedFolderKeys = Object.keys(node.children ?? {}).sort((left, right) => {
                if (left === './') return -1
                if (right === './') return 1
                return left.localeCompare(right)
            })

            const sortedFileKeys = Object.keys(node.files ?? {}).sort((left, right) => left.localeCompare(right))

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
                const path = comment.filePath || comment.file_path || ''
                return path === selectedCommentPath.value || path.startsWith(`${selectedCommentPath.value}/`)
            })
        }

        const groups: Record<string, ProtocolReviewComment[]> = {}

        comments.forEach(comment => {
            const path = comment.filePath || comment.file_path || ''
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
                const pathA = left.filePath || left.file_path || ''
                const pathB = right.filePath || right.file_path || ''
                if (pathA !== pathB) return pathA.localeCompare(pathB)
                return (left.lineNumber || left.line_number || 0) - (right.lineNumber || right.line_number || 0)
            }),
        }))
    })

    // Aggregate findings view, grouped by FILE path (mirrors the selector) with
    // each finding's coarse origin label resolved for the origin badge. Findings
    // with an unknown origin carry a null label so the UI renders no badge.
    const aggregateFindingsByFile = computed<Array<{ directory: string; comments: ProtocolReviewComment[] }>>(() => {
        const comments = filteredCommentsForDetail.value
        const groups: Record<string, ProtocolReviewComment[]> = {}

        comments.forEach(comment => {
            const path = comment.filePath || comment.file_path || 'PR-level'
            if (!groups[path]) groups[path] = []
            groups[path].push(comment)
        })

        const sortedKeys = Object.keys(groups).sort((left, right) => {
            if (left === 'PR-level') return 1
            if (right === 'PR-level') return -1
            return left.localeCompare(right)
        })

        const severityRank: Record<string, number> = { error: 0, warning: 1, info: 2, suggestion: 3 }
        return sortedKeys.map(path => ({
            directory: path,
            comments: [...groups[path]].sort((left, right) => {
                const sevA = severityRank[(left.severity ?? '').toLowerCase()] ?? 9
                const sevB = severityRank[(right.severity ?? '').toLowerCase()] ?? 9
                if (sevA !== sevB) return sevA - sevB
                return (left.lineNumber || left.line_number || 0) - (right.lineNumber || right.line_number || 0)
            }),
        }))
    })

    // Deep-link from an aggregate-view origin badge to the (file, pass) trace
    // that produced the finding. Finding provenance is COARSE. Match on the
    // passKind FAMILY, not the rendered label: a "Baseline" origin lands on the
    // baseline pass in the file; anything else falls back to the file's first pass.
    function selectFindingOrigin(comment: CommentGroupComment): void {
        const filePath = comment.filePath || comment.file_path || ''
        const targetGroup = fileGroups.value.find(group => group.path === filePath)
            ?? fileGroups.value.find(group => group.isPrLevel)
        if (!targetGroup || targetGroup.passes.length === 0) return

        const origin = comment.originPassKind

        let matchByFamily: ReviewProtocolPass | undefined
        if (origin === 'Baseline') {
            matchByFamily = targetGroup.passes.find(pass => pass.passKind === 'Baseline')
        }

        const target = matchByFamily ?? targetGroup.passes[0]

        if (target?.id) {
            activePassId.value = target.id
        }
        activeTab.value = 'traces'
    }

    const activePass = computed<ReviewProtocolPass | null>(() => {
        if (!protocols.value.length || activeTab.value === 'summary') return null
        return protocols.value.find(protocol => protocol.id === activePassId.value) ?? protocols.value[0]
    })

    const activePassFinalComments = computed<ReviewCommentRecord[]>(() => activePass.value?.finalComments ?? [])

    // The `multi_pass_union_completed` event lives on a file's BASELINE pass. Within a file group the
    // baseline is the pass that is not a union resample.
    function findBaselinePassForFile(passes: ReviewProtocolPass[]): ReviewProtocolPass | null {
        return passes.find(pass => pass.passKind !== 'MultiPassUnion') ?? null
    }

    // Per-pass contribution counts for the active file, parsed from its baseline pass's union-completion
    // event. Keyed by 1-based pass index (baseline = Pass 1, additional passes 2..k).
    const multiPassUnionContributions = computed<Map<number, UnionPassContribution>>(() => {
        const baselinePass = findBaselinePassForFile(passesForActiveFile.value)
        const event = baselinePass?.events?.find(candidate => (candidate.name ?? '') === 'multi_pass_union_completed')
        return parseUnionContributions(event?.outputSummary)
    })

    // Contribution line for the active pass when it is a union resample: how many findings that pass
    // contributed to the file's union. A count of 0 is meaningful ("Pass 2 caught nothing extra"), so it
    // is surfaced too. Null when the active pass is not a resample or the completion event is unavailable.
    const activePassUnionContribution = computed<{ passIndex: number; catchCount: number; model: string | null } | null>(() => {
        const pass = activePass.value
        if (!pass || pass.passKind !== 'MultiPassUnion') {
            return null
        }

        const passIndex = parseUnionPassIndex(pass.reason)
        if (passIndex === null) {
            return null
        }

        const contribution = multiPassUnionContributions.value.get(passIndex)
        return contribution ? { passIndex, ...contribution } : null
    })

    // The union-completion event is on the baseline pass, whose events are lazily loaded. When a resample
    // pass is active, make sure its file's baseline pass is loaded so the contribution line can resolve.
    watch([activePass, passesForActiveFile], () => {
        if (activePass.value?.passKind !== 'MultiPassUnion') {
            return
        }

        const baselinePass = findBaselinePassForFile(passesForActiveFile.value)
        if (baselinePass?.id) {
            void ensureProtocolPassLoaded(baselinePass.id)
        }
    }, { flush: 'post' })

    // "You are here" metadata for the breadcrumb + reason line.
    const activePassLabel = computed<string>(() =>
        activePass.value ? passKindLabel(activePass.value.passKind, activePass.value.label, activePass.value.reason) : '',
    )

    const activeFileDisplayPath = computed<string>(() => {
        const group = activeFile.value
        if (!group) return ''
        return group.isPrLevel ? 'PR-level' : group.path
    })

    const activePassReason = computed<string | null>(() => {
        const reason = (activePass.value?.reason ?? '').trim()
        return reason.length > 0 ? reason : null
    })

    const activePassFailed = computed<boolean>(() => (activePass.value?.outcome ?? '').toLowerCase() === 'failed')

    watch(activePassId, protocolId => {
        if (!protocolId) {
            return
        }

        void ensureProtocolPassLoaded(protocolId)
    }, { flush: 'post' })

    watch(activePassId, () => {
        clearDiff()
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

    // Number of distinct phase groups without materializing groups or their
    // (expensive) shared-value summaries. Used by hot callers that only need the count.
    function countToolPhaseGroups(phases: ProtocolEventPhaseTimingDto[]): number {
        const keys = new Set<string>()
        for (const phase of phases) {
            keys.add(phaseGroupKey(phase))
        }
        return keys.size
    }

    function mergeIntoPhaseGroup(existing: ToolPhaseGroup, phase: ProtocolEventPhaseTimingDto): void {
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
    }

    function createPhaseGroup(key: string, phase: ProtocolEventPhaseTimingDto): ToolPhaseGroup {
        return {
            key,
            title: phase.displayName ?? phase.name ?? 'Unnamed phase',
            count: 1,
            totalDurationMs: getPhaseTimingDurationMs(phase),
            availability: phase.availability ?? null,
            outcome: phase.outcome ?? null,
            startedAt: phase.startedAt ?? null,
            completedAt: phase.completedAt ?? null,
            summary: phase.summary?.trim() ?? null,
            phases: [phase],
        }
    }

    function buildToolPhaseGroups(phases: ProtocolEventPhaseTimingDto[]): ToolPhaseGroup[] {
        const groups = new Map<string, ToolPhaseGroup>()

        for (const phase of phases) {
            const key = phaseGroupKey(phase)
            const existing = groups.get(key)

            if (existing) {
                mergeIntoPhaseGroup(existing, phase)
                continue
            }

            groups.set(key, createPhaseGroup(key, phase))
        }

        // Compute the shared-value summaries once per group after all phases are
        // collected. Doing this inside the loop above is O(n²) per group and was
        // the dominant cost when rendering large traces.
        for (const group of groups.values()) {
            if (group.count === 1) {
                continue
            }

            group.availability = summarizeSharedValue(group.phases.map(entry => entry.availability))
            group.outcome = summarizeSharedValue(group.phases.map(entry => entry.outcome))
            group.summary = summarizePhaseGroupSummary(group.phases)
        }

        return [...groups.values()]
    }

    function computeEventTimingSummary(event: ProtocolEventDto): string | null {
        if (event.durationMs != null) {
            return formatDurationWithMs(event.durationMs)
        }

        if (event.startedAt && event.completedAt) {
            const duration = new Date(event.completedAt).getTime() - new Date(event.startedAt).getTime()
            return !Number.isNaN(duration) && duration >= 0 ? formatDurationWithMs(duration) : 'Timing recorded'
        }

        return hasToolTiming(event) ? 'Timing recorded' : null
    }

    function computeEventTimingDetailParts(event: ProtocolEventDto, phaseTimings: ProtocolEventPhaseTimingDto[], phaseGroupCount: number): string[] {
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

        return parts
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
        const phaseGroupCount = phaseTimings.length > 0 ? countToolPhaseGroups(phaseTimings) : 0
        const summary = computeEventTimingSummary(event)
        const parts = computeEventTimingDetailParts(event, phaseTimings, phaseGroupCount)

        const presentation: EventTimingPresentation = {
            phaseTimings,
            phaseGroupCount,
            summary,
            detail: parts.length > 0 ? parts.join(' · ') : null,
        }

        eventTimingPresentationCache.set(event, presentation)
        return presentation
    }

    // Cache keyed by (protocol object, collapsed-set identity, filter snapshot) so unchanged passes skip full recompute.
    // The filter snapshot covers both the text filters and the active quick-filter chips so chip toggles invalidate it.
    const eventRowsCache = new WeakMap<ReviewProtocolPass, { collapseKey: Set<string>, filterKey: string, rows: EventDisplayRow[] }>()

    function traceFilterCacheKey(): string {
        return `${JSON.stringify(normalizedTraceFilters.value)}|${serializeTraceChips(activeTraceChipIds.value) ?? ''}`
    }

    // Groups tool-call events under the primary AI-turn event they belong to. A
    // run of tool calls with no primary AI-turn event yet (or one that trails the
    // rest of the log with none following) has nothing to attach to and is
    // dropped from the grouping — those calls still render, just as standalone
    // rows via the childEventIds membership check in buildEventRows.
    function groupToolCallsByAiTurn(mergedEvents: MergedEvent[]): { childEventsByParentId: Map<string, MergedEvent[]>; childEventIds: Set<string> } {
        const state: AiTurnGroupingState = {
            activeAiTurnParentId: null,
            pendingToolRows: [],
            childEventsByParentId: new Map<string, MergedEvent[]>(),
        }

        for (const merged of mergedEvents) {
            routeMergedEventForAiTurnGrouping(state, merged)
        }

        const childEventIds = new Set<string>()
        for (const children of state.childEventsByParentId.values()) {
            for (const child of children) {
                childEventIds.add(child.id)
            }
        }

        return { childEventsByParentId: state.childEventsByParentId, childEventIds }
    }

    // Routes a single merged event into the AI-turn grouping state. Pulled out of groupToolCallsByAiTurn
    // so the orchestrator stays linear; this helper owns the four-way isPrimaryAiTurn / isToolCall / parentId
    // decision tree and mutates the shared state in place.
    function routeMergedEventForAiTurnGrouping(state: AiTurnGroupingState, merged: MergedEvent): void {
        const isToolCall = (merged.callDetails.kind ?? '').toLowerCase() === 'toolcall'

        if (isPrimaryAiTurnEvent(merged)) {
            state.activeAiTurnParentId = merged.id

            if (state.pendingToolRows.length > 0) {
                state.childEventsByParentId.set(
                    merged.id,
                    state.pendingToolRows.map(pendingToolRow => pendingToolRow.merged),
                )
            }

            state.pendingToolRows = []
            return
        }

        const parentId = isToolCall ? state.activeAiTurnParentId : null

        if (isToolCall && !parentId) {
            state.pendingToolRows.push({ merged })
            return
        }

        if (isToolCall && parentId) {
            const existingChildren = state.childEventsByParentId.get(parentId) ?? []
            existingChildren.push(merged)
            state.childEventsByParentId.set(parentId, existingChildren)
            return
        }

        state.pendingToolRows = []
    }

    interface AiTurnGroupingState {
        activeAiTurnParentId: string | null
        pendingToolRows: PendingToolRow[]
        childEventsByParentId: Map<string, MergedEvent[]>
    }

    function buildEventRows(protocol: ReviewProtocolPass | null | undefined): EventDisplayRow[] {
        if (protocol) {
            const expanded = expandedEventParents.value
            const filterKey = traceFilterCacheKey()
            const cached = eventRowsCache.get(protocol)
            if (cached?.collapseKey === expanded && cached?.filterKey === filterKey) {
                return cached.rows
            }
        }
        const mergedEvents = processEvents(protocol?.events)
        const filteredMergedEvents = protocol
            ? mergedEvents.filter(merged => matchesTraceFilters(protocol, merged.callDetails))
            : mergedEvents
        const rows: EventDisplayRow[] = []
        const { childEventsByParentId, childEventIds } = groupToolCallsByAiTurn(filteredMergedEvents)

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

        for (const merged of filteredMergedEvents) {
            if (isPrimaryAiTurnEvent(merged)) {
                const children = childEventsByParentId.get(merged.id) ?? []
                // AI-turn parents collapse their tool-call children by default; the set tracks the ones the user
                // has explicitly expanded. While a trace search is active everything expands so a matching child
                // is never hidden inside a collapsed parent.
                const isExpanded = hasActiveTraceFilters.value || expandedEventParents.value.has(merged.id)
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
                collapseKey: expandedEventParents.value,
                filterKey: traceFilterCacheKey(),
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
    const activePassFileOutcome = computed<ProtocolFileOutcome | null>(() => activePass.value?.fileOutcome ?? null)
    const activePassFollowUp = computed<ProtocolFollowUp | null>(() => activePass.value?.followUp ?? null)
    const activePassRepeatedJudgment = computed<ProtocolRepeatedJudgment | null>(() => activePass.value?.repeatedJudgment ?? null)
    const activePassProRvPrefilter = computed(() => activePass.value?.proRvPrefilter ?? null)
    const reviewTemperatureDisplay = computed(() => formatTemperature(jobDetail.value?.reviewTemperature))
    const inheritedProtocolCount = computed(() => protocols.value.filter(protocol => protocol.isInherited).length)
    const activePassInheritance = computed<ProtocolInheritance | null>(() => activePass.value?.inheritance ?? null)

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

    const selectedTriageDecision = computed<TriageDecisionPresentation | null>(() => {
        if (selectedMergedEvent.value?.callDetails.name !== 'triage_decision') return null
        return isPlainObject(parsedInputResult.value)
            ? formatTriageDecision(parsedInputResult.value as TriageDecisionEventDetails)
            : null
    })

    const parsedOutputResult = computed(() => {
        if (!selectedMergedEvent.value?.resultDetails?.outputSummary) return null
        try {
            const parsed = JSON.parse(selectedMergedEvent.value.resultDetails.outputSummary)

            if (parsed && typeof parsed === 'object' && parsed.confidence_evaluations) {
                const evaluations = parsed.confidence_evaluations
                if (Array.isArray(evaluations)) {
                    parsed.confidence_evaluations = evaluations.map((evaluation: unknown) => {
                        if (typeof evaluation === 'object' && evaluation !== null) {
                            const record = evaluation as Record<string, unknown>
                            return {
                                concern: record.concern || record.category || record.metric || record.type || record.name || 'Unknown',
                                confidence: record.confidence || record.level || record.score || 'N/A',
                            }
                        }
                        return { concern: 'Unknown', confidence: String(evaluation) }
                    })
                } else if (typeof evaluations === 'object') {
                    parsed.confidence_evaluations = Object.entries(evaluations as Record<string, unknown>).map(([key, value]) => ({
                        concern: key,
                        confidence: typeof value === 'object' && value !== null ? ((value as Record<string, unknown>).confidence || (value as Record<string, unknown>).level || (value as Record<string, unknown>).score || 'N/A') : value,
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

            if (event.kind === 'aiCall') {
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
    // Guards against overlapping loads: on a heavy review the protocol fetch can take longer than the
    // 3 s poll cadence, so without this a new tick would stack a second request on top of the unfinished
    // one (they pile up on the browser's per-host connection pool). A tick that fires while a previous
    // load is still running is skipped. Always reset in the finally below so it can never wedge.
    let loadInFlight = false

    function resetProtocolState() {
        error.value = ''
        protocols.value = []
        loadedProtocolIds.value = new Set()
        loadingProtocolIds.value = new Set()
        traceBackfillToken++
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

    // Incremental refresh: a completed pass's events are immutable, so keep the
    // already-loaded body (reusing the same object so eventRowsCache stays warm)
    // instead of dropping it and re-downloading the full pass on every poll.
    // Passes that are still processing are dropped from the loaded set so the
    // active one keeps re-fetching and shows newly recorded events.
    function reconcileProtocolOverview(
        sortedOverview: ReviewProtocolPass[],
        existingById: Map<string, ReviewProtocolPass>,
        previouslyLoadedIds: Set<string>,
    ): { normalizedProtocols: ReviewProtocolPass[]; nextLoaded: Set<string> } {
        const nextLoaded = new Set<string>()
        const normalizedProtocols = sortedOverview.map(overview => {
            const existing = overview.id ? existingById.get(overview.id) : undefined
            const wasLoaded = !!overview.id && previouslyLoadedIds.has(overview.id)
            // Preserve the loaded body only while BOTH the cached copy and the fresh
            // overview agree the pass is complete (a restart can re-open a completed id).
            if (existing && wasLoaded && existing.completedAt && overview.completedAt) {
                nextLoaded.add(overview.id as string)
                // Refresh pass-level scalars (outcome, token totals, finalComments, …)
                // from the overview while keeping the immutable loaded event bodies, and
                // keep the same object instance so eventRowsCache stays warm.
                const loadedEvents = existing.events
                Object.assign(existing, overview)
                existing.events = loadedEvents
                return existing
            }
            return overview
        })

        return { normalizedProtocols, nextLoaded }
    }

    // Resolve the active pass from the URL. `pass` (the new param) wins, then the
    // legacy `protocolId`, then `file` (first pass of the named file), then the
    // current/first pass. Invalid ids fall back without crashing.
    function resolveProtocolIdToLoad(
        normalizedProtocols: ReviewProtocolPass[],
        routeSelection: { routePassId: string | null; routeProtocolId: string | null; routeFile: string | null },
        currentActivePassId: string | null,
    ): string | undefined {
        const { routePassId, routeProtocolId, routeFile } = routeSelection
        const passFromFile = routeFile
            ? normalizedProtocols.find(protocol => fileKeyForPass(protocol) === routeFile)?.id ?? null
            : null

        if (routePassId && normalizedProtocols.some(protocol => protocol.id === routePassId)) {
            return routePassId
        }

        if (routeProtocolId && normalizedProtocols.some(protocol => protocol.id === routeProtocolId)) {
            return routeProtocolId
        }

        if (passFromFile) {
            return passFromFile
        }

        if (currentActivePassId && normalizedProtocols.some(protocol => protocol.id === currentActivePassId)) {
            return currentActivePassId
        }

        return normalizedProtocols[0]?.id
    }

    async function activateProtocolPass(protocolIdToLoad: string | undefined): Promise<void> {
        if (!protocolIdToLoad) {
            return
        }

        if (activePassId.value !== protocolIdToLoad) {
            activePassId.value = protocolIdToLoad
        }

        await ensureProtocolPassLoaded(protocolIdToLoad)
        await focusRouteEventIfRequested()
    }

    // Drive the poll off the JOB's lifecycle status, not per-protocol completedAt.
    // A 'pending' state must keep polling because startup recovery flips
    // Processing→Pending→Processing, and once reconcile stamps orphaned protocols the old
    // `.some(!completedAt)` heuristic would wrongly stop an active job (or run forever on a
    // completed job that still carries a dangling protocol). The job status is authoritative.
    // Arm while non-terminal; tear down ONLY on a CONFIRMED terminal status — an
    // undefined/error status (e.g. a transient job-detail fetch miss on a poll tick, since
    // the client returns { data: undefined } rather than throwing) must NOT stop an active
    // poll, or one network blip would permanently freeze the live view.
    function updatePollingLifecycle(jobLifecycleStatus: string | null | undefined): void {
        const isProcessing = jobLifecycleStatus === 'processing' || jobLifecycleStatus === 'pending'
        const isTerminal = jobLifecycleStatus === 'completed'
            || jobLifecycleStatus === 'failed'
            || jobLifecycleStatus === 'cancelled'
        if (isProcessing && !pollInterval) {
            pollInterval = setInterval(() => {
                void loadProtocol(false)
            }, 3000)
        } else if (isTerminal && pollInterval) {
            clearInterval(pollInterval)
            pollInterval = null
        }
    }

    async function loadProtocol(showLoading = false) {
        // Skip a background POLL tick if a load is already running (on a heavy review the fetch can take
        // longer than the 3 s cadence, so unguarded ticks stack). Explicit loads (showLoading=true: mount,
        // route change, restart) always proceed so navigating to another job is never dropped. The flag is
        // reset in the finally below, so it can never wedge.
        if (loadInFlight && !showLoading) return
        loadInFlight = true
        if (showLoading) loading.value = true
        try {
            const jobId = route.params.id as string
            const [protocolRes, resultRes, detailRes] = await Promise.all([
                createAdminClient().GET('/jobs/{id}/protocol', { params: { path: { id: jobId }, query: { includeEvents: false } } }),
                createAdminClient().GET('/jobs/{id}/result', { params: { path: { id: jobId } } }),
                createAdminClient().GET('/jobs/{id}', { params: { path: { id: jobId } } }),
            ])

            const data = protocolRes.data as ReviewProtocolPass[] | undefined
            const fetchError = protocolRes.error

            if (fetchError) {
                if (showLoading) error.value = 'Protocol not found for this job.'
                return
            }

            if (!Array.isArray(data)) {
                return
            }

            const sortedOverview = [...data].sort(compareProtocols)
            const existingById = new Map(
                protocols.value
                    .filter((pass): pass is ReviewProtocolPass & { id: string } => !!pass.id)
                    .map(pass => [pass.id, pass] as const),
            )
            const { normalizedProtocols, nextLoaded } = reconcileProtocolOverview(sortedOverview, existingById, loadedProtocolIds.value)
            protocols.value = normalizedProtocols
            loadedProtocolIds.value = nextLoaded
            if (resultRes.data) {
                reviewStatus.value = resultRes.data
            }
            if (detailRes.data) {
                const detail = detailRes.data
                jobStatus.value = detail.status ?? null
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

            // Restore the active view (Findings / Execution trace / Tokens)
            // from the URL before resolving the pass selection.
            const routeView = typeof route.query.view === 'string' ? route.query.view : null
            if (routeView === 'summary' || routeView === 'traces' || routeView === 'tokens') {
                activeTab.value = routeView
            }

            const protocolIdToLoad = resolveProtocolIdToLoad(normalizedProtocols, {
                routePassId: typeof route.query.pass === 'string' ? route.query.pass : null,
                routeProtocolId: typeof route.query.protocolId === 'string' ? route.query.protocolId : null,
                routeFile: typeof route.query.file === 'string' ? route.query.file : null,
            }, activePassId.value)

            await activateProtocolPass(protocolIdToLoad)
            updatePollingLifecycle(detailRes.data?.status)
        } catch {
            if (showLoading) error.value = 'Failed to load protocol.'
        } finally {
            if (showLoading) loading.value = false
            loadInFlight = false
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
            // Overlay the detailed (generated, nullable-typed) pass onto the existing
            // strict ReviewProtocolPass; the detailed response supplies the same fields
            // at runtime, so assert back to the element type.
            nextProtocols[index] = {
                ...nextProtocols[index],
                ...detailedPass,
            } as ReviewProtocolPass
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

    // Apply the chip set encoded in the URL to the active chips. Returns the
    // canonical token list it applied so the writer can avoid redundant pushes.
    function applyTraceChipsFromRoute(): string | null {
        const chipIds = parseTraceChipParam(route.query.traceChips as string | string[] | undefined)
        setActiveTraceChips(chipIds)
        return serializeTraceChips(activeTraceChipIds.value)
    }

    onMounted(() => {
        applyTraceChipsFromRoute()
        void loadProtocol(true)
    })

    // Mirror the file / pass / view selection into the URL so a trace is a
    // shareable deep link. The view-model owns selection; the URL only mirrors
    // it (never blocks render). Existing protocolId / eventId / clientId query
    // params are preserved untouched.
    watch(
        [activeFileDisplayPath, activePassId, activeTab],
        ([filePath, passId, view]) => {
            if (!router || typeof router.replace !== 'function') {
                return
            }
            if (protocols.value.length === 0) {
                return
            }

            const nextQuery: LocationQueryRaw = { ...route.query }

            if (filePath) {
                nextQuery.file = filePath === 'PR-level' ? '' : filePath
            } else {
                delete nextQuery.file
            }

            if (passId) {
                nextQuery.pass = passId
            } else {
                delete nextQuery.pass
            }

            nextQuery.view = view

            const sameFile = String(nextQuery.file ?? '') === String(route.query.file ?? '')
            const samePass = String(nextQuery.pass ?? '') === String(route.query.pass ?? '')
            const sameView = String(nextQuery.view ?? '') === String(route.query.view ?? '')
            if (sameFile && samePass && sameView) {
                return
            }

            void router.replace({ query: nextQuery }).catch(() => {
                // Redundant navigation (NavigationDuplicated) is harmless here.
            })
        },
        { flush: 'post' },
    )

    // Reconcile state FROM the URL so browser back/forward (which only mutates
    // the address bar) drives the UI. Guarded by compare-before-set so it does
    // not fight the state→URL write watcher above: it only acts when the URL's
    // file/pass/view actually diverge from current state, and only for ids that
    // exist. This watcher does NOT touch protocolId/eventId/clientId — those
    // keep their reload behavior in the separate watcher below.
    watch(
        () => [
            typeof route.query.view === 'string' ? route.query.view : null,
            typeof route.query.pass === 'string' ? route.query.pass : null,
            typeof route.query.file === 'string' ? route.query.file : null,
        ] as const,
        ([routeView, routePass, routeFile]) => {
            if (protocols.value.length === 0) {
                return
            }

            if ((routeView === 'summary' || routeView === 'traces' || routeView === 'tokens')
                && routeView !== activeTab.value) {
                activeTab.value = routeView
            }

            // Resolve the desired pass: an explicit `pass` wins, else the first
            // pass of the named `file`. Ignore ids/files that don't exist.
            let desiredPassId: string | null = null
            if (routePass && protocols.value.some(protocol => protocol.id === routePass)) {
                desiredPassId = routePass
            } else if (routeFile !== null) {
                desiredPassId = protocols.value.find(protocol => fileKeyForPass(protocol) === routeFile)?.id ?? null
            }

            if (desiredPassId && desiredPassId !== activePassId.value) {
                activePassId.value = desiredPassId
            }
        },
    )

    // Mirror chip toggles into the URL query string so back/forward navigation
    // and shared links restore the same chip state. Uses replace to avoid
    // flooding history while toggling chips.
    watch(activeTraceChipIds, () => {
        const serialized = serializeTraceChips(activeTraceChipIds.value)
        const current = typeof route.query.traceChips === 'string' ? route.query.traceChips : null
        if (serialized === current) {
            return
        }

        const nextQuery = { ...route.query }
        if (serialized) {
            nextQuery.traceChips = serialized
        } else {
            delete nextQuery.traceChips
        }

        void router.replace({ query: nextQuery })
    })

    // Restore chip state when the URL's chip param changes from outside (browser
    // back/forward or a shared link landing on this view).
    watch(
        () => (typeof route.query.traceChips === 'string' ? route.query.traceChips : ''),
        nextParam => {
            if (serializeTraceChips(activeTraceChipIds.value) === (nextParam || null)) {
                return
            }

            setActiveTraceChips(parseTraceChipParam(nextParam))
        },
    )

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

    async function backfillTracePasses(): Promise<void> {
        const token = ++traceBackfillToken
        // The viewed pass must be present immediately; the rest stream in behind it.
        if (activePassId.value) {
            await ensureProtocolPassLoaded(activePassId.value)
        }
        if (token !== traceBackfillToken) {
            return
        }

        const pending = protocols.value
            .map(protocol => protocol.id)
            .filter((protocolId): protocolId is string => !!protocolId && protocolId !== activePassId.value)

        // Bounded, abortable queue — never the old N-way parallel burst that fetched every
        // pass's full event body at once (multi-GB on large reviews).
        const TRACE_BACKFILL_CONCURRENCY = 2
        let cursor = 0
        const worker = async (): Promise<void> => {
            while (cursor < pending.length) {
                if (token !== traceBackfillToken) {
                    return
                }
                const protocolId = pending[cursor++]
                if (loadedProtocolIds.value.has(protocolId) || loadingProtocolIds.value.has(protocolId)) {
                    continue
                }
                await ensureProtocolPassLoaded(protocolId)
            }
        }

        await Promise.all(Array.from({ length: TRACE_BACKFILL_CONCURRENCY }, () => worker()))
    }

    watch(activeTab, tab => {
        if (tab !== 'traces') {
            traceBackfillToken++
            return
        }

        void backfillTracePasses()
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
        traceBackfillToken++
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


    function clearDiff(): void {
        detailTab.value = 'events'
        resetDiff()
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
        jobStatus,
        canRestart,
        restarting,
        restart,
        collapsedFolders,
        expandedEventParents,
        selectedCommentPath,
        isSummaryModalOpen,
        focusedEventId,
        isTraceSearchCollapsed,
        traceFindingsOnly,
        activeTraceChipIds,
        traceChips,
        traceChipGroups,
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
        visiblePassCount,
        sidebarItems,
        commentSidebarItems,
        groupedReviewComments,
        aggregateFindingsByFile,
        fileGroups,
        activeFile,
        activeFileDisplayPath,
        passesForActiveFile,
        activeFilePassTabs,
        activePassLabel,
        activePassReason,
        activePassFailed,
        previousPassId,
        nextPassId,
        goToPass,
        selectFile,
        selectFindingOrigin,
        commentOriginLabel,
        activePassFinalComments,
        activePassUnionContribution,
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
        selectedTriageDecision,
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
        toggleTraceChip,
        clearTraceChips,
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
