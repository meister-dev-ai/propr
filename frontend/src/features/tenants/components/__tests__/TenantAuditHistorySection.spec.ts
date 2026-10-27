// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { flushPromises, mount } from '@vue/test-utils'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import TenantAuditHistorySection from '../TenantAuditHistorySection.vue'

const mocks = vi.hoisted(() => ({
  listTenantAuditEntries: vi.fn(),
}))

vi.mock('@/services/elevationsService', () => mocks)

function entry(id: string, overrides: Record<string, unknown> = {}) {
  return {
    id,
    tenantId: 'tenant-1',
    eventType: 'elevation.content_read',
    summary: 'Platform administrator operator read the protocol of a review job of client Payments.',
    detail: 'Request: GET /jobs/job-1/protocol. Reason: SUP-1042',
    actorUserId: 'operator-id',
    actorName: 'operator',
    elevationId: 'elevation-1',
    occurredAt: '2026-10-26T10:00:00Z',
    ...overrides,
  }
}

describe('TenantAuditHistorySection', () => {
  beforeEach(() => {
    vi.clearAllMocks()
  })

  it('shows the elevation records next to the other changes of the tenant', async () => {
    mocks.listTenantAuditEntries.mockResolvedValue({
      items: [
        entry('3', { eventType: 'elevation.started', summary: 'Platform administrator operator started an elevation.' }),
        entry('2', { eventType: 'tenant.membership.assigned', summary: 'Assigned a membership.', actorName: 'admin', elevationId: null }),
      ],
      totalCount: 2,
      page: 1,
      pageSize: 100,
    })

    const wrapper = mount(TenantAuditHistorySection, { props: { tenantId: 'tenant-1' } })
    await flushPromises()

    const rows = wrapper.findAll('[data-testid="tenant-audit-row"]')
    expect(rows).toHaveLength(2)
    expect(rows[0].text()).toContain('started an elevation')
    expect(rows[0].classes()).toContain('audit-row--elevation')
    expect(rows[1].text()).toContain('Assigned a membership.')
    expect(mocks.listTenantAuditEntries).toHaveBeenCalledWith('tenant-1', 1, 100)
  })

  it('folds consecutive reads of the same request into one row with their number', async () => {
    mocks.listTenantAuditEntries.mockResolvedValue({
      items: [
        entry('5', { occurredAt: '2026-10-26T10:00:09Z' }),
        entry('4', { occurredAt: '2026-10-26T10:00:06Z' }),
        entry('3', { occurredAt: '2026-10-26T10:00:03Z' }),
        entry('2', { detail: 'Request: GET /jobs/job-2/protocol. Reason: SUP-1042' }),
      ],
      totalCount: 4,
      page: 1,
      pageSize: 100,
    })

    const wrapper = mount(TenantAuditHistorySection, { props: { tenantId: 'tenant-1' } })
    await flushPromises()

    const rows = wrapper.findAll('[data-testid="tenant-audit-row"]')
    expect(rows).toHaveLength(2)
    expect(rows[0].get('[data-testid="tenant-audit-repeat"]').text()).toContain('3 times')
    expect(rows[1].find('[data-testid="tenant-audit-repeat"]').exists()).toBe(false)
    expect(rows[1].text()).toContain('/jobs/job-2/protocol')
  })

  it('shows the history of the current tenant when the answer for the previous tenant arrives later', async () => {
    let answerPrevious!: (value: unknown) => void
    mocks.listTenantAuditEntries
      .mockReturnValueOnce(new Promise((resolve) => {
        answerPrevious = resolve
      }))
      .mockResolvedValueOnce({
        items: [entry('9', { tenantId: 'tenant-2', summary: 'Entry of the second tenant.', elevationId: null })],
        totalCount: 1,
        page: 1,
        pageSize: 100,
      })

    const wrapper = mount(TenantAuditHistorySection, { props: { tenantId: 'tenant-1' } })
    await wrapper.setProps({ tenantId: 'tenant-2' })
    await flushPromises()
    answerPrevious({
      items: [entry('1', { summary: 'Entry of the first tenant.' })],
      totalCount: 1,
      page: 1,
      pageSize: 100,
    })
    await flushPromises()

    const text = wrapper.get('[data-testid="tenant-audit-history"]').text()
    expect(text).toContain('Entry of the second tenant.')
    expect(text).not.toContain('Entry of the first tenant.')
  })

  it('keeps the loaded entries in view when an older page cannot be loaded', async () => {
    mocks.listTenantAuditEntries
      .mockResolvedValueOnce({
        items: [entry('2', { summary: 'Newest record.', elevationId: null }), entry('1', { summary: 'Older record.' })],
        totalCount: 3,
        page: 1,
        pageSize: 100,
      })
      .mockRejectedValueOnce(new Error('The audit history could not be loaded.'))
    const wrapper = mount(TenantAuditHistorySection, { props: { tenantId: 'tenant-1' } })
    await flushPromises()

    await wrapper.get('button').trigger('click')
    await flushPromises()

    expect(wrapper.text()).toContain('The audit history could not be loaded.')
    expect(wrapper.findAll('[data-testid="tenant-audit-row"]')).toHaveLength(2)
    expect(wrapper.text()).toContain('Newest record.')
    expect(wrapper.get('button').text()).toBe('Load older entries')
  })

  it('says so when the history is empty', async () => {
    mocks.listTenantAuditEntries.mockResolvedValue({ items: [], totalCount: 0, page: 1, pageSize: 100 })

    const wrapper = mount(TenantAuditHistorySection, { props: { tenantId: 'tenant-1' } })
    await flushPromises()

    expect(wrapper.find('[data-testid="tenant-audit-empty"]').exists()).toBe(true)
  })
})
