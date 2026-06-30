<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->

<template>
    <section class="section-card retained-archive-section" data-testid="retained-archive-section">
        <h3 class="section-title">File Browser</h3>

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

        <div v-else class="retained-archive-grid">
            <div class="retained-archive-files">
                <h4 class="retained-subhead">Changed files ({{ retained.files.value.length }})</h4>
                <RetainedFileList
                    :files="retained.files.value"
                    :selected-file-path="selectedFilePath"
                    :comment-count="retained.commentCountForFile"
                    :thread-count="retained.threadCountForFile"
                    @select="onSelectFile"
                />
            </div>

            <div class="retained-archive-detail">
                <p v-if="!selectedFilePath" class="retained-notice" data-testid="retained-no-selection">
                    <i class="fi fi-rr-arrow-left" aria-hidden="true"></i>
                    <span>Select a file to view its retained diff and comment threads.</span>
                </p>

                <template v-else>
                    <h4 class="retained-subhead">Diff</h4>
                    <JobProtocolDiffViewer
                        :file-result-id="selectedFilePath"
                        :diff="selectedDiff"
                        :loading="diffLoading"
                        :diff-error="diffError"
                        :on-retry="retryDiff"
                        :inline-threads="inlineThreads"
                        @update:anchored-ids="onAnchoredIdsChange"
                    >
                        <template #thread="{ thread }">
                            <RetainedInlineThread :thread="(thread.payload as RetainedThread)" :client-id="clientId" />
                        </template>
                    </JobProtocolDiffViewer>

                    <template v-if="unanchoredThreads.length > 0">
                        <h4 class="retained-subhead retained-subhead--threads">Unanchored comments</h4>
                        <RetainedThreadPanel
                            :threads="unanchoredThreads"
                            :client-id="clientId"
                            empty-message="No comment threads retained for this file."
                        />
                    </template>
                </template>
            </div>
        </div>
    </section>
</template>

<script setup lang="ts">
import { computed, ref } from 'vue'
import JobProtocolDiffViewer, {
    type InlineDiffThread,
} from '@/features/job-protocol/components/JobProtocolDiffViewer.vue'
import RetainedFileList from '@/features/reviews/components/RetainedFileList.vue'
import RetainedThreadPanel from '@/features/reviews/components/RetainedThreadPanel.vue'
import RetainedInlineThread from '@/features/reviews/components/RetainedInlineThread.vue'
import type {
    RetainedFile,
    RetainedThread,
    UseRetainedPrData,
    ViewerFileDiff,
} from '@/features/reviews/composables/useRetainedPrData'

const props = defineProps<{
    retained: UseRetainedPrData
    /** Tenant context threaded into the per-comment "View trace" links. */
    clientId: string
}>()

const selectedFilePath = ref<string | null>(null)
const selectedRevisionKey = ref<string | null>(null)
const selectedDiff = ref<ViewerFileDiff | null>(null)
const diffLoading = ref(false)
const diffError = ref<string | null>(null)
const anchoredThreadIds = ref<Set<string>>(new Set())

const selectedFileThreads = computed<RetainedThread[]>(() => {
    if (!selectedFilePath.value) return []
    return props.retained.threads.value.filter(thread => thread.filePath === selectedFilePath.value)
})

/** Stable id for a thread, even when the provider thread id is missing. */
function threadKey(thread: RetainedThread, index: number): string {
    return thread.threadId ?? `thread-${index}`
}

// The diff viewer anchors these by their NEW-side `line`; whatever it can't place falls back to
// the below-diff "Unanchored comments" list (see `unanchoredThreads`).
const inlineThreads = computed<InlineDiffThread<RetainedThread>[]>(() =>
    selectedFileThreads.value.map((thread, index) => ({
        id: threadKey(thread, index),
        line: thread.line,
        payload: thread,
    })),
)

const unanchoredThreads = computed<RetainedThread[]>(() =>
    selectedFileThreads.value.filter(
        (thread, index) => !anchoredThreadIds.value.has(threadKey(thread, index)),
    ),
)

function onAnchoredIdsChange(ids: string[]): void {
    anchoredThreadIds.value = new Set(ids)
}

async function loadDiff(): Promise<void> {
    if (!selectedFilePath.value) return
    diffLoading.value = true
    diffError.value = null
    selectedDiff.value = null
    try {
        selectedDiff.value = await props.retained.loadFileDiff(selectedFilePath.value, selectedRevisionKey.value)
    } catch (err) {
        diffError.value = err instanceof Error ? err.message : 'Failed to load the retained file diff.'
    } finally {
        diffLoading.value = false
    }
}

async function onSelectFile(file: RetainedFile): Promise<void> {
    if (!file.filePath) return
    selectedFilePath.value = file.filePath
    selectedRevisionKey.value = file.revisionKey ?? null
    await loadDiff()
}

function retryDiff(): void {
    void loadDiff()
}
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

.retained-subhead {
    margin: 0 0 0.75rem 0;
    font-size: 0.8rem;
    font-weight: 700;
    text-transform: uppercase;
    letter-spacing: 0.08em;
    color: var(--color-text-muted);
}

.retained-subhead--threads {
    margin-top: 1.25rem;
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

.retained-archive-grid {
    display: grid;
    grid-template-columns: minmax(16rem, 22rem) minmax(0, 1fr);
    gap: 1.25rem;
    align-items: start;
}

@media (max-width: 900px) {
    .retained-archive-grid {
        grid-template-columns: 1fr;
    }
}

.retained-archive-files {
    min-width: 0;
}

.retained-archive-detail {
    min-width: 0;
}
</style>
