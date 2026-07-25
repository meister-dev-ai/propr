// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { describe, expect, it } from 'vitest'
import type { components } from '@/types'
import { severitySetsEqual } from '../useClientDetailViewModel'

type CommentSeverity = components['schemas']['CommentSeverity']

// The auto-resolve dirty check compares severity SETS, so a reorder is not a change but adding/removing a
// severity is — otherwise the Save button would never activate for a real edit (or would activate on a no-op).
describe('severitySetsEqual', () => {
  it('treats sets with the same members as equal regardless of order', () => {
    const left: CommentSeverity[] = ['warning', 'error']
    const right: CommentSeverity[] = ['error', 'warning']
    expect(severitySetsEqual(left, right)).toBe(true)
    expect(severitySetsEqual([], [])).toBe(true)
  })

  it('treats sets with different members or sizes as different so Save activates', () => {
    expect(severitySetsEqual(['warning'], ['warning', 'error'])).toBe(false)
    expect(severitySetsEqual(['warning'], ['error'])).toBe(false)
    expect(severitySetsEqual([], ['info'])).toBe(false)
  })

  it('is true set equality — equal length but a duplicate does not make two different sets look equal', () => {
    // Both have length 2, but as sets {warning} != {warning,error}.
    expect(severitySetsEqual(['warning', 'warning'], ['warning', 'error'])).toBe(false)
    // Duplicates within one side collapse to the same set.
    expect(severitySetsEqual(['warning', 'warning'], ['warning'])).toBe(true)
  })
})
