// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

// Parsing helpers for the per-pass contribution surfaced on a multi-pass union resample pass. The backend
// records a `multi_pass_union_completed` event on the file's BASELINE protocol whose output payload carries
// per-pass catch counts and models, indexed from the baseline (index 0 = the tier baseline = "Pass 1").

/** One pass's contribution to a file's union: how many findings it caught and which model produced them. */
export interface UnionPassContribution {
    catchCount: number
    model: string | null
}

/**
 * Parses the `multi_pass_union_completed` event output into a map keyed by the 1-based pass index
 * ("Pass N", where the tier baseline is Pass 1 and additional passes are 2..k). A missing/malformed
 * payload yields an empty map. A catch count of 0 is a valid, informative value and is preserved.
 */
export function parseUnionContributions(outputSummary: string | null | undefined): Map<number, UnionPassContribution> {
    const contributions = new Map<number, UnionPassContribution>()
    if (!outputSummary) {
        return contributions
    }

    try {
        const parsed = JSON.parse(outputSummary) as {
            perPassCatchCounts?: unknown
            perPassModels?: unknown
        }
        const counts = Array.isArray(parsed.perPassCatchCounts) ? parsed.perPassCatchCounts : []
        const models = Array.isArray(parsed.perPassModels) ? parsed.perPassModels : []

        counts.forEach((count, index) => {
            const catchCount = typeof count === 'number' && Number.isFinite(count) ? count : 0
            const model = typeof models[index] === 'string' ? (models[index] as string) : null
            contributions.set(index + 1, { catchCount, model })
        })
    } catch {
        // Malformed payload: surface no contributions rather than throwing in a computed.
    }

    return contributions
}

/**
 * Extracts the 1-based pass index from a multi-pass union pass's `reason`. The backend records each
 * additional union pass with a reason of the form `"multi-pass union {arm} pass #{index}"`. Returns null
 * when no index is present (legacy rows) so callers can fall back rather than inventing a number.
 */
export function parseUnionPassIndex(reason: string | null | undefined): number | null {
    const match = /pass #(\d+)/i.exec(reason ?? '')
    return match ? Number(match[1]) : null
}
