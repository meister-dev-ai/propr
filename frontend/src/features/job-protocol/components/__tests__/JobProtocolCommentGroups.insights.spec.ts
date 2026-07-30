// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { mount } from '@vue/test-utils'
import { describe, expect, it, vi } from 'vitest'
import type { CodeInsightFindingClassification } from '@/services/codeInsightFindingsService'
import type { JobProtocolViewModel } from '@/features/job-protocol/composables/useJobProtocolViewModel'
import type { CommentGroupComment } from '../../types'
import JobProtocolCommentGroups from '../JobProtocolCommentGroups.vue'

/** The component reads a handful of view-model members; only those need to exist. */
function stubViewModel(): JobProtocolViewModel {
  return {
    routeClientId: undefined,
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

function mountGroups(codeInsights?: CodeInsightFindingClassification | null) {
  const comment: CommentGroupComment = {
    filePath: 'src/Service.cs',
    lineNumber: 42,
    severity: 'error',
    message: 'The null check is missing.',
    codeInsights,
  }

  return mount(JobProtocolCommentGroups, {
    props: {
      vm: stubViewModel(),
      groups: [{ directory: 'src', comments: [comment] }],
      emptyMessage: 'none',
      showOrigin: true,
    },
  })
}

function classified(
  overrides: Partial<CodeInsightFindingClassification> = {},
): CodeInsightFindingClassification {
  return {
    ordinal: 0,
    status: 'classified',
    coreTags: ['data-validation'],
    customTags: [],
    level: 'member',
    qualifier: 'missing',
    confidence: 0.8,
    ...overrides,
  }
}

describe('JobProtocolCommentGroups. Code Insights badges', () => {
  it('renders the core type tags on a classified finding', () => {
    const wrapper = mountGroups(classified({ coreTags: ['data-validation', 'security'] }))

    const tags = wrapper.findAll('[data-testid="insight-core-tag"]').map(tag => tag.text())
    expect(tags).toEqual(['data-validation', 'security'])
  })

  it('distinguishes a client-defined tag from a core one', () => {
    // Only the core set means the same thing across clients, so the two must not look identical.
    const wrapper = mountGroups(classified({ customTags: ['domain-rule'] }))

    expect(wrapper.get('[data-testid="insight-custom-tag"]').text()).toBe('domain-rule')
    expect(wrapper.get('[data-testid="insight-custom-tag"]').classes()).toContain(
      'insight-badge--custom',
    )
    expect(wrapper.get('[data-testid="insight-core-tag"]').classes()).toContain(
      'insight-badge--core',
    )
  })

  it('renders the qualifier and level as one readable phrase', () => {
    const wrapper = mountGroups(classified({ qualifier: 'missing', level: 'member' }))

    // "member" is the storage word; a reviewer reads "method".
    expect(wrapper.get('[data-testid="insight-row"]').text()).toContain('missing, method-level')
  })

  it('omits the phrase when neither axis was classified', () => {
    const wrapper = mountGroups(classified({ qualifier: null, level: null }))

    expect(wrapper.get('[data-testid="insight-row"]').text()).not.toContain('-level')
  })

  it('shows a pending state rather than looking like an untyped finding', () => {
    // Classification is post-hoc, so a just-finished review has none for a cycle or two. "Not yet" must not
    // read the same as "nothing to say".
    const wrapper = mountGroups(classified({ status: 'pending', coreTags: [], customTags: [] }))

    expect(wrapper.get('[data-testid="insight-pending"]').text()).toContain('classifying')
    expect(wrapper.findAll('[data-testid="insight-core-tag"]')).toHaveLength(0)
  })

  it('distinguishes a finding the classifier could not place from one still pending', () => {
    const wrapper = mountGroups(
      classified({ status: 'unclassifiable', coreTags: [], customTags: [] }),
    )

    expect(wrapper.get('[data-testid="insight-unclassifiable"]').text()).toContain('not classified')
    expect(wrapper.find('[data-testid="insight-pending"]').exists()).toBe(false)
  })

  it('renders no insight row at all when nothing was collected', () => {
    // The ordinary case on Community, or for a client that never opted in: the view is exactly as before.
    const wrapper = mountGroups(undefined)

    expect(wrapper.find('[data-testid="insight-row"]').exists()).toBe(false)
    expect(wrapper.text()).toContain('The null check is missing.')
  })
})
