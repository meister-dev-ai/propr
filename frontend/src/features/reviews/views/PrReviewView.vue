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

            <!--
                The one place a person is told the pull request has moved on. Automatic triggers review only
                the first increment, so without this the branch sits ahead of its review with nothing saying so.
            -->
            <div v-if="pendingReview" class="pending-review" role="status">
                <div class="pending-review__text">
                    <strong>New commits since the last review.</strong>
                    <span>
                        {{ pendingReviewDescription }}
                    </span>
                </div>
                <button
                    v-if="canRequestReview"
                    type="button"
                    class="pending-review__action"
                    :disabled="requestingReview"
                    @click="requestReview()"
                >
                    <i class="fi fi-rr-play"></i>
                    {{ requestingReview ? 'Requesting…' : 'Review current state' }}
                </button>
            </div>
            <p v-if="requestReviewMessage" class="pending-review__result">{{ requestReviewMessage }}</p>
            <p v-if="requestReviewError" class="error">{{ requestReviewError }}</p>
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
                <!-- Absent rather than disabled when the capability is not licensed: a licence is not a role, and a
                     tab that can only apologise is worse than no tab. The server repeats the check. -->
                <button
                    v-if="canViewCodeQuality"
                    type="button"
                    role="tab"
                    class="tab-btn pr-tab-btn"
                    :class="{ 'tab-active': activeTab === 'codeQuality' }"
                    :aria-selected="activeTab === 'codeQuality'"
                    data-testid="pr-tab-code-quality"
                    @click="activeTab = 'codeQuality'"
                >
                    Code Quality
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
                <h3 class="section-title">Thread Passes</h3>
                <p class="section-hint">
                    Answers to the developer's replies, and thread resolutions. These run on their own cadence
                    beside the file reviews, and their spend counts toward the same budgets.
                </p>
                <p v-if="threadPasses.length === 0" class="empty-state">No thread passes have run for this PR.</p>
                <div v-else class="jobs-list">
                    <div
                        v-for="pass in threadPasses"
                        :key="pass.threadPassId"
                        class="job-summary-row thread-pass-row"
                    >
                        <span :class="threadPassBadgeClass(pass.status)">{{ threadPassStatusLabel(pass.status) }}</span>
                        <span class="job-date">{{ formatDate(pass.createdAt) }}</span>
                        <span class="job-tokens">{{ pass.threadCount }} thread(s)</span>
                        <span class="job-tokens">
                            {{ formatTokens(pass.totalInputTokens) }} in / {{ formatTokens(pass.totalOutputTokens) }} out
                        </span>
                        <span class="job-tokens">
                            {{ formatCost(pass.totalEstimatedCostUsd, pass.costIsApproximate) }}
                        </span>
                        <RouterLink :to="protocolLink(pass.threadPassId)" class="btn-ghost protocol-btn">
                            Protocol ↗
                        </RouterLink>
                        <button
                            v-if="canRestartThreadPass(pass.status)"
                            class="btn-ghost protocol-btn"
                            :disabled="restartingThreadPassId === pass.threadPassId"
                            @click="restartThreadPass(pass.threadPassId)"
                        >
                            {{ restartingThreadPassId === pass.threadPassId ? 'Restarting…' : 'Restart ↻' }}
                        </button>
                        <span v-if="threadPassBlockReason(pass)" class="thread-pass-note">
                            {{ threadPassBlockReason(pass) }}
                        </span>
                    </div>
                </div>
                <p v-if="threadPassError" class="empty-state-small">{{ threadPassError }}</p>
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
                                <th>Outcome</th>
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
                                        class="outcome-badge"
                                        :class="'outcome-' + outcomeOf(mem).tone"
                                        :title="outcomeOf(mem).description"
                                    >
                                        {{ outcomeOf(mem).label }}
                                    </span>
                                </td>
                                <td>
                                    <span class="source-badge" :class="isDismissed(mem.source) ? 'source-dismissed' : 'source-resolved'">
                                        {{ isDismissed(mem.source) ? 'Admin Dismissed' : 'Thread Resolved' }}
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
                                    <span class="source-badge" :class="isDismissed(mem.source) ? 'source-dismissed' : 'source-resolved'">
                                        {{ isDismissed(mem.source) ? 'Admin Dismissed' : 'Thread Resolved' }}
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

            <!-- Rendered only once opened: the reads behind it are three requests nobody asked for while looking at
                 the stats. Everything it needs is already on this page's own scope. -->
            <div v-show="activeTab === 'codeQuality'" role="tabpanel" data-testid="pr-panel-code-quality">
                <PrCodeQualityTab
                    v-if="activeTab === 'codeQuality' && canViewCodeQuality && clientId && repositoryId && pullRequestId != null"
                    :client-id="clientId"
                    :repository-id="repositoryId"
                    :pull-request-id="pullRequestId"
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
import PrCodeQualityTab from '@/features/reviews/components/PrCodeQualityTab.vue'
import {
    useRetainedPrData,
    type RetainedPrIdentity,
    type UseRetainedPrData,
} from '@/features/reviews/composables/useRetainedPrData'
import {
    blockPr,
    getPrView,
    listBlockedPrs,
    restartJob,
    submitReviewByCoordinates,
    unblockPr,
    type PrReviewViewDto,
    type PrThreadPassSummaryDto,
    type PullRequestIdentity,
    type SubmitReviewByCoordinatesOutcome,
} from '@/services/jobsService'
import { useSession } from '@/composables/useSession'
import { describeMemoryOutcome } from '@/features/thread-memory/memoryOutcome'
import { RoleLevel } from '@/composables/roles'

const route = useRoute()
const { hasClientRole, isCapabilityAvailable } = useSession()

const loading = ref(false)
const error = ref('')
const data = ref<PrReviewViewDto | null>(null)
const memoryTab = ref<'originated' | 'contributed'>('originated')
const activeTab = ref<'stats' | 'conversation' | 'browser' | 'codeQuality'>('stats')

const clientId = computed(() => route.query.clientId as string | undefined)
const providerScopePath = computed(() => route.query.providerScopePath as string | undefined)
const providerProjectKey = computed(() => route.query.providerProjectKey as string | undefined)
const repositoryId = computed(() => route.query.repositoryId as string | undefined)
const pullRequestId = computed(() => route.query.pullRequestId ? Number(route.query.pullRequestId) : undefined)

// The same rule as the top-level Code Quality area: client access (which this whole view already requires) plus
// the licence.
const canViewCodeQuality = computed(() => isCapabilityAvailable('code-insights'))

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

// Whether the pull request is waiting is the backend's answer, not a comparison made here, so this view
// and the browser extension offer the action on the same terms.
const pendingReview = computed(() => data.value?.pendingReview ?? null)

const canRequestReview = computed(() => canInspect.value && !isBlocked.value)

const pendingReviewDescription = computed(() => {
    const pending = pendingReview.value
    if (!pending) {
        return ''
    }

    const reviewed = pending.reviewedRevisionKey
        ? `The files were last reviewed at revision ${pending.reviewedRevisionKey}.`
        : 'The files have not been reviewed yet.'
    const since = pending.detectedAt
        ? ` Waiting since ${new Date(pending.detectedAt).toLocaleString()}.`
        : ''

    return `${reviewed}${since} Comment threads are still being answered on every push.`
})

const requestingReview = ref(false)
const requestReviewMessage = ref('')
const requestReviewError = ref('')

/** What each named outcome means to the person who asked for the review. */
const requestReviewOutcomes: Record<SubmitReviewByCoordinatesOutcome, string> = {
    submitted: 'Review requested. It will appear in the list below once it starts.',
    duplicateActiveJob: 'A review of this revision is already running.',
    notAuthorized: 'You do not have permission to request a review for this client.',
    pullRequestNotFound: 'The provider reports no such pull request.',
    revisionUnresolvable: 'The provider could not be asked which revision this pull request is at.',
    notSubmittable: 'This pull request cannot be reviewed right now.',
    submissionFailed: 'The pull request resolved, but queueing the review failed.',
}

async function requestReview() {
    if (!canRequestReview.value || requestingReview.value || !clientId.value || !prIdentity.value) {
        return
    }

    requestingReview.value = true
    requestReviewMessage.value = ''
    requestReviewError.value = ''
    try {
        const result = await submitReviewByCoordinates(clientId.value, prIdentity.value)
        requestReviewMessage.value = result.reason || requestReviewOutcomes[result.outcome]

        // Only a queued review changes what this page shows. Reloading after a refusal would replace the
        // explanation with an unchanged page and leave the person wondering whether anything happened.
        if (result.outcome === 'submitted') {
            await loadData()
        }
    } catch (err) {
        requestReviewError.value = err instanceof Error ? err.message : 'Failed to request a review.'
    } finally {
        requestingReview.value = false
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
        instance.load()
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

function outcomeOf(mem: { source: string; resolutionIntent?: string | null; resolutionClarity?: string | null }) {
    return describeMemoryOutcome(mem.source, mem.resolutionIntent, mem.resolutionClarity)
}

function isDismissed(source: string | number): boolean {
    return source === 'adminDismissed' || source === 1
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

const threadPasses = computed<PrThreadPassSummaryDto[]>(() => data.value?.threadPasses ?? [])
const restartingThreadPassId = ref<string | null>(null)
const threadPassError = ref('')

// ThreadPassJobStatus, which adds the two budget states and the did-nothing state after the lifecycle ones.
function threadPassStatusLabel(status: number): string {
    switch (status) {
        case 5: return 'Budget held'
        case 6: return 'Budget exceeded'
        case 7: return 'Nothing to do'
        default: return statusLabel(status)
    }
}

function threadPassBadgeClass(status: number): string {
    if (status === 7) {
        return 'badge badge-cancelled'
    }

    return status === 5 || status === 6 ? 'badge badge-failed' : statusBadgeClass(status)
}

/** A budget-blocked or exhausted pass waits on an operator; nothing resumes it on its own. */
function canRestartThreadPass(status: number): boolean {
    return status === 3 || status === 5 || status === 6
}

function threadPassBlockReason(pass: PrThreadPassSummaryDto): string {
    if (pass.budgetBlockThresholdUsd == null) {
        return ''
    }

    const spent = formatCost(pass.budgetBlockSpentUsd, false)
    const cap = formatCost(pass.budgetBlockThresholdUsd, false)
    return `Stopped at ${spent} of a ${cap} cap. Restart it after freeing budget.`
}

async function restartThreadPass(threadPassId: string): Promise<void> {
    if (restartingThreadPassId.value) {
        return
    }

    restartingThreadPassId.value = threadPassId
    threadPassError.value = ''
    try {
        await restartJob(threadPassId)
        await loadData()
    } catch (err) {
        threadPassError.value = err instanceof Error ? err.message : 'Failed to restart the thread pass.'
    } finally {
        restartingThreadPassId.value = null
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

.pending-review {
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 1rem;
    flex-wrap: wrap;
    margin-top: 0.75rem;
    padding: 0.65rem 0.9rem;
    border: 1px solid var(--color-border);
    border-left: 3px solid var(--color-accent, #6366f1);
    border-radius: var(--radius-xs);
    background: var(--color-surface-raised, rgba(99, 102, 241, 0.06));
}

.pending-review__text {
    display: flex;
    flex-direction: column;
    gap: 0.15rem;
    font-size: 0.85rem;
}

.pending-review__text span {
    color: var(--color-text-muted);
    font-size: 0.8rem;
}

.pending-review__action {
    display: inline-flex;
    align-items: center;
    gap: 0.4rem;
    font: inherit;
    font-size: 0.82rem;
    font-weight: 600;
    padding: 0.4rem 0.85rem;
    border: 1px solid var(--color-border);
    border-radius: var(--radius-xs);
    background: var(--color-surface);
    color: var(--color-text);
    cursor: pointer;
    white-space: nowrap;
}

.pending-review__action:hover:not(:disabled) {
    border-color: var(--color-accent, #6366f1);
}

.pending-review__action:disabled {
    opacity: 0.55;
    cursor: not-allowed;
}

.pending-review__result {
    margin: 0.5rem 0 0;
    font-size: 0.82rem;
    color: var(--color-text-muted);
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

.section-hint {
    margin: -0.5rem 0 1rem 0;
    color: var(--color-text-muted);
    font-size: 0.85rem;
}

.thread-pass-row {
    border: 1px solid var(--color-border);
    border-radius: 0.5rem;
    cursor: default;
    flex-wrap: wrap;
}

.thread-pass-note {
    color: var(--color-text-muted);
    font-size: 0.85rem;
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

.outcome-badge {
    display: inline-flex;
    align-items: center;
    padding: 0.2rem 0.5rem;
    border-radius: var(--radius-pill);
    font-size: 0.8rem;
    white-space: nowrap;
}

.outcome-rejected {
    background: var(--color-warning-soft);
    color: var(--color-warning);
}

.outcome-fixed {
    background: rgba(34, 197, 94, 0.15);
    color: var(--color-success);
}

.outcome-dismissed {
    background: rgba(139, 92, 246, 0.15);
    color: #a78bfa;
}

.outcome-unknown {
    background: rgba(148, 163, 184, 0.15);
    color: var(--color-text-muted, #94a3b8);
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
