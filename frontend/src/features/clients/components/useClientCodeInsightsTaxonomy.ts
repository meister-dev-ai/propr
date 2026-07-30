// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

import { computed, ref } from 'vue'
import {
  createCustomTag,
  fetchTaxonomy,
  retireCustomTag,
  updateCustomTag,
  type CodeInsightCoreTag,
  type CodeInsightCustomTag,
  type CodeInsightCustomTagWrite,
} from '@/services/codeInsightTaxonomyService'

/** Blank draft used for both the create form and an edit that has been cancelled. */
function emptyDraft(): CodeInsightCustomTagWrite {
  return { slug: '', displayName: '', definition: '' }
}

/**
 * State and actions for the client's finding-type taxonomy section.
 *
 * The two vocabularies are deliberately presented differently: the core set is read-only reference
 * material (editing it per installation would destroy cross-client comparability), while custom tags
 * are the client's own and fully editable. Retirement, not deletion, is the only way a tag leaves
 * circulation: deleting one would leave every finding that carried it unlabelled.
 */
export function useClientCodeInsightsTaxonomy(getClientId: () => string) {
  const coreTags = ref<CodeInsightCoreTag[]>([])
  const customTags = ref<CodeInsightCustomTag[]>([])
  const taxonomyVersion = ref(0)

  const loading = ref(false)
  const loadError = ref('')
  const saving = ref(false)
  const saveError = ref('')

  const showCreateForm = ref(false)
  const draft = ref<CodeInsightCustomTagWrite>(emptyDraft())
  const editingTagId = ref<string | null>(null)

  const activeCustomTags = computed(() => customTags.value.filter((tag) => tag.retiredAt === null))
  const retiredCustomTags = computed(() => customTags.value.filter((tag) => tag.retiredAt !== null))

  /** A draft is submittable once all three fields carry something; the server validates the shape. */
  const isDraftComplete = computed(
    () =>
      draft.value.slug.trim().length > 0 &&
      draft.value.displayName.trim().length > 0 &&
      draft.value.definition.trim().length > 0,
  )

  async function load(): Promise<void> {
    loading.value = true
    loadError.value = ''
    try {
      const taxonomy = await fetchTaxonomy(getClientId())
      // Defended here as well as in the service: a stubbed or partial response must degrade to an empty
      // list, never to a render failure that takes the surrounding tab down with it.
      coreTags.value = taxonomy.coreTags ?? []
      customTags.value = taxonomy.customTags ?? []
      taxonomyVersion.value = taxonomy.version ?? 0
    } catch (error) {
      loadError.value = error instanceof Error ? error.message : 'Failed to load the taxonomy.'
    } finally {
      loading.value = false
    }
  }

  function beginCreate(): void {
    editingTagId.value = null
    draft.value = emptyDraft()
    saveError.value = ''
    showCreateForm.value = true
  }

  function beginEdit(tag: CodeInsightCustomTag): void {
    editingTagId.value = tag.id
    draft.value = { slug: tag.slug, displayName: tag.displayName, definition: tag.definition }
    saveError.value = ''
    showCreateForm.value = true
  }

  function cancelEdit(): void {
    editingTagId.value = null
    draft.value = emptyDraft()
    saveError.value = ''
    showCreateForm.value = false
  }

  async function save(): Promise<void> {
    if (!isDraftComplete.value) {
      return
    }

    saving.value = true
    saveError.value = ''
    try {
      const request: CodeInsightCustomTagWrite = {
        slug: draft.value.slug.trim(),
        displayName: draft.value.displayName.trim(),
        definition: draft.value.definition.trim(),
      }

      if (editingTagId.value) {
        await updateCustomTag(getClientId(), editingTagId.value, request)
      } else {
        await createCustomTag(getClientId(), request)
      }

      cancelEdit()
      await load()
    } catch (error) {
      // The rejection reason matters here (shadowing a core type and reusing a retired slug are
      // different mistakes with different fixes) so it is surfaced verbatim rather than generalised.
      saveError.value = error instanceof Error ? error.message : 'Failed to save the custom tag.'
    } finally {
      saving.value = false
    }
  }

  async function retire(tag: CodeInsightCustomTag): Promise<void> {
    saving.value = true
    saveError.value = ''
    try {
      await retireCustomTag(getClientId(), tag.id)
      await load()
    } catch (error) {
      saveError.value = error instanceof Error ? error.message : 'Failed to retire the custom tag.'
    } finally {
      saving.value = false
    }
  }

  return {
    coreTags,
    customTags,
    activeCustomTags,
    retiredCustomTags,
    taxonomyVersion,
    loading,
    loadError,
    saving,
    saveError,
    showCreateForm,
    draft,
    editingTagId,
    isDraftComplete,
    load,
    beginCreate,
    beginEdit,
    cancelEdit,
    save,
    retire,
  }
}
