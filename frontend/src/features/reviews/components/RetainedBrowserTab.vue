<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->

<template>
    <section class="retained-archive-section" data-testid="retained-archive-section" aria-label="File browser">
        <p v-if="retained.loading.value" class="retained-notice" data-testid="retained-loading">Loading retained data…</p>
        <p v-else-if="retained.error.value" class="retained-error" data-testid="retained-error">{{ retained.error.value }}</p>
        <p v-else-if="retained.empty.value" class="retained-notice" data-testid="retained-empty">
            No retained data for this pull request. Retention may be disabled for this connection, or the retention window has elapsed.
        </p>
        <template v-else>
            <div class="browser-mobile-toolbar">
                <button type="button" :aria-expanded="mobileFilesOpen" aria-controls="browser-file-pane" @click="mobileFilesOpen = !mobileFilesOpen">
                    <v-icon size="small" icon="mdi-file-tree-outline" /> Files ({{ filteredFiles.length }})
                </button>
            </div>
            <div class="retained-archive-grid" :class="{ 'mobile-files-open': mobileFilesOpen }">
                <aside id="browser-file-pane" class="retained-archive-files" aria-label="Changed files">
                    <div class="browser-file-controls">
                        <div class="browser-files-heading">
                            <h3>Changed files</h3>
                            <span data-testid="browser-file-count">{{ filteredFiles.length }} / {{ retained.files.value.length }}</span>
                        </div>
                        <input v-model="filters.query" type="search" placeholder="Search files…" aria-label="Search files" />
                        <div class="browser-filter-grid">
                            <label>Findings
                                <select v-model="filters.presence" aria-label="Filter files by findings">
                                    <option value="all">All files</option>
                                    <option value="findings">With findings</option>
                                    <option value="withoutFindings">Without findings</option>
                                    <option value="comments">With comments</option>
                                </select>
                            </label>
                            <template v-if="showFindingFilters">
                                <label>Kind
                                    <select v-model="filters.kind" aria-label="Filter finding kinds" :disabled="metadataLoading || !!metadataError">
                                        <option value="">All kinds</option>
                                        <option v-for="kind in kinds" :key="kind" :value="kind">{{ findingLabel(kind) }}</option>
                                    </select>
                                </label>
                                <label>Severity
                                    <select v-model="filters.severity" aria-label="Filter finding severity" :disabled="metadataLoading || !!metadataError">
                                        <option value="">All severities</option>
                                        <option value="error">Error</option>
                                        <option value="warning">Warning</option>
                                        <option value="suggestion">Suggestion</option>
                                        <option value="info">Info</option>
                                    </select>
                                </label>
                                <label>Outcome
                                    <select v-model="filters.outcome" aria-label="Filter finding outcomes" :disabled="metadataLoading || !!metadataError">
                                        <option value="">All outcomes</option>
                                        <option value="pending">Pending</option>
                                        <option value="addressed">Addressed</option>
                                        <option value="acknowledged">Acknowledged</option>
                                        <option value="dismissed">Dismissed</option>
                                        <option value="falsepositive">False positive</option>
                                        <option value="discussed">Discussed</option>
                                        <option value="duplicate">Duplicate</option>
                                    </select>
                                </label>
                            </template>
                        </div>
                        <button v-if="hasFilters" type="button" class="browser-clear" data-testid="browser-clear-filters" @click="clearFilters">Clear filters</button>
                        <p v-if="metadataLoading" class="browser-metadata-notice" role="status">
                            {{ findings.length ? 'Refreshing finding details…' : 'Loading finding details…' }}
                        </p>
                        <p v-if="metadataError" class="browser-metadata-notice" role="status">
                            {{ findings.length ? 'Finding details refresh failed. Showing the last loaded details.' : 'Finding details unavailable.' }} {{ metadataError }}
                            <button v-if="onRetryMetadata" type="button" class="browser-clear" @click="onRetryMetadata">Retry</button>
                        </p>
                    </div>
                    <div class="browser-file-scroll" data-testid="browser-file-scroll" tabindex="0" role="region" aria-label="Changed file list">
                        <p v-if="filteredFiles.length === 0" class="retained-notice" data-testid="browser-no-matches">No files match these filters.</p>
                        <RetainedFileList
                            v-else
                            :files="filteredFiles"
                            :selected-file-path="selectedFilePath"
                            :comment-count="retained.commentCountForFile"
                            :thread-count="retained.threadCountForFile"
                            :finding-count="countFindings"
                            :expand-all="hasFilters"
                            @select="onSelectFile"
                        />
                    </div>
                </aside>
                <div class="retained-archive-detail" data-testid="browser-diff-scroll" tabindex="0" role="region" aria-label="Diff and comments">
                    <p v-if="!selectedFilePath" class="retained-notice" data-testid="retained-no-selection">Select a file to view its retained diff and comment threads.</p>
                    <template v-else>
                        <JobProtocolDiffViewer
                            :key="selectedFilePath"
                            :file-result-id="selectedFilePath"
                            :diff="selectedDiff"
                            :loading="diffLoading"
                            :diff-error="diffError"
                            :on-retry="retryDiff"
                            :inline-threads="inlineThreads"
                            scroll-mode="content"
                            @update:anchored-ids="onAnchoredIdsChange"
                        >
                            <template #thread="{ thread }">
                                <RetainedInlineThread :thread="(thread.payload as RetainedThread)" :client-id="clientId" :findings="findings" />
                            </template>
                        </JobProtocolDiffViewer>
                        <template v-if="unanchoredThreads.length > 0">
                            <h4 class="retained-subhead">Unanchored comments</h4>
                            <RetainedThreadPanel :threads="unanchoredThreads" :client-id="clientId" :findings="findings" />
                        </template>
                    </template>
                </div>
            </div>
        </template>
    </section>
</template>

<script setup lang="ts">
import { computed, onBeforeUnmount, reactive, ref, watch } from 'vue'
import JobProtocolDiffViewer, { type InlineDiffThread } from '@/features/job-protocol/components/JobProtocolDiffViewer.vue'
import RetainedFileList from './RetainedFileList.vue'
import RetainedThreadPanel from './RetainedThreadPanel.vue'
import RetainedInlineThread from './RetainedInlineThread.vue'
import { filterBrowserFiles, findingCountForFile, findingLabel, normalizeFilePath, type BrowserFileFilters } from './reviewBrowserFindings'
import type { CodeInsightFinding } from '@/services/codeInsightsAnalyticsService'
import type { RetainedFile, RetainedThread, UseRetainedPrData, ViewerFileDiff } from '@/features/reviews/composables/useRetainedPrData'

const props = withDefaults(defineProps<{
    retained: UseRetainedPrData
    clientId: string
    findings?: CodeInsightFinding[]
    showFindingFilters?: boolean
    metadataLoading?: boolean
    metadataError?: string | null
    onRetryMetadata?: () => void
}>(), { findings: () => [], showFindingFilters: false, metadataLoading: false, metadataError: null })

const filters = reactive<BrowserFileFilters>({ query: '', presence: 'all', kind: '', severity: '', outcome: '' })
const mobileFilesOpen = ref(false)
const selectedFilePath = ref<string | null>(null)
const selectedRevisionKey = ref<string | null>(null)
const selectedDiff = ref<ViewerFileDiff | null>(null)
const diffLoading = ref(false)
const diffError = ref<string | null>(null)
const anchoredThreadIds = ref<Set<string>>(new Set())
let diffRequest = 0

const filteredFiles = computed(() => filterBrowserFiles(props.retained.files.value, props.retained.threads.value, props.findings, filters))
const kinds = computed(() => [...new Set(props.findings.flatMap(finding => finding.coreTags))].sort())
const hasFilters = computed(() => !!filters.query.trim() || filters.presence !== 'all' || !!filters.kind || !!filters.severity || !!filters.outcome)
const selectedFileThreads = computed(() => !selectedFilePath.value ? [] : props.retained.threads.value.filter(
    thread => normalizeFilePath(thread.filePath) === normalizeFilePath(selectedFilePath.value),
))

function countFindings(path: string): number {
    return findingCountForFile(path, props.retained.threads.value, props.findings)
}
function clearFilters(): void {
    Object.assign(filters, { query: '', presence: 'all', kind: '', severity: '', outcome: '' })
}
function threadKey(thread: RetainedThread, index: number): string { return thread.threadId ?? 'thread-' + index }
const inlineThreads = computed<InlineDiffThread<RetainedThread>[]>(() => selectedFileThreads.value.map((thread, index) => ({ id: threadKey(thread, index), line: thread.line, payload: thread })))
const unanchoredThreads = computed(() => selectedFileThreads.value.filter((thread, index) => !anchoredThreadIds.value.has(threadKey(thread, index))))
function onAnchoredIdsChange(ids: string[]): void { anchoredThreadIds.value = new Set(ids) }

function clearSelection(): void {
    diffRequest++
    selectedFilePath.value = null
    selectedRevisionKey.value = null
    selectedDiff.value = null
    diffLoading.value = false
    diffError.value = null
    anchoredThreadIds.value = new Set()
}

async function loadDiff(): Promise<void> {
    if (!selectedFilePath.value) {
        clearSelection()
        return
    }
    const request = ++diffRequest
    diffLoading.value = true
    diffError.value = null
    selectedDiff.value = null
    anchoredThreadIds.value = new Set()
    try {
        const diff = await props.retained.loadFileDiff(selectedFilePath.value, selectedRevisionKey.value)
        if (request === diffRequest) selectedDiff.value = diff
    } catch (err) {
        if (request === diffRequest) diffError.value = err instanceof Error ? err.message : 'Failed to load the retained file diff.'
    } finally {
        if (request === diffRequest) diffLoading.value = false
    }
}
async function onSelectFile(file: RetainedFile): Promise<void> {
    if (!file.filePath) return
    selectedFilePath.value = file.filePath
    selectedRevisionKey.value = file.revisionKey ?? null
    mobileFilesOpen.value = false
    await loadDiff()
}
function retryDiff(): void { void loadDiff() }
watch(filteredFiles, files => {
    if (selectedFilePath.value && !files.some(file =>
        normalizeFilePath(file.filePath) === normalizeFilePath(selectedFilePath.value)
        && (file.revisionKey ?? null) === selectedRevisionKey.value,
    )) {
        clearSelection()
    }
})
watch(() => props.showFindingFilters, available => {
    if (!available) Object.assign(filters, { kind: '', severity: '', outcome: '' })
})
watch(() => props.retained, () => {
    clearSelection()
    clearFilters()
})
onBeforeUnmount(() => { diffRequest++ })
</script>

<style scoped>
.retained-archive-section {
    display: flex;
    flex-direction: column;
    height: 100%;
    min-height: 0;
    overflow: hidden;
}
.retained-archive-grid {
    display: grid;
    grid-template-columns: clamp(15rem, 23vw, 22rem) minmax(0, 1fr);
    flex: 1;
    min-height: 0;
    position: relative;
    border: 1px solid var(--color-border);
    border-radius: var(--radius-md);
    overflow: hidden;
}
.retained-archive-files {
    display: flex;
    flex-direction: column;
    min-height: 0;
    min-width: 0;
    background: var(--color-surface);
    border-right: 1px solid var(--color-border);
}
.browser-file-controls {
    flex: 0 0 auto;
    padding: 0.75rem;
    border-bottom: 1px solid var(--color-border);
}
.browser-files-heading {
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 0.5rem;
    margin-bottom: 0.5rem;
}
.browser-files-heading h3 {
    margin: 0;
    font-size: 0.85rem;
}
.browser-files-heading span {
    color: var(--color-text-muted);
    font-size: 0.75rem;
}
.browser-file-controls input, .browser-file-controls select {
    padding: 0.35rem 0.5rem;
    font-size: 0.8rem;
}
.browser-file-controls input {
    margin-bottom: 0.5rem;
}
.browser-filter-grid {
    display: grid;
    grid-template-columns: repeat(2, minmax(0, 1fr));
    gap: 0.4rem 0.5rem;
}
.browser-filter-grid label {
    margin: 0;
    font-size: 0.7rem;
}
.browser-filter-grid select {
    margin-top: 0.15rem;
}
.browser-clear {
    color: var(--color-accent);
    background: transparent;
    border: 0;
    padding: 0.3rem 0;
    font: inherit;
    font-size: 0.75rem;
    cursor: pointer;
}
.browser-metadata-notice {
    margin: 0.3rem 0 0;
    color: var(--color-text-muted);
    font-size: 0.75rem;
    overflow-wrap: anywhere;
}
.browser-file-scroll {
    flex: 1;
    min-height: 0;
    overflow: auto;
    padding: 0.5rem;
    overscroll-behavior: contain;
}
.retained-archive-detail {
    min-width: 0;
    min-height: 0;
    overflow: auto;
    padding: 0.6rem;
    overscroll-behavior: contain;
    background: var(--color-bg);
}
.retained-archive-detail :deep(.retained-inline-thread) {
    max-width: calc(100cqi - 1.5rem);
}
.retained-subhead {
    margin: 1rem 0 0.75rem;
    font-size: 0.8rem;
    color: var(--color-text-muted);
}
.retained-notice {
    padding: 1rem;
    margin: 0;
    color: var(--color-text-muted);
    font-size: 0.85rem;
}
.retained-error {
    color: var(--color-danger);
}
.browser-mobile-toolbar {
    display: none;
}
.browser-mobile-toolbar button {
    display: inline-flex;
    gap: 0.4rem;
    align-items: center;
    color: var(--color-text);
    font: inherit;
    font-size: 0.8rem;
    padding: 0.3rem 0.5rem;
}
.browser-file-scroll:focus-visible, .retained-archive-detail:focus-visible {
    outline: 2px solid var(--color-accent);
    outline-offset: -2px;
}
@media (max-width: 700px) {
    .browser-mobile-toolbar {
        display: flex;
        flex: 0 0 auto;
        margin-bottom: 0.3rem;
    }
    .retained-archive-grid {
        grid-template-columns: minmax(0, 1fr);
    }
    .retained-archive-files {
        display: none;
    }
    .mobile-files-open .retained-archive-files {
        display: flex;
        position: absolute;
        inset: 0 auto 0 0;
        width: min(22rem, 100%);
        z-index: 5;
        box-shadow: 4px 0 12px rgb(0 0 0 / 25%);
    }
}
</style>
