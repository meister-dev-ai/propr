// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { describe, expect, it } from 'vitest'
import { mount } from '@vue/test-utils'

describe('ProCursorUsageRecentEventsTable', () => {
  it('renders safe recent-event fields and estimated badges', async () => {
    const { default: ProCursorUsageRecentEventsTable } = await import('@/components/ProCursorUsageRecentEventsTable.vue')
    const wrapper = mount(ProCursorUsageRecentEventsTable, {
      props: {
        items: [
          {
            occurredAtUtc: '2026-03-30T10:15:00Z',
            requestId: 'pcidx:test:source-1:1',
            callType: 'embedding',
            modelName: 'text-embedding-3-small',
            deploymentName: 'text-embedding-3-small',
            promptTokens: 12000,
            completionTokens: 0,
            totalTokens: 12000,
            estimatedCostUsd: 0.024,
            tokensEstimated: true,
            costEstimated: true,
            sourcePath: '/docs/intro.md',
            resourceId: 'ado://wiki/intro',
          },
        ],
      },
    })

    expect(wrapper.text()).toContain('text-embedding-3-small')
    expect(wrapper.text()).toContain('/docs/intro.md')
    expect(wrapper.text()).toContain('12,000')
    expect(wrapper.text()).toContain('Estimated')
  })

  it('renders the empty state when there are no events', async () => {
    const { default: ProCursorUsageRecentEventsTable } = await import('@/components/ProCursorUsageRecentEventsTable.vue')
    const wrapper = mount(ProCursorUsageRecentEventsTable, {
      props: {
        items: [],
      },
    })

    expect(wrapper.text()).toContain('No recent usage events')
  })
})
