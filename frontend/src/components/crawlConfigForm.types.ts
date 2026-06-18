// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import type { CanonicalSourceReferenceDto } from '@/services/adoDiscoveryService'
import type { components } from '@/types'

export type ScmProvider = components['schemas']['ScmProvider']
export type CrawlConfigResponse = components['schemas']['CrawlConfigResponse'] & { provider?: ScmProvider }
export type CrawlRepoFilterResponse = components['schemas']['CrawlRepoFilterResponse']
export type CrawlRepoFilterRequest = components['schemas']['CrawlRepoFilterRequest']
export type CreateAdminCrawlConfigRequest = components['schemas']['CreateAdminCrawlConfigRequest'] & { provider?: ScmProvider }
export type PromptOverrideDto = components['schemas']['PromptOverrideDto']
export type ProCursorSourceScopeMode = components['schemas']['ProCursorSourceScopeMode']

/** One editable repository-filter row in the crawl-config form. */
export interface FilterRow {
  id: string
  selectedFilterKey: string
  repositoryName: string
  displayName: string
  canonicalSourceRef: CanonicalSourceReferenceDto | null
  targetBranchPatterns: string[]
  isLegacy: boolean
}
