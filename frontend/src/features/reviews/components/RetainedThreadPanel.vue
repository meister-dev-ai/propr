<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->

<template>
    <div class="retained-thread-panel" data-testid="retained-thread-panel">
        <p v-if="threads.length === 0" class="retained-thread-empty" data-testid="retained-thread-empty">
            {{ emptyMessage }}
        </p>

        <ul v-else class="retained-thread-list">
            <li
                v-for="(thread, threadIdx) in threads"
                :key="thread.threadId ?? threadIdx"
                class="retained-thread"
                data-testid="retained-thread"
            >
                <div class="retained-thread-header">
                    <span
                        v-if="showFilePath && thread.filePath"
                        class="retained-thread-file monospace-value"
                        :title="thread.filePath"
                        data-testid="retained-thread-file"
                    >
                        {{ thread.filePath }}
                    </span>
                    <span v-if="thread.line != null" class="retained-thread-anchor monospace-value">
                        L{{ thread.line }}
                    </span>
                    <span
                        v-else-if="!(showFilePath && thread.filePath)"
                        class="retained-thread-anchor retained-thread-anchor--pr"
                    >
                        PR-level
                    </span>
                    <v-chip
                        v-if="thread.status"
                        size="x-small"
                        variant="tonal"
                        class="retained-thread-status"
                        :class="statusClass(thread.status)"
                        data-testid="retained-thread-status"
                    >
                        {{ thread.status }}
                    </v-chip>
                </div>

                <ol class="retained-comment-list">
                    <li
                        v-for="(comment, commentIdx) in thread.comments ?? []"
                        :key="comment.commentId ?? commentIdx"
                        class="retained-comment"
                        :class="comment.isAiAuthored ? 'retained-comment--ai' : 'retained-comment--human'"
                        :data-testid="comment.isAiAuthored ? 'retained-comment-ai' : 'retained-comment-human'"
                    >
                        <div class="retained-comment-meta">
                            <v-icon
                                size="small"
                                class="retained-comment-author-icon"
                                :icon="comment.isAiAuthored ? 'mdi-robot-outline' : 'mdi-account-outline'"
                            />
                            <span class="retained-comment-author">
                                {{ comment.authorIdentity || (comment.isAiAuthored ? 'AI reviewer' : 'Unknown author') }}
                            </span>
                            <v-chip
                                v-if="comment.isAiAuthored"
                                size="x-small"
                                variant="flat"
                                class="retained-comment-ai-chip"
                            >
                                AI
                            </v-chip>
                            <span v-if="comment.publishedAt" class="retained-comment-time">
                                {{ formatDate(comment.publishedAt) }}
                            </span>
                            <RouterLink
                                v-if="comment.originatingJobId"
                                :to="buildProtocolHref(comment.originatingJobId, clientId)"
                                class="retained-comment-trace-link"
                                data-testid="comment-trace-link"
                                :title="'View the review run that produced this comment'"
                            >
                                <v-icon size="x-small" icon="mdi-radar" />
                                View trace
                            </RouterLink>
                        </div>
                        <div
                            class="retained-comment-body markdown-content"
                            v-html="renderMarkdown(comment.body)"
                        ></div>
                    </li>
                </ol>
            </li>
        </ul>
    </div>
</template>

<script setup lang="ts">
import { renderMarkdown } from '@/features/job-protocol/utils/formatters'
import { buildProtocolHref, type RetainedThread } from '@/features/reviews/composables/useRetainedPrData'

interface Props {
    threads: RetainedThread[]
    /** Tenant context used to build the per-comment "View trace" link to the originating review run. */
    clientId: string
    emptyMessage?: string
    /** When true, each thread header surfaces its anchored file path (used in the combined conversation view). */
    showFilePath?: boolean
}

withDefaults(defineProps<Props>(), {
    emptyMessage: 'No comment threads retained for this selection.',
    showFilePath: false,
})

function statusClass(status: string | null | undefined): string {
    switch ((status ?? '').toLowerCase()) {
        case 'active':
            return 'retained-thread-status--active'
        case 'fixed':
            return 'retained-thread-status--fixed'
        case 'closed':
            return 'retained-thread-status--closed'
        case 'wontfix':
        case 'won\'t fix':
            return 'retained-thread-status--wontfix'
        case 'bydesign':
        case 'by design':
            return 'retained-thread-status--bydesign'
        default:
            return 'retained-thread-status--default'
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
.retained-thread-panel {
    width: 100%;
    min-width: 0;
}

.retained-thread-empty {
    color: var(--color-text-muted);
    font-style: italic;
    padding: 1rem 0;
}

.retained-thread-list {
    list-style: none;
    margin: 0;
    padding: 0;
    display: flex;
    flex-direction: column;
    gap: 1rem;
}

.retained-thread {
    border: 1px solid var(--color-border);
    border-radius: var(--radius-md);
    background: var(--color-bg);
    padding: 1rem;
}

.retained-thread-header {
    display: flex;
    align-items: center;
    gap: 0.6rem;
    margin-bottom: 0.75rem;
}

.retained-thread-anchor {
    font-size: 0.78rem;
    color: var(--color-text-muted);
}

.retained-thread-file {
    font-size: 0.78rem;
    color: var(--color-text);
    font-weight: 600;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
    max-width: 100%;
}

.retained-thread-anchor--pr {
    text-transform: uppercase;
    letter-spacing: 0.05em;
    font-weight: 600;
}

.retained-thread-status {
    text-transform: uppercase;
    letter-spacing: 0.04em;
    font-weight: 700;
}

.retained-thread-status--active {
    color: var(--color-info);
}

.retained-thread-status--fixed {
    color: var(--color-success);
}

.retained-thread-status--wontfix,
.retained-thread-status--closed {
    color: var(--color-warning);
}

.retained-comment-list {
    list-style: none;
    margin: 0;
    padding: 0;
    display: flex;
    flex-direction: column;
    gap: 0.6rem;
}

.retained-comment {
    padding: 0.5rem 0;
}

.retained-comment-meta {
    display: flex;
    align-items: center;
    gap: 0.5rem;
    margin-bottom: 0.3rem;
    font-size: 0.78rem;
}

.retained-comment-author-icon {
    color: var(--color-text-muted);
}

.retained-comment--ai .retained-comment-author-icon {
    color: var(--color-accent);
}

.retained-comment-author {
    font-weight: 600;
    color: var(--color-text);
}

.retained-comment-ai-chip {
    background: rgba(34, 211, 238, 0.18);
    color: var(--color-accent);
    font-weight: 700;
}

.retained-comment-time {
    margin-left: auto;
    font-family: monospace;
    color: var(--color-text-muted);
}

.retained-comment-trace-link {
    display: inline-flex;
    align-items: center;
    gap: 0.2rem;
    color: var(--color-text-muted);
    text-decoration: none;
    font-size: 0.72rem;
    white-space: nowrap;
}

/* When no timestamp pushed the row, the link still hugs the right edge. */
.retained-comment-time + .retained-comment-trace-link {
    margin-left: 0.5rem;
}

.retained-comment-trace-link:not(.retained-comment-time + .retained-comment-trace-link) {
    margin-left: auto;
}

.retained-comment-trace-link:hover {
    color: var(--color-accent);
    text-decoration: underline;
}

.retained-comment-body {
    margin: 0;
    font-size: 0.88rem;
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
