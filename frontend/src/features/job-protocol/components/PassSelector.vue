<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->

<template>
    <div class="pass-selector" data-testid="pass-selector">
        <!-- Prev / file trigger / next: step through passes sequentially or jump to any file. -->
        <div class="pass-selector-trigger-row">
            <button
                type="button"
                class="pass-nav-btn"
                :disabled="!canGoPrevious"
                aria-label="Previous pass"
                title="Previous pass"
                data-testid="pass-nav-previous"
                @click="emit('go-previous')"
            >
                <i class="fi fi-rr-angle-small-left" aria-hidden="true"></i>
            </button>

            <div class="pass-selector-trigger-wrap">
                <button
                    type="button"
                    class="pass-selector-trigger"
                    :class="{ 'is-open': isOpen }"
                    :disabled="fileGroups.length === 0"
                    aria-haspopup="listbox"
                    :aria-expanded="isOpen"
                    data-testid="file-trigger"
                    @click="toggleOpen"
                    @keydown="onTriggerKeydown"
                >
                    <div class="pass-selector-trigger-content">
                        <template v-if="activeGroup">
                            <span class="pass-selector-trigger-path">{{ activeGroup.label }}</span>
                            <span class="pass-selector-trigger-stats">
                                <span class="stat-tokens" :aria-label="`${formatTokens(activeGroup.totalTokens)} tokens`">
                                    <i class="fi fi-rr-coins" aria-hidden="true"></i>
                                    {{ formatTokens(activeGroup.totalTokens) }}
                                </span>
                                <span
                                    v-if="activeGroup.totalFindings > 0"
                                    class="stat-findings"
                                    :aria-label="`${activeGroup.totalFindings} findings`"
                                >
                                    <i class="fi fi-rr-bug" aria-hidden="true"></i>
                                    {{ activeGroup.totalFindings }}
                                </span>
                            </span>
                        </template>
                        <span v-else class="pass-selector-placeholder">{{ emptyPlaceholder }}</span>
                    </div>
                    <i class="fi fi-rr-angle-small-down pass-selector-chevron" :class="{ 'is-rotated': isOpen }"></i>
                </button>

                <div
                    v-if="isOpen"
                    ref="dropdownRef"
                    class="pass-selector-dropdown"
                    role="listbox"
                    aria-label="Reviewed files"
                    :style="dropdownStyle"
                >
            <div
                v-for="group in dropdownGroups"
                :key="group.directory || 'root'"
                class="pass-selector-group"
                role="group"
                :aria-label="group.directory || 'Root'"
            >
                <div class="pass-selector-group-label">{{ group.directory || 'Root' }}</div>
                <button
                    v-for="file in group.files"
                    :key="file.path || 'pr-level'"
                    type="button"
                    role="option"
                    class="pass-selector-option"
                    :class="{ 'is-selected': file.path === activeFilePath }"
                    :aria-selected="file.path === activeFilePath"
                    @click="onSelectFile(file.path)"
                >
                    <span class="pass-selector-option-path">{{ file.label }}</span>
                    <span class="pass-selector-option-stats">
                        <span class="stat-tokens" :aria-label="`${formatTokens(file.totalTokens)} tokens`">
                            <i class="fi fi-rr-coins" aria-hidden="true"></i>
                            {{ formatTokens(file.totalTokens) }}
                        </span>
                        <span
                            v-if="file.totalFindings > 0"
                            class="stat-findings"
                            :aria-label="`${file.totalFindings} findings`"
                        >
                            <i class="fi fi-rr-bug" aria-hidden="true"></i>
                            {{ file.totalFindings }}
                        </span>
                    </span>
                </button>
            </div>
                </div>
            </div>

            <button
                type="button"
                class="pass-nav-btn"
                :disabled="!canGoNext"
                aria-label="Next pass"
                title="Next pass"
                data-testid="pass-nav-next"
                @click="emit('go-next')"
            >
                <i class="fi fi-rr-angle-small-right" aria-hidden="true"></i>
            </button>
        </div>

        <!-- Pass switcher (within the selected file): a segmented pill control, not a second tab bar. -->
        <div
            v-if="passTabs.length > 1"
            class="pass-tab-strip"
            role="tablist"
            :aria-label="`Passes for ${activeGroup?.label ?? 'file'}`"
            data-testid="pass-tab-strip"
            @keydown="onTabStripKeydown"
        >
            <button
                v-for="(tab, index) in passTabs"
                :id="`pass-tab-${tab.id}`"
                :key="tab.id"
                ref="tabRefs"
                type="button"
                role="tab"
                class="pass-tab"
                :class="{ 'is-active': tab.id === activePassId }"
                :aria-selected="tab.id === activePassId"
                :tabindex="tab.id === activePassId ? 0 : -1"
                :data-testid="`pass-tab-${index}`"
                @click="onSelectPass(tab.id)"
            >
                <span class="pass-tab-label">{{ tab.label }}</span>
                <span class="stat-tokens" :aria-label="`${formatTokens(tab.tokens)} tokens`">
                    <i class="fi fi-rr-coins" aria-hidden="true"></i>
                    {{ formatTokens(tab.tokens) }}
                </span>
                <span
                    v-if="tab.findingCount > 0"
                    class="stat-findings"
                    :aria-label="`${tab.findingCount} findings`"
                >
                    <i class="fi fi-rr-bug" aria-hidden="true"></i>
                    {{ tab.findingCount }}
                </span>
                <span v-if="tab.failed" class="pass-tab-failed-pill">Failed</span>
            </button>
        </div>
    </div>
</template>

<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref } from 'vue'
import type { FileGroup, PassTab } from '../types'

const props = defineProps<{
    fileGroups: FileGroup[]
    activeFilePath: string
    passTabs: PassTab[]
    activePassId: string | null
    emptyPlaceholder?: string
    canGoPrevious?: boolean
    canGoNext?: boolean
}>()

const emit = defineEmits<{
    (e: 'select-file', value: string): void
    (e: 'select-pass', value: string): void
    (e: 'go-previous'): void
    (e: 'go-next'): void
}>()

const isOpen = ref(false)
const dropdownRef = ref<HTMLElement | null>(null)
const tabRefs = ref<HTMLButtonElement[]>([])

const emptyPlaceholder = computed(() => props.emptyPlaceholder ?? 'Select a file...')

const activeGroup = computed<FileGroup | null>(
    () => props.fileGroups.find(group => group.path === props.activeFilePath) ?? props.fileGroups[0] ?? null,
)

// Group files by their folder for the dropdown listbox. PR-level passes form a
// distinct top group with an empty directory; their label reads "PR-level".
const dropdownGroups = computed(() => {
    const order: string[] = []
    const byDir = new Map<string, FileGroup[]>()

    for (const group of props.fileGroups) {
        const dir = group.isPrLevel ? 'PR-level' : group.directory
        if (!byDir.has(dir)) {
            byDir.set(dir, [])
            order.push(dir)
        }
        byDir.get(dir)!.push(group)
    }

    return order.map(directory => ({
        directory: directory === 'PR-level' ? 'PR-level' : directory,
        files: byDir.get(directory) ?? [],
    }))
})

const dropdownStyle = computed(() => {
    if (!isOpen.value) return {}
    return {
        position: 'absolute' as const,
        top: '100%',
        left: '0',
        right: '0',
        marginTop: '4px',
    }
})

function formatTokens(value: number): string {
    if (value >= 1_000_000) return `${(value / 1_000_000).toFixed(1)}M`
    if (value >= 1_000) return `${(value / 1_000).toFixed(1)}k`
    return String(value)
}

function toggleOpen() {
    if (props.fileGroups.length === 0) return
    isOpen.value = !isOpen.value
}

function onSelectFile(filePath: string) {
    emit('select-file', filePath)
    isOpen.value = false
}

function onSelectPass(passId: string) {
    emit('select-pass', passId)
}

function onTriggerKeydown(event: KeyboardEvent) {
    if (event.key === 'Escape') {
        isOpen.value = false
        return
    }

    if (!isOpen.value) {
        return
    }

    if (event.key === 'ArrowDown' || event.key === 'ArrowUp' || event.key === 'Home' || event.key === 'End') {
        event.preventDefault()
        moveFileSelection(event.key)
    }
}

function moveFileSelection(key: string) {
    const groups = props.fileGroups
    if (groups.length === 0) return

    const currentIndex = groups.findIndex(group => group.path === props.activeFilePath)
    let nextIndex = currentIndex < 0 ? 0 : currentIndex

    if (key === 'ArrowDown') nextIndex = Math.min(groups.length - 1, currentIndex + 1)
    else if (key === 'ArrowUp') nextIndex = Math.max(0, currentIndex - 1)
    else if (key === 'Home') nextIndex = 0
    else if (key === 'End') nextIndex = groups.length - 1

    const next = groups[nextIndex]
    if (next) emit('select-file', next.path)
}

function onTabStripKeydown(event: KeyboardEvent) {
    if (event.key !== 'ArrowLeft' && event.key !== 'ArrowRight') {
        return
    }

    event.preventDefault()
    const tabs = props.passTabs
    const currentIndex = tabs.findIndex(tab => tab.id === props.activePassId)
    if (currentIndex < 0) return

    const delta = event.key === 'ArrowRight' ? 1 : -1
    const nextIndex = (currentIndex + delta + tabs.length) % tabs.length
    const next = tabs[nextIndex]
    if (next) {
        emit('select-pass', next.id)
        tabRefs.value[nextIndex]?.focus()
    }
}

function handleOutsideClick(event: MouseEvent) {
    const target = event.target as HTMLElement
    if (!target.closest('[data-testid="pass-selector"]')) {
        isOpen.value = false
    }
}

onMounted(() => {
    document.addEventListener('click', handleOutsideClick)
})

onUnmounted(() => {
    document.removeEventListener('click', handleOutsideClick)
})
</script>

<style scoped>
.pass-selector {
    position: sticky;
    top: var(--pass-selector-sticky-top, 8.5rem);
    z-index: 3;
    display: flex;
    flex-wrap: wrap;
    align-items: center;
    gap: 0.6rem;
    padding: 0.75rem 1rem;
    border-bottom: 1px solid var(--color-border);
    background: var(--color-surface);
}

/* Prev / trigger / next stepper row. */
.pass-selector-trigger-row {
    flex: 1 1 100%;
    display: flex;
    align-items: stretch;
    gap: 0.5rem;
}

.pass-selector-trigger-wrap {
    position: relative;
    flex: 1 1 auto;
    min-width: 0;
    display: flex;
}

.pass-nav-btn {
    display: inline-flex;
    align-items: center;
    justify-content: center;
    flex: 0 0 auto;
    width: 2.4rem;
    padding: 0;
    background: rgba(15, 17, 22, 0.8);
    border: 1px solid var(--color-border);
    border-radius: var(--radius-md);
    color: var(--color-text);
    cursor: pointer;
    transition: border-color 0.15s, color 0.15s, opacity 0.15s;
}

.pass-nav-btn:hover:not(:disabled) {
    border-color: rgba(34, 211, 238, 0.4);
    color: var(--color-accent);
}

.pass-nav-btn:disabled {
    opacity: 0.35;
    cursor: not-allowed;
}

.pass-selector-trigger {
    flex: 1 1 auto;
    width: 100%;
    min-width: 0;
    display: flex;
    align-items: center;
    gap: 0.5rem;
    padding: 0.5rem 0.75rem;
    background: rgba(15, 17, 22, 0.8);
    border: 1px solid var(--color-border);
    border-radius: var(--radius-md);
    color: var(--color-text);
    font-size: 0.85rem;
    cursor: pointer;
    outline: none;
    transition: border-color 0.15s;
    text-align: left;
}

.pass-selector-trigger:hover {
    border-color: rgba(34, 211, 238, 0.4);
}

.pass-selector-trigger.is-open {
    border-color: var(--color-accent);
}

.pass-selector-trigger:disabled {
    cursor: not-allowed;
    opacity: 0.7;
}

.pass-selector-trigger-content {
    flex: 1;
    min-width: 0;
    display: flex;
    align-items: center;
    gap: 0.75rem;
}

.pass-selector-trigger-path {
    flex: 1;
    min-width: 0;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
    font-family: 'JetBrains Mono', 'Fira Code', monospace;
}

.pass-selector-trigger-stats {
    display: flex;
    align-items: center;
    gap: 0.5rem;
    flex-shrink: 0;
}

.pass-selector-placeholder {
    color: var(--color-text-muted);
    font-style: italic;
}

.pass-selector-chevron {
    font-size: 0.7rem;
    transition: transform 0.15s ease;
    opacity: 0.6;
    flex-shrink: 0;
}

.pass-selector-chevron.is-rotated {
    transform: rotate(180deg);
}

.pass-selector-dropdown {
    position: absolute;
    z-index: 100;
    background: var(--color-surface);
    border: 1px solid var(--color-border);
    border-radius: var(--radius-md);
    box-shadow: 0 8px 24px rgba(0, 0, 0, 0.4);
    max-height: 400px;
    overflow-y: auto;
    width: 100%;
}

.pass-selector-group {
    display: flex;
    flex-direction: column;
}

.pass-selector-group-label {
    padding: 0.5rem 0.75rem 0.25rem;
    font-size: 0.7rem;
    font-weight: 700;
    text-transform: uppercase;
    letter-spacing: 0.08em;
    color: var(--color-accent);
    background: rgba(34, 211, 238, 0.06);
}

.pass-selector-option {
    display: flex;
    align-items: center;
    gap: 0.5rem;
    padding: 0.5rem 0.75rem;
    background: transparent;
    border: none;
    color: var(--color-text);
    font-size: 0.85rem;
    cursor: pointer;
    text-align: left;
    width: 100%;
    transition: background 0.1s;
}

.pass-selector-option:hover {
    background: var(--color-surface-raised, rgba(255, 255, 255, 0.06));
}

.pass-selector-option.is-selected {
    background: rgba(34, 211, 238, 0.12);
    color: var(--color-accent);
}

.pass-selector-option-path {
    flex: 1;
    min-width: 0;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
    font-family: 'JetBrains Mono', 'Fira Code', monospace;
}

.pass-selector-option-stats {
    display: flex;
    align-items: center;
    gap: 0.5rem;
    flex-shrink: 0;
}

/* Pass switcher: a segmented pill control beneath the trigger, deliberately styled unlike the
   Events/Diff underline tabs below it so the two don't read as nested tab bars. */
.pass-tab-strip {
    display: inline-flex;
    flex-wrap: wrap;
    gap: 0.25rem;
    align-items: center;
    padding: 0.25rem;
    background: rgba(255, 255, 255, 0.04);
    border: 1px solid var(--color-border);
    border-radius: var(--radius-md);
}

.pass-tab {
    display: inline-flex;
    align-items: center;
    gap: 0.5rem;
    padding: 0.35rem 0.7rem;
    border: none;
    background: transparent;
    color: var(--color-text-muted);
    font-family: inherit;
    font-size: 0.8rem;
    cursor: pointer;
    border-radius: var(--radius-sm);
    transition: background 0.15s, color 0.15s;
}

.pass-tab:hover {
    background: rgba(255, 255, 255, 0.06);
    color: var(--color-text);
}

.pass-tab.is-active {
    background: var(--color-surface-raised);
    color: var(--color-accent);
    box-shadow: 0 1px 2px rgba(0, 0, 0, 0.25);
}

.pass-tab-label {
    font-weight: 600;
}

.pass-tab-failed-pill {
    color: var(--color-warning);
    background: rgba(245, 158, 11, 0.12);
    font-size: 0.7rem;
    font-weight: 600;
    padding: 0.05rem 0.4rem;
    border-radius: var(--radius-xs);
}

.stat-tokens {
    display: inline-flex;
    align-items: center;
    gap: 0.25rem;
    font-size: 0.75rem;
    color: var(--color-text-muted);
    background: rgba(255, 255, 255, 0.06);
    padding: 0.15rem 0.4rem;
    border-radius: var(--radius-xs);
}

.stat-tokens i {
    font-size: 0.65rem;
    opacity: 0.7;
}

.stat-findings {
    display: inline-flex;
    align-items: center;
    gap: 0.25rem;
    font-size: 0.75rem;
    font-weight: 600;
    color: var(--color-danger);
    background: rgba(239, 68, 68, 0.12);
    padding: 0.15rem 0.4rem;
    border-radius: var(--radius-xs);
}

.stat-findings i {
    font-size: 0.65rem;
}
</style>
