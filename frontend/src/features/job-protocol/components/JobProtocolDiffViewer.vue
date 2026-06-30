<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license information. -->

<template>
    <div class="job-protocol-diff-viewer" data-testid="job-protocol-diff-viewer">
        <div v-if="!fileResultId" class="diff-fallback diff-fallback--no-file" data-testid="diff-no-file">
            <i class="fi fi-rr-file-slash" aria-hidden="true"></i>
            <div class="diff-fallback-body">
                <p class="diff-fallback-title">No file associated with this protocol pass</p>
                <p class="diff-fallback-detail">Synthesis and other non-file protocol passes do not have a diff to display.</p>
            </div>
        </div>

        <template v-else>
            <header v-if="diff" class="diff-file-header" data-testid="diff-file-path">
                <i class="fi fi-rr-file-code" aria-hidden="true"></i>
                <span class="diff-file-path">{{ diff.filePath }}</span>
                <span v-if="diff.changeType" class="diff-change-type-chip">{{ diff.changeType }}</span>
                <span v-if="diff.originalPath" class="diff-original-path">from {{ diff.originalPath }}</span>
            </header>

            <div v-if="diffError" class="diff-fallback diff-fallback--error" data-testid="diff-error" role="alert">
                <i class="fi fi-rr-exclamation" aria-hidden="true"></i>
                <div class="diff-fallback-body">
                    <p class="diff-fallback-title">Diff unavailable</p>
                    <p class="diff-fallback-detail">{{ diffErrorMessage }}</p>
                    <button
                        v-if="onRetry"
                        type="button"
                        class="btn-secondary btn-sm diff-fallback-retry"
                        data-testid="diff-error-retry"
                        @click="onRetry"
                    >
                        Retry
                    </button>
                </div>
            </div>

            <div v-else-if="loading" class="diff-fallback diff-fallback--loading" data-testid="diff-loading">
                <ProgressOrb class="waiting-orb" />
                <p>Loading reviewed diff…</p>
            </div>

            <div
                v-else-if="diff?.availability === 'Binary'"
                class="diff-fallback diff-fallback--binary"
                data-testid="diff-binary"
            >
                <i class="fi fi-rr-file-binary" aria-hidden="true"></i>
                <div class="diff-fallback-body">
                    <p class="diff-fallback-title">Binary file</p>
                    <p class="diff-fallback-detail">{{ diff.availabilityMessage ?? 'This file is binary and does not have a renderable diff.' }}</p>
                </div>
            </div>

            <div
                v-else-if="diff?.availability === 'NotFound'"
                class="diff-fallback diff-fallback--not-found"
                data-testid="diff-error"
            >
                <i class="fi fi-rr-search" aria-hidden="true"></i>
                <div class="diff-fallback-body">
                    <p class="diff-fallback-title">File not found</p>
                    <p class="diff-fallback-detail">{{ diff.availabilityMessage ?? 'This file was not found in the pull request changed files.' }}</p>
                </div>
            </div>

            <div
                v-else-if="diff?.availability === 'ProviderUnavailable'"
                class="diff-fallback diff-fallback--error"
                data-testid="diff-error"
                role="alert"
            >
                <i class="fi fi-rr-exclamation" aria-hidden="true"></i>
                <div class="diff-fallback-body">
                    <p class="diff-fallback-title">Diff unavailable</p>
                    <p class="diff-fallback-detail">{{ diff.availabilityMessage ?? 'The source control provider could not be reached.' }}</p>
                    <button
                        v-if="onRetry"
                        type="button"
                        class="btn-secondary btn-sm diff-fallback-retry"
                        data-testid="diff-error-retry"
                        @click="onRetry"
                    >
                        Retry
                    </button>
                </div>
            </div>

            <div
                v-else-if="diff?.availability === 'Available' && diff.unifiedDiff"
                class="diff-viewer-mount"
            >
                <div class="diff-viewer-toolbar">
                    <div class="diff-viewer-mode-toggle" role="tablist" aria-label="Diff layout">
                        <button
                            type="button"
                            role="tab"
                            :aria-selected="outputFormat === 'side-by-side'"
                            class="diff-viewer-mode-btn"
                            :class="{ 'is-active': outputFormat === 'side-by-side' }"
                            data-testid="diff-mode-side-by-side"
                            @click="setOutputFormat('side-by-side')"
                        >
                            Split
                        </button>
                        <button
                            type="button"
                            role="tab"
                            :aria-selected="outputFormat === 'line-by-line'"
                            class="diff-viewer-mode-btn"
                            :class="{ 'is-active': outputFormat === 'line-by-line' }"
                            data-testid="diff-mode-line-by-line"
                            @click="setOutputFormat('line-by-line')"
                        >
                            Unified
                        </button>
                    </div>
                </div>
                <div
                    ref="diffContainer"
                    class="diff-viewer d2h-dark-color-scheme"
                    data-testid="diff-viewer"
                ></div>

                <!--
                    Inline comment threads. Each anchored thread is teleported into a placeholder
                    cell injected beneath its diff line (see `applyInlineThreads`). Rendering the
                    bodies here — rather than as raw HTML strings — keeps Vue reactivity and the
                    shared sanitized-markdown rendering owned by the caller's slot. The teleport
                    target is the injected element; until it exists the teleport is disabled so the
                    node simply stays parked in this off-diff host.
                -->
                <div ref="inlineThreadHost" class="diff-inline-thread-host" aria-hidden="true">
                    <template v-for="anchor in anchoredThreads" :key="anchor.thread.id">
                        <Teleport :to="anchor.target" :disabled="!anchor.target">
                            <slot name="thread" :thread="anchor.thread" />
                        </Teleport>
                    </template>
                </div>
            </div>

            <div v-else class="diff-fallback diff-fallback--empty" data-testid="diff-empty">
                <i class="fi fi-rr-document" aria-hidden="true"></i>
                <p class="diff-fallback-title">No diff available</p>
            </div>
        </template>
    </div>
</template>

<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, ref, watch } from 'vue'
import { Diff2HtmlUI } from 'diff2html/lib/ui/js/diff2html-ui-base.js'
import 'diff2html/bundles/css/diff2html.min.css'
import type { components } from '@/types'
import ProgressOrb from '@/components/ProgressOrb.vue'

type FileDiffDto = components['schemas']['FileDiffDto']

/**
 * A comment thread to anchor inline at a NEW-side diff line. Kept deliberately minimal and
 * provider-neutral so this shared viewer does not depend on any caller's domain types; the
 * caller renders the thread body through the `thread` slot. `T` lets the slot receive its own
 * richer thread shape back unchanged.
 */
export interface InlineDiffThread<T = unknown> {
    /** Stable identity for keying and anchored/unanchored bookkeeping. */
    id: string
    /** NEW-side line number the thread anchors to; null/0 is treated as unanchored. */
    line?: number | null
    /** Opaque payload handed back to the `thread` slot for rendering. */
    payload: T
}

interface AnchoredThread {
    thread: InlineDiffThread
    target: HTMLElement
}

const props = withDefaults(
    defineProps<{
        fileResultId?: string | null
        diff?: FileDiffDto | null
        loading?: boolean
        diffError?: string | null
        onRetry?: (() => void) | null
        /**
         * Optional comment threads to render inline at their NEW-side line. Empty (the default)
         * means no DOM post-processing runs at all, so callers that don't pass threads — e.g. the
         * job-protocol view — get exactly the original behavior.
         */
        inlineThreads?: InlineDiffThread[]
    }>(),
    {
        fileResultId: null,
        diff: null,
        loading: false,
        diffError: null,
        onRetry: null,
        inlineThreads: () => [],
    },
)

/** Reports which inline-thread ids were successfully anchored in the rendered diff. */
const emit = defineEmits<{
    (event: 'update:anchoredIds', ids: string[]): void
}>()

const outputFormat = ref<'side-by-side' | 'line-by-line'>('line-by-line')
const diffContainer = ref<HTMLElement | null>(null)
const inlineThreadHost = ref<HTMLElement | null>(null)
const anchoredThreads = ref<AnchoredThread[]>([])

const diffErrorMessage = computed(() => {
    if (props.diffError) return props.diffError
    return props.diff?.availabilityMessage ?? 'The diff could not be loaded.'
})

function setOutputFormat(value: 'side-by-side' | 'line-by-line') {
    outputFormat.value = value
}

function clearInlineThreads() {
    // Drop the teleport targets first so Vue moves the rendered widgets back into the
    // off-diff host before the injected rows are removed, then strip the injected rows.
    anchoredThreads.value = []
    if (diffContainer.value) {
        diffContainer.value
            .querySelectorAll('.d2h-inline-thread-row')
            .forEach(row => row.remove())
    }
}

function clearContainer() {
    clearInlineThreads()
    if (diffContainer.value) {
        diffContainer.value.innerHTML = ''
    }
}

/**
 * Injects a placeholder row beneath the diff line matching each thread's NEW-side line number,
 * then records that placeholder as a teleport target so the thread widget renders inside it.
 *
 * Only runs in line-by-line mode: side-by-side splits old/new into separate tables, where a
 * single full-width inline row has no well-defined home, so those threads stay in the caller's
 * below-diff fallback. NEW-side anchoring reads `.line-num2` (line-by-line renders the new line
 * number there; deleted lines leave it empty so they never match).
 */
function applyInlineThreads() {
    clearInlineThreads()

    if (props.inlineThreads.length === 0) {
        emit('update:anchoredIds', [])
        return
    }
    if (outputFormat.value !== 'line-by-line' || !diffContainer.value) {
        // Nothing anchors: the caller renders every thread in the below-diff fallback.
        emit('update:anchoredIds', [])
        return
    }

    // Map each NEW-side line number to its diff row (first occurrence wins).
    const rowByLine = new Map<number, HTMLTableRowElement>()
    const rows = diffContainer.value.querySelectorAll<HTMLTableRowElement>('tr')
    rows.forEach(row => {
        const newNumberText = row.querySelector('.line-num2')?.textContent?.trim()
        if (!newNumberText) return
        const lineNumber = Number.parseInt(newNumberText, 10)
        if (Number.isNaN(lineNumber) || rowByLine.has(lineNumber)) return
        rowByLine.set(lineNumber, row)
    })

    const anchored: AnchoredThread[] = []
    const anchoredIds: string[] = []

    for (const thread of props.inlineThreads) {
        const line = thread.line ?? 0
        const row = line > 0 ? rowByLine.get(line) : undefined
        if (!row?.parentElement) continue

        const placeholderRow = document.createElement('tr')
        placeholderRow.className = 'd2h-inline-thread-row'
        const cell = document.createElement('td')
        // Span both the line-number and code columns so the widget runs the table's full width.
        cell.colSpan = 2
        cell.className = 'd2h-inline-thread-cell'
        placeholderRow.appendChild(cell)
        row.parentElement.insertBefore(placeholderRow, row.nextSibling)

        anchored.push({ thread, target: cell })
        anchoredIds.push(thread.id)
    }

    anchoredThreads.value = anchored
    emit('update:anchoredIds', anchoredIds)
}

function renderDiff() {
    if (!diffContainer.value || !props.diff?.unifiedDiff) {
        clearContainer()
        return
    }

    clearContainer()
    let diffContent = props.diff.unifiedDiff
    if (!diffContent.startsWith('---') && !diffContent.startsWith('diff --git')) {
        const filePath = props.diff.filePath ?? 'file'
        const lines = diffContent.split('\n').filter(line => line.length > 0)
        let oldLineCount = 0
        let newLineCount = 0
        for (const line of lines) {
            if (line.startsWith('- ')) oldLineCount++
            else if (line.startsWith('+ ')) newLineCount++
            else if (line.startsWith('  ')) { oldLineCount++; newLineCount++ }
        }
        const hunkHeader = `@@ -1,${oldLineCount} +1,${newLineCount} @@`
        diffContent = `--- a/${filePath}\n+++ b/${filePath}\n${hunkHeader}\n${diffContent}`
    }
    const ui = new Diff2HtmlUI(diffContainer.value, diffContent, {
        outputFormat: outputFormat.value,
        drawFileList: false,
        matching: 'lines',
        highlight: false,
        synchronisedScroll: true,
        stickyFileHeaders: true,
        diffMaxChanges: 5000,
        diffMaxLineLength: 1000,
    })
    ui.draw()
    applyInlineThreads()
}

// Re-render (and re-anchor) when the diff text or layout mode changes.
watch(
    () => [props.diff?.unifiedDiff, outputFormat.value],
    () => {
        void nextTick(renderDiff)
    },
    { immediate: true },
)

// Re-anchor without a full re-render when only the threads change (the diff DOM is untouched).
watch(
    () => props.inlineThreads,
    () => {
        void nextTick(applyInlineThreads)
    },
)

onBeforeUnmount(clearContainer)
</script>

<style scoped>
.job-protocol-diff-viewer {
    display: flex;
    flex-direction: column;
    gap: 0.75rem;
    width: 100%;
    min-width: 0;
}

.diff-file-header {
    display: flex;
    align-items: center;
    gap: 0.5rem;
    padding: 0.65rem 0.9rem;
    border: 1px solid var(--color-border);
    border-radius: var(--radius-lg);
    background: rgba(255, 255, 255, 0.03);
    font-family: 'JetBrains Mono', 'Fira Code', monospace;
    font-size: 0.85rem;
    color: var(--color-text);
    overflow: hidden;
    white-space: nowrap;
    text-overflow: ellipsis;
}

.diff-file-header i {
    color: var(--color-accent);
    flex: 0 0 auto;
}

.diff-file-path {
    flex: 1 1 auto;
    overflow: hidden;
    text-overflow: ellipsis;
}

.diff-change-type-chip,
.diff-original-path {
    display: inline-flex;
    align-items: center;
    padding: 0.1rem 0.55rem;
    border-radius: var(--radius-pill);
    background: rgba(34, 211, 238, 0.12);
    color: var(--color-accent);
    font-family: 'Inter', sans-serif;
    font-size: 0.7rem;
    font-weight: 700;
    letter-spacing: 0.04em;
    text-transform: uppercase;
    flex: 0 0 auto;
}

.diff-original-path {
    background: rgba(168, 85, 247, 0.14);
    color: var(--color-suggestion);
    text-transform: none;
    letter-spacing: 0;
    font-weight: 500;
}

.diff-fallback {
    display: flex;
    align-items: flex-start;
    gap: 0.85rem;
    padding: 1.1rem 1.25rem;
    border: 1px dashed var(--color-border);
    border-radius: var(--radius-lg);
    background: rgba(255, 255, 255, 0.02);
    color: var(--color-text-muted);
}

.diff-fallback--binary {
    border-color: rgba(34, 211, 238, 0.35);
    background: rgba(34, 211, 238, 0.05);
}

.diff-fallback--error,
.diff-fallback--not-found {
    border-color: rgba(239, 68, 68, 0.4);
    background: rgba(239, 68, 68, 0.06);
}

.diff-fallback--loading {
    align-items: center;
    justify-content: center;
    gap: 0.5rem;
}

.diff-fallback i {
    color: var(--color-accent);
    font-size: 1.5rem;
    flex: 0 0 auto;
}

.diff-fallback--error i,
.diff-fallback--not-found i {
    color: var(--color-danger);
}

.diff-fallback--binary i {
    color: var(--color-accent);
}

.diff-fallback-body {
    display: flex;
    flex-direction: column;
    gap: 0.3rem;
    min-width: 0;
}

.diff-fallback-title {
    margin: 0;
    font-size: 0.95rem;
    font-weight: 600;
    color: var(--color-text);
}

.diff-fallback-detail {
    margin: 0;
    font-size: 0.85rem;
    line-height: 1.4;
    color: var(--color-text-muted);
    overflow-wrap: anywhere;
}

.diff-fallback-retry {
    align-self: flex-start;
    margin-top: 0.3rem;
}

.diff-viewer-mount {
    display: flex;
    flex-direction: column;
    gap: 0.5rem;
    overflow: hidden;
    min-width: 0;
    width: 100%;
}

.diff-viewer-toolbar {
    display: flex;
    justify-content: flex-end;
    gap: 0.4rem;
}

.diff-viewer-mode-toggle {
    display: inline-flex;
    border: 1px solid var(--color-border);
    border-radius: var(--radius-pill);
    padding: 2px;
    background: rgba(255, 255, 255, 0.03);
}

.diff-viewer-mode-btn {
    appearance: none;
    background: transparent;
    border: none;
    color: var(--color-text-muted);
    padding: 0.25rem 0.75rem;
    font: inherit;
    font-size: 0.75rem;
    font-weight: 600;
    border-radius: var(--radius-pill);
    cursor: pointer;
    transition: background 0.15s, color 0.15s;
}

.diff-viewer-mode-btn.is-active {
    background: rgba(34, 211, 238, 0.16);
    color: var(--color-accent);
}

.diff-viewer {
    border: 1px solid var(--color-border);
    border-radius: var(--radius-lg);
    background: var(--color-bg);
    overflow: auto;
    height: 70vh;
    width: 100%;
    min-width: 0;
    position: relative;
}

.diff-viewer :deep(.d2h-wrapper) {
    width: 100%;
    height: 100%;
}

.diff-viewer :deep(.d2h-file-wrapper) {
    border: none;
    margin: 0;
}

/*
 * diff2html's `generic-file-path` template renders its own file header
 * (file name + change-type tag + "Viewed" checkbox) inside every file wrapper.
 * The custom `diff-file-header` above already shows the file name and change
 * type, so the duplicated diff2html header is hidden. Hiding `.d2h-file-header`
 * also drops the unused "Viewed" checkbox in the same pass.
 */
.diff-viewer :deep(.d2h-file-header) {
    display: none;
}

.diff-viewer :deep(.d2h-diff-table) {
    table-layout: auto;
    width: 100%;
}

/*
 * The app's global `td { padding: 1rem; border-bottom: … }` (base.css) bleeds
 * into every diff cell, inflating each code row to ~50px and crushing the diff to
 * a handful of visible lines. Neutralise it so diff2html's own compact layout wins.
 */
.diff-viewer :deep(.d2h-diff-table td),
.diff-viewer :deep(.d2h-diff-table th) {
    padding: 0;
    border-bottom: none;
}

.diff-viewer :deep(.d2h-code-wrapper) {
    overflow: visible;
    width: 100%;
}

.diff-viewer :deep(.d2h-code-side-line),
.diff-viewer :deep(.d2h-code-line) {
    overflow: visible;
    /*
     * Must stay `nowrap` (diff2html's default), NOT `pre`: this wrapper holds
     * the prefix/content spans separated by newlines + indentation from
     * diff2html's HTML template. `white-space: pre` would render those template
     * newlines as real line breaks, inflating every row to ~4 lines tall. Actual
     * code indentation is preserved by the inner `.d2h-code-line-ctn` span, which
     * sets its own `white-space: pre`.
     */
    white-space: nowrap;
    line-height: 1.4;
    /*
     * Do NOT override horizontal padding here. diff2html gives this wrapper a
     * large left padding (4.5em side-by-side / 8em line-by-line) to reserve space
     * for the absolutely-positioned line-number column. Shrinking it makes the
     * line numbers overlap and clip the first characters of each code line.
     */
}

.diff-viewer :deep(.d2h-info) {
    overflow: visible;
}

.diff-viewer :deep(.d2h-sticky-header) {
    position: relative !important;
}

.diff-viewer :deep(.d2h-files-diff) {
    overflow: visible;
}

.diff-viewer :deep(.d2h-code-side-linenumber),
.diff-viewer :deep(.d2h-code-linenumber) {
    font-size: 0.8rem;
}

.diff-viewer :deep(.d2h-code-line-ctn) {
    font-size: 0.85rem;
}

/*
 * Off-diff parking host for inline-thread widgets. Each widget is teleported out of here into an
 * injected diff row; anything still parked (e.g. an unanchored thread, or while side-by-side mode
 * disables injection) must not be visible or take layout space.
 */
.diff-inline-thread-host {
    display: none;
}

/*
 * The injected inline-thread row reuses the diff table's cell, so it inherits the `padding: 0`
 * reset above (which already neutralises the global `td` bleed). Give the cell a normal block
 * context for the teleported widget and let it break out of the monospaced code styling.
 */
.diff-viewer :deep(.d2h-inline-thread-row) {
    background: transparent;
}

.diff-viewer :deep(.d2h-inline-thread-cell) {
    white-space: normal;
}
</style>
