<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->

<template>
    <ModalDialog v-model:isOpen="vm.isSummaryModalOpen" title="Review Summary & Findings" size="large">
        <div class="summary-modal-layout">
            <header class="modal-filter-bar">
                <div class="findings-header">
                    <div class="header-main">
                        <h4>Findings Matrix</h4>
                        <div class="severity-summary-row mini-stats">
                            <div
                                v-for="(count, sev) in vm.severityCounts"
                                :key="sev"
                                class="sev-summary-pill"
                                :class="`pill-${sev}`"
                                v-show="count > 0"
                            >
                                <span class="pill-count">{{ count }}</span>
                            </div>
                        </div>
                    </div>
                    <div class="comments-filter-controls">
                        <i class="fi fi-rr-search filter-icon"></i>
                        <input v-model="vm.localSearchQuery" type="text" class="comment-search-input" placeholder="Search findings…" />
                        <div class="severity-pills">
                            <button
                                v-for="sev in severities"
                                :key="sev"
                                class="severity-pill"
                                :class="[`severity-pill--${sev}`, { 'severity-pill--active': vm.localSeverities.has(sev) }]"
                                :data-severity="sev"
                                @click="vm.toggleSeverity(sev)"
                            >
                                {{ sev }}
                            </button>
                        </div>
                    </div>
                </div>
            </header>

            <div class="modal-body-scroll">
                <section class="summary-text-section">
                    <details open class="summary-details">
                        <summary>Executive Summary</summary>
                        <div
                            v-if="vm.reviewStatus?.result?.summary"
                            class="markdown-content summary-full-text"
                            v-html="vm.renderMarkdown(vm.reviewStatus.result.summary)"
                        ></div>
                        <p v-else class="empty-state">No detailed summary available.</p>
                    </details>
                </section>

                <section class="summary-findings-section">
                    <div class="findings-list-container">
                        <JobProtocolCommentGroups
                            :vm="vm"
                            :groups="vm.groupedReviewComments"
                            empty-message="No findings match your filters."
                            show-dismiss
                            show-root-header
                        />
                    </div>
                </section>
            </div>
        </div>
    </ModalDialog>
</template>

<script setup lang="ts">
import ModalDialog from '@/components/ModalDialog.vue'
import type { JobProtocolViewModel } from '@/features/job-protocol/composables/useJobProtocolViewModel'
import JobProtocolCommentGroups from './JobProtocolCommentGroups.vue'

defineProps<{ vm: JobProtocolViewModel }>()

const severities = ['error', 'warning', 'info', 'suggestion']
</script>

<style scoped>
.summary-modal-layout {
    display: flex;
    flex-direction: column;
    height: 70vh;
    overflow: hidden;
}

.modal-filter-bar {
    padding: 1rem 1.5rem;
    border-bottom: 2px solid var(--color-border);
    z-index: 10;
}

.modal-body-scroll {
    flex: 1;
    overflow-y: auto;
    padding: 1.5rem;
    background: var(--color-surface);
}

.findings-header {
    display: flex;
    justify-content: space-between;
    align-items: center;
    gap: 1.5rem;
}

.header-main {
    display: flex;
    align-items: center;
    gap: 1rem;
}

.severity-summary-row {
    display: flex;
    gap: 0.75rem;
    margin-top: 1.5rem;
    flex-wrap: wrap;
}

.mini-stats {
    margin-top: 0 !important;
}

.sev-summary-pill {
    display: flex;
    align-items: center;
    gap: 0.5rem;
    padding: 0.35rem 0.85rem;
    border-radius: var(--radius-md);
    font-size: 0.85rem;
    font-weight: 600;
    text-transform: capitalize;
    border: 1px solid var(--color-border);
}

.mini-stats .sev-summary-pill {
    padding: 0.1rem 0.5rem;
    font-size: 0.75rem;
}

.pill-count {
    font-size: 1rem;
    font-family: monospace;
}

.pill-error { background: rgba(239, 68, 68, 0.1); border-color: rgba(239, 68, 68, 0.3); color: #ef4444; }
.pill-warning { background: rgba(234, 179, 8, 0.1); border-color: rgba(234, 179, 8, 0.3); color: #eab308; }
.pill-info { background: rgba(59, 130, 246, 0.1); border-color: rgba(59, 130, 246, 0.3); color: #3b82f6; }
.pill-suggestion { background: rgba(168, 85, 247, 0.1); border-color: rgba(168, 85, 247, 0.3); color: #a855f7; }

.comments-filter-controls {
    display: flex;
    align-items: center;
    gap: 0.75rem;
    flex-wrap: wrap;
}

.filter-icon {
    font-size: 0.9rem;
    opacity: 0.5;
    margin-right: -0.25rem;
}

.comment-search-input {
    background: rgba(255, 255, 255, 0.05);
    border: 1px solid var(--color-border);
    border-radius: var(--radius-sm);
    padding: 0.35rem 0.75rem;
    color: var(--color-text);
    font-size: 0.85rem;
    width: 200px;
}

.comment-search-input:focus {
    outline: none;
    border-color: var(--color-accent);
    background: rgba(255, 255, 255, 0.08);
}

.severity-pills {
    display: flex;
    gap: 0.35rem;
    flex-wrap: wrap;
}

.severity-pill {
    padding: 0.35rem 0.85rem;
    border-radius: var(--radius-md);
    font-size: 0.85rem;
    font-weight: 600;
    cursor: pointer;
    text-transform: capitalize;
    border: 1px solid var(--color-border);
    background: rgba(255, 255, 255, 0.05);
    color: var(--color-text-muted);
    transition: all 0.15s ease;
    display: flex;
    align-items: center;
    justify-content: center;
}

.severity-pill:hover {
    background: rgba(255, 255, 255, 0.1);
    color: var(--color-text);
    border-color: rgba(255, 255, 255, 0.2);
}

.severity-pill--error.severity-pill--active { background: rgba(239, 68, 68, 0.15); border-color: rgba(239, 68, 68, 0.4); color: #ef4444; }
.severity-pill--warning.severity-pill--active { background: rgba(234, 179, 8, 0.15); border-color: rgba(234, 179, 8, 0.4); color: #eab308; }
.severity-pill--info.severity-pill--active { background: rgba(59, 130, 246, 0.15); border-color: rgba(59, 130, 246, 0.4); color: #3b82f6; }
.severity-pill--suggestion.severity-pill--active { background: rgba(168, 85, 247, 0.15); border-color: rgba(168, 85, 247, 0.4); color: #a855f7; }

.summary-details {
    background: rgba(255, 255, 255, 0.02);
    border-radius: var(--radius-lg);
    border: 1px solid var(--color-border);
    margin-bottom: 2rem;
}

.summary-details summary {
    padding: 0.75rem 1.25rem;
    font-weight: 600;
    cursor: pointer;
    color: var(--color-accent);
    user-select: none;
    outline: none;
}

.summary-details summary:hover {
    background: rgba(255, 255, 255, 0.04);
}

.summary-full-text {
    padding: 0 1.25rem 1.25rem;
    max-height: 250px;
    overflow-y: auto;
    font-size: 0.95rem;
}

.findings-list-container {
    display: flex;
    flex-direction: column;
    gap: 1rem;
}

.markdown-content :deep(:first-child) {
    margin-top: 0;
}

.markdown-content :deep(:last-child) {
    margin-bottom: 0;
}

.markdown-content :deep(p) {
    margin-bottom: 0.75rem;
    line-height: 1.6;
}

.markdown-content :deep(ul),
.markdown-content :deep(ol) {
    margin-bottom: 0.75rem;
    padding-left: 1.5rem;
}

.markdown-content :deep(code) {
    background: rgba(255, 255, 255, 0.1);
    padding: 0.1rem 0.3rem;
    border-radius: var(--radius-xs);
    font-family: monospace;
}

.markdown-content :deep(pre) {
    background: var(--color-bg);
    border: 1px solid var(--color-border);
    padding: 1rem;
    border-radius: var(--radius-md);
    overflow-x: auto;
    margin-bottom: 1rem;
}

.markdown-content :deep(h1),
.markdown-content :deep(h2),
.markdown-content :deep(h3) {
    margin: 1rem 0 0.5rem 0;
}
</style>
