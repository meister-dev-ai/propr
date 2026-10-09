// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import type { CanonicalSourceReferenceDto } from '@/services/providerDiscoveryService'
import type { WebhookEventType } from '@/services/webhookConfigurationService'

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

export function sourceOptionKey(canonicalSourceRef?: CanonicalSourceReferenceDto | null): string {
  if (!canonicalSourceRef?.provider || !canonicalSourceRef.value) {
    return ''
  }

  return `${canonicalSourceRef.provider}::${canonicalSourceRef.value}`
}
