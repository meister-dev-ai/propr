// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

import type { DiscoveryBranch, CanonicalSourceReferenceDto } from '@/services/providerDiscoveryService'
import type { ProCursorKnowledgeSourceDto } from '@/services/proCursorService'

// Pure normalization/format/sort helpers for the crawl-config form. Extracted
// from CrawlConfigForm.vue so the component holds only state + orchestration.

const uuidPattern = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i

export function normalizeText(value: string | null | undefined): string {
  return value?.trim() ?? ''
}

export function normalizeStringList(values: ReadonlyArray<string | null | undefined> | null | undefined): string[] {
  const normalizedValues: string[] = []
  const seen = new Set<string>()

  for (const value of values ?? []) {
    const normalizedValue = normalizeText(value)
    if (!normalizedValue || seen.has(normalizedValue)) {
      continue
    }

    seen.add(normalizedValue)
    normalizedValues.push(normalizedValue)
  }

  return normalizedValues
}

export function isValidUuid(value: string): boolean {
  return uuidPattern.test(value)
}

export function cloneCanonicalSourceRef(canonicalSourceRef: CanonicalSourceReferenceDto | null | undefined): CanonicalSourceReferenceDto | null {
  const provider = normalizeText(canonicalSourceRef?.provider)
  const value = normalizeText(canonicalSourceRef?.value)
  if (!provider || !value) {
    return null
  }

  return { provider, value }
}

export function sourceOptionKey(canonicalSourceRef: CanonicalSourceReferenceDto | null | undefined): string {
  const canonical = cloneCanonicalSourceRef(canonicalSourceRef)
  if (!canonical) {
    return ''
  }

  return `${canonical.provider}::${canonical.value}`
}

export function formatProCursorSourceLabel(source: ProCursorKnowledgeSourceDto): string {
  return normalizeText(source.displayName) || normalizeText(source.sourceDisplayName) || normalizeText(source.repositoryId) || 'Unnamed source'
}

export function formatProCursorSourcePath(source: ProCursorKnowledgeSourceDto): string {
  const providerScopePath = normalizeText(source.providerScopePath) || 'No organization'
  const sourceDisplayName = normalizeText(source.sourceDisplayName) || normalizeText(source.repositoryId) || 'No selected source'
  return `${providerScopePath} / ${normalizeText(source.providerProjectKey) || 'No project'} / ${sourceDisplayName}`
}

export function sortProCursorSources(sources: ProCursorKnowledgeSourceDto[]): ProCursorKnowledgeSourceDto[] {
  return [...sources].sort((left, right) => formatProCursorSourceLabel(left).localeCompare(formatProCursorSourceLabel(right)))
}

export function sortBranchSuggestions(branchSuggestions: DiscoveryBranch[] | null | undefined): DiscoveryBranch[] {
  return [...(branchSuggestions ?? [])].sort((left, right) => {
    if (!!left.isDefault !== !!right.isDefault) {
      return left.isDefault ? -1 : 1
    }

    return normalizeText(left.branchName).localeCompare(normalizeText(right.branchName))
  })
}

export function formatBranchSuggestion(branchSuggestion: DiscoveryBranch): string {
  const branchName = normalizeText(branchSuggestion.branchName)
  return branchSuggestion.isDefault ? `${branchName} (default)` : branchName
}

export function formatProCursorScopeRepairMessage(repairCount: number): string {
  return repairCount === 1
    ? '1 saved ProCursor source is no longer eligible for this client. That selection was removed locally; save to persist the repaired scope.'
    : `${repairCount} saved ProCursor sources are no longer eligible for this client. Those selections were removed locally; save to persist the repaired scope.`
}
