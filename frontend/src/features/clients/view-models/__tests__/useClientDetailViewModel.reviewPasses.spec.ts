// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { describe, expect, it } from 'vitest'
import type { components } from '@/types'
import { normalizeReviewPasses, reviewPassesEqual } from '../useClientDetailViewModel'

type ReviewPassEntry = components['schemas']['ReviewPassEntry']

// Regression guard: the review-pass normalizer used to drop the per-pass lens and the equality
// check used to ignore it, so a Security lens was never sent to the server and a lens-only edit
// left the Save button disabled. Both must round-trip the lens.
describe('review-pass normalize/equal preserve the lens', () => {
  it('normalizeReviewPasses keeps each pass lens so it is sent to the server', () => {
    const normalized = normalizeReviewPasses([
      { ordinal: 0, configuredModelId: 'm1', lens: 'security' },
      { ordinal: 1, configuredModelId: 'm2', lens: null },
    ])

    expect(normalized).toEqual([
      { ordinal: 0, configuredModelId: 'm1', logicalModelName: null, lens: 'security', scope: null, shadow: false, reasoningEffort: 'none' },
      { ordinal: 1, configuredModelId: 'm2', logicalModelName: null, lens: null, scope: null, shadow: false, reasoningEffort: 'none' },
    ])
  })

  it('normalizeReviewPasses defaults a missing lens to null (ordinary resample pass)', () => {
    const normalized = normalizeReviewPasses([{ ordinal: 0, configuredModelId: 'm1' }])

    expect(normalized[0].lens).toBeNull()
  })

  it('reviewPassesEqual treats a lens-only change as different so Save activates', () => {
    const before: ReviewPassEntry[] = [{ ordinal: 0, configuredModelId: 'm1', lens: null }]
    const afterLensChange: ReviewPassEntry[] = [{ ordinal: 0, configuredModelId: 'm1', lens: 'security' }]

    expect(reviewPassesEqual(before, afterLensChange)).toBe(false)
    expect(reviewPassesEqual(before, [{ ordinal: 0, configuredModelId: 'm1', lens: null }])).toBe(true)
  })

  it('normalize keeps scope/shadow and equality treats a scope- or shadow-only change as different', () => {
    const normalized = normalizeReviewPasses([{ ordinal: 0, configuredModelId: 'm1', scope: 'pr_wide', shadow: true }])
    expect(normalized).toEqual([
      { ordinal: 0, configuredModelId: 'm1', logicalModelName: null, lens: null, scope: 'pr_wide', shadow: true, reasoningEffort: 'none' },
    ])

    const base: ReviewPassEntry[] = [{ ordinal: 0, configuredModelId: 'm1', lens: null, scope: null, shadow: false }]
    expect(reviewPassesEqual(base, [{ ordinal: 0, configuredModelId: 'm1', lens: null, scope: 'pr_wide', shadow: false }])).toBe(false)
    expect(reviewPassesEqual(base, [{ ordinal: 0, configuredModelId: 'm1', lens: null, scope: null, shadow: true }])).toBe(false)
    expect(reviewPassesEqual(base, [{ ordinal: 0, configuredModelId: 'm1', lens: null, scope: null, shadow: false }])).toBe(true)
  })

  // A pass can run on a named logical model instead of a configured model, and then carries no configured-model
  // id at all. Dropping those was why saving a role-based pass appeared to work and left the list empty.
  it('normalizeReviewPasses keeps a pass that runs on a logical model', () => {
    const normalized = normalizeReviewPasses([
      { ordinal: 0, logicalModelName: 'Low Budget' },
      { ordinal: 1, configuredModelId: 'm1' },
    ])

    expect(normalized).toHaveLength(2)
    expect(normalized[0].logicalModelName).toBe('Low Budget')
    // Omitted, not empty: an empty string is not a uuid and the server rejects the whole body over it.
    expect(normalized[0].configuredModelId).toBeUndefined()
    expect(normalized[1].logicalModelName).toBeNull()
  })

  it('normalizeReviewPasses still drops a pass that names neither a model nor a role', () => {
    expect(normalizeReviewPasses([{ ordinal: 0 }, { ordinal: 1, configuredModelId: '' }])).toEqual([])
  })

  // The API sends the all-zeros id for a role-based pass because the contract's field is a non-nullable uuid.
  // Echoing it back would be meaningless, so it is treated as the absence it represents.
  it('normalizeReviewPasses treats the all-zeros id as no configured model', () => {
    const normalized = normalizeReviewPasses([
      { ordinal: 0, configuredModelId: '00000000-0000-0000-0000-000000000000', logicalModelName: 'Medium Budget' },
    ])

    expect(normalized[0].configuredModelId).toBeUndefined()
    expect(normalized[0].logicalModelName).toBe('Medium Budget')
  })

  // Without this the dirty check treats a role swap as no change, so Save does nothing.
  it('reviewPassesEqual notices a changed logical model', () => {
    expect(
      reviewPassesEqual(
        [{ ordinal: 0, logicalModelName: 'Low Budget' }],
        [{ ordinal: 0, logicalModelName: 'High Budget' }],
      ),
    ).toBe(false)
  })
})
