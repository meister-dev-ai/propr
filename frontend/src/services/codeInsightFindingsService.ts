// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

/**
 * Reads the collected classification of a review job's findings, so a review view can show what kind of
 * problem each finding describes next to the finding itself.
 *
 * Deliberately separate from the review-result read. The review contract is what the review path produces
 * and knows nothing about this slice; the caller lines these up by `ordinal`, which is each finding's index
 * in that persisted result. An unlicensed install, or a client that never opted in, simply yields an empty
 * list: "no tags" is a normal state the view renders anyway, not an error.
 */

import { authedFetch } from '@/services/api'
import { getJobsBaseUrl } from '@/services/jobsService'

export type CodeInsightClassificationStatus = 'classified' | 'pending' | 'unclassifiable'

export type CodeInsightFindingLevel = 'statement' | 'member' | 'type' | 'file' | 'crossFile'

export type CodeInsightFindingQualifier = 'missing' | 'incorrect' | 'extraneous'

export interface CodeInsightFindingClassification {
  /** Index of the finding within its job's persisted review result. The join key. */
  ordinal: number
  status: CodeInsightClassificationStatus
  /** Core type slugs, comparable across clients. */
  coreTags: string[]
  /** The client's own type slugs, including any since retired. */
  customTags: string[]
  level?: CodeInsightFindingLevel | null
  qualifier?: CodeInsightFindingQualifier | null
  confidence?: number | null
}

/**
 * Returns the classification of each finding of the job, or an empty list when nothing was collected.
 * Never throws for the ordinary "not collected / not licensed" cases: those are an empty 200.
 */
export async function fetchFindingClassifications(
  jobId: string,
): Promise<CodeInsightFindingClassification[]> {
  const res = await authedFetch(`${getJobsBaseUrl()}/jobs/${jobId}/code-insights/findings`)
  if (!res.ok) {
    throw new Error(`GET /jobs/${jobId}/code-insights/findings: ${res.status}`)
  }

  const data = (await res.json()) as CodeInsightFindingClassification[] | null
  return data ?? []
}
