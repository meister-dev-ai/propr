<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->

<template>
    <div class="retained-file-list" data-testid="retained-file-list">
        <p v-if="files.length === 0" class="retained-file-list-empty" data-testid="retained-file-list-empty">
            No retained changed files for this pull request.
        </p>
        <div v-else class="retained-file-tree">
            <div v-for="row in treeRows" :key="rowKey(row)" class="retained-tree-node">
                <button
                    v-if="row.type === 'folder'"
                    type="button"
                    class="retained-folder-header retained-tree-folder-btn"
                    :class="{ 'retained-folder--active': isFolderActive(row.path) }"
                    :style="{ paddingLeft: `${row.depth * 1.1 + 0.5}rem` }"
                    :data-testid="`retained-file-folder`"
                    :data-folder-path="row.path"
                    @click="toggleFolder(row.path)"
                >
                    <v-icon
                        size="x-small"
                        class="retained-folder-chevron"
                        :class="{ collapsed: isCollapsed(row.path) }"
                        icon="mdi-chevron-down"
                    />
                    <v-icon
                        size="small"
                        class="retained-folder-icon"
                        :icon="isCollapsed(row.path) ? 'mdi-folder-outline' : 'mdi-folder-open-outline'"
                    />
                    <span class="retained-folder-name">{{ row.name }}</span>
                </button>

                <button
                    v-else
                    type="button"
                    class="retained-file-item retained-tree-file-btn"
                    :class="{ 'retained-file-item--active': row.file.filePath === selectedFilePath }"
                    :style="{ paddingLeft: `${row.depth * 1.1 + 0.5}rem` }"
                    :data-testid="`retained-file-item`"
                    :data-file-path="row.file.filePath ?? ''"
                    @click="emit('select', row.file)"
                >
                    <v-icon size="small" class="retained-file-icon" :icon="iconForChangeType(row.file.changeType)" />
                    <span class="retained-file-name" :title="row.file.filePath ?? ''">
                        {{ row.name }}
                    </span>
                    <span class="retained-file-badges">
                        <v-chip
                            v-if="row.file.changeType"
                            size="x-small"
                            variant="tonal"
                            class="retained-file-change-chip"
                            :data-testid="`retained-file-change-type`"
                        >
                            {{ row.file.changeType }}
                        </v-chip>
                        <v-chip
                            v-if="row.file.isBinary"
                            size="x-small"
                            variant="tonal"
                            class="retained-file-binary-chip"
                        >
                            binary
                        </v-chip>
                        <v-chip
                            v-if="commentCount(row.file.filePath) > 0"
                            size="x-small"
                            variant="flat"
                            class="retained-file-comment-chip"
                            data-testid="retained-file-comment-badge"
                            :title="`${threadCount(row.file.filePath)} thread(s), ${commentCount(row.file.filePath)} comment(s)`"
                        >
                            <v-icon size="x-small" icon="mdi-comment-text-outline" start />
                            {{ commentCount(row.file.filePath) }}
                        </v-chip>
                    </span>
                </button>
            </div>
        </div>
    </div>
</template>

<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import type { RetainedFile } from '@/features/reviews/composables/useRetainedPrData'
import { buildRetainedFileTree, flattenRetainedFileTree, folderPathsForFile, type RetainedTreeRow } from './retainedFileTree'

interface Props {
    files: RetainedFile[]
    selectedFilePath?: string | null
    commentCount: (filePath: string) => number
    threadCount: (filePath: string) => number
}

const props = withDefaults(defineProps<Props>(), {
    selectedFilePath: null,
})

const emit = defineEmits<{
    (event: 'select', file: RetainedFile): void
}>()

// Folders are expanded by default; this set tracks the ones the user has collapsed.
// Keeping the default expanded means every leaf — and its `data-file-path` — stays in
// the DOM, so selectors and the diff-on-select flow can reach any file without a toggle.
const collapsedFolders = ref<Set<string>>(new Set())

const tree = computed(() => buildRetainedFileTree(props.files))

const treeRows = computed<RetainedTreeRow[]>(() =>
    flattenRetainedFileTree(tree.value, collapsedFolders.value),
)

function isCollapsed(path: string): boolean {
    return collapsedFolders.value.has(path)
}

function toggleFolder(path: string): void {
    const next = new Set(collapsedFolders.value)
    if (next.has(path)) {
        next.delete(path)
    } else {
        next.add(path)
    }
    collapsedFolders.value = next
}

function isFolderActive(path: string): boolean {
    const selected = props.selectedFilePath
    if (!selected) return false
    return selected === path || selected.startsWith(`${path}/`)
}

function rowKey(row: RetainedTreeRow): string {
    return row.type === 'folder' ? `folder:${row.path}` : `file:${row.file.filePath ?? row.name}`
}

// If the selection changes to a file inside a collapsed folder, re-expand the path to it
// so its leaf is reachable.
watch(
    () => props.selectedFilePath,
    selected => {
        if (!selected) return
        const onPath = folderPathsForFile(selected)
        if (onPath.some(path => collapsedFolders.value.has(path))) {
            const next = new Set(collapsedFolders.value)
            for (const path of onPath) next.delete(path)
            collapsedFolders.value = next
        }
    },
)

function commentCount(filePath: string | null | undefined): number {
    if (!filePath) return 0
    return props.commentCount(filePath)
}

function threadCount(filePath: string | null | undefined): number {
    if (!filePath) return 0
    return props.threadCount(filePath)
}

function iconForChangeType(changeType: string | null | undefined): string {
    switch ((changeType ?? '').toLowerCase()) {
        case 'added':
            return 'mdi-file-plus-outline'
        case 'deleted':
            return 'mdi-file-remove-outline'
        case 'renamed':
            return 'mdi-file-move-outline'
        case 'copied':
            return 'mdi-file-multiple-outline'
        default:
            return 'mdi-file-document-outline'
    }
}
</script>

<style scoped>
.retained-file-list {
    width: 100%;
    min-width: 0;
}

.retained-file-list-empty {
    color: var(--color-text-muted);
    font-style: italic;
    padding: 1rem 0;
}

.retained-file-tree {
    display: flex;
    flex-direction: column;
    gap: 1px;
    width: 100%;
}

.retained-tree-node {
    display: flex;
    flex-direction: column;
    width: 100%;
    min-width: 0;
}

.retained-folder-header {
    appearance: none;
    display: flex;
    align-items: center;
    gap: 0.3rem;
    width: 100%;
    padding: 0.3rem 0.5rem;
    border: none;
    background: transparent;
    color: var(--color-accent);
    font-size: 0.78rem;
    font-weight: 700;
    text-align: left;
    cursor: pointer;
    border-radius: var(--radius-md);
    transition: background 0.1s ease;
}

.retained-folder-header:hover {
    background: rgba(255, 255, 255, 0.06);
}

.retained-folder--active {
    background: rgba(255, 255, 255, 0.03);
}

.retained-folder-chevron {
    opacity: 0.6;
    transition: transform 0.15s ease;
}

.retained-folder-chevron.collapsed {
    transform: rotate(-90deg);
}

.retained-folder-icon {
    color: var(--color-accent);
    opacity: 0.9;
}

.retained-folder-name {
    flex: 1;
    min-width: 0;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
}

.retained-file-item {
    appearance: none;
    display: flex;
    align-items: center;
    gap: 0.5rem;
    width: 100%;
    padding: 0.3rem 0.5rem;
    border: none;
    background: transparent;
    color: var(--color-text);
    text-align: left;
    cursor: pointer;
    border-radius: var(--radius-md);
    transition: background 0.1s ease;
}

.retained-file-item:hover {
    background: rgba(255, 255, 255, 0.04);
}

.retained-file-item--active {
    background: rgba(34, 211, 238, 0.12);
}

.retained-file-icon {
    color: var(--color-accent);
    flex: 0 0 auto;
}

.retained-file-name {
    flex: 1;
    min-width: 0;
    font-family: 'JetBrains Mono', 'Fira Code', monospace;
    font-size: 0.82rem;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
}

.retained-file-badges {
    display: inline-flex;
    align-items: center;
    gap: 0.35rem;
    flex: 0 0 auto;
}

.retained-file-change-chip {
    text-transform: uppercase;
    letter-spacing: 0.04em;
    font-weight: 700;
}

.retained-file-comment-chip {
    background: rgba(168, 85, 247, 0.18);
    color: var(--color-suggestion, var(--color-accent));
    font-weight: 700;
}
</style>
