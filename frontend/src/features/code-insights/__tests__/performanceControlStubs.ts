// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { defineComponent, h, nextTick, type PropType } from 'vue'

/** Preserves selector events in workspace state tests; control interactions are tested separately. */
export const performanceSelectStub = defineComponent({
  name: 'ReviewerPerformanceSelect',
  inheritAttrs: false,
  props: {
    modelValue: String,
    items: Array as PropType<{ value: string; label: string }[]>,
    label: String,
    disabled: Boolean,
    containedMenu: Boolean,
  },
  emits: ['update:modelValue'],
  setup(props, { attrs, emit }) {
    return () => h('select', {
      ...attrs,
      value: props.modelValue,
      disabled: props.disabled,
      'aria-label': props.label,
      onChange: async (event: Event) => {
        const input = event.target as HTMLSelectElement
        emit('update:modelValue', input.value)
        await nextTick()
        input.value = props.modelValue ?? ''
      },
    }, (props.items ?? []).map((item) => h('option', { value: item.value }, item.label)))
  },
})
