<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->

<template>
    <template v-if="groups.length">
        <div v-for="group in groups" :key="group.directory" class="comment-group">
            <div v-if="showRootHeader || group.directory !== 'Root'" class="comment-group-header">{{ group.directory }}</div>
            <ul class="json-comments-list synthesis-comments">
                <li
                    v-for="(comment, idx) in group.comments"
                    :key="idx"
                    class="json-comment-item synthesis-comment"
                    :class="`severity-${comment.severity}`"
                >
                    <div class="comment-header">
                        <strong class="comment-sev">{{ (comment.severity ?? 'note').toUpperCase() }}</strong>
                        <span class="monospace-value">{{ comment.filePath ?? comment.file_path }}:L{{ comment.lineNumber ?? comment.line_number }}</span>
                        <button
                            v-if="showDismiss && vm.routeClientId"
                            class="dismiss-btn"
                            :disabled="vm.dismissingIds.has(vm.commentKey(comment))"
                            title="Dismiss this finding"
                            @click.stop="vm.dismissComment(comment)"
                        >
                            {{ vm.dismissingIds.has(vm.commentKey(comment)) ? '…' : 'Dismiss' }}
                        </button>
                    </div>
                    <div class="comment-msg-container markdown-content">
                        <div v-html="vm.renderMarkdown(comment.message)"></div>
                    </div>
                </li>
            </ul>
        </div>
    </template>
    <p v-else class="comments-empty-state">{{ emptyMessage }}</p>
</template>

<script setup lang="ts">
import type { JobProtocolViewModel } from '@/features/job-protocol/composables/useJobProtocolViewModel'

defineProps<{
    vm: JobProtocolViewModel
    groups: Array<{ directory: string; comments: any[] }>
    emptyMessage: string
    showDismiss?: boolean
    showRootHeader?: boolean
}>()
</script>

<style scoped>
.comments-empty-state {
    text-align: center;
    color: var(--color-text-muted);
    font-style: italic;
    padding: 2rem 0;
}

.comment-group-header {
    font-size: 0.8rem;
    font-weight: 700;
    color: var(--color-text-muted);
    text-transform: uppercase;
    letter-spacing: 0.1em;
    margin: 2.5rem 0 1rem 0;
    padding-bottom: 0.5rem;
    border-bottom: 1px solid rgba(255, 255, 255, 0.08);
}

.synthesis-comments {
    list-style: none;
    padding: 0;
    margin: 0;
    display: flex;
    flex-direction: column;
    gap: 1rem;
}

.synthesis-comment {
    background: var(--color-bg);
    border: 1px solid var(--color-border);
    border-radius: var(--radius-md);
    padding: 1.5rem;
    display: flex;
    flex-direction: column;
    overflow: hidden;
}

.comment-header {
    display: flex;
    align-items: center;
    gap: 1rem;
    margin-bottom: 0.75rem;
    font-size: 0.85rem;
}

.comment-sev {
    font-weight: 700;
}

.comment-msg-container {
    width: 100%;
    overflow: hidden;
}

.json-comments-list {
    margin: 0;
    padding: 0 0 0 1.25rem;
    color: var(--color-text);
    display: flex;
    flex-direction: column;
    gap: 0.5rem;
}

.json-comment-item.severity-error {
    border-left: 2px solid var(--color-danger);
    padding-left: 0.5rem;
    list-style-type: none;
    margin-left: -1.25rem;
}

.json-comment-item.severity-warning {
    border-left: 2px solid #eab308;
    padding-left: 0.5rem;
    list-style-type: none;
    margin-left: -1.25rem;
}

.json-comment-item.severity-suggestion {
    border-left: 2px solid var(--color-accent);
    padding-left: 0.5rem;
    list-style-type: none;
    margin-left: -1.25rem;
}

.json-comment-item.severity-info,
.json-comment-item.severity-note {
    border-left: 2px solid #3b82f6;
    padding-left: 0.5rem;
    list-style-type: none;
    margin-left: -1.25rem;
}

.dismiss-btn {
    margin-left: auto;
    padding: 0.15rem 0.6rem;
    font-size: 0.75rem;
    border: 1px solid var(--color-border);
    border-radius: var(--radius-xs);
    background: rgba(255, 255, 255, 0.05);
    color: var(--color-text-muted);
    cursor: pointer;
    white-space: nowrap;
    flex-shrink: 0;
    transition: background 0.15s ease, color 0.15s ease;
}

.dismiss-btn:hover:not(:disabled) {
    background: rgba(255, 255, 255, 0.1);
    color: var(--color-text);
}

.dismiss-btn:disabled {
    opacity: 0.5;
    cursor: not-allowed;
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
