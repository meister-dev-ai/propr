// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { mount } from '@vue/test-utils'
import { describe, expect, it, vi } from 'vitest'
import { modelLabel, modelTitle, symbolLabel } from '@/features/job-protocol/composables/passLabels'
import type { JobProtocolViewModel } from '@/features/job-protocol/composables/useJobProtocolViewModel'
import type { CommentGroupComment } from '../../types'
import JobProtocolCommentGroups from '../JobProtocolCommentGroups.vue'

/**
 * Which model produced a finding, shown where the finding is read. "Pass 2" answers half of "where did this come
 * from" once passes can run on different models; the model answers the other half, and it is the same attribution
 * the per-model metrics are built on.
 */
function stubViewModel(originLabel: string | null = 'Pass 2'): JobProtocolViewModel {
  return {
    routeClientId: undefined,
    dismissingIds: new Set<string>(),
    commentKey: (comment: CommentGroupComment) => String(comment.message),
    dismissComment: vi.fn(),
    renderMarkdown: (text?: string | null) => text ?? '',
    commentOriginLabel: () => originLabel,
    commentModelLabel: (comment: CommentGroupComment) =>
      modelLabel(comment.originModelId, comment.originLogicalModelName),
    commentModelTitle: (comment: CommentGroupComment) =>
      modelTitle(comment.originModelId, comment.originLogicalModelName),
    commentSymbolLabel: (comment: CommentGroupComment) =>
      symbolLabel(comment.originSymbolName, comment.originSymbolKind),
    selectFindingOrigin: vi.fn(),
  } as unknown as JobProtocolViewModel
}

function mountGroups(comment: Partial<CommentGroupComment>, originLabel: string | null = 'Pass 2') {
  return mount(JobProtocolCommentGroups, {
    props: {
      vm: stubViewModel(originLabel),
      groups: [
        {
          directory: 'src',
          comments: [
            {
              filePath: 'src/Service.cs',
              lineNumber: 42,
              severity: 'error',
              message: 'The null check is missing.',
              ...comment,
            },
          ],
        },
      ],
      emptyMessage: 'none',
      showOrigin: true,
    },
  })
}

describe('JobProtocolCommentGroups: producing model', () => {
  it('names the model beside the pass that produced the finding', () => {
    const wrapper = mountGroups({
      originModelId: 'gpt-5.4-mini',
      originLogicalModelName: 'thrifty-reviewer',
    })

    const badge = wrapper.get('[data-testid="model-badge"]')
    expect(badge.text()).toContain('thrifty-reviewer')
    // The remote model is not lost: a logical name can be repointed, so the tooltip carries both.
    expect(badge.attributes('title')).toContain('gpt-5.4-mini')
    expect(wrapper.get('[data-testid="origin-badge"]').text()).toContain('Pass 2')
  })

  it('falls back to the remote model when no logical model is configured', () => {
    const wrapper = mountGroups({ originModelId: 'gpt-5.4-mini' })

    expect(wrapper.get('[data-testid="model-badge"]').text()).toContain('gpt-5.4-mini')
  })

  it('renders no model badge for a finding whose model was never recorded', () => {
    // Reviews that ran before the attribution existed, and findings no single pass owns.
    const wrapper = mountGroups({})

    expect(wrapper.find('[data-testid="model-badge"]').exists()).toBe(false)
    expect(wrapper.text()).toContain('The null check is missing.')
  })

  it('names the definition the finding sits inside, beside the pass and the model', () => {
    // A line number says where; this says what the finding is about.
    const wrapper = mountGroups({
      originModelId: 'gpt-5.4-mini',
      originSymbolName: 'Process',
      originSymbolKind: 'Method',
    })

    const badge = wrapper.get('[data-testid="symbol-badge"]')
    expect(badge.text()).toContain('Process()')
    expect(badge.attributes('title')).toContain('method')
  })

  it('renders no symbol badge for a finding nothing placed', () => {
    // Pull-request-level findings, unsupported languages, and lines outside every definition.
    const wrapper = mountGroups({ originModelId: 'gpt-5.4-mini' })

    expect(wrapper.find('[data-testid="symbol-badge"]').exists()).toBe(false)
  })

  it('shows the model even when the producing pass is unknown', () => {
    // The two attributions are independent: a finding can know its model and not its pass.
    const wrapper = mountGroups({ originModelId: 'gpt-5.4-mini' }, null)

    expect(wrapper.get('[data-testid="model-badge"]').text()).toContain('gpt-5.4-mini')
    expect(wrapper.find('[data-testid="origin-badge"]').exists()).toBe(false)
  })
})
