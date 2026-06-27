<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->

<template>
    <div class="page-view">
        <AppTopBar class="header-stack">
            <div class="header-nav-links">
                <RouterLink class="back-link" :to="vm.backToReviewsLink">← Back to reviews</RouterLink>
                <RouterLink v-if="vm.prReviewLink" :to="vm.prReviewLink" class="back-link pr-view-link">PR Review ↗</RouterLink>
                <button
                    v-if="vm.canRestart"
                    class="restart-review-btn"
                    :disabled="vm.restarting"
                    title="Restart this failed review"
                    @click="vm.restart()"
                >
                    {{ vm.restarting ? 'Restarting…' : 'Restart review ↻' }}
                </button>
            </div>
            <h2>Job Protocol</h2>
        </AppTopBar>

        <p v-if="vm.loading" class="loading">Loading…</p>
        <p v-else-if="vm.error" class="error">{{ vm.error }}</p>
        <p v-else-if="vm.protocols.length === 0" class="empty-state">No protocol available for this job.</p>

        <template v-else>
            <div class="job-stat-strip compact-stats">
                <div class="stat-pill"><span class="stat-label">Job</span><span class="stat-value monospace-value" :title="vm.protocols[0].jobId">{{ vm.jobShortId }}</span></div>
                <div class="stat-pill"><span class="stat-label">Duration</span><span class="stat-value">{{ vm.overallDuration }}</span></div>
                <div class="stat-pill"><span class="stat-label">Visible Passes</span><span class="stat-value" data-testid="visible-pass-count">{{ vm.visiblePassCount }}</span></div>
                <div class="stat-pill"><span class="stat-label">Inherited</span><span class="stat-value">{{ vm.inheritedProtocolCount }}</span></div>
                <div class="stat-pill"><span class="stat-label">Total Tokens</span><span class="stat-value fat-tokens">{{ vm.formatTokens(vm.totalInputTokens + vm.totalOutputTokens) }}</span></div>
                <div class="stat-pill"><span class="stat-label">Cached Input</span><span class="stat-value fat-tokens">{{ vm.formatTokens(vm.totalCachedInputTokens) }}</span></div>
                <div class="stat-pill"><span class="stat-label">Effective Input</span><span class="stat-value fat-tokens">{{ vm.formatTokens(vm.totalEffectiveInputTokens) }}</span></div>
            </div>

            <div class="detail-tabs">
                <button class="tab-btn" :class="{ 'tab-active': vm.activeTab === 'summary' }" @click="vm.activeTab = 'summary'">Review Summary</button>
                <button class="tab-btn" :class="{ 'tab-active': vm.activeTab === 'traces' }" @click="vm.activeTab = 'traces'">Execution Traces</button>
                <button class="tab-btn" :class="{ 'tab-active': vm.activeTab === 'tokens' }" @click="vm.activeTab = 'tokens'">Token Breakdown</button>
            </div>

            <JobProtocolSummaryTab v-if="vm.activeTab === 'summary'" :vm="vm" />
            <JobProtocolTraceTab v-else-if="vm.activeTab === 'traces'" :vm="vm" />
            <JobProtocolTokensTab v-else-if="vm.activeTab === 'tokens'" :vm="vm" />

            <JobProtocolEventModal :vm="vm" />
            <JobProtocolSummaryModal :vm="vm" />
        </template>

        <Transition name="toast-fade">
            <div v-if="vm.dismissToast" class="dismiss-toast" :class="{ 'dismiss-toast--error': vm.dismissToast.isError }">
                {{ vm.dismissToast.message }}
            </div>
        </Transition>
    </div>
</template>

<script setup lang="ts">
import { RouterLink } from 'vue-router'
import { AppTopBar } from '@/components'
import JobProtocolEventModal from '@/features/job-protocol/components/JobProtocolEventModal.vue'
import JobProtocolSummaryModal from '@/features/job-protocol/components/JobProtocolSummaryModal.vue'
import JobProtocolSummaryTab from '@/features/job-protocol/components/JobProtocolSummaryTab.vue'
import JobProtocolTokensTab from '@/features/job-protocol/components/JobProtocolTokensTab.vue'
import JobProtocolTraceTab from '@/features/job-protocol/components/JobProtocolTraceTab.vue'
import { useJobProtocolViewModel } from '@/features/job-protocol/composables/useJobProtocolViewModel'

const vm = useJobProtocolViewModel()
</script>

<style scoped>
.header-stack {
    margin-bottom: 2rem;
}

.header-nav-links {
    display: flex;
    gap: 1rem;
    align-items: center;
    margin-bottom: 0.5rem;
}

.pr-view-link {
    color: var(--color-accent) !important;
}

.restart-review-btn {
    margin-left: auto;
    font-size: 0.85rem;
    padding: 0.3rem 0.75rem;
    border-radius: var(--radius-md);
    color: var(--color-warning);
    background: transparent;
    border: 1px solid rgba(245, 158, 11, 0.35);
    cursor: pointer;
    white-space: nowrap;
}

.restart-review-btn:hover:not(:disabled) {
    background: rgba(245, 158, 11, 0.1);
    border-color: rgba(245, 158, 11, 0.6);
}

.restart-review-btn:disabled {
    opacity: 0.6;
    cursor: not-allowed;
}

.job-stat-strip {
    display: flex;
    flex-wrap: wrap;
    gap: 2rem;
    margin-bottom: 2rem;
    padding: 1.5rem;
    background: var(--color-surface);
    border: 1px solid var(--color-border);
    border-radius: var(--radius-lg);
}

.stat-pill {
    display: flex;
    flex-direction: column;
    gap: 0.25rem;
}

.stat-label {
    font-size: 0.8rem;
    color: var(--color-text-muted);
    font-weight: 600;
    text-transform: uppercase;
    letter-spacing: 0.05em;
}

.stat-value {
    font-size: 1.25rem;
    font-weight: 600;
    color: var(--color-text);
}

.monospace-value {
    font-family: var(--font-mono, monospace);
    font-size: 1.1rem;
    overflow: hidden;
    text-overflow: ellipsis;
}

.compact-stats {
    margin-bottom: 1.5rem !important;
}

.dismiss-toast {
    position: fixed;
    bottom: 1.5rem;
    right: 1.5rem;
    padding: 0.75rem 1.25rem;
    border-radius: var(--radius-md);
    background: var(--color-surface);
    border: 1px solid var(--color-border);
    color: var(--color-text);
    font-size: 0.9rem;
    font-weight: 500;
    box-shadow: 0 4px 16px rgba(0, 0, 0, 0.3);
    z-index: 9999;
}

.dismiss-toast--error {
    border-color: var(--color-danger);
    color: var(--color-danger);
}

.toast-fade-enter-active,
.toast-fade-leave-active {
    transition: opacity 0.25s ease, transform 0.25s ease;
}

.toast-fade-enter-from,
.toast-fade-leave-to {
    opacity: 0;
    transform: translateY(8px);
}
</style>
