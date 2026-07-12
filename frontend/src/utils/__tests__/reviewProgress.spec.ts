// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { describe, expect, it } from 'vitest'
import { formatFilesReviewed } from '@/utils/reviewProgress'

describe('formatFilesReviewed', () => {
    it('renders the X/Y fraction when the denominator is known', () => {
        expect(formatFilesReviewed(12, 40)).toBe('12/40')
    })

    it('renders 0/Y at the start of a review', () => {
        expect(formatFilesReviewed(0, 40)).toBe('0/40')
    })

    it('renders Y/Y when every file is reviewed', () => {
        expect(formatFilesReviewed(40, 40)).toBe('40/40')
    })

    it('treats a missing numerator as zero', () => {
        expect(formatFilesReviewed(undefined, 8)).toBe('0/8')
        expect(formatFilesReviewed(null, 8)).toBe('0/8')
    })

    it('hides the metric until the denominator is fixed (null / undefined)', () => {
        expect(formatFilesReviewed(0, null)).toBeNull()
        expect(formatFilesReviewed(3, undefined)).toBeNull()
    })

    it('hides the metric when the denominator is zero (e.g. all files excluded)', () => {
        expect(formatFilesReviewed(0, 0)).toBeNull()
    })
})
