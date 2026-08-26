<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->

<script setup lang="ts">
/**
 * A file picker built from the application's own controls rather than the browser's native file input.
 *
 * The native input stays in the markup and keeps the interaction. It is moved out of view rather than
 * hidden, so it still receives focus, still opens the file dialog from the keyboard, and still announces as
 * a file input. The styled label next to it is the visible control and shows the focus ring on its behalf.
 *
 * Two labels point at the input: the field label at the call site and the button label here. A screen reader
 * concatenates both into the control's name, which reads as "License file Choose file". Both parts are
 * visible on the page, so the spoken name matches what is on screen.
 *
 * The picked file is not stored here. Each call site already holds the file it read, and a second copy of
 * that state could diverge from the first. The change event is forwarded unchanged, so a call site keeps
 * the handler it already had.
 */
import { computed } from 'vue'

const props = withDefaults(defineProps<{
  /** Id of the native input, so the field label at the call site can point at it. */
  inputId: string
  /** File types offered in the dialog, in the accept attribute's own syntax. */
  accept?: string
  /** Name of the file the call site holds, or null while none is picked. */
  fileName?: string | null
  /**
   * Id of an element the call site renders that describes the input, such as the text stating why a file was
   * rejected. It is announced with the control rather than only being visible beside it.
   */
  describedBy?: string
  disabled?: boolean
  /** Placed on the native input, because that is the element a test drives. */
  testId?: string
  buttonLabel?: string
  emptyLabel?: string
}>(), {
  fileName: null,
  disabled: false,
  buttonLabel: 'Choose file',
  emptyLabel: 'No file chosen',
})

const emit = defineEmits<{
  change: [event: Event]
}>()

// The file name describes the input rather than labelling it, so it is read out after the control instead of
// joining the control's name.
const nameId = computed(() => `${props.inputId}-name`)

// The name and the call site's description both point at the input, so a call site that describes the field
// adds to what is announced rather than replacing the file name. Empty ids are dropped, because a call site
// passes nothing while it has nothing to describe.
const describedByIds = computed(() =>
  [nameId.value, props.describedBy]
    .filter((id): id is string => id !== undefined && id.length > 0)
    .join(' '))

const nameTestId = computed(() => (props.testId === undefined ? undefined : `${props.testId}-name`))
</script>

<template>
  <div class="file-picker">
    <input
      :id="inputId"
      class="visually-hidden file-picker__input"
      type="file"
      :accept="accept"
      :disabled="disabled"
      :aria-describedby="describedByIds"
      :data-testid="testId"
      @change="emit('change', $event)"
    />
    <label class="btn-secondary file-picker__button" :for="inputId">{{ buttonLabel }}</label>
    <span :id="nameId" class="file-picker__name" :data-testid="nameTestId">
      {{ fileName ?? emptyLabel }}
    </span>
  </div>
</template>

<style scoped>
.file-picker {
  display: flex;
  align-items: center;
  gap: 0.75rem;
  flex-wrap: wrap;
}

/* Drops the bottom margin the global `label` rule sets, because this label sits inline beside the file name.
 * The inline layout itself comes from `.btn-secondary`. */
.file-picker__button {
  margin: 0;
}

/* Focus lands on the input, which is out of view, so the ring is drawn on the label instead. */
.file-picker__input:focus-visible+.file-picker__button {
  border-color: var(--color-accent);
  box-shadow: 0 0 0 1px var(--color-accent);
}

.file-picker__input:disabled+.file-picker__button {
  opacity: 0.5;
  cursor: not-allowed;
}

.file-picker__name {
  color: var(--color-text-muted);
  font-size: 0.85rem;
  min-width: 0;
  overflow-wrap: anywhere;
}
</style>
