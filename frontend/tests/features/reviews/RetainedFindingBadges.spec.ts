// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { describe, expect, it } from 'vitest'
import { mount } from '@vue/test-utils'
import RetainedFindingBadges from '@/features/reviews/components/RetainedFindingBadges.vue'
import type { CodeInsightFinding } from '@/services/codeInsightsAnalyticsService'

describe('RetainedFindingBadges', () => {
  it('groups the recorded severity, kinds and outcome under an accessible name', () => {
    const finding: CodeInsightFinding = {
      id: 'finding-1', clientId: 'client-1', repositoryId: 'repo-1', pullRequestId: 42,
      jobId: 'job-1', filePath: 'auth.ts', lineNumber: 7, severity: 'Warning',
      message: 'Check the token.', coreTags: ['security'], disposition: 'Dismissed',
      rejectionReason: 'Redundant', providerThreadId: 'thread-1', observedAt: '2026-01-01T00:00:00Z',
    }
    const wrapper = mount(RetainedFindingBadges, { props: { finding } })
    const group = wrapper.get('[role="group"][aria-label="Finding details"]')
    expect(group.text()).toContain('Warning')
    expect(group.text()).toContain('Security')
    expect(group.text()).toContain('Dismissed')
    expect(group.get('[title="Recorded rejection reason: Redundant"]').text()).toBe('Duplicate')
  })
})
