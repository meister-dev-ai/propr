<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->

<template>
    <div class="page-view pr-review-page">
        <div class="header-stack">
            <div class="header-row">
                <RouterLink class="back-link" :to="{ name: 'reviews' }">← Back to reviews</RouterLink>
                <OverflowMenu v-if="canManage" class="pr-actions-menu" title="PR actions">
                    <template #default="{ close }">
                        <button
                            type="button"
                            class="overflow-menu-item"
                            :disabled="blocking"
                            @click="toggleBlock(); close()"
                        >
                            <i :class="isBlocked ? 'fi fi-rr-play' : 'fi fi-rr-ban'"></i>
                            {{ isBlocked ? 'Unblock PR' : 'Block PR' }}
                        </button>
                    </template>
                </OverflowMenu>
            </div>
            <div class="pr-title-row">
                <h2>PR Review View</h2>
                <span
                    v-if="isBlocked"
                    class="blocked-badge"
                    title="Blocked from review processing — new pushes are not reviewed"
                >
                    <i class="fi fi-rr-ban"></i> Blocked
                </span>
            </div>
            <p v-if="blockError" class="error block-error">{{ blockError }}</p>
        </div>

        <p v-if="loading" class="loading">Loading…</p>
        <p v-else-if="error" class="error">{{ error }}</p>

        <template v-else-if="data">
            <div class="pr-tabs" role="tablist" aria-label="Pull request review sections">
                <button
                    type="button"
                    role="tab"
                    class="tab-btn pr-tab-btn"
                    :class="{ 'tab-active': activeTab === 'stats' }"
                    :aria-selected="activeTab === 'stats'"
                    data-testid="pr-tab-stats"
                    @click="activeTab = 'stats'"
                >
                    Stats
                </button>
                <button
                    type="button"
                    role="tab"
                    class="tab-btn pr-tab-btn"
                    :class="{ 'tab-active': activeTab === 'conversation' }"
                    :aria-selected="activeTab === 'conversation'"
                    data-testid="pr-tab-conversation"
                    @click="activeTab = 'conversation'"
                >
                    Conversation
                </button>
                <button
                    type="button"
                    role="tab"
                    class="tab-btn pr-tab-btn"
                    :class="{ 'tab-active': activeTab === 'browser' }"
                    :aria-selected="activeTab === 'browser'"
                    data-testid="pr-tab-browser"
                    @click="activeTab = 'browser'"
                >
                    Browser
                </button>
            </div>

            <div v-show="activeTab === 'stats'" role="tabpanel" data-testid="pr-panel-stats">
            <div class="pr-header-card">
                <div class="pr-meta">
                    <span class="pr-id-badge">PR #{{ data.pullRequestId }}</span>
                    <span class="pr-repo">{{ data.repositoryId }}</span>
                    <span class="pr-project">{{ data.providerProjectKey }}</span>
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
                        <span class="stat-label">Est. Cost</span>
                        <span class="stat-value">{{ formatCost(data.totalEstimatedCostUsd, data.costIsApproximate) }}</span>
                    </div>
                    <div class="stat-pill">
                        <span class="stat-label">Memories</span>
                        <span class="stat-value">{{ data.originatedMemoryCount }}</span>
                    </div>
                </div>
            </div>

            <div v-if="(data.aggregatedTokenBreakdown?.length ?? 0) > 0" class="breakdown-section">
                <TokenBreakdownTable
                    :breakdown="data.aggregatedTokenBreakdown ?? []"
                    :breakdown-consistent="data.breakdownConsistent"
                />
            </div>

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

            </div>

            <div v-show="activeTab === 'conversation'" role="tabpanel" data-testid="pr-panel-conversation">
                <RetainedConversationTab
                    v-if="retained && retainedIdentity"
                    :retained="retained"
                    :client-id="retainedIdentity.clientId"
                />
            </div>

            <div v-show="activeTab === 'browser'" role="tabpanel" data-testid="pr-panel-browser">
                <RetainedBrowserTab
                    v-if="retained && retainedIdentity"
                    :retained="retained"
                    :client-id="retainedIdentity.clientId"
                />
            </div>
        </template>

        <p v-else class="empty-state">No data. Provide clientId, providerScopePath, providerProjectKey, repositoryId and pullRequestId query parameters.</p>
    </div>
</template>

<script lang="ts" setup>
import { computed, ref, shallowRef, onMounted, watch } from 'vue'
import { useRoute } from 'vue-router'
import TokenBreakdownTable from '@/components/usage/TokenBreakdownTable.vue'
import OverflowMenu from '@/components/OverflowMenu.vue'
import RetainedConversationTab from '@/features/reviews/components/RetainedConversationTab.vue'
import RetainedBrowserTab from '@/features/reviews/components/RetainedBrowserTab.vue'
import {
    useRetainedPrData,
    type RetainedPrIdentity,
    type UseRetainedPrData,
} from '@/features/reviews/composables/useRetainedPrData'
import { blockPr, getPrView, listBlockedPrs, unblockPr, type PrReviewViewDto, type PullRequestIdentity } from '@/services/jobsService'
import { useSession } from '@/composables/useSession'
import { RoleLevel } from '@/composables/roles'

const route = useRoute()
const { hasClientRole } = useSession()

const loading = ref(false)
const error = ref('')
const data = ref<PrReviewViewDto | null>(null)
const memoryTab = ref<'originated' | 'contributed'>('originated')
const activeTab = ref<'stats' | 'conversation' | 'browser'>('stats')

const clientId = computed(() => route.query.clientId as string | undefined)
const providerScopePath = computed(() => route.query.providerScopePath as string | undefined)
const providerProjectKey = computed(() => route.query.providerProjectKey as string | undefined)
const repositoryId = computed(() => route.query.repositoryId as string | undefined)
const pullRequestId = computed(() => route.query.pullRequestId ? Number(route.query.pullRequestId) : undefined)

// Block/unblock controls are admin-gated. The PR identity comes from the route query params.
const isBlocked = ref(false)
const blocking = ref(false)
const blockError = ref('')

const canManage = computed(() =>
    typeof clientId.value === 'string' && clientId.value.length > 0 && hasClientRole(clientId.value, RoleLevel.Administrator),
)

// Any viewer who can inspect the client sees the blocked badge; only administrators can toggle the block.
const canInspect = computed(() =>
    typeof clientId.value === 'string' && clientId.value.length > 0 && hasClientRole(clientId.value, RoleLevel.User),
)

const prIdentity = computed<PullRequestIdentity | null>(() => {
    if (!providerScopePath.value || !providerProjectKey.value || !repositoryId.value || pullRequestId.value == null) {
        return null
    }
    return {
        providerScopePath: providerScopePath.value,
        providerProjectKey: providerProjectKey.value,
        repositoryId: repositoryId.value,
        pullRequestId: pullRequestId.value,
    }
})

async function loadBlockedState() {
    if (!canInspect.value || !clientId.value || !prIdentity.value) {
        return
    }
    try {
        const blocked = await listBlockedPrs(clientId.value)
        const identity = prIdentity.value
        isBlocked.value = blocked.some((entry) =>
            (entry.providerScopePath ?? '') === identity.providerScopePath &&
            (entry.providerProjectKey ?? '') === identity.providerProjectKey &&
            (entry.repositoryId ?? '') === identity.repositoryId &&
            (entry.pullRequestId ?? 0) === identity.pullRequestId,
        )
    } catch {
        // Best-effort: leave the PR presented as unblocked when the state cannot be loaded.
    }
}

async function toggleBlock() {
    if (!canManage.value || blocking.value || !clientId.value || !prIdentity.value) {
        return
    }
    blockError.value = ''
    blocking.value = true
    try {
        if (isBlocked.value) {
            await unblockPr(clientId.value, prIdentity.value)
        } else {
            await blockPr(clientId.value, prIdentity.value)
        }
        await loadBlockedState()
    } catch (err) {
        blockError.value = err instanceof Error ? err.message : 'Failed to update the block state.'
    } finally {
        blocking.value = false
    }
}

// Identity for the retained-archive section. The retained endpoints resolve the owning connection
// server-side from the retained data, so the section only needs clientId + repositoryId +
// pullRequestId. We build the identity once the data load has succeeded.
const retainedIdentity = computed<RetainedPrIdentity | null>(() => {
    if (!data.value) return null
    if (!clientId.value || !repositoryId.value || pullRequestId.value == null) {
        return null
    }
    return {
        clientId: clientId.value,
        providerScopePath: providerScopePath.value,
        repositoryId: repositoryId.value,
        pullRequestId: pullRequestId.value,
    }
})

// The retained threads and files are shared across the Conversation and Browser tabs, so the
// archive is fetched exactly once per identity here (rather than per tab). The composable is keyed to a
// concrete identity; a fresh instance is created and loaded whenever the identity resolves or changes to
// a different pull request, so navigating between pull requests on the same component instance does not
// leave the earlier pull request's retained data on screen.
const retained = shallowRef<UseRetainedPrData | null>(null)

watch(
    () => {
        const identity = retainedIdentity.value
        return identity
            ? `${identity.clientId} ${identity.providerScopePath} ${identity.repositoryId} ${identity.pullRequestId}`
            : null
    },
    () => {
        const identity = retainedIdentity.value
        if (!identity) {
            retained.value = null
            return
        }

        const instance = useRetainedPrData(identity)
        retained.value = instance
        void instance.load()
    },
    { immediate: true },
)

async function loadData() {
    if (!clientId.value || !providerScopePath.value || !providerProjectKey.value || !repositoryId.value || !pullRequestId.value) {
        return
    }

    loading.value = true
    error.value = ''
    try {
        data.value = await getPrView(clientId.value, {
            providerScopePath: providerScopePath.value,
            providerProjectKey: providerProjectKey.value,
            repositoryId: repositoryId.value,
            pullRequestId: pullRequestId.value,
        })
    } catch (err) {
        error.value = err instanceof Error ? err.message : 'Failed to load PR view.'
    } finally {
        loading.value = false
    }
}

onMounted(() => {
    void loadData()
})

// The view can be reused across SPA navigation while the route query changes, so reload the blocked
// state whenever the PR identity changes, resetting to a safe default first so the badge never reflects
// a previous pull request.
watch(
    () => [clientId.value, providerScopePath.value, providerProjectKey.value, repositoryId.value, pullRequestId.value].join('|'),
    () => {
        isBlocked.value = false
        void loadBlockedState()
    },
    { immediate: true },
)

function protocolLink(jobId: string): string {
    return `/jobs/${jobId}/protocol${clientId.value ? '?clientId=' + clientId.value : ''}`
}

function formatTokens(n: number | null | undefined): string {
    if (n == null) return '—'
    if (n >= 1_000_000) return (n / 1_000_000).toFixed(1) + 'M'
    if (n >= 1_000) return (n / 1_000).toFixed(1) + 'K'
    return String(n)
}

const usdFormatter = new Intl.NumberFormat('en-US', {
    style: 'currency',
    currency: 'USD',
    minimumFractionDigits: 2,
    maximumFractionDigits: 2,
})

function formatCost(value: number | null | undefined, approximate: boolean | null | undefined): string {
    if (value == null) return '—'
    return `${approximate ? '≈' : ''}${usdFormatter.format(value)}`
}

function formatDate(iso: string): string {
    if (!iso) return '—'
    const d = new Date(iso)
    return d.toLocaleDateString() + ' ' + d.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })
}

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
/* This view (esp. the Browser tab's diff) needs the room, so it spans the full
   width instead of the shared centered page max-width. */
.pr-review-page {
    max-width: none;
}

.header-stack {
    margin-bottom: 1.5rem;
}

.header-row {
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 1rem;
}

.block-error {
    margin: 0.5rem 0 0;
}

.pr-title-row {
    display: flex;
    align-items: center;
    gap: 0.6rem;
    flex-wrap: wrap;
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
    padding: 0.15rem 0.5rem;
    border-radius: var(--radius-xs);
}

.blocked-badge i {
    font-size: 0.7rem;
}

.back-link {
    display: inline-block;
    margin-bottom: 0.5rem;
    color: var(--color-text-muted);
    text-decoration: none;
    font-size: 0.875rem;
}

.back-link:hover {
    text-decoration: underline;
}

.pr-header-card {
    background: var(--color-surface);
    border: 1px solid var(--color-border);
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
    background: var(--color-info-soft);
    color: var(--color-info);
    border-radius: 0.375rem;
    padding: 0.2rem 0.6rem;
    font-weight: 600;
    font-size: 0.9rem;
}

.pr-repo {
    font-family: monospace;
    font-size: 0.875rem;
    color: var(--color-text-muted);
}

.pr-project {
    font-size: 0.8rem;
    color: var(--color-text-muted);
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
    background: var(--color-surface-raised);
    border-radius: 0.375rem;
    padding: 0.35rem 0.75rem;
    font-size: 0.85rem;
}

.stat-label {
    color: var(--color-text-muted);
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
    background: var(--color-surface);
    border: 1px solid var(--color-border);
    border-radius: 0.5rem;
    padding: 1.25rem;
    margin-bottom: 1.25rem;
}

.section-title {
    margin: 0 0 1rem 0;
    font-size: 1rem;
    font-weight: 600;
}

.empty-state,
.empty-state-small,
.loading {
    color: var(--color-text-muted);
}

.jobs-list {
    display: flex;
    flex-direction: column;
    gap: 0.75rem;
}

.job-detail-item {
    border: 1px solid var(--color-border);
    border-radius: 0.5rem;
    overflow: hidden;
}

.job-summary-row {
    display: flex;
    align-items: center;
    gap: 0.75rem;
    padding: 0.75rem 1rem;
    cursor: pointer;
    list-style: none;
}

.job-summary-row::-webkit-details-marker {
    display: none;
}

.job-date,
.job-tokens,
.monospace-value,
.file-cell,
.date-cell {
    font-family: monospace;
}

.job-breakdown-content {
    padding: 0 1rem 1rem;
}

.detail-tabs {
    display: flex;
    gap: 0.5rem;
    margin-bottom: 1rem;
    flex-wrap: wrap;
}

.tab-btn {
    border: 1px solid var(--color-border);
    background: transparent;
    color: inherit;
    padding: 0.5rem 0.75rem;
    border-radius: 0.375rem;
    cursor: pointer;
}

.tab-active {
    background: rgba(124, 124, 255, 0.12);
    border-color: rgba(124, 124, 255, 0.45);
}

.pr-tabs {
    display: flex;
    gap: 0.5rem;
    margin-bottom: 1.25rem;
    flex-wrap: wrap;
}

.pr-tab-btn {
    padding: 0.55rem 1.1rem;
    font-size: 0.95rem;
    font-weight: 600;
}

.memory-table {
    width: 100%;
    border-collapse: collapse;
}

.memory-table th,
.memory-table td {
    padding: 0.65rem 0.5rem;
    border-bottom: 1px solid var(--color-border);
    text-align: left;
    vertical-align: top;
}

.summary-cell {
    max-width: 32rem;
}

.source-badge {
    display: inline-flex;
    align-items: center;
    padding: 0.2rem 0.5rem;
    border-radius: var(--radius-pill);
    font-size: 0.8rem;
}

.source-dismissed {
    background: var(--color-warning-soft);
    color: var(--color-warning);
}

.source-resolved {
    background: rgba(34, 197, 94, 0.15);
    color: var(--color-success);
}

.error {
    color: var(--color-danger, var(--color-danger));
}

.retained-archive-section {
    margin-bottom: 1.25rem;
}

.retained-notice {
    display: flex;
    align-items: flex-start;
    gap: 0.65rem;
    padding: 1rem 1.1rem;
    border: 1px dashed var(--color-border);
    border-radius: var(--radius-lg);
    background: rgba(255, 255, 255, 0.02);
    color: var(--color-text-muted);
    margin: 0;
}

.retained-notice i {
    color: var(--color-accent);
    flex: 0 0 auto;
    margin-top: 0.1rem;
}

.retained-notice-title {
    margin: 0 0 0.25rem 0;
    font-weight: 600;
    color: var(--color-text);
}

.retained-notice-detail {
    margin: 0;
    font-size: 0.85rem;
    line-height: 1.4;
}
</style>
