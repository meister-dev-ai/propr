<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->

<template>
    <section class="section-card retained-archive-section" data-testid="retained-archive-section">
        <h3 class="section-title">Conversation</h3>

        <p v-if="retained.loading.value" class="retained-loading" data-testid="retained-loading">Loading retained data…</p>

        <p v-else-if="retained.error.value" class="retained-error" data-testid="retained-error">
            {{ retained.error.value }}
        </p>

        <p v-else-if="retained.empty.value" class="retained-notice" data-testid="retained-empty">
            <i class="fi fi-rr-info" aria-hidden="true"></i>
            <span>
                No retained data for this pull request. Retention may be disabled for this connection, or the retention
                window has elapsed.
            </span>
        </p>

        <RetainedThreadPanel
            v-else
            :threads="conversationThreads"
            :client-id="clientId"
            :show-file-path="true"
            empty-message="No retained discussion for this pull request."
        />
    </section>
</template>

<script setup lang="ts">
import { computed } from 'vue'
import RetainedThreadPanel from '@/features/reviews/components/RetainedThreadPanel.vue'
import type { RetainedThread, UseRetainedPrData } from '@/features/reviews/composables/useRetainedPrData'

const props = defineProps<{
    retained: UseRetainedPrData
    /** Tenant context threaded into the per-comment "View trace" links. */
    clientId: string
}>()

// The combined conversation surfaces the pull-request-level discussion first, then the
// file-anchored threads, so the whole exchange reads top-to-bottom as one conversation.
const conversationThreads = computed<RetainedThread[]>(() => {
    const prLevel = props.retained.prLevelThreads.value
    const fileAnchored = props.retained.threads.value.filter(thread => !!thread.filePath)
    return [...prLevel, ...fileAnchored]
})
</script>

<style scoped>
.retained-archive-section {
    margin-bottom: 1.25rem;
}

.section-card {
    background: var(--color-surface);
    border: 1px solid var(--color-border);
    border-radius: 0.5rem;
    padding: 1.25rem;
}

.section-title {
    margin: 0 0 1rem 0;
    font-size: 1rem;
    font-weight: 600;
}

.retained-loading,
.retained-error {
    color: var(--color-text-muted);
}

.retained-error {
    color: var(--color-danger);
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
