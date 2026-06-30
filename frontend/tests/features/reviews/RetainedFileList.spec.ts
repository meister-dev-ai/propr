// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { describe, expect, it } from 'vitest'
import { mount } from '@vue/test-utils'
import RetainedFileList from '@/features/reviews/components/RetainedFileList.vue'
import type { RetainedFile } from '@/features/reviews/composables/useRetainedPrData'

const files: RetainedFile[] = [
  { filePath: 'src/auth/tokens.ts', changeType: 'Modified', isBinary: false },
  { filePath: 'assets/logo.png', changeType: 'Added', isBinary: true },
  { filePath: 'README.md', changeType: 'Modified', isBinary: false },
]

function mountList(props: Partial<InstanceType<typeof RetainedFileList>['$props']> = {}) {
  return mount(RetainedFileList, {
    props: {
      files,
      commentCount: (filePath: string) => (filePath === 'src/auth/tokens.ts' ? 3 : 0),
      threadCount: (filePath: string) => (filePath === 'src/auth/tokens.ts' ? 1 : 0),
      ...props,
    },
  })
}

describe('RetainedFileList', () => {
  it('renders nested folders for the files\' directory paths', () => {
    const wrapper = mountList()

    const folderPaths = wrapper
      .findAll('[data-testid="retained-file-folder"]')
      .map(f => f.attributes('data-folder-path'))

    // `src/auth/tokens.ts` produces nested `src` and `src/auth` folders; `assets/logo.png`
    // produces an `assets` folder; the root-level `README.md` produces no folder.
    expect(folderPaths).toContain('src')
    expect(folderPaths).toContain('src/auth')
    expect(folderPaths).toContain('assets')
    expect(folderPaths).not.toContain('') // root is never a folder row
  })

  it('renders a file leaf showing its basename, change type, and (full path) data attribute', () => {
    const wrapper = mountList()

    const items = wrapper.findAll('[data-testid="retained-file-item"]')
    expect(items).toHaveLength(3)

    const tokensLeaf = wrapper.find('[data-file-path="src/auth/tokens.ts"]')
    expect(tokensLeaf.exists()).toBe(true)
    // Leaf text is the basename, not the full path, so long paths stay readable.
    expect(tokensLeaf.text()).toContain('tokens.ts')
    expect(wrapper.text()).not.toContain('src/auth/tokens.ts')
    // Full path is preserved as a hover title on the file name.
    expect(tokensLeaf.find('.retained-file-name').attributes('title')).toBe('src/auth/tokens.ts')

    const changeTypes = wrapper.findAll('[data-testid="retained-file-change-type"]').map(c => c.text())
    expect(changeTypes).toContain('Modified')
    expect(changeTypes).toContain('Added')
  })

  it('shows a comment badge only on files that have retained comments', () => {
    const wrapper = mountList()

    const badges = wrapper.findAll('[data-testid="retained-file-comment-badge"]')
    expect(badges).toHaveLength(1)
    expect(badges[0].text()).toContain('3')
  })

  it('emits select with the file when a leaf is clicked', async () => {
    const wrapper = mountList()

    await wrapper.find('[data-file-path="src/auth/tokens.ts"]').trigger('click')

    const emitted = wrapper.emitted('select')
    expect(emitted).toBeTruthy()
    expect((emitted?.[0]?.[0] as RetainedFile).filePath).toBe('src/auth/tokens.ts')
  })

  it('reflects the selected file with the active highlight', () => {
    const wrapper = mountList({ selectedFilePath: 'src/auth/tokens.ts' })

    const tokensLeaf = wrapper.find('[data-file-path="src/auth/tokens.ts"]')
    expect(tokensLeaf.classes()).toContain('retained-file-item--active')

    const logoLeaf = wrapper.find('[data-file-path="assets/logo.png"]')
    expect(logoLeaf.classes()).not.toContain('retained-file-item--active')
  })

  it('collapses a folder when its header is clicked, hiding its descendant leaves', async () => {
    const wrapper = mountList()

    // Initially expanded: the leaf under src/auth is in the DOM.
    expect(wrapper.find('[data-file-path="src/auth/tokens.ts"]').exists()).toBe(true)

    const srcFolder = wrapper
      .findAll('[data-testid="retained-file-folder"]')
      .find(f => f.attributes('data-folder-path') === 'src')
    expect(srcFolder).toBeTruthy()

    await srcFolder!.trigger('click')

    // Collapsing `src` removes its nested folder and the leaf beneath it.
    expect(wrapper.find('[data-file-path="src/auth/tokens.ts"]').exists()).toBe(false)
    const foldersAfter = wrapper
      .findAll('[data-testid="retained-file-folder"]')
      .map(f => f.attributes('data-folder-path'))
    expect(foldersAfter).toContain('src')
    expect(foldersAfter).not.toContain('src/auth')
  })

  it('renders an empty state when there are no retained files', () => {
    const wrapper = mountList({ files: [] })

    expect(wrapper.find('[data-testid="retained-file-list-empty"]').exists()).toBe(true)
    expect(wrapper.findAll('[data-testid="retained-file-item"]')).toHaveLength(0)
    expect(wrapper.findAll('[data-testid="retained-file-folder"]')).toHaveLength(0)
  })
})
