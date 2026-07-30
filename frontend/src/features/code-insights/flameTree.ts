// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

/**
 * Turns a flat list of hierarchical keys with weights into the tree a flame graph draws.
 *
 * Deliberately ignorant of what the keys mean. Today they are repository-relative file paths; a symbol key
 * (`namespace.Type.Member`) is the same shape with a different separator, so the graph will not need rewriting to
 * show findings per symbol once findings record one.
 */

/** One row to place in the tree: a hierarchical key and what it weighs. */
export interface FlameRow {
  key: string
  value: number
  /** Anything the caller wants back when the node is clicked; carried through untouched on leaves. */
  payload?: unknown
}

/** One frame of the graph. Values are summed from the leaves up, so a parent is never smaller than its children. */
export interface FlameNode {
  /** The last segment: what the frame is labelled with. */
  name: string
  /** The whole key up to and including this segment, which is what a leaf drills through on. */
  key: string
  value: number
  depth: number
  children: FlameNode[]
  /** Present only on nodes that came from a row of their own. */
  payload?: unknown
}

/**
 * Builds the tree. Rows whose key is empty are dropped rather than becoming a nameless frame: for findings they
 * are the pull-request-level ones, which belong to no file and are reported beside the graph instead.
 */
export function buildFlameTree(rows: readonly FlameRow[], separator = '/'): FlameNode {
  const root: FlameNode = { name: '', key: '', value: 0, depth: -1, children: [] }

  for (const row of rows) {
    const segments = row.key.split(separator).filter((segment) => segment.length > 0)
    if (segments.length === 0 || row.value <= 0) {
      continue
    }

    let node = root
    node.value += row.value

    segments.forEach((segment, index) => {
      const key = segments.slice(0, index + 1).join(separator)
      let child = node.children.find((candidate) => candidate.name === segment)
      if (!child) {
        child = { name: segment, key, value: 0, depth: index, children: [] }
        node.children.push(child)
      }

      child.value += row.value
      if (index === segments.length - 1) {
        child.payload = row.payload
      }

      node = child
    })
  }

  sort(root)
  return root
}

/**
 * Collapses runs of single-child frames into one: `src/Payments/RefundProcessor.cs` reads better than three frames
 * of identical width stacked on each other. The surviving frame keeps the child's key and payload, so a click still
 * means exactly what it did, and a folder that holds one file collapses onto that file rather than framing it twice.
 */
export function collapseSingleChildRuns(node: FlameNode, separator = '/'): FlameNode {
  const collapsed = node.children.map((child) => collapseSingleChildRuns(child, separator))

  if (collapsed.length === 1 && node.depth >= 0) {
    const only = collapsed[0]
    return {
      ...only,
      name: `${node.name}${separator}${only.name}`,
      depth: node.depth,
    }
  }

  return { ...node, children: collapsed }
}

/** Every frame of a subtree, level by level, so a template can render rows without recursing itself. */
export function flattenByDepth(node: FlameNode): FlameNode[][] {
  const levels: FlameNode[][] = []

  const walk = (current: FlameNode, level: number): void => {
    for (const child of current.children) {
      levels[level] ??= []
      levels[level].push(child)
      walk(child, level + 1)
    }
  }

  walk(node, 0)
  return levels
}

/** Finds a frame by its key, for zooming into one without threading node references through the DOM. */
export function findNode(node: FlameNode, key: string): FlameNode | null {
  if (node.key === key) {
    return node
  }

  for (const child of node.children) {
    const found = findNode(child, key)
    if (found) {
      return found
    }
  }

  return null
}

function sort(node: FlameNode): void {
  // Heaviest first, then by name: a ranked graph that reshuffles between reads of the same data is unreadable.
  node.children.sort((left, right) => right.value - left.value || left.name.localeCompare(right.name))
  node.children.forEach(sort)
}
