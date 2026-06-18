// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import type { AdoCrawlFilterOptionDto, CanonicalSourceReferenceDto } from '@/services/adoDiscoveryService'
import { formatProviderFamily } from '@/services/providerActivationService'
import type { WebhookEventType, WebhookProviderType } from '@/services/webhookConfigurationService'
import type { FilterRow } from './webhookConfigForm.types'

// Static option table and pure helpers for the webhook-config form. Extracted
// from WebhookConfigForm.vue so the component holds only state + orchestration.

export const eventOptions: Array<{ value: WebhookEventType; label: string; description: string }> = [
  {
    value: 'pullRequestCreated',
    label: 'PR Created',
    description: 'Accept deliveries when a pull request is first opened.',
  },
  {
    value: 'pullRequestUpdated',
    label: 'PR Updated',
    description: 'Handle pushes, reviewer changes, and close or abandon updates.',
  },
  {
    value: 'pullRequestCommented',
    label: 'PR Commented',
    description: 'Accept comment events that should refresh the review pipeline.',
  },
]

export function defaultManualOrganizationUrl(providerType?: WebhookProviderType): string {
  switch (providerType) {
    case 'gitLab':
      return 'https://gitlab.example.com'
    case 'forgejo':
      return 'https://codeberg.org'
    case 'github':
    default:
      return 'https://github.com'
  }
}

export function formatManualProviderName(providerType: WebhookProviderType): string {
  return providerType === 'azureDevOps' ? 'Provider' : formatProviderFamily(providerType)
}

export function sourceOptionKey(canonicalSourceRef?: CanonicalSourceReferenceDto | null): string {
  if (!canonicalSourceRef?.provider || !canonicalSourceRef.value) {
    return ''
  }

  return `${canonicalSourceRef.provider}::${canonicalSourceRef.value}`
}

export function matchesFilterOption(filter: FilterRow, option: AdoCrawlFilterOptionDto): boolean {
  const optionKey = sourceOptionKey(option.canonicalSourceRef)
  if (optionKey && optionKey === filter.selectedFilterKey) {
    return true
  }

  const candidateNames = [
    filter.repositoryName,
    filter.displayName,
    filter.canonicalSourceRef?.value ?? '',
  ]
    .map((value) => value.trim())
    .filter((value) => value.length > 0)

  const optionNames = [
    option.displayName ?? '',
    option.canonicalSourceRef?.value ?? '',
  ]
    .map((value) => value.trim())
    .filter((value) => value.length > 0)

  return candidateNames.some((candidate) =>
    optionNames.some((optionName) => optionName.localeCompare(candidate, undefined, { sensitivity: 'accent' }) === 0),
  )
}
