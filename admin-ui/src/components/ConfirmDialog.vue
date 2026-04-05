<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->

<template>
  <div v-if="open" class="confirm-dialog-overlay">
    <div class="confirm-dialog">
      <p>{{ message }}</p>
      <div class="confirm-dialog-actions">
        <button class="btn-danger" @click="$emit('confirm')">Confirm</button>
        <button @click="$emit('cancel')">Cancel</button>
      </div>
    </div>
  </div>
</template>

<script setup lang="ts">
defineProps<{
  open: boolean
  message: string
}>()

defineEmits<{
  confirm: []
  cancel: []
}>()
</script>
