<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->

<template>
    <div class="page-view">
        <div class="header-stack">
            <RouterLink class="back-link" to="/reviews">← Back to reviews</RouterLink>
            <h2>PR Review View</h2>
        </div>

        <p v-if="loading" class="loading">Loading…</p>
        <p v-else-if="error" class="error">{{ error }}</p>

        <template v-else-if="data">
            <!-- PR Header -->
            <div class="pr-header-card">
                <div class="pr-meta">
                    <span class="pr-id-badge">PR #{{ data.pullRequestId }}</span>
                    <span class="pr-repo">{{ data.repositoryId }}</span>
                    <span class="pr-project">{{ data.projectId }}</span>
                </div>
                <div class="pr-stat-strip">
                    <div class="stat-pill">
                        <span class="stat-label">Jobs</span>
                        <span class="stat-value">{{ data.totalJobs }}</span>
                    </div>
                    <div class="stat-pill">
                        <span class="stat-label">In Tokens</span>
                        <span class="stat-value fat-tokens">{{ formatTokens(data.totalInputTokens) }}</span>
                    </div>
                    <div class="stat-pill">
                        <span class="stat-label">Out Tokens</span>
                        <span class="stat-value fat-tokens">{{ formatTokens(data.totalOutputTokens) }}</span>
                    </div>
                    <div class="stat-pill">
                        <span class="stat-label">Memories</span>
                        <span class="stat-value">{{ data.originatedMemoryCount }}</span>
                    </div>
                </div>
            </div>

            <!-- Aggregated Token Breakdown -->
            <div v-if="(data.aggregatedTokenBreakdown?.length ?? 0) > 0" class="breakdown-section">
                <TokenBreakdownTable
                    :breakdown="data.aggregatedTokenBreakdown ?? []"
                    :breakdown-consistent="data.breakdownConsistent"
                />
            </div>

            <!-- Jobs List -->
            <section class="section-card">
                <h3 class="section-title">Review Jobs</h3>
                <p v-if="(data.jobs?.length ?? 0) === 0" class="empty-state">No review jobs found for this PR.</p>
                <div v-else class="jobs-list">
                    <details
                        v-for="job in data.jobs"
                        :key="job.jobId"
                        class="job-detail-item"
                    >
                        <summary class="job-summary-row">
                            <span :class="statusBadgeClass(job.status)">{{ statusLabel(job.status) }}</span>
                            <span class="job-date">{{ formatDate(job.submittedAt) }}</span>
                            <span v-if="job.totalInputTokens != null" class="job-tokens">
                                {{ formatTokens(job.totalInputTokens) }} in / {{ formatTokens(job.totalOutputTokens ?? 0) }} out
                            </span>
                            <RouterLink
                                :to="protocolLink(job.jobId)"
                                class="btn-ghost protocol-btn"
                                @click.stop
                            >
                                Protocol ↗
                            </RouterLink>
                        </summary>
                        <div class="job-breakdown-content">
                            <TokenBreakdownTable
                                v-if="(job.tokenBreakdown?.length ?? 0) > 0"
                                :breakdown="job.tokenBreakdown ?? []"
                            />
                            <p v-else class="empty-state-small">No per-tier breakdown available.</p>
                        </div>
                    </details>
                </div>
            </section>

            <!-- Memory Panel -->
            <section class="section-card">
                <h3 class="section-title">Memory Records</h3>
                <div class="detail-tabs">
                    <button
                        class="tab-btn"
                        :class="{ 'tab-active': memoryTab === 'originated' }"
                        @click="memoryTab = 'originated'"
                    >
                        Originated ({{ data.originatedMemoryCount }})
                    </button>
                    <button
                        class="tab-btn"
                        :class="{ 'tab-active': memoryTab === 'contributed' }"
                        @click="memoryTab = 'contributed'"
                    >
                        Contributing External ({{ data.contributedMemoryCount }})
                    </button>
                </div>

                <!-- Originated Memories -->
                <div v-if="memoryTab === 'originated'">
                    <p v-if="(data.originatedMemories?.length ?? 0) === 0" class="empty-state">
                        No memory records originated from this PR.
                    </p>
                    <table v-else class="memory-table">
                        <thead>
                            <tr>
                                <th>Thread</th>
                                <th>File</th>
                                <th>Source</th>
                                <th>Summary</th>
                                <th>Stored At</th>
                            </tr>
                        </thead>
                        <tbody>
                            <tr v-for="mem in data.originatedMemories" :key="mem.memoryRecordId">
                                <td class="monospace-value">#{{ mem.threadId }}</td>
                                <td class="file-cell">{{ mem.filePath ?? '—' }}</td>
                                <td>
                                    <span
                                        class="source-badge"
                                        :class="mem.source === 1 ? 'source-dismissed' : 'source-resolved'"
                                    >
                                        {{ mem.source === 1 ? 'Admin Dismissed' : 'Thread Resolved' }}
                                    </span>
                                </td>
                                <td class="summary-cell">{{ mem.resolutionSummaryExcerpt }}</td>
                                <td class="date-cell">{{ formatDate(mem.storedAt) }}</td>
                            </tr>
                        </tbody>
                    </table>
                </div>

                <!-- Contributing Memories -->
                <div v-if="memoryTab === 'contributed'">
                    <p v-if="(data.contributedMemories?.length ?? 0) === 0" class="empty-state">
                        No external memory records contributed to reviews in this PR.
                    </p>
                    <table v-else class="memory-table">
                        <thead>
                            <tr>
                                <th>Repository</th>
                                <th>Origin PR</th>
                                <th>File</th>
                                <th>Source</th>
                                <th>Summary</th>
                                <th>Max Similarity</th>
                            </tr>
                        </thead>
                        <tbody>
                            <tr v-for="mem in data.contributedMemories" :key="mem.memoryRecordId">
                                <td class="monospace-value">{{ mem.originRepositoryId ?? '—' }}</td>
                                <td>{{ mem.originPullRequestId != null ? '#' + mem.originPullRequestId : '—' }}</td>
                                <td class="file-cell">{{ mem.filePath ?? '—' }}</td>
                                <td>
                                    <span
                                        class="source-badge"
                                        :class="mem.source === 1 ? 'source-dismissed' : 'source-resolved'"
                                    >
                                        {{ mem.source === 1 ? 'Admin Dismissed' : 'Thread Resolved' }}
                                    </span>
                                </td>
                                <td class="summary-cell">{{ mem.resolutionSummaryExcerpt }}</td>
                                <td>{{ mem.maxSimilarityScore != null ? (mem.maxSimilarityScore * 100).toFixed(1) + '%' : '—' }}</td>
                            </tr>
                        </tbody>
                    </table>
                </div>
            </section>
        </template>

        <p v-else class="empty-state">No data. Provide clientId, organizationUrl, projectId, repositoryId and pullRequestId query parameters.</p>
    </div>
</template>

<script lang="ts" setup>
import { computed, ref, onMounted } from 'vue'
import { useRoute } from 'vue-router'
import TokenBreakdownTable from '@/components/TokenBreakdownTable.vue'
import { getPrView, type PrReviewViewDto } from '@/services/jobsService'

const route = useRoute()

const loading = ref(false)
const error = ref('')
const data = ref<PrReviewViewDto | null>(null)
const memoryTab = ref<'originated' | 'contributed'>('originated')

const clientId = computed(() => route.query.clientId as string | undefined)
const organizationUrl = computed(() => route.query.orgUrl as string | undefined)
const projectId = computed(() => route.query.project as string | undefined)
const repositoryId = computed(() => route.query.repositoryId as string | undefined)
const pullRequestId = computed(() => route.query.pullRequestId ? Number(route.query.pullRequestId) : undefined)

async function loadData() {
    if (!clientId.value || !organizationUrl.value || !projectId.value || !repositoryId.value || !pullRequestId.value) {
        return
    }

    loading.value = true
    error.value = ''
    try {
        data.value = await getPrView(clientId.value, {
            organizationUrl: organizationUrl.value,
            projectId: projectId.value,
            repositoryId: repositoryId.value,
            pullRequestId: pullRequestId.value,
        })
    } catch (err) {
        error.value = err instanceof Error ? err.message : 'Failed to load PR view.'
    } finally {
        loading.value = false
    }
}

onMounted(loadData)

function protocolLink(jobId: string): string {
    return `/jobs/${jobId}/protocol${clientId.value ? '?clientId=' + clientId.value : ''}`
}

function formatTokens(n: number | null | undefined): string {
    if (n == null) return '—'
    if (n >= 1_000_000) return (n / 1_000_000).toFixed(1) + 'M'
    if (n >= 1_000) return (n / 1_000).toFixed(1) + 'K'
    return String(n)
}

function formatDate(iso: string): string {
    if (!iso) return '—'
    const d = new Date(iso)
    return d.toLocaleDateString() + ' ' + d.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })
}

// JobStatus enum: 0=Pending, 1=Processing, 2=Completed, 3=Failed, 4=Cancelled
function statusLabel(status: number): string {
    switch (status) {
        case 0: return 'Pending'
        case 1: return 'Processing'
        case 2: return 'Completed'
        case 3: return 'Failed'
        case 4: return 'Cancelled'
        default: return String(status)
    }
}

function statusBadgeClass(status: number): string {
    switch (status) {
        case 0: return 'badge badge-pending'
        case 1: return 'badge badge-processing'
        case 2: return 'badge badge-completed'
        case 3: return 'badge badge-failed'
        case 4: return 'badge badge-cancelled'
        default: return 'badge'
    }
}
</script>

<style scoped>
.header-stack {
    margin-bottom: 1.5rem;
}

.back-link {
    display: inline-block;
    margin-bottom: 0.5rem;
    color: var(--color-text-muted, #888);
    text-decoration: none;
    font-size: 0.875rem;
}

.back-link:hover {
    text-decoration: underline;
}

.pr-header-card {
    background: var(--color-surface, #1e1e2e);
    border: 1px solid var(--color-border, #2e2e3e);
    border-radius: 0.5rem;
    padding: 1rem 1.25rem;
    margin-bottom: 1.25rem;
}

.pr-meta {
    display: flex;
    align-items: center;
    gap: 0.75rem;
    margin-bottom: 0.75rem;
    flex-wrap: wrap;
}

.pr-id-badge {
    background: var(--color-primary-muted, #3b3b6d);
    color: var(--color-primary, #7c7cff);
    border-radius: 0.375rem;
    padding: 0.2rem 0.6rem;
    font-weight: 600;
    font-size: 0.9rem;
}

.pr-repo {
    font-family: monospace;
    font-size: 0.875rem;
    color: var(--color-text-muted, #aaa);
}

.pr-project {
    font-size: 0.8rem;
    color: var(--color-text-muted, #888);
}

.pr-stat-strip {
    display: flex;
    flex-wrap: wrap;
    gap: 0.75rem;
}

.stat-pill {
    display: flex;
    align-items: center;
    gap: 0.4rem;
    background: var(--color-surface-raised, #252535);
    border-radius: 0.375rem;
    padding: 0.35rem 0.75rem;
    font-size: 0.85rem;
}

.stat-label {
    color: var(--color-text-muted, #888);
    font-size: 0.8rem;
}

.stat-value {
    font-weight: 600;
}

.fat-tokens {
    font-family: monospace;
}

.breakdown-section {
    margin-bottom: 1.25rem;
}

.section-card {
    background: var(--color-surface, #1e1e2e);
    border: 1px solid var(--color-border, #2e2e3e);
    border-radius: 0.5rem;
    padding: 1.25rem;
    margin-bottom: 1.25rem;
}

.section-title {
    margin: 0 0 1rem 0;
    font-size: 1rem;
    font-weight: 600;
}

.jobs-list {
    display: flex;
    flex-direction: column;
    gap: 0.5rem;
}

.job-detail-item {
    border: 1px solid var(--color-border, #2e2e3e);
    border-radius: 0.375rem;
    overflow: hidden;
}

.job-summary-row {
    display: flex;
    align-items: center;
    gap: 0.75rem;
    padding: 0.6rem 0.875rem;
    cursor: pointer;
    list-style: none;
    flex-wrap: wrap;
}

.job-summary-row::-webkit-details-marker {
    display: none;
}

.job-date {
    font-size: 0.8rem;
    color: var(--color-text-muted, #aaa);
}

.job-tokens {
    font-family: monospace;
    font-size: 0.8rem;
    color: var(--color-text-muted, #aaa);
    margin-left: auto;
}

.protocol-btn {
    font-size: 0.8rem;
    flex-shrink: 0;
}

.job-breakdown-content {
    padding: 1.25rem;
    border-top: 1px solid var(--color-border, #333);
    background: var(--color-bg);
}

.empty-state {
    color: var(--color-text-muted, #888);
    font-style: italic;
    padding: 0.5rem 0;
}

.empty-state-small {
    color: var(--color-text-muted, #888);
    font-style: italic;
    font-size: 0.85rem;
    margin: 0;
}

.memory-table {
    width: 100%;
    border-collapse: collapse;
    font-size: 0.875rem;
}

.memory-table th {
    text-align: left;
    padding: 0.5rem 0.75rem;
    color: var(--color-text-muted, #888);
    border-bottom: 1px solid var(--color-border, #2e2e3e);
    font-weight: 500;
    font-size: 0.8rem;
}

.memory-table td {
    padding: 0.5rem 0.75rem;
    border-bottom: 1px solid var(--color-border-subtle, #282838);
    vertical-align: top;
}

.file-cell {
    font-family: monospace;
    font-size: 0.8rem;
    max-width: 200px;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
}

.summary-cell {
    max-width: 320px;
    color: var(--color-text-muted, #bbb);
    font-size: 0.8rem;
}

.date-cell {
    white-space: nowrap;
    font-size: 0.8rem;
    color: var(--color-text-muted, #aaa);
}

.source-badge {
    display: inline-block;
    border-radius: 0.25rem;
    padding: 0.15rem 0.5rem;
    font-size: 0.75rem;
    font-weight: 500;
}

.source-resolved {
    background: rgba(34, 197, 94, 0.15);
    color: #4ade80;
}

.source-dismissed {
    background: rgba(251, 146, 60, 0.15);
    color: #fb923c;
}

.badge {
    display: inline-block;
    border-radius: 0.25rem;
    padding: 0.2rem 0.6rem;
    font-size: 0.75rem;
    font-weight: 600;
}

.badge-pending { background: rgba(148, 163, 184, 0.15); color: #94a3b8; }
.badge-processing { background: rgba(59, 130, 246, 0.2); color: #60a5fa; }
.badge-completed { background: rgba(34, 197, 94, 0.15); color: #4ade80; }
.badge-failed { background: rgba(239, 68, 68, 0.15); color: #f87171; }
.badge-cancelled { background: rgba(148, 163, 184, 0.1); color: #94a3b8; }

.monospace-value {
    font-family: monospace;
}



.loading {
    color: var(--color-text-muted, #888);
}

.error {
    color: var(--color-danger, #f87171);
}
</style>
