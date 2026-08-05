// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { mount } from '@vue/test-utils'
import { describe, expect, it, vi } from 'vitest'
import type { JobProtocolViewModel } from '@/features/job-protocol/composables/useJobProtocolViewModel'
import type { CommentGroupComment } from '../../types'
import JobProtocolCommentGroups from '../JobProtocolCommentGroups.vue'

/**
 * Dismissing writes a memory record that suppresses similar findings for the whole client, so the endpoint
 * takes the client-administrator role. A reader who only has access to the client sees no button rather
 * than one that answers with a refusal.
 */
function stubViewModel(canDismissFindings: boolean): JobProtocolViewModel {
  return {
    routeClientId: '7e2456e5-f799-4aea-b749-9bf543308780',
    canDismissFindings,
    dismissingIds: new Set<string>(),
    commentKey: (comment: CommentGroupComment) => String(comment.message),
    dismissComment: vi.fn(),
    renderMarkdown: (text?: string | null) => text ?? '',
    commentOriginLabel: () => null,
    commentModelLabel: () => null,
    commentModelTitle: () => null,
    commentSymbolLabel: () => null,
    selectFindingOrigin: vi.fn(),
  } as unknown as JobProtocolViewModel
}

function mountGroups(canDismissFindings: boolean) {
  return mount(JobProtocolCommentGroups, {
    props: {
      vm: stubViewModel(canDismissFindings),
      groups: [
        {
          directory: 'src',
          comments: [
            {
              filePath: 'src/Service.cs',
              lineNumber: 42,
              severity: 'error',
              message: 'The null check is missing.',
            },
          ],
        },
      ],
      emptyMessage: 'none',
      showDismiss: true,
    },
  })
}

describe('JobProtocolCommentGroups: who may dismiss', () => {
  it('offers dismissal to a client administrator', () => {
    expect(mountGroups(true).find('.dismiss-btn').exists()).toBe(true)
  })

  it('offers no dismissal to a reader who only has client access', () => {
    const wrapper = mountGroups(false)

    expect(wrapper.find('.dismiss-btn').exists()).toBe(false)
    // The finding itself still reads exactly as it did; only the action is gone.
    expect(wrapper.text()).toContain('The null check is missing.')
  })
})
