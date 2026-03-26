<template>
    <div class="review-history-section">
        <p v-if="loading" class="loading">Loading…</p>
        <p v-else-if="error" class="error">{{ error }}</p>
        <p v-else-if="groups.length === 0" class="empty-state">No reviews yet.</p>

        <template v-else>
            <section
                v-for="group in groups"
                :key="group.key"
                class="pr-group"
            >
                <h3 class="pr-group-header">
                    <a :href="group.prUrl" target="_blank" rel="noopener noreferrer" class="pr-link">
                        PR #{{ group.pullRequestId }}
                    </a>
                    <span class="repo-name">{{ group.repositoryId }}</span>
                </h3>

                <table class="review-table">
                    <thead>
                        <tr>
                            <th>Status</th>
                            <th>Date</th>
                            <th>Iteration</th>
                            <th>In Tokens</th>
                            <th>Out Tokens</th>
                            <th>Summary</th>
                            <th></th>
                        </tr>
                    </thead>
                    <tbody>
                        <tr
                            v-for="item in group.items"
                            :key="item.id"
                            :class="rowClass(item)"
                        >
                            <td class="status-cell">
                                <div style="display: flex; align-items: center; gap: 0.5rem">
                                    <span :class="statusBadgeClass(item.status)">
                                        {{ item.status === 'processing' ? `Pass ${item.iterationId ?? 1}` : item.status }}
                                    </span>
                                    <ProgressOrb v-if="item.status === 'processing'" />
                                </div>
                            </td>
                            <td class="date-cell">{{ formatItemDate(item) }}</td>
                            <td class="iter-cell">#{{ item.iterationId }}</td>
                            <td class="tokens-cell fat-tokens">{{ formatTokens(item.totalInputTokens) }}</td>
                            <td class="tokens-cell fat-tokens">{{ formatTokens(item.totalOutputTokens) }}</td>
                            <td class="summary-cell summary-truncate" @click="openSummaryModal(item)">
                                {{ item.resultSummary ?? item.errorMessage ?? '—' }}
                            </td>
                            <td class="protocol-cell">
                                <RouterLink :to="`/jobs/${item.id}/protocol`" class="protocol-link">
                                    Protocol
                                </RouterLink>
                            </td>
                        </tr>
                        <tr v-if="group.totalInTokens > 0 || group.totalOutTokens > 0" class="totals-row">
                            <td colspan="3" class="totals-label">Total</td>
                            <td class="tokens-cell">{{ formatTokens(group.totalInTokens) }}</td>
                            <td class="tokens-cell">{{ formatTokens(group.totalOutTokens) }}</td>
                            <td colspan="2"></td>
                        </tr>
                    </tbody>
                </table>
            </section>
        </template>
        
        <ModalDialog v-model:isOpen="isSummaryModalOpen" title="Review Summary">
            <div class="summary-modal-content">
                {{ selectedSummary }}
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

const props = withDefaults(defineProps<{ clientId?: string }>(), { clientId: undefined })

type JobListItem = components['schemas']['JobListItem']
type JobStatus = components['schemas']['JobStatus']

interface PrGroup {
    key: string
    pullRequestId: number
    repositoryId: string
    prUrl: string
    latestActivityAt: string
    totalInTokens: number
    totalOutTokens: number
    items: JobListItem[]
}

const loading = ref(false)
const error = ref('')
const groups = ref<PrGroup[]>([])

const isSummaryModalOpen = ref(false)
const selectedSummary = ref('')

function openSummaryModal(item: JobListItem) {
    const text = item.resultSummary ?? item.errorMessage
    if (text && text.trim() !== '') {
        selectedSummary.value = text
        isSummaryModalOpen.value = true
    }
}

let pollInterval: ReturnType<typeof setInterval> | null = null

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
        if (isProcessing && !pollInterval) {
            pollInterval = setInterval(() => loadJobs(false), 3000)
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

.pr-group {
    margin-bottom: 2rem;
    border: 1px solid var(--color-border);
    border-radius: 8px;
    background: var(--color-bg);
    overflow: hidden;
    padding: 0;
}

.pr-group-header {
    display: flex;
    align-items: center;
    gap: 0.75rem;
    margin: 0;
    padding: 0.75rem 1rem;
    background: rgba(255, 255, 255, 0.03);
    border-bottom: 1px solid var(--color-border);
    font-size: 1rem;
}

.pr-link {
    font-weight: 600;
}

.repo-name {
    font-size: 0.85rem;
    color: var(--color-text-muted);
    font-weight: normal;
}

.review-table {
    width: 100%;
    border-collapse: collapse;
    font-size: 0.9rem;
}

.review-table th,
.review-table td {
    padding: 0.75rem 1rem;
    text-align: left;
    border-bottom: 1px solid var(--color-border);
}

.review-table th {
    background: rgba(255, 255, 255, 0.01);
    font-weight: 600;
    color: var(--color-text-muted);
}

.review-table tbody tr:last-child td {
    border-bottom: none;
}

/* Status badge */
.status-cell {
    white-space: nowrap;
}

.status-badge {
    display: inline-block;
    padding: 0.15rem 0.6rem;
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
.row-failed td {
    background: rgba(239, 68, 68, 0.05);
}

.row-processing td {
    background: rgba(34, 211, 238, 0.05);
}

.row-pending td {
    background: rgba(255, 255, 255, 0.02);
}

/* Totals aggregate row */
.totals-row td {
    background: rgba(255, 255, 255, 0.03);
    border-top: 2px solid var(--color-border);
    font-weight: 600;
    color: var(--color-text);
}

.totals-label {
    color: var(--color-text-muted);
    font-size: 0.85rem;
}

.date-cell {
    white-space: nowrap;
    color: var(--color-text-muted);
    min-width: 14rem;
}

.iter-cell {
    white-space: nowrap;
    color: var(--color-text-muted);
    width: 5rem;
}

.summary-cell {
    color: var(--color-text);
}

.summary-truncate {
    max-width: 250px;
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
    cursor: pointer;
    transition: color 0.15s;
}

.summary-truncate:hover {
    color: var(--color-accent);
}

.summary-modal-content {
    white-space: pre-wrap;
    word-break: break-word;
}

.tokens-cell {
    text-align: right;
    font-family: monospace;
    color: var(--color-text-muted);
    white-space: nowrap;
}

.protocol-cell {
    white-space: nowrap;
}

.protocol-link {
    font-size: 0.82rem;
    font-weight: 500;
}
</style>
