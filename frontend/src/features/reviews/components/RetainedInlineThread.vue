<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->

<template>
    <div class="retained-inline-thread" data-testid="inline-thread" :data-line="thread.line ?? undefined">
        <div class="retained-inline-thread-header">
            <i class="fi fi-rr-comment-alt" aria-hidden="true"></i>
            <span v-if="thread.line != null" class="retained-inline-thread-anchor monospace-value">
                L{{ thread.line }}
            </span>
            <v-chip
                v-if="thread.status"
                size="x-small"
                variant="tonal"
                class="retained-inline-thread-status"
                :class="statusClass(thread.status)"
                data-testid="inline-thread-status"
            >
                {{ thread.status }}
            </v-chip>
        </div>

        <ol class="retained-inline-comment-list">
            <li
                v-for="(comment, commentIdx) in thread.comments ?? []"
                :key="comment.commentId ?? commentIdx"
                class="retained-inline-comment"
                :class="comment.isAiAuthored ? 'retained-inline-comment--ai' : 'retained-inline-comment--human'"
                :data-testid="comment.isAiAuthored ? 'inline-comment-ai' : 'inline-comment-human'"
            >
                <div class="retained-inline-comment-meta">
                    <v-icon
                        size="small"
                        class="retained-inline-comment-author-icon"
                        :icon="comment.isAiAuthored ? 'mdi-robot-outline' : 'mdi-account-outline'"
                    />
                    <span class="retained-inline-comment-author">
                        {{ comment.authorIdentity || (comment.isAiAuthored ? 'AI reviewer' : 'Unknown author') }}
                    </span>
                    <v-chip
                        v-if="comment.isAiAuthored"
                        size="x-small"
                        variant="flat"
                        class="retained-inline-comment-ai-chip"
                    >
                        AI
                    </v-chip>
                    <span v-if="comment.publishedAt" class="retained-inline-comment-time">
                        {{ formatDate(comment.publishedAt) }}
                    </span>
                    <RouterLink
                        v-if="comment.originatingJobId"
                        :to="buildProtocolHref(comment.originatingJobId, clientId)"
                        class="retained-inline-comment-trace-link"
                        data-testid="comment-trace-link"
                        :title="'View the review run that produced this comment'"
                    >
                        <v-icon size="x-small" icon="mdi-radar" />
                        View trace
                    </RouterLink>
                </div>
                <div
                    class="retained-inline-comment-body markdown-content"
                    v-html="renderMarkdown(comment.body)"
                ></div>
            </li>
        </ol>
    </div>
</template>

<script setup lang="ts">
import { renderMarkdown } from '@/features/job-protocol/utils/formatters'
import { buildProtocolHref, type RetainedThread } from '@/features/reviews/composables/useRetainedPrData'

defineProps<{
    thread: RetainedThread
    /** Tenant context used to build the per-comment "View trace" link to the originating review run. */
    clientId: string
}>()

function statusClass(status: string | null | undefined): string {
    switch ((status ?? '').toLowerCase()) {
        case 'active':
            return 'retained-inline-thread-status--active'
        case 'fixed':
            return 'retained-inline-thread-status--fixed'
        case 'closed':
            return 'retained-inline-thread-status--closed'
        case 'wontfix':
        case 'won\'t fix':
            return 'retained-inline-thread-status--wontfix'
        case 'bydesign':
        case 'by design':
            return 'retained-inline-thread-status--bydesign'
        default:
            return 'retained-inline-thread-status--default'
    }
}

function formatDate(iso: string): string {
    if (!iso) return ''
    const date = new Date(iso)
    if (Number.isNaN(date.getTime())) return iso
    return date.toLocaleDateString() + ' ' + date.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })
}
</script>

<style scoped>
.retained-inline-thread {
    border: 1px solid var(--color-border);
    border-radius: var(--radius-md);
    background: var(--color-surface);
    padding: 0.65rem 0.85rem;
    margin: 0.35rem 0.75rem 0.55rem;
    /* Inline threads sit inside the diff's monospaced table; restore proportional text. */
    font-family: 'Inter', sans-serif;
    white-space: normal;
}

.retained-inline-thread-header {
    display: flex;
    align-items: center;
    gap: 0.5rem;
    margin-bottom: 0.5rem;
}

.retained-inline-thread-header i {
    color: var(--color-accent);
    flex: 0 0 auto;
}

.retained-inline-thread-anchor {
    font-size: 0.75rem;
    color: var(--color-text-muted);
}

.retained-inline-thread-status {
    text-transform: uppercase;
    letter-spacing: 0.04em;
    font-weight: 700;
}

.retained-inline-thread-status--active {
    color: var(--color-info);
}

.retained-inline-thread-status--fixed {
    color: var(--color-success);
}

.retained-inline-thread-status--wontfix,
.retained-inline-thread-status--closed {
    color: var(--color-warning);
}

.retained-inline-comment-list {
    list-style: none;
    margin: 0;
    padding: 0;
    display: flex;
    flex-direction: column;
    gap: 0.5rem;
}

.retained-inline-comment {
    padding: 0.35rem 0;
}

.retained-inline-comment-meta {
    display: flex;
    align-items: center;
    gap: 0.45rem;
    margin-bottom: 0.25rem;
    font-size: 0.75rem;
}

.retained-inline-comment-author-icon {
    color: var(--color-text-muted);
}

.retained-inline-comment--ai .retained-inline-comment-author-icon {
    color: var(--color-accent);
}

.retained-inline-comment-author {
    font-weight: 600;
    color: var(--color-text);
}

.retained-inline-comment-ai-chip {
    background: rgba(34, 211, 238, 0.18);
    color: var(--color-accent);
    font-weight: 700;
}

.retained-inline-comment-time {
    margin-left: auto;
    font-family: monospace;
    color: var(--color-text-muted);
}

.retained-inline-comment-trace-link {
    display: inline-flex;
    align-items: center;
    gap: 0.2rem;
    color: var(--color-text-muted);
    text-decoration: none;
    font-size: 0.7rem;
    white-space: nowrap;
}

.retained-inline-comment-time + .retained-inline-comment-trace-link {
    margin-left: 0.5rem;
}

.retained-inline-comment-trace-link:not(.retained-inline-comment-time + .retained-inline-comment-trace-link) {
    margin-left: auto;
}

.retained-inline-comment-trace-link:hover {
    color: var(--color-accent);
    text-decoration: underline;
}

.retained-inline-comment-body {
    margin: 0;
    font-size: 0.85rem;
    line-height: 1.5;
    color: var(--color-text);
    overflow-wrap: anywhere;
    overflow: hidden;
}

.markdown-content :deep(:first-child) {
    margin-top: 0;
}

.markdown-content :deep(:last-child) {
    margin-bottom: 0;
}

.markdown-content :deep(p) {
    margin-bottom: 0.5rem;
    line-height: 1.55;
}

.markdown-content :deep(ul),
.markdown-content :deep(ol) {
    margin-bottom: 0.5rem;
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
    padding: 0.75rem;
    border-radius: var(--radius-md);
    overflow-x: auto;
    margin-bottom: 0.75rem;
}
</style>
