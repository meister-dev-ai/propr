// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { beforeEach, describe, expect, it, vi, type Mock } from 'vitest'

const notifyMock = vi.fn()

vi.mock('@/composables/useSession', () => ({
  useSession: () => ({ getAccessToken: () => 'test-token' }),
}))

vi.mock('@/composables/useNotification', () => ({
  useNotification: () => ({ notify: notifyMock }),
}))

const base = 'http://localhost/api'

interface MockUser {
  id: string
  username: string
  globalRole: string
  isActive: boolean
  createdAt: string
}

function mockRes(body: unknown, status = 200) {
  return { ok: status >= 200 && status < 300, status, json: () => Promise.resolve(body) }
}

let users: MockUser[]
let patchResponse: () => ReturnType<typeof mockRes>
let deleteResponse: () => ReturnType<typeof mockRes>

function installFetch() {
  const fetchMock = global.fetch as unknown as Mock
  fetchMock.mockImplementation((url: string, opts?: RequestInit) => {
    const method = opts?.method ?? 'GET'
    if (url === `${base}/admin/users` && method === 'GET') return Promise.resolve(mockRes(users))
    if (url === `${base}/clients` && method === 'GET') return Promise.resolve(mockRes([]))
    if (url.endsWith('/permanent') && method === 'DELETE') return Promise.resolve(deleteResponse())
    if (url.startsWith(`${base}/admin/users/`) && method === 'PATCH') return Promise.resolve(patchResponse())
    return Promise.resolve(mockRes(null))
  })
}

function findButton(wrapper: VueWrapper, label: string) {
  return wrapper.findAll('button').find(b => b.text().trim() === label)
}

function patchCalls() {
  const fetchMock = global.fetch as unknown as Mock
  return fetchMock.mock.calls.filter(c => (c[1] as RequestInit | undefined)?.method === 'PATCH')
}

function deleteCalls() {
  const fetchMock = global.fetch as unknown as Mock
  return fetchMock.mock.calls.filter(c => (c[1] as RequestInit | undefined)?.method === 'DELETE')
}

async function mountView() {
  const { default: UsersView } = await import('@/features/users/views/UsersView.vue')
  const wrapper = mount(UsersView)
  await flushPromises()
  return wrapper
}

describe('UsersView enable/disable actions', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    users = [
      { id: 'active-admin', username: 'admin', globalRole: 'Admin', isActive: true, createdAt: '2026-01-01T00:00:00Z' },
      { id: 'disabled-user', username: 'former.employee', globalRole: 'User', isActive: false, createdAt: '2026-01-01T00:00:00Z' },
    ]
    patchResponse = () => mockRes(null, 204)
    deleteResponse = () => mockRes(null, 204)
    window.confirm = vi.fn(() => true)
    installFetch()
  })

  it('renders a Disable button on active rows and a Re-enable button on disabled rows', async () => {
    const wrapper = await mountView()

    expect(findButton(wrapper, 'Disable')).toBeTruthy()
    expect(findButton(wrapper, 'Re-enable')).toBeTruthy()
  })

  it('disables an active user via PATCH with isActive=false and updates the row optimistically', async () => {
    const wrapper = await mountView()

    await findButton(wrapper, 'Disable')!.trigger('click')
    await flushPromises()

    const calls = patchCalls()
    expect(calls).toHaveLength(1)
    expect(calls[0][0]).toBe(`${base}/admin/users/active-admin`)
    expect(JSON.parse((calls[0][1] as RequestInit).body as string)).toEqual({ isActive: false })
    expect(notifyMock).toHaveBeenCalledWith('User disabled.', 'success')
    // Both rows are now disabled, so no Disable button remains.
    expect(findButton(wrapper, 'Disable')).toBeUndefined()
  })

  it('re-enables a disabled user via PATCH with isActive=true', async () => {
    const wrapper = await mountView()

    await findButton(wrapper, 'Re-enable')!.trigger('click')
    await flushPromises()

    const calls = patchCalls()
    expect(calls).toHaveLength(1)
    expect(calls[0][0]).toBe(`${base}/admin/users/disabled-user`)
    expect(JSON.parse((calls[0][1] as RequestInit).body as string)).toEqual({ isActive: true })
    expect(notifyMock).toHaveBeenCalledWith('User re-enabled.', 'success')
  })

  it('does not call the API when the confirm dialog is declined', async () => {
    window.confirm = vi.fn(() => false)
    const wrapper = await mountView()

    await findButton(wrapper, 'Disable')!.trigger('click')
    await flushPromises()

    expect(patchCalls()).toHaveLength(0)
    expect(notifyMock).not.toHaveBeenCalled()
  })

  it('surfaces the server error message and leaves the row unchanged on a non-204 response', async () => {
    patchResponse = () => mockRes({ error: 'Cannot disable the last active global admin.' }, 409)
    const wrapper = await mountView()

    await findButton(wrapper, 'Disable')!.trigger('click')
    await flushPromises()

    expect(notifyMock).toHaveBeenCalledWith('Cannot disable the last active global admin.', 'error')
    // The optimistic update must not fire on failure: the active admin keeps its Disable button.
    expect(findButton(wrapper, 'Disable')).toBeTruthy()
  })
})

describe('UsersView permanent delete', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    users = [
      { id: 'active-admin', username: 'admin', globalRole: 'Admin', isActive: true, createdAt: '2026-01-01T00:00:00Z' },
      { id: 'disabled-user', username: 'former.employee', globalRole: 'User', isActive: false, createdAt: '2026-01-01T00:00:00Z' },
    ]
    patchResponse = () => mockRes(null, 204)
    deleteResponse = () => mockRes(null, 204)
    window.confirm = vi.fn(() => true)
    installFetch()
  })

  function deleteButtonInRow(wrapper: VueWrapper, rowIndex: number) {
    const row = wrapper.findAll('tbody tr')[rowIndex]
    return row.findAll('button').find(b => b.text().trim() === 'Delete')
  }

  it('disables Delete for the last active admin and enables it for other users', async () => {
    const wrapper = await mountView()

    const adminDelete = deleteButtonInRow(wrapper, 0)
    const userDelete = deleteButtonInRow(wrapper, 1)
    expect(adminDelete!.attributes('disabled')).toBeDefined()
    expect(adminDelete!.attributes('title')).toContain('last active administrator')
    expect(userDelete!.attributes('disabled')).toBeUndefined()
  })

  it('requires typing the exact username before the permanent-delete button is enabled', async () => {
    const wrapper = await mountView()

    await deleteButtonInRow(wrapper, 1)!.trigger('click')
    const confirmButton = findButton(wrapper, 'Permanently delete')
    expect(confirmButton).toBeTruthy()
    expect(confirmButton!.attributes('disabled')).toBeDefined()

    await wrapper.find('.delete-confirm-form input').setValue('wrong-name')
    expect(findButton(wrapper, 'Permanently delete')!.attributes('disabled')).toBeDefined()

    await wrapper.find('.delete-confirm-form input').setValue('former.employee')
    expect(findButton(wrapper, 'Permanently delete')!.attributes('disabled')).toBeUndefined()
  })

  it('permanently deletes the user and removes the row after the typed confirmation', async () => {
    const wrapper = await mountView()

    await deleteButtonInRow(wrapper, 1)!.trigger('click')
    await wrapper.find('.delete-confirm-form input').setValue('former.employee')
    await findButton(wrapper, 'Permanently delete')!.trigger('click')
    await flushPromises()

    const calls = deleteCalls()
    expect(calls).toHaveLength(1)
    expect(calls[0][0]).toBe(`${base}/admin/users/disabled-user/permanent`)
    expect(notifyMock).toHaveBeenCalledWith('User permanently deleted.', 'success')
    expect(wrapper.text()).not.toContain('former.employee')
  })

  it('surfaces the server error and keeps the row on a non-204 response', async () => {
    deleteResponse = () => mockRes({ error: 'Cannot delete the last active administrator.' }, 409)
    const wrapper = await mountView()

    await deleteButtonInRow(wrapper, 1)!.trigger('click')
    await wrapper.find('.delete-confirm-form input').setValue('former.employee')
    await findButton(wrapper, 'Permanently delete')!.trigger('click')
    await flushPromises()

    expect(notifyMock).toHaveBeenCalledWith('Cannot delete the last active administrator.', 'error')
    expect(wrapper.text()).toContain('former.employee')
  })
})
