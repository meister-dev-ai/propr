// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

/**
 * Formats the "X/Y" files-reviewed fraction shared by the review-jobs list and the job-protocol viewer
 * header. Returns null when the in-scope denominator is unknown (not yet fixed at dispatch planning) or
 * zero (e.g. every changed file was excluded), so callers can hide the metric rather than render a
 * meaningless "0/0".
 */
export function formatFilesReviewed(
    filesReviewed: number | null | undefined,
    filesInScope: number | null | undefined,
): string | null {
    if (filesInScope == null || filesInScope === 0) {
        return null
    }

    return `${filesReviewed ?? 0}/${filesInScope}`
}
