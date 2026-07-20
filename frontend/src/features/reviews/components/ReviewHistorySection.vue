<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->

<template>
    <div class="review-history-section">
        <p v-if="restartError" class="error restart-error">{{ restartError }}</p>
        <p v-if="stopError" class="error restart-error">{{ stopError }}</p>
        <p v-if="blockError" class="error restart-error">{{ blockError }}</p>
        <p v-if="loading" class="loading">Loading…</p>
        <p v-else-if="error" class="error">{{ error }}</p>
        <p v-else-if="groups.length === 0" class="empty-state">No reviews yet.</p>

        <template v-else>
            <section v-for="group in paginatedGroups" :key="group.key" class="pr-group-card">
                <div class="pr-card-header">
                    <div class="pr-card-title">
                        <a :href="group.prUrl" target="_blank" rel="noopener noreferrer" class="pr-link">
                            {{ group.prTitle ?? 'PR #' + group.pullRequestId }}
                        </a>
                        <span class="repo-name">{{ group.prRepositoryName ?? group.repositoryId }}</span>
                        <span v-if="group.prSourceBranch && group.prTargetBranch" class="branch-context">
                            {{ group.prSourceBranch }} &rarr; {{ group.prTargetBranch }}
                        </span>
                        <span
                            v-if="isPrBlocked(group)"
                            class="blocked-badge"
                            title="Blocked from review processing — new pushes are not reviewed"
                        >
                            <i class="fi fi-rr-ban"></i> Blocked
                        </span>
                    </div>
            <div class="pr-card-header-right">
                <div class="pr-card-totals" v-if="group.totalInTokens > 0 || group.totalOutTokens > 0">
                    <span class="totals-label">Tokens:</span>
                    <span class="fat-tokens total-value">{{ formatTokens(group.totalInTokens) }} In</span>
                    <span class="totals-separator">/</span>
                    <span class="fat-tokens total-value">{{ formatTokens(group.totalOutTokens) }} Out</span>
                    <template v-if="group.totalEstimatedCostUsd != null">
                        <span class="totals-separator">·</span>
                        <span class="fat-tokens total-value" title="Estimated cost">{{ group.costIsApproximate ? '≈ ' : '' }}{{ formatUsd(group.totalEstimatedCostUsd) }}</span>
                    </template>
                </div>
                <div class="pr-card-actions">
                    <RouterLink
                        v-if="canInspectClient(group.clientId)"
                        :to="prReviewLink(group)"
                        class="btn-ghost pr-view-btn"
                        title="View PR analysis"
                    >
                        PR View ↗
                    </RouterLink>
                    <button
                        v-if="group.items.length > itemsVisibleDefault"
                        class="btn-ghost expand-btn"
                        @click="toggleGroupExpanded(group.key)"
                    >
                        <span v-if="expandedGroups.has(group.key)">
                            Show less ▴
                        </span>
                        <span v-else>
                            +{{ group.items.length - itemsVisibleDefault }} more ▾
                        </span>
                    </button>
                    <OverflowMenu
                        v-if="canManageClient(group.clientId)"
                        class="pr-block-menu"
                        title="PR actions"
                    >
                        <template #default="{ close }">
                            <button
                                type="button"
                                class="overflow-menu-item"
                                :disabled="blockingPrs.has(group.key)"
                                @click="toggleBlockPr(group); close()"
                            >
                                <i :class="isPrBlocked(group) ? 'fi fi-rr-play' : 'fi fi-rr-ban'"></i>
                                {{ isPrBlocked(group) ? 'Unblock PR' : 'Block PR' }}
                            </button>
                        </template>
                    </OverflowMenu>
                </div>
            </div>
                </div>

                <div class="review-list">
                    <div
                        v-for="item in visibleItems(group)"
                        :key="item.id"
                        class="review-list-item"
                        :class="rowClass(item)"
                    >
                        <div class="list-status-col">
                            <div style="display: flex; align-items: center; gap: 0.5rem">
                                <span :class="statusBadgeClass(item.status)">
                                    {{ statusLabel(item.status) }}
                                </span>
                            </div>
                        </div>

                        <div class="list-date-col date-cell">{{ formatItemDate(item) }}</div>
                        
                        <div class="list-iter-col iter-cell" v-if="item.iterationId">#{{ item.iterationId }}</div>
                        <div class="list-iter-col iter-cell" v-else></div>

                        <div class="list-model-col">
                            <div class="list-meta-stack">
                                <span v-if="item.aiModel" class="model-badge" :title="item.aiModel">{{ item.aiModel }}</span>
                            </div>
                        </div>

                        <div
                            class="list-summary-col"
                            :class="{ 'summary-truncate': item.status !== 'processing' && hasSummaryContent(item) }"
                            @click="hasSummaryContent(item) && openSummaryModal(item)"
                        >
                            <template v-if="item.status === 'processing' && item.id">
                                <div class="active-chips">
                                    <RouterLink 
                                        v-if="canInspectClient(props.clientId || item.clientId)"
                                        :to="{ name: 'job-protocol', params: { id: item.id }, query: { clientId: props.clientId || item.clientId } }"
                                        class="chip-processing"
                                    >
                                        <ProgressOrb class="chip-orb" />
                                        <span class="chip-label">
                                            {{ filesProgressLabel(item) }}
                                        </span>
                                    </RouterLink>
                                    <span v-else class="chip-processing">
                                        <ProgressOrb class="chip-orb" />
                                        <span class="chip-label">
                                            {{ filesProgressLabel(item) }}
                                        </span>
                                    </span>
                                </div>
                            </template>
                            <template v-else>
                                {{ item.resultSummary ?? item.errorMessage ?? '—' }}
                            </template>
                        </div>

                        <div class="list-action-col">
                            <button
                                v-if="(item.status === 'failed' || item.status === 'budgetHeld' || item.status === 'budgetExceeded') && item.id && canInspectClient(props.clientId || item.clientId)"
                                class="btn-ghost restart-btn"
                                :disabled="restartingJobs.has(item.id)"
                                :title="item.status === 'failed' ? 'Restart this failed review' : 'Restart this budget-blocked review after freeing budget'"
                                @click="restartJob(item)"
                            >
                                {{ restartingJobs.has(item.id) ? 'Restarting…' : 'Restart ↻' }}
                            </button>
                            <button
                                v-if="(item.status === 'processing' || item.status === 'pending') && item.id && canManageClient(item.clientId)"
                                class="btn-ghost stop-btn"
                                :disabled="stoppingJobs.has(item.id)"
                                title="Stop this running review"
                                @click="stopJob(item)"
                            >
                                {{ stoppingJobs.has(item.id) ? 'Stopping…' : 'Stop ◼' }}
                            </button>
                            <RouterLink
                                v-if="canInspectClient(props.clientId || item.clientId)"
                                :to="{ name: 'job-protocol', params: { id: item.id }, query: { clientId: props.clientId || item.clientId } }"
                                class="btn-ghost protocol-btn"
                            >
                                Protocol ↗
                            </RouterLink>
                        </div>
                    </div>
                </div>
            </section>

            <div v-if="totalPages > 1" class="pagination-controls">
                <div class="pagination-info">
                    Page {{ currentPage }} of {{ totalPages }}
                </div>
                <div class="pagination-buttons">
                    <button 
                        class="btn-secondary" 
                        @click="previousPage" 
                        :disabled="currentPage === 1"
                        title="Previous page"
                    >
                        <i class="fi fi-rr-arrow-left"></i>
                        Previous
                    </button>
                    <button 
                        class="btn-secondary" 
                        @click="nextPage" 
                        :disabled="currentPage === totalPages"
                        title="Next page"
                    >
                        Next
                        <i class="fi fi-rr-arrow-right"></i>
                    </button>
                </div>
            </div>
        </template>
        
        <ModalDialog :isOpen="isSummaryModalOpen" @update:isOpen="isSummaryModalOpen = $event" title="Review Summary" size="lg">
            <div class="summary-modal-content markdown-content">
                <div v-html="renderMarkdown(selectedSummary)"></div>
            </div>
        </ModalDialog>
    </div>
</template>

<script lang="ts" setup>
import { RouterLink } from 'vue-router'
import ModalDialog from '@/components/dialogs/ModalDialog.vue'
import OverflowMenu from '@/components/OverflowMenu.vue'
import ProgressOrb from '@/components/ProgressOrb.vue'
import { useReviewHistoryViewModel, type PrGroup } from '@/features/reviews/view-models/useReviewHistoryViewModel'
import { formatUsd } from '@/components/usageDashboardFormatters'
import { formatFilesReviewed } from '@/utils/reviewProgress'
import type { components } from '@/types'
import MarkdownIt from 'markdown-it'
import DOMPurify from 'dompurify'

const md = new MarkdownIt({
    html: false,
    linkify: true,
    breaks: true
})

function renderMarkdown(content: string | null | undefined): string {
    if (!content) return ''
    return DOMPurify.sanitize(md.render(content))
}

const props = withDefaults(defineProps<{ clientId?: string }>(), { clientId: undefined })
const vm = useReviewHistoryViewModel({ clientId: props.clientId })

type JobListItem = components['schemas']['JobListItem']
type JobStatus = components['schemas']['JobStatus']

const {
    loading,
    error,
    groups,
    expandedGroups,
    currentPage,
    isSummaryModalOpen,
    selectedSummary,
    totalPages,
    paginatedGroups,
    itemsVisibleDefault,
    openSummaryModal,
    toggleGroupExpanded,
    nextPage,
    previousPage,
    refresh,
    visibleItems,
    canInspectClient,
    canManageClient,
    restartingJobs,
    restartError,
    restartJob,
    stoppingJobs,
    stopError,
    stopJob,
    blockingPrs,
    blockError,
    isPrBlocked,
    toggleBlockPr,
} = vm

defineExpose({
    refresh,
})

function formatItemDate(item: JobListItem): string {
    if (item.status === 'processing') {
        const since = item.processingStartedAt ? ` since ${formatDate(item.processingStartedAt)}` : ''
        return `In progress${since}`
    }
    if (item.status === 'pending') {
        return item.submittedAt ? `Queued ${formatDate(item.submittedAt)}` : 'Queued'
    }
    return formatDate(item.completedAt)
}

// Reliable per-file progress for a running review. The in-scope denominator is null until dispatch
// planning fixes it, so fall back to a neutral label rather than showing "0/0".
function filesProgressLabel(item: JobListItem): string {
    const fraction = formatFilesReviewed(item.filesReviewed, item.filesInScope)
    return fraction ? `${fraction} files reviewed` : 'Reviewing…'
}

function formatDate(iso: string | null | undefined): string {
    if (!iso) return '—'
    return new Date(iso).toLocaleString()
}

function formatTokens(n: number | null | undefined): string {
    if (n == null) return '—'
    return n.toLocaleString()
}

function rowClass(item: JobListItem): string {
    if (item.status === 'failed') return 'row-failed'
    if (item.status === 'processing') return 'row-processing'
    if (item.status === 'pending') return 'row-pending'
    if (item.status === 'budgetHeld' || item.status === 'budgetExceeded') return 'row-budget'
    return ''
}

// Human-readable status label; the raw enum name is shown for statuses without a friendlier form.
function statusLabel(status: JobStatus | undefined): string {
    switch (status) {
        case 'processing': return 'Reviewing'
        case 'budgetHeld': return 'Budget held'
        case 'budgetExceeded': return 'Budget stopped'
        default: return status ?? ''
    }
}

// The summary cell is only clickable when it has real content to show; a bare '—' fallback should not
// open an empty modal.
function hasSummaryContent(item: JobListItem): boolean {
    return Boolean(item.resultSummary || item.errorMessage)
}

function statusBadgeClass(status: JobStatus | undefined): string {
    switch (status) {
        case 'completed': return 'status-badge status-completed'
        case 'processing': return 'status-badge status-processing'
        case 'pending': return 'status-badge status-pending'
        case 'failed': return 'status-badge status-failed'
        case 'superseded': return 'status-badge status-superseded'
        case 'budgetHeld': return 'status-badge status-budget-held'
        case 'budgetExceeded': return 'status-badge status-budget-exceeded'
        default: return 'status-badge'
    }
}

function prReviewLink(group: PrGroup): object {
    return {
        name: 'pr-review',
        query: {
            clientId: group.clientId,
            providerScopePath: group.providerScopePath,
            providerProjectKey: group.providerProjectKey,
            repositoryId: group.repositoryId,
            pullRequestId: String(group.pullRequestId),
        },
    }
}
</script>

<style scoped>
.empty-state,
.loading {
    color: var(--color-text-muted);
    font-style: italic;
}

.error {
    color: var(--color-danger);
}

/* Card Layout */
.pr-group-card {
    margin-bottom: 2rem;
    border: 1px solid var(--color-border);
    border-radius: var(--radius-lg);
    background: var(--color-surface);
    overflow: hidden;
    padding: 0;
}

.pr-card-header {
    display: flex;
    align-items: center;
    justify-content: space-between;
    flex-wrap: wrap;
    gap: 1rem;
    padding: 1rem 1.5rem;
    background: rgba(255, 255, 255, 0.02);
    border-bottom: 1px solid var(--color-border);
}

.pr-card-header-right {
    display: flex;
    align-items: center;
    flex-wrap: wrap;
    gap: 1rem;
    flex-shrink: 0;
}

.pr-card-actions {
    display: flex;
    align-items: center;
    gap: 0.5rem;
}

.pr-card-title {
    display: flex;
    align-items: center;
    gap: 0.75rem;
    font-size: 1rem;
    margin: 0;
}

.pr-link {
    font-weight: 600;
    color: var(--color-accent);
}

.repo-name {
    font-size: 0.85rem;
    color: var(--color-text-muted);
    font-weight: normal;
}

.branch-context {
    font-size: 0.80rem;
    color: var(--color-text-muted);
    font-family: var(--font-mono, monospace);
    background: var(--color-bg-subtle, rgba(0,0,0,0.06));
    padding: 0.1rem 0.4rem;
    border-radius: var(--radius-xs);
}

.blocked-badge {
    display: inline-flex;
    align-items: center;
    gap: 0.25rem;
    font-size: 0.72rem;
    font-weight: 600;
    text-transform: uppercase;
    letter-spacing: 0.03em;
    color: var(--color-danger);
    background: var(--color-danger-soft, rgba(239, 68, 68, 0.12));
    border: 1px solid rgba(239, 68, 68, 0.4);
    padding: 0.1rem 0.45rem;
    border-radius: var(--radius-xs);
}

.blocked-badge i {
    font-size: 0.7rem;
}

.pr-card-totals {
    display: flex;
    align-items: center;
    gap: 0.5rem;
    font-size: 0.85rem;
}

.expand-btn {
    font-size: 0.78rem;
    padding: 0.25rem 0.6rem;
    color: var(--color-accent);
    border: 1px solid rgba(34, 211, 238, 0.25);
    white-space: nowrap;
}

.expand-btn:hover {
    background: rgba(34, 211, 238, 0.08);
    border-color: rgba(34, 211, 238, 0.5);
}

.totals-label {
    color: var(--color-text-muted);
    text-transform: uppercase;
    font-size: 0.75rem;
    letter-spacing: 0.05em;
    font-weight: 600;
}

.total-value {
    color: var(--color-text);
}

.totals-separator {
    color: var(--color-border);
}

/* List Layout */
.review-list {
    display: flex;
    flex-direction: column;
}

.review-list-item {
    display: flex;
    align-items: center;
    flex-wrap: wrap; /* Allow wrapping to prevent overlap */
    padding: 1rem 1.5rem;
    border-bottom: 1px solid var(--color-border);
    gap: 1rem;
    min-height: 4rem;
    transition: all 0.2s ease;
}

.review-list-item:hover {
    background: rgba(255, 255, 255, 0.02);
}

.review-list-item:last-child {
    border-bottom: none;
}

/* Item Columns */
.list-status-col {
    flex: 0 0 100px;
}

.list-date-col {
    flex: 0 0 160px;
}

.list-iter-col {
    flex: 0 0 40px;
}

.list-model-col {
    flex: 0 0 120px;
    min-width: 0;
    overflow: hidden;
}

.list-meta-stack {
    display: flex;
    flex-direction: column;
    gap: 0.35rem;
    align-items: flex-start;
}

.list-summary-col {
    flex: 1 1 200px;
    min-width: 200px; /* Prevent shrinking past chips width */
    color: var(--color-text);
    padding: 0;
}

.model-badge {
    display: inline-block;
    max-width: 100%;
    padding: 0.15rem 0.5rem;
    border-radius: var(--radius-xs);
    font-size: 0.72rem;
    font-weight: 500;
    font-family: var(--font-mono, monospace);
    color: var(--color-text-muted);
    background: rgba(255, 255, 255, 0.05);
    border: 1px solid var(--color-border);
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
}

.list-action-col {
    flex: 0 0 auto;
    margin-left: auto; /* Keep it on the right */
    display: flex;
    align-items: center;
    gap: 0.5rem;
}

.restart-btn {
    font-size: 0.78rem;
    padding: 0.25rem 0.6rem;
    color: var(--color-warning);
    border: 1px solid rgba(245, 158, 11, 0.3);
    white-space: nowrap;
}

.restart-btn:hover:not(:disabled) {
    background: rgba(245, 158, 11, 0.1);
    border-color: rgba(245, 158, 11, 0.55);
}

.restart-btn:disabled {
    opacity: 0.6;
    cursor: not-allowed;
}

.stop-btn {
    font-size: 0.78rem;
    padding: 0.25rem 0.6rem;
    color: var(--color-danger);
    border: 1px solid rgba(239, 68, 68, 0.3);
    white-space: nowrap;
}

.stop-btn:hover:not(:disabled) {
    background: rgba(239, 68, 68, 0.1);
    border-color: rgba(239, 68, 68, 0.55);
}

.stop-btn:disabled {
    opacity: 0.6;
    cursor: not-allowed;
}

.restart-error {
    margin-bottom: 1rem;
}

/* Status badge */
.status-badge {
    display: inline-block;
    padding: 0.2rem 0.6rem;
    border-radius: var(--radius-pill);
    font-size: 0.75rem;
    font-weight: 600;
    text-transform: capitalize;
}

.status-completed {
    background: rgba(34, 197, 94, 0.15);
    color: var(--color-success);
}

.status-processing {
    background: rgba(34, 211, 238, 0.15);
    color: var(--color-accent);
    animation: pulseBadge 2s cubic-bezier(0.4, 0, 0.6, 1) infinite;
}

@keyframes pulseBadge {
    0%, 100% { opacity: 1; }
    50% { opacity: 0.6; }
}

.status-pending {
    background: rgba(161, 161, 170, 0.15);
    color: var(--color-text-muted);
}

.status-failed {
    background: rgba(239, 68, 68, 0.15);
    color: var(--color-danger);
}

.status-superseded {
    background: rgba(245, 158, 11, 0.15);
    color: var(--color-warning);
}

.status-budget-held {
    background: rgba(245, 158, 11, 0.15);
    color: var(--color-warning);
}

.status-budget-exceeded {
    background: rgba(249, 115, 22, 0.18);
    color: #f97316;
}

/* Row variants */
.row-failed {
    background: rgba(239, 68, 68, 0.05);
}
.row-failed:hover {
    background: rgba(239, 68, 68, 0.08);
}

.row-processing {
    background: rgba(34, 211, 238, 0.03);
}
.row-processing:hover {
    background: rgba(34, 211, 238, 0.06);
}

.row-pending {
    background: rgba(255, 255, 255, 0.01);
}

.row-budget {
    background: rgba(245, 158, 11, 0.04);
}
.row-budget:hover {
    background: rgba(245, 158, 11, 0.07);
}

.date-cell {
    white-space: nowrap;
    color: var(--color-text-muted);
    font-size: 0.9rem;
}

.iter-cell {
    white-space: nowrap;
    color: var(--color-text-muted);
    font-size: 0.9rem;
}

.summary-truncate {
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
    cursor: pointer;
    transition: color 0.15s;
    font-size: 0.9rem;
}

.summary-truncate:hover {
    color: var(--color-accent);
}

.active-chips {
    display: flex;
    align-items: center;
    gap: 0.75rem;
    flex-wrap: wrap;
}

.chip-processing {
    display: inline-flex;
    align-items: center;
    gap: 0.4rem;
    padding: 0.2rem 0.6rem;
    border-radius: var(--radius-pill);
    background: rgba(34, 211, 238, 0.1);
    border: 1px solid rgba(34, 211, 238, 0.25);
    color: var(--color-accent);
    font-size: 0.75rem;
    font-weight: 500;
    text-decoration: none;
    transition: all 0.2s ease;
}

.chip-processing:hover {
    background: rgba(34, 211, 238, 0.15);
    border-color: rgba(34, 211, 238, 0.4);
    text-decoration: none;
}

.chip-orb {
    transform: scale(0.7);
}

.chip-label {
    max-width: 15rem;
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
}

/* Markdown Support in Modal */
.markdown-content :first-child { margin-top: 0; }
.markdown-content :last-child { margin-bottom: 0; }
.markdown-content p { margin-bottom: 0.75rem; line-height: 1.6; }
.markdown-content ul, .markdown-content ol { margin-bottom: 0.75rem; padding-left: 1.5rem; }
.markdown-content code { background: rgba(255, 255, 255, 0.1); padding: 0.1rem 0.3rem; border-radius: var(--radius-xs); font-family: monospace; }
.markdown-content pre { background: var(--color-bg); border: 1px solid var(--color-border); padding: 1rem; border-radius: var(--radius-md); overflow-x: auto; margin-bottom: 1rem; }
.markdown-content h1, .markdown-content h2, .markdown-content h3 { margin: 1rem 0 0.5rem 0; font-weight: 600; }
.markdown-content h2 { font-size: 1.1rem; border-bottom: 1px solid var(--color-border); padding-bottom: 0.25rem; }

.summary-modal-content {
    white-space: pre-wrap;
    word-break: break-word;
    font-size: 0.9rem;
    color: var(--color-text-muted);
    line-height: 1.5;
}

.text-muted {
    color: var(--color-text-muted);
}



/* Pagination Controls */
.pagination-controls {
    display: flex;
    align-items: center;
    justify-content: space-between;
    padding: 1.5rem;
    margin-top: 2rem;
    border: 1px solid var(--color-border);
    border-radius: var(--radius-lg);
    background: var(--color-surface);
}

.pagination-info {
    font-size: 0.9rem;
    color: var(--color-text-muted);
    font-weight: 500;
}

.pagination-buttons {
    display: flex;
    gap: 0.75rem;
}

.pagination-buttons button {
    display: inline-flex;
    align-items: center;
    gap: 0.5rem;
}

.pagination-buttons button:disabled {
    opacity: 0.5;
    cursor: not-allowed;
}

@media (max-width: 1200px) {
    .pr-card-header {
        flex-direction: column;
        align-items: flex-start;
    }
    
    .pr-card-header-right {
        width: 100%;
        justify-content: space-between;
    }

    .review-list-item {
        flex-wrap: wrap;
        gap: 0.5rem;
        padding: 1rem;
        height: auto;
    }

    .list-status-col {
        flex: 0 0 auto;
    }

    .list-date-col, .list-iter-col, .list-model-col {
        flex: 0 0 auto;
    }

    .list-model-col {
        margin-right: auto;
    }

    .list-action-col {
        flex: 0 0 auto;
    }

    .list-summary-col {
        flex: 1 1 100%;
        order: 99;
        margin-top: 0.25rem;
        padding: 0;
    }
}
</style>
