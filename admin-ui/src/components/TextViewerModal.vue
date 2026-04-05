<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->

<template>
    <div v-if="isOpen" class="modal-overlay" @click="close">
        <div class="modal-dialog text-viewer-modal" @click.stop>
            <div class="modal-header">
                <h3>{{ title }}</h3>
                <button class="modal-close" @click="close" aria-label="Close">✕</button>
            </div>
            <div class="modal-body text-content">
                <pre v-if="plainText" class="text-viewer-content">{{ text }}</pre>
                <div v-else class="markdown-content" v-html="renderedMarkdown"></div>
            </div>
            <div class="modal-footer">
                <button type="button" class="btn-secondary" @click="copyToClipboard">
                    <i class="fi fi-rr-copy"></i>
                    Copy
                </button>
                <button type="button" class="btn-primary" @click="close">Close</button>
            </div>
        </div>
    </div>
</template>

<script setup lang="ts">
import { computed, onMounted, onUnmounted, watch } from 'vue'

const props = withDefaults(
    defineProps<{
        isOpen: boolean
        title: string
        text: string
        plainText?: boolean
    }>(),
    {
        plainText: false,
    }
)

const emit = defineEmits<{
    (e: 'update:isOpen', value: boolean): void
}>()

const renderedMarkdown = computed(() => {
    if (props.plainText) return ''
    // Simple markdown rendering - you can enhance this later with a proper markdown library
    return props.text
        .replace(/`([^`]+)`/g, '<code>$1</code>')
        .replace(/\*\*([^*]+)\*\*/g, '<strong>$1</strong>')
        .replace(/\n/g, '<br/>')
})

function close() {
    emit('update:isOpen', false)
}

function handleEscape(e: KeyboardEvent) {
    if (e.key === 'Escape' && props.isOpen) {
        close()
    }
}

function copyToClipboard() {
    navigator.clipboard.writeText(props.text).then(() => {
        // Optional: Show toast notification
        console.log('Copied to clipboard')
    })
}

watch(
    () => props.isOpen,
    (val) => {
        if (val) {
            document.body.style.overflow = 'hidden'
        } else {
            document.body.style.overflow = ''
        }
    }
)

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
    from {
        opacity: 0;
        transform: scale(0.95) translateY(10px);
    }
    to {
        opacity: 1;
        transform: scale(1) translateY(0);
    }
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
    flex: 1;
}

.modal-body.text-content {
    font-family: 'Courier New', monospace;
    font-size: 0.9rem;
    line-height: 1.6;
    color: var(--color-text-muted);
}

.text-viewer-content {
    margin: 0;
    padding: 1rem;
    background: var(--color-bg);
    border: 1px solid var(--color-border);
    border-radius: 8px;
    overflow-x: auto;
    word-wrap: break-word;
    white-space: pre-wrap;
    color: var(--color-text);
}

.markdown-content {
    font-size: 0.95rem;
    line-height: 1.6;
    color: var(--color-text);
}

.markdown-content code {
    background: var(--color-bg);
    border: 1px solid var(--color-border);
    border-radius: 4px;
    padding: 0.2rem 0.5rem;
    font-family: 'Courier New', monospace;
    font-size: 0.85rem;
    color: var(--color-accent);
}

.markdown-content strong {
    font-weight: 600;
    color: var(--color-text);
}

.markdown-content br {
    display: block;
    height: 0.5rem;
    content: '';
}

.modal-footer {
    padding: 1.25rem 1.5rem;
    border-top: 1px solid var(--color-border);
    display: flex;
    justify-content: flex-end;
    gap: 0.75rem;
    background: var(--color-bg);
}

.modal-footer button {
    display: inline-flex;
    align-items: center;
    gap: 0.5rem;
}
</style>
