<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->

<template>
    <div class="pass-selector" data-testid="pass-selector">
        <button
            type="button"
            class="pass-selector-trigger"
            :class="{ 'is-open': isOpen }"
            @click="isOpen = !isOpen"
        >
            <div class="pass-selector-trigger-content">
                <template v-if="selectedProtocol">
                    <span class="pass-selector-trigger-path">{{ selectedProtocol.label }}</span>
                    <span class="pass-selector-trigger-stats">
                        <span class="stat-tokens" :title="`${formatTokens(totalTokens)} tokens`">
                            <i class="fi fi-rr-coins" aria-hidden="true"></i>
                            {{ formatTokens(totalTokens) }}
                        </span>
                        <span v-if="findingsCount > 0" class="stat-findings" :title="`${findingsCount} finding${findingsCount === 1 ? '' : 's'}`">
                            <i class="fi fi-rr-bug" aria-hidden="true"></i>
                            {{ findingsCount }}
                        </span>
                    </span>
                </template>
                <span v-else class="pass-selector-placeholder">Select a file...</span>
            </div>
            <i class="fi fi-rr-angle-small-down pass-selector-chevron" :class="{ 'is-rotated': isOpen }"></i>
        </button>

        <div
            v-if="isOpen"
            ref="dropdownRef"
            class="pass-selector-dropdown"
            :style="dropdownStyle"
        >
                <div
                    v-for="group in groupedItems"
                    :key="group.path"
                    class="pass-selector-group"
                >
                    <div class="pass-selector-group-label">{{ group.label }}</div>
                    <button
                        v-for="protocol in group.protocols"
                        :key="protocol.id"
                        type="button"
                        class="pass-selector-option"
                        :class="{ 'is-selected': protocol.id === modelValue }"
                        @click="selectProtocol(protocol.id ?? null)"
                    >
                        <span class="pass-selector-option-path">{{ protocol.label }}</span>
                        <span class="pass-selector-option-stats">
                            <span class="stat-tokens">
                                <i class="fi fi-rr-coins" aria-hidden="true"></i>
                                {{ formatTokens((protocol.totalInputTokens ?? 0) + (protocol.totalOutputTokens ?? 0)) }}
                            </span>
                            <span
                                v-if="(protocol.finalComments?.length ?? 0) > 0"
                                class="stat-findings"
                            >
                                <i class="fi fi-rr-bug" aria-hidden="true"></i>
                                {{ protocol.finalComments!.length }}
                            </span>
                        </span>
                    </button>
                </div>
        </div>
    </div>
</template>

<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref } from 'vue'
import type { ReviewProtocolPass } from '../types'

interface SidebarItem {
    type: 'folder' | 'pass'
    name: string
    path?: string
    depth: number
    protocol?: ReviewProtocolPass
    isLast?: boolean
}

const props = defineProps<{
    items: SidebarItem[]
    modelValue: string | null
}>()

const emit = defineEmits<{
    (e: 'update:modelValue', value: string | null): void
}>()

const isOpen = ref(false)
const dropdownRef = ref<HTMLElement | null>(null)

const groupedItems = computed(() => {
    const groups: { path: string; label: string; protocols: ReviewProtocolPass[] }[] = []
    let currentGroup: { path: string; label: string; protocols: ReviewProtocolPass[] } | null = null

    for (const item of props.items) {
        if (item.type === 'folder') {
            if (currentGroup && currentGroup.protocols.length > 0) {
                groups.push(currentGroup)
            }
            currentGroup = {
                path: item.path ?? '',
                label: item.name,
                protocols: [],
            }
        } else if (item.type === 'pass' && item.protocol) {
            if (!currentGroup) {
                currentGroup = {
                    path: '',
                    label: 'Root',
                    protocols: [],
                }
            }
            currentGroup.protocols.push(item.protocol)
        }
    }

    if (currentGroup && currentGroup.protocols.length > 0) {
        groups.push(currentGroup)
    }

    return groups
})

const selectedProtocol = computed(() => {
    if (!props.modelValue) return null
    for (const item of props.items) {
        if (item.type === 'pass' && item.protocol?.id === props.modelValue) {
            return item.protocol
        }
    }
    return null
})

const totalTokens = computed(() => {
    if (!selectedProtocol.value) return 0
    return (selectedProtocol.value.totalInputTokens ?? 0) + (selectedProtocol.value.totalOutputTokens ?? 0)
})

const findingsCount = computed(() => {
    if (!selectedProtocol.value) return 0
    return selectedProtocol.value.finalComments?.length ?? 0
})

const dropdownStyle = computed(() => {
    if (!isOpen.value) return {}
    return {
        position: 'absolute' as const,
        top: '100%',
        left: '0',
        right: '0',
        marginTop: '4px',
    }
})

function formatTokens(value: number): string {
    if (value >= 1_000_000) return `${(value / 1_000_000).toFixed(1)}M`
    if (value >= 1_000) return `${(value / 1_000).toFixed(1)}k`
    return String(value)
}

function selectProtocol(id: string | null) {
    emit('update:modelValue', id)
    isOpen.value = false
}

function handleOutsideClick(event: MouseEvent) {
    const target = event.target as HTMLElement
    if (!target.closest('[data-testid="pass-selector"]')) {
        isOpen.value = false
    }
}

onMounted(() => {
    document.addEventListener('click', handleOutsideClick)
})

onUnmounted(() => {
    document.removeEventListener('click', handleOutsideClick)
})
</script>

<style scoped>
.pass-selector {
    position: relative;
    display: flex;
    align-items: center;
    gap: 0.75rem;
    padding: 0.75rem 1rem;
    border-bottom: 1px solid var(--color-border);
    background: rgba(255, 255, 255, 0.02);
}

.pass-selector-trigger {
    flex: 1;
    min-width: 0;
    display: flex;
    align-items: center;
    gap: 0.5rem;
    padding: 0.5rem 0.75rem;
    background: rgba(15, 17, 22, 0.8);
    border: 1px solid var(--color-border);
    border-radius: var(--radius-md);
    color: var(--color-text);
    font-size: 0.85rem;
    cursor: pointer;
    outline: none;
    transition: border-color 0.15s;
    text-align: left;
}

.pass-selector-trigger:hover {
    border-color: rgba(34, 211, 238, 0.4);
}

.pass-selector-trigger.is-open {
    border-color: var(--color-accent);
}

.pass-selector-trigger-content {
    flex: 1;
    min-width: 0;
    display: flex;
    align-items: center;
    gap: 0.75rem;
}

.pass-selector-trigger-path {
    flex: 1;
    min-width: 0;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
    font-family: 'JetBrains Mono', 'Fira Code', monospace;
}

.pass-selector-trigger-stats {
    display: flex;
    align-items: center;
    gap: 0.5rem;
    flex-shrink: 0;
}

.pass-selector-placeholder {
    color: var(--color-text-muted);
    font-style: italic;
}

.pass-selector-chevron {
    font-size: 0.7rem;
    transition: transform 0.15s ease;
    opacity: 0.6;
    flex-shrink: 0;
}

.pass-selector-chevron.is-rotated {
    transform: rotate(180deg);
}

.pass-selector-dropdown {
    position: absolute;
    z-index: 100;
    background: var(--color-surface);
    border: 1px solid var(--color-border);
    border-radius: var(--radius-md);
    box-shadow: 0 8px 24px rgba(0, 0, 0, 0.4);
    max-height: 400px;
    overflow-y: auto;
    width: 100%;
}

.pass-selector-group {
    display: flex;
    flex-direction: column;
}

.pass-selector-group-label {
    padding: 0.5rem 0.75rem 0.25rem;
    font-size: 0.7rem;
    font-weight: 700;
    text-transform: uppercase;
    letter-spacing: 0.08em;
    color: var(--color-accent);
    background: rgba(34, 211, 238, 0.06);
}

.pass-selector-option {
    display: flex;
    align-items: center;
    gap: 0.5rem;
    padding: 0.5rem 0.75rem;
    background: transparent;
    border: none;
    color: var(--color-text);
    font-size: 0.85rem;
    cursor: pointer;
    text-align: left;
    width: 100%;
    transition: background 0.1s;
}

.pass-selector-option:hover {
    background: rgba(255, 255, 255, 0.06);
}

.pass-selector-option.is-selected {
    background: rgba(59, 130, 246, 0.15);
}

.pass-selector-option-path {
    flex: 1;
    min-width: 0;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
    font-family: 'JetBrains Mono', 'Fira Code', monospace;
}

.pass-selector-option-stats {
    display: flex;
    align-items: center;
    gap: 0.5rem;
    flex-shrink: 0;
}

.stat-tokens {
    display: inline-flex;
    align-items: center;
    gap: 0.25rem;
    font-size: 0.75rem;
    color: var(--color-text-muted);
    background: rgba(255, 255, 255, 0.06);
    padding: 0.15rem 0.4rem;
    border-radius: var(--radius-xs);
}

.stat-tokens i {
    font-size: 0.65rem;
    opacity: 0.7;
}

.stat-findings {
    display: inline-flex;
    align-items: center;
    gap: 0.25rem;
    font-size: 0.75rem;
    font-weight: 600;
    color: var(--color-danger);
    background: rgba(239, 68, 68, 0.12);
    padding: 0.15rem 0.4rem;
    border-radius: var(--radius-xs);
}

.stat-findings i {
    font-size: 0.65rem;
}
</style>
