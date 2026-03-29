<template>
    <div class="review-history-section">
        <p v-if="loading" class="loading">Loading…</p>
        <p v-else-if="error" class="error">{{ error }}</p>
        <p v-else-if="groups.length === 0" class="empty-state">No reviews yet.</p>

        <template v-else>
            <section v-for="group in groups" :key="group.key" class="pr-group-card">
                <div class="pr-card-header">
                    <div class="pr-card-title">
                        <a :href="group.prUrl" target="_blank" rel="noopener noreferrer" class="pr-link">
                            {{ group.prTitle ?? 'PR #' + group.pullRequestId }}
                        </a>
                        <span class="repo-name">{{ group.prRepositoryName ?? group.repositoryId }}</span>
                        <span v-if="group.prSourceBranch && group.prTargetBranch" class="branch-context">
                            {{ group.prSourceBranch }} &rarr; {{ group.prTargetBranch }}
                        </span>
                    </div>
                    <div class="pr-card-header-right">
                        <div class="pr-card-totals" v-if="group.totalInTokens > 0 || group.totalOutTokens > 0">
                            <span class="totals-label">Tokens:</span>
                            <span class="fat-tokens total-value">{{ formatTokens(group.totalInTokens) }} In</span>
                            <span class="totals-separator">/</span>
                            <span class="fat-tokens total-value">{{ formatTokens(group.totalOutTokens) }} Out</span>
                        </div>
                        <button
                            v-if="group.items.length > ITEMS_VISIBLE_DEFAULT"
                            class="btn-ghost expand-btn"
                            @click="toggleGroupExpanded(group.key)"
                        >
                            <span v-if="expandedGroups.has(group.key)">
                                Show less ▴
                            </span>
                            <span v-else>
                                +{{ group.items.length - ITEMS_VISIBLE_DEFAULT }} more ▾
                            </span>
                        </button>
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
                                    {{ item.status === 'processing' ? 'Reviewing' : item.status }}
                                </span>
                            </div>
                        </div>

                        <div class="list-date-col date-cell">{{ formatItemDate(item) }}</div>
                        
                        <div class="list-iter-col iter-cell" v-if="item.iterationId">#{{ item.iterationId }}</div>
                        <div class="list-iter-col iter-cell" v-else></div>

                        <div class="list-model-col">
                            <span v-if="item.aiModel" class="model-badge" :title="item.aiModel">{{ item.aiModel }}</span>
                        </div>

                        <div class="list-summary-col" :class="{ 'summary-truncate': item.status !== 'processing' }" @click="openSummaryModal(item)">
                            <template v-if="item.status === 'processing' && item.id && processingProtocols[item.id]">
                                <div class="active-chips">
                                    <RouterLink 
                                        :to="`/jobs/${item.id}/protocol${props.clientId ? '?clientId=' + props.clientId : ''}`"
                                        class="chip-processing"
                                    >
                                        <ProgressOrb class="chip-orb" />
                                        <span class="chip-label">
                                            {{ processingProtocols[item.id].length }} Reviews
                                        </span>
                                    </RouterLink>
                                </div>
                            </template>
                            <template v-else>
                                {{ item.resultSummary ?? item.errorMessage ?? '—' }}
                            </template>
                        </div>

                        <div class="list-action-col">
                            <RouterLink :to="`/jobs/${item.id}/protocol${props.clientId ? '?clientId=' + props.clientId : ''}`" class="btn-ghost protocol-btn">
                                Protocol ↗
                            </RouterLink>
                        </div>
                    </div>
                </div>
            </section>
        </template>
        
        <ModalDialog v-model:isOpen="isSummaryModalOpen" title="Review Summary" size="lg">
            <div class="summary-modal-content markdown-content">
                <div v-html="renderMarkdown(selectedSummary)"></div>
            </div>
        </ModalDialog>
    </div>
</template>

<script lang="ts" setup>
import { onMounted, onUnmounted, ref } from 'vue'
import { RouterLink } from 'vue-router'
import ModalDialog from '@/components/ModalDialog.vue'
import ProgressOrb from '@/components/ProgressOrb.vue'
import { createAdminClient } from '@/services/api'
import type { components } from '@/services/generated/openapi'
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

const ITEMS_VISIBLE_DEFAULT = 3

type JobListItem = components['schemas']['JobListItem']
type JobStatus = components['schemas']['JobStatus']
type ReviewJobProtocolDto = components['schemas']['ReviewJobProtocolDto']

interface PrGroup {
    key: string
    pullRequestId: number
    repositoryId: string
    prTitle: string | null
    prRepositoryName: string | null
    prSourceBranch: string | null
    prTargetBranch: string | null
    prUrl: string
    latestActivityAt: string
    totalInTokens: number
    totalOutTokens: number
    items: JobListItem[]
}

const loading = ref(false)
const error = ref('')
const groups = ref<PrGroup[]>([])
const expandedGroups = ref<Set<string>>(new Set())

const isSummaryModalOpen = ref(false)
const selectedSummary = ref('')

const processingProtocols = ref<Record<string, ReviewJobProtocolDto[]>>({})

function openSummaryModal(item: JobListItem) {
    if (item.status === 'processing' && item.id && processingProtocols.value[item.id]) return // Don't open empty modal on chips
    const text = item.resultSummary ?? item.errorMessage
    if (text && text.trim() !== '') {
        selectedSummary.value = text
        isSummaryModalOpen.value = true
    }
}

let pollInterval: ReturnType<typeof setInterval> | null = null

async function loadProcessingProtocols(items: JobListItem[]) {
    const activeIds = items.filter(i => i.status === 'processing' && i.id).map(i => i.id as string)

    // Run without blocking the main poll
    for (const id of activeIds) {
        try {
            const { data } = await createAdminClient().GET('/jobs/{id}/protocol', { params: { path: { id } } })
            if (data) {
                processingProtocols.value[id] = data as ReviewJobProtocolDto[]
            }
        } catch {
            // ignore
        }
    }
}

async function loadJobs(showLoadingIndicator = false) {
    if (showLoadingIndicator) loading.value = true
    try {
        const { data } = await createAdminClient().GET('/jobs', {
            params: {
                query: {
                    limit: 500,
                    ...(props.clientId ? { clientId: props.clientId } : {}),
                },
            },
        })
        const items = (data as { items?: JobListItem[] })?.items ?? []
        groups.value = buildGroups(items)
        
        const isProcessing = items.some(i => i.status === 'processing' || i.status === 'pending')
        if (isProcessing) {
            loadProcessingProtocols(items)
            if (!pollInterval) {
                pollInterval = setInterval(() => loadJobs(false), 3000)
            }
        } else if (!isProcessing && pollInterval) {
            clearInterval(pollInterval)
            pollInterval = null
        }
    } catch {
        if (showLoadingIndicator) error.value = 'Failed to load review history.'
    } finally {
        if (showLoadingIndicator) loading.value = false
    }
}

onMounted(() => {
    loadJobs(true)
})

onUnmounted(() => {
    if (pollInterval) clearInterval(pollInterval)
})

function toggleGroupExpanded(key: string) {
    if (expandedGroups.value.has(key)) {
        expandedGroups.value.delete(key)
    } else {
        expandedGroups.value.add(key)
    }
    expandedGroups.value = new Set(expandedGroups.value)
}

function visibleItems(group: PrGroup): JobListItem[] {
    if (expandedGroups.value.has(group.key)) return group.items
    return group.items.slice(0, ITEMS_VISIBLE_DEFAULT)
}

function buildGroups(items: JobListItem[]): PrGroup[] {
    const map = new Map<string, PrGroup>()

    for (const item of items) {
        const orgUrl = item.organizationUrl ?? ''
        const project = item.projectId ?? ''
        const repo = item.repositoryId ?? ''
        const prId = item.pullRequestId ?? 0

        const key = `${orgUrl}|${project}|${repo}|${prId}`
        const prUrl = `${orgUrl}/${project}/_git/${repo}/pullrequest/${prId}`

        if (!map.has(key)) {
            map.set(key, {
                key,
                pullRequestId: prId,
                repositoryId: repo,
                prTitle: item.prTitle ?? null,
                prRepositoryName: item.prRepositoryName ?? null,
                prSourceBranch: item.prSourceBranch ?? null,
                prTargetBranch: item.prTargetBranch ?? null,
                prUrl,
                latestActivityAt: item.submittedAt ?? '',
                totalInTokens: 0,
                totalOutTokens: 0,
                items: [],
            })
        }

        const group = map.get(key)!
        group.items.push(item)
        group.totalInTokens += item.totalInputTokens ?? 0
        group.totalOutTokens += item.totalOutputTokens ?? 0

        const itemDate = item.completedAt ?? item.processingStartedAt ?? item.submittedAt ?? ''
        if (itemDate > group.latestActivityAt) {
            group.latestActivityAt = itemDate
        }
    }

    for (const group of map.values()) {
        group.items.sort((a, b) => {
            // Active jobs (pending/processing) first, then by date descending
            const aActive = a.status === 'processing' || a.status === 'pending'
            const bActive = b.status === 'processing' || b.status === 'pending'
            if (aActive !== bActive) return aActive ? -1 : 1
            const aDate = a.completedAt ?? a.processingStartedAt ?? a.submittedAt ?? ''
            const bDate = b.completedAt ?? b.processingStartedAt ?? b.submittedAt ?? ''
            return bDate.localeCompare(aDate)
        })
    }

    return [...map.values()].sort((a, b) =>
        b.latestActivityAt.localeCompare(a.latestActivityAt),
    )
}

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
    return ''
}

function statusBadgeClass(status: JobStatus | undefined): string {
    switch (status) {
        case 'completed': return 'status-badge status-completed'
        case 'processing': return 'status-badge status-processing'
        case 'pending': return 'status-badge status-pending'
        case 'failed': return 'status-badge status-failed'
        default: return 'status-badge'
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
    border-radius: 12px;
    background: var(--color-surface);
    overflow: hidden;
    padding: 0;
}

.pr-card-header {
    display: flex;
    align-items: center;
    justify-content: space-between;
    padding: 1rem 1.5rem;
    background: rgba(255, 255, 255, 0.02);
    border-bottom: 1px solid var(--color-border);
}

.pr-card-header-right {
    display: flex;
    align-items: center;
    gap: 1rem;
    flex-shrink: 0;
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
    border-radius: 4px;
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
    padding: 1rem 1.75rem;
    border-bottom: 1px solid var(--color-border);
    gap: 1.5rem;
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
    flex: 0 0 120px;
}

.list-date-col {
    flex: 0 0 180px;
}

.list-iter-col {
    flex: 0 0 40px;
}

.list-model-col {
    flex: 0 0 160px;
    min-width: 0;
    overflow: hidden;
}

.list-summary-col {
    flex: 1;
    min-width: 0;
    color: var(--color-text);
    padding: 0 0.5rem;
}

.model-badge {
    display: inline-block;
    max-width: 100%;
    padding: 0.15rem 0.5rem;
    border-radius: 4px;
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

.list-summary-col {
    flex: 1;
    min-width: 0;
    color: var(--color-text);
}

.list-action-col {
    flex: 0 0 auto;
}

/* Status badge */
.status-badge {
    display: inline-block;
    padding: 0.2rem 0.6rem;
    border-radius: 9999px;
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
    border-radius: 9999px;
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
.markdown-content code { background: rgba(255, 255, 255, 0.1); padding: 0.1rem 0.3rem; border-radius: 4px; font-family: monospace; }
.markdown-content pre { background: var(--color-bg); border: 1px solid var(--color-border); padding: 1rem; border-radius: 8px; overflow-x: auto; margin-bottom: 1rem; }
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

.btn-ghost {
    display: inline-flex;
    align-items: center;
    gap: 0.5rem;
    padding: 0.4rem 0.8rem;
    border-radius: 6px;
    font-size: 0.8rem;
    font-weight: 600;
    color: var(--color-text-muted);
    background: transparent;
    border: 1px solid transparent;
    transition: all 0.2s;
    text-decoration: none;
}

.btn-ghost:hover {
    color: var(--color-text);
    background: var(--color-surface);
    border-color: var(--color-border);
    text-decoration: none;
}
</style>
