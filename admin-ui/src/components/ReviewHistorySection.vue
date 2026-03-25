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
                                <span :class="statusBadgeClass(item.status)">{{ item.status }}</span>
                            </td>
                            <td class="date-cell">{{ formatItemDate(item) }}</td>
                            <td class="iter-cell">#{{ item.iterationId }}</td>
                            <td class="tokens-cell">{{ formatTokens(item.totalInputTokens) }}</td>
                            <td class="tokens-cell">{{ formatTokens(item.totalOutputTokens) }}</td>
                            <td class="summary-cell">{{ item.resultSummary ?? item.errorMessage ?? '—' }}</td>
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
    </div>
</template>

<script lang="ts" setup>
import { onMounted, ref } from 'vue'
import { RouterLink } from 'vue-router'
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

onMounted(async () => {
    loading.value = true
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
    } catch {
        error.value = 'Failed to load review history.'
    } finally {
        loading.value = false
    }
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
    color: #888;
    font-style: italic;
}

.error {
    color: #c00;
}

.pr-group {
    margin-bottom: 2rem;
    border: 1px solid #ddd;
    border-radius: 6px;
    overflow: hidden;
}

.pr-group-header {
    display: flex;
    align-items: center;
    gap: 0.75rem;
    margin: 0;
    padding: 0.6rem 1rem;
    background: #f5f5f5;
    border-bottom: 1px solid #ddd;
    font-size: 1rem;
}

.pr-link {
    font-weight: bold;
    color: #0366d6;
    text-decoration: none;
}

.pr-link:hover {
    text-decoration: underline;
}

.repo-name {
    font-size: 0.85rem;
    color: #666;
    font-weight: normal;
}

.review-table {
    width: 100%;
    border-collapse: collapse;
    font-size: 0.9rem;
}

.review-table th,
.review-table td {
    padding: 0.5rem 1rem;
    text-align: left;
    border-bottom: 1px solid #eee;
}

.review-table th {
    background: #fafafa;
    font-weight: 600;
    color: #444;
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
    padding: 0.1rem 0.5rem;
    border-radius: 0.75rem;
    font-size: 0.78rem;
    font-weight: 600;
    text-transform: capitalize;
}

.status-completed {
    background: #d1fae5;
    color: #065f46;
}

.status-processing {
    background: #dbeafe;
    color: #1e40af;
}

.status-pending {
    background: #fef9c3;
    color: #713f12;
}

.status-failed {
    background: #fee2e2;
    color: #991b1b;
}

/* Row variants */
.row-failed td {
    background: #fff5f5;
}

.row-processing td {
    background: #f0f7ff;
}

.row-pending td {
    background: #fffbeb;
}

/* Totals aggregate row */
.totals-row td {
    background: #f5f5f5;
    border-top: 2px solid #ddd;
    font-weight: 600;
    color: #333;
}

.totals-label {
    color: #555;
    font-size: 0.85rem;
}

.date-cell {
    white-space: nowrap;
    color: #555;
    min-width: 14rem;
}

.iter-cell {
    white-space: nowrap;
    color: #777;
    width: 5rem;
}

.summary-cell {
    color: #333;
}

.tokens-cell {
    text-align: right;
    font-family: monospace;
    color: #555;
    white-space: nowrap;
}

.protocol-cell {
    white-space: nowrap;
}

.protocol-link {
    font-size: 0.82rem;
    color: #0366d6;
    text-decoration: none;
}

.protocol-link:hover {
    text-decoration: underline;
}
</style>
