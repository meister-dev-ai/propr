// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { describe, expect, it } from 'vitest'
import {
  buildFlameTree,
  collapseSingleChildRuns,
  findNode,
  flattenByDepth,
} from '@/features/code-insights/flameTree'

describe('buildFlameTree', () => {
  it('sums children into their parents, so a folder is never smaller than its files', () => {
    const tree = buildFlameTree([
      { key: 'src/a.cs', value: 3 },
      { key: 'src/b.cs', value: 2 },
      { key: 'tests/c.cs', value: 1 },
    ])

    expect(tree.value).toBe(6)
    expect(tree.children.map((child) => [child.name, child.value])).toEqual([
      ['src', 5],
      ['tests', 1],
    ])
  })

  it('orders heaviest first and breaks ties by name, so the same data draws the same graph', () => {
    const tree = buildFlameTree([
      { key: 'b.cs', value: 2 },
      { key: 'a.cs', value: 2 },
      { key: 'c.cs', value: 5 },
    ])

    expect(tree.children.map((child) => child.name)).toEqual(['c.cs', 'a.cs', 'b.cs'])
  })

  it('keys every frame on its full path, which is what a click has to mean', () => {
    const tree = buildFlameTree([{ key: 'src/Payments/Refund.cs', value: 1 }])

    expect(tree.children[0].key).toBe('src')
    expect(tree.children[0].children[0].key).toBe('src/Payments')
    expect(tree.children[0].children[0].children[0].key).toBe('src/Payments/Refund.cs')
  })

  it('carries the payload onto the leaf only', () => {
    const tree = buildFlameTree([{ key: 'src/a.cs', value: 1, payload: { findings: 1 } }])

    expect(tree.children[0].payload).toBeUndefined()
    expect(tree.children[0].children[0].payload).toEqual({ findings: 1 })
  })

  it('drops keyless and weightless rows rather than drawing a nameless frame', () => {
    // Pull-request-level findings have no file; they belong beside the graph, not inside it.
    const tree = buildFlameTree([
      { key: '', value: 4 },
      { key: 'a.cs', value: 0 },
      { key: 'b.cs', value: 2 },
    ])

    expect(tree.value).toBe(2)
    expect(tree.children.map((child) => child.name)).toEqual(['b.cs'])
  })

  it('reads any separator, so a symbol key is the same graph', () => {
    const tree = buildFlameTree(
      [{ key: 'MeisterDev.Payments.Refunds.Process', value: 2 }],
      '.',
    )

    expect(tree.children[0].name).toBe('MeisterDev')
    expect(findNode(tree, 'MeisterDev.Payments')?.value).toBe(2)
  })
})

describe('collapseSingleChildRuns', () => {
  it('folds a chain of single children into one frame', () => {
    // Four frames a pixel apart say nothing that one frame reading src/a/b does not.
    const tree = collapseSingleChildRuns(
      buildFlameTree([
        { key: 'src/a/b/one.cs', value: 1 },
        { key: 'src/a/b/two.cs', value: 1 },
      ]),
    )

    const folded = tree.children[0]
    expect(folded.name).toBe('src/a/b')
    expect(folded.key).toBe('src/a/b')
    expect(folded.children.map((child) => child.name)).toEqual(['one.cs', 'two.cs'])
  })

  it('folds a folder that holds a single file onto that file', () => {
    // Two frames of identical width stacked on each other say nothing the lower one does not.
    const tree = collapseSingleChildRuns(
      buildFlameTree([
        { key: 'src/a.cs', value: 1, payload: 'a' },
        { key: 'tests/b.cs', value: 1, payload: 'b' },
      ]),
    )

    expect(tree.children.map((child) => child.name)).toEqual(['src/a.cs', 'tests/b.cs'])
    // The frame is still the file: same key, same payload, so a click means what it did before folding.
    expect(tree.children.map((child) => child.key)).toEqual(['src/a.cs', 'tests/b.cs'])
    expect(tree.children.map((child) => child.payload)).toEqual(['a', 'b'])
  })

  it('leaves a branching level alone', () => {
    const tree = collapseSingleChildRuns(
      buildFlameTree([
        { key: 'src/a.cs', value: 1 },
        { key: 'src/b.cs', value: 1 },
      ]),
    )

    expect(tree.children.map((child) => child.name)).toEqual(['src'])
    expect(tree.children[0].children.map((child) => child.name)).toEqual(['a.cs', 'b.cs'])
  })
})

describe('flattenByDepth', () => {
  it('returns one row of frames per level', () => {
    const levels = flattenByDepth(
      buildFlameTree([
        { key: 'src/a.cs', value: 2 },
        { key: 'src/b.cs', value: 1 },
        { key: 'tests/c.cs', value: 1 },
      ]),
    )

    expect(levels[0].map((frame) => frame.name)).toEqual(['src', 'tests'])
    expect(levels[1].map((frame) => frame.name)).toEqual(['a.cs', 'b.cs', 'c.cs'])
  })
})
