<template>
    <div v-if="isOpen" class="modal-overlay" @click="close">
        <div class="modal-dialog" @click.stop>
            <div class="modal-header">
                <h3>{{ title }}</h3>
                <button class="modal-close" @click="close" aria-label="Close">✕</button>
            </div>
            <div class="modal-body">
                <slot />
            </div>
            <div v-if="$slots.footer" class="modal-footer">
                <slot name="footer" />
            </div>
        </div>
    </div>
</template>

<script setup lang="ts">
import { onMounted, onUnmounted, watch } from 'vue'

const props = defineProps<{
    isOpen: boolean
    title: string
}>()

const emit = defineEmits<{
    (e: 'update:isOpen', value: boolean): void
}>()

function close() {
    emit('update:isOpen', false)
}

function handleEscape(e: KeyboardEvent) {
    if (e.key === 'Escape' && props.isOpen) {
        close()
    }
}

watch(() => props.isOpen, (val) => {
    if (val) {
        document.body.style.overflow = 'hidden'
    } else {
        document.body.style.overflow = ''
    }
})

onMounted(() => {
    document.addEventListener('keydown', handleEscape)
})

onUnmounted(() => {
    document.removeEventListener('keydown', handleEscape)
    document.body.style.overflow = ''
})
</script>

<style scoped>
.modal-overlay {
    position: fixed;
    inset: 0;
    z-index: 1000;
    background: rgba(0, 0, 0, 0.6);
    backdrop-filter: blur(4px);
    display: flex;
    align-items: center;
    justify-content: center;
    padding: 2rem;
}

.modal-dialog {
    background: var(--color-surface);
    border: 1px solid var(--color-border);
    border-radius: 16px;
    width: 90vw;
    max-width: 1000px;
    max-height: 85vh;
    display: flex;
    flex-direction: column;
    box-shadow: 0 20px 25px -5px rgba(0, 0, 0, 0.5), 0 10px 10px -5px rgba(0, 0, 0, 0.3);
    overflow: hidden;
    animation: modal-enter 0.2s cubic-bezier(0.16, 1, 0.3, 1);
}

@keyframes modal-enter {
    from { opacity: 0; transform: scale(0.95) translateY(10px); }
    to { opacity: 1; transform: scale(1) translateY(0); }
}

.modal-header {
    display: flex;
    align-items: center;
    justify-content: space-between;
    padding: 1.25rem 1.5rem;
    border-bottom: 1px solid var(--color-border);
    background: var(--color-bg);
}

.modal-header h3 {
    margin: 0;
    font-size: 1.1rem;
    font-weight: 600;
    letter-spacing: -0.01em;
}

.modal-close {
    background: none;
    border: none;
    color: var(--color-text-muted);
    font-size: 1.25rem;
    cursor: pointer;
    padding: 0.25rem 0.5rem;
    line-height: 1;
    border-radius: 4px;
    transition: all 0.2s;
}

.modal-close:hover {
    color: var(--color-danger);
    background: rgba(239, 68, 68, 0.1);
}

.modal-body {
    padding: 1.5rem;
    overflow-y: auto;
    font-size: 0.95rem;
    line-height: 1.6;
    color: var(--color-text);
}

.modal-footer {
    padding: 1.25rem 1.5rem;
    border-top: 1px solid var(--color-border);
    display: flex;
    justify-content: flex-end;
    gap: 0.75rem;
    background: var(--color-bg);
}
</style>
