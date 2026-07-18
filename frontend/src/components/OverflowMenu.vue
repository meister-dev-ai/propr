<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->

<template>
    <div ref="rootRef" class="overflow-menu">
        <button
            type="button"
            class="btn-ghost overflow-menu-trigger"
            :title="title"
            aria-haspopup="menu"
            :aria-expanded="isOpen"
            @click.stop="toggle"
        >
            <i class="fi fi-rr-menu-dots-vertical"></i>
        </button>

        <div v-if="isOpen" class="card overflow-menu-list" role="menu">
            <slot :close="close" />
        </div>
    </div>
</template>

<script lang="ts" setup>
import { onBeforeUnmount, onMounted, ref } from 'vue'

withDefaults(defineProps<{ title?: string }>(), { title: 'More actions' })

const rootRef = ref<HTMLElement | null>(null)
const isOpen = ref(false)

function open(): void {
    isOpen.value = true
}

function close(): void {
    isOpen.value = false
}

function toggle(): void {
    isOpen.value ? close() : open()
}

function handleDocumentClick(event: MouseEvent): void {
    if (!isOpen.value) return
    const target = event.target as Node | null
    if (rootRef.value && target && !rootRef.value.contains(target)) {
        close()
    }
}

function handleKeydown(event: KeyboardEvent): void {
    if (isOpen.value && event.key === 'Escape') {
        close()
    }
}

onMounted(() => {
    document.addEventListener('click', handleDocumentClick)
    document.addEventListener('keydown', handleKeydown)
})

onBeforeUnmount(() => {
    document.removeEventListener('click', handleDocumentClick)
    document.removeEventListener('keydown', handleKeydown)
})

defineExpose({ open, close })
</script>

<style scoped>
.overflow-menu {
    position: relative;
    display: inline-flex;
}

.overflow-menu-trigger {
    display: inline-flex;
    align-items: center;
    justify-content: center;
    padding: 0.25rem 0.5rem;
    line-height: 1;
    color: var(--color-text-muted);
    border: 1px solid var(--color-border);
    white-space: nowrap;
}

.overflow-menu-trigger:hover {
    color: var(--color-text);
    background: rgba(255, 255, 255, 0.04);
    border-color: var(--color-border);
}

.overflow-menu-list {
    position: absolute;
    top: calc(100% + 0.35rem);
    right: 0;
    z-index: 50;
    min-width: 10rem;
    padding: 0.35rem;
    display: flex;
    flex-direction: column;
    gap: 0.15rem;
    background: var(--color-surface);
    border: 1px solid var(--color-border);
    border-radius: var(--radius-md);
    box-shadow: 0 8px 24px rgba(0, 0, 0, 0.35);
}

/* Style slotted menu items (owned by the parent, so reach them via :slotted). */
.overflow-menu-list :slotted(.overflow-menu-item) {
    display: flex;
    align-items: center;
    gap: 0.5rem;
    width: 100%;
    padding: 0.45rem 0.6rem;
    font-size: 0.85rem;
    text-align: left;
    color: var(--color-text);
    background: transparent;
    border: none;
    border-radius: var(--radius-sm);
    cursor: pointer;
    white-space: nowrap;
}

.overflow-menu-list :slotted(.overflow-menu-item:hover:not(:disabled)) {
    background: rgba(255, 255, 255, 0.06);
}

.overflow-menu-list :slotted(.overflow-menu-item:disabled) {
    opacity: 0.6;
    cursor: not-allowed;
}
</style>
