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
      { ordinal: 0, configuredModelId: 'm1', lens: 'security' },
      { ordinal: 1, configuredModelId: 'm2', lens: null },
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
})
