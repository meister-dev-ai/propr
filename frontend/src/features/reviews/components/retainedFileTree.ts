// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import type { RetainedFile } from '@/features/reviews/composables/useRetainedPrData'

/** A folder node in the retained-file directory tree. */
export interface RetainedTreeFolder {
    name: string
    /** Slash-joined path of this folder from the root (e.g. `src/auth`). */
    path: string
    children: Map<string, RetainedTreeFolder>
    files: RetainedFile[]
}

/** A flattened row: either a folder header or a file leaf, carrying its nesting depth. */
export type RetainedTreeRow =
    | { type: 'folder'; name: string; path: string; depth: number }
    | { type: 'file'; name: string; depth: number; file: RetainedFile }

function createFolder(name: string, path: string): RetainedTreeFolder {
    return { name, path, children: new Map(), files: [] }
}

/** The directory part of a path, normalized to forward slashes, with empty segments dropped. */
function directorySegments(filePath: string): string[] {
    const normalized = filePath.replaceAll('\\', '/')
    const parts = normalized.split('/').filter(Boolean)
    // Drop the basename; what remains is the directory chain.
    parts.pop()
    return parts
}

/** The basename (file name) of a path, normalized to forward slashes. */
function basename(filePath: string): string {
    const normalized = filePath.replaceAll('\\', '/')
    const parts = normalized.split('/').filter(Boolean)
    return parts[parts.length - 1] ?? normalized
}

/**
 * Build a directory tree from a flat list of retained files. Each file's path is split on
 * `/` (and `\`); the leading segments become nested folders and the basename becomes a leaf.
 * Files with no directory (root-level) attach to the root node directly.
 */
export function buildRetainedFileTree(files: RetainedFile[]): RetainedTreeFolder {
    const root = createFolder('', '')

    for (const file of files) {
        const path = file.filePath ?? ''
        const segments = path ? directorySegments(path) : []

        let current = root
        segments.forEach((segment, index) => {
            let child = current.children.get(segment)
            if (!child) {
                const childPath = segments.slice(0, index + 1).join('/')
                child = createFolder(segment, childPath)
                current.children.set(segment, child)
            }
            current = child
        })

        current.files.push(file)
    }

    return root
}

/**
 * Flatten the tree into an ordered row list for rendering. Folders sort before files at each
 * level, both alphabetically. A folder whose path is in `collapsed` still emits its own header
 * row but suppresses its descendants.
 */
export function flattenRetainedFileTree(
    root: RetainedTreeFolder,
    collapsed: ReadonlySet<string>,
): RetainedTreeRow[] {
    const rows: RetainedTreeRow[] = []

    const walk = (node: RetainedTreeFolder, depth: number): void => {
        const sortedFolders = [...node.children.values()].sort((left, right) =>
            left.name.localeCompare(right.name),
        )
        for (const folder of sortedFolders) {
            rows.push({ type: 'folder', name: folder.name, path: folder.path, depth })
            if (!collapsed.has(folder.path)) {
                walk(folder, depth + 1)
            }
        }

        const sortedFiles = [...node.files].sort((left, right) =>
            basename(left.filePath ?? '').localeCompare(basename(right.filePath ?? '')),
        )
        for (const file of sortedFiles) {
            rows.push({ type: 'file', name: basename(file.filePath ?? ''), depth, file })
        }
    }

    walk(root, 0)
    return rows
}

/**
 * The chain of folder paths a file lives under, from outermost to innermost
 * (e.g. `src/auth/tokens.ts` → [`src`, `src/auth`]). Used to expand the path to a selection.
 */
export function folderPathsForFile(filePath: string): string[] {
    const segments = directorySegments(filePath)
    return segments.map((_, index) => segments.slice(0, index + 1).join('/'))
}
