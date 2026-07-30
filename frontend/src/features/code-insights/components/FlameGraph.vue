<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->
<!-- This file implements commercial-only functionality. A commercial license is required to activate or use that functionality. -->

<template>
  <div class="flame">
    <!-- Zooming is what makes a deep tree readable, so where you are has to be visible and reversible. -->
    <nav v-if="trail.length > 1" class="flame-trail" aria-label="Zoom level">
      <template v-for="(frame, index) in trail" :key="frame.key || 'root'">
        <button type="button" class="trail-step" @click="zoomTo(frame.key)">
          {{ frame.name || rootLabel }}
        </button>
        <span v-if="index < trail.length - 1" class="trail-sep" aria-hidden="true">/</span>
      </template>
    </nav>

    <div
      class="flame-canvas"
      role="img"
      :aria-label="`${chartLabel}. ${zoomed.value} ${unit} across ${leafCount} ${leafNoun}. The data table below carries the same numbers.`"
    >
      <div class="flame-row flame-row--root">
        <button
          type="button"
          class="flame-frame flame-frame--root"
          :title="`${zoomed.key || rootLabel}: ${zoomed.value} ${unit}`"
          @click="zoomOut"
        >
          <span class="frame-label">{{ zoomed.name || rootLabel }}</span>
          <span class="frame-value">{{ zoomed.value }}</span>
        </button>
      </div>

      <div v-for="(level, depth) in levels" :key="depth" class="flame-row">
        <button
          v-for="frame in level"
          :key="frame.key"
          type="button"
          class="flame-frame"
          :class="[`flame-frame--heat-${heat(frame)}`, { 'flame-frame--leaf': frame.children.length === 0 }]"
          :style="{ width: `${width(frame)}%` }"
          :title="describe(frame)"
          :aria-label="describe(frame)"
          @click="onFrameClick(frame)"
        >
          <span class="frame-label">{{ frame.name }}</span>
        </button>
      </div>
    </div>

    <p v-if="levels.length === 0" class="flame-empty">Nothing to draw here yet.</p>
  </div>
</template>

<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import {
  collapseSingleChildRuns,
  findNode,
  flattenByDepth,
  type FlameNode,
} from '@/features/code-insights/flameTree'

const props = withDefaults(
  defineProps<{
    /** The tree to draw. Its root is the whole scope; its children are the first level of frames. */
    root: FlameNode
    /** What a value counts, for the labels and tooltips ("findings"). */
    unit?: string
    /** What a leaf is, for the summary label ("files"). */
    leafNoun?: string
    /** Name for the outermost frame, which has no segment of its own. */
    rootLabel?: string
    /** Accessible description of the whole graph. */
    chartLabel?: string
    /** Extra detail per frame, appended to its tooltip: the caller knows what its payload means. */
    detail?: (frame: FlameNode) => string | null
  }>(),
  {
    unit: 'findings',
    leafNoun: 'files',
    rootLabel: 'Everything in scope',
    chartLabel: 'Flame graph',
    detail: undefined,
  },
)

const emit = defineEmits<{ select: [frame: FlameNode] }>()

/** Which frame is the current root. Kept as a key rather than a node so a reload cannot leave a stale reference. */
const zoomKey = ref('')

// A new dataset invalidates wherever the reader had zoomed to; keep the zoom only if the frame still exists.
watch(
  () => props.root,
  (root) => {
    if (zoomKey.value && !findNode(root, zoomKey.value)) {
      zoomKey.value = ''
    }
  },
)

const collapsed = computed(() => collapseSingleChildRuns(props.root))
const zoomed = computed(() => findNode(collapsed.value, zoomKey.value) ?? collapsed.value)
const levels = computed(() => flattenByDepth(zoomed.value))

const leafCount = computed(() => countLeaves(zoomed.value))

/** The path from the outermost frame down to where the reader has zoomed. */
const trail = computed(() => {
  const steps: FlameNode[] = []
  const walk = (node: FlameNode): boolean => {
    steps.push(node)
    if (node.key === zoomed.value.key) return true
    for (const child of node.children) {
      if (walk(child)) return true
    }
    steps.pop()
    return false
  }

  walk(collapsed.value)
  return steps
})

function countLeaves(node: FlameNode): number {
  return node.children.length === 0 ? 1 : node.children.reduce((total, child) => total + countLeaves(child), 0)
}

function width(frame: FlameNode): number {
  // Share of what is currently zoomed to, so the widths always fill the row.
  const total = zoomed.value.value
  return total === 0 ? 0 : Math.max((frame.value / total) * 100, 0.4)
}

/** Five steps rather than a gradient, so the scale survives a theme and can be read at a glance. */
function heat(frame: FlameNode): number {
  const total = zoomed.value.value
  if (total === 0) return 0
  const share = frame.value / total
  if (share >= 0.4) return 4
  if (share >= 0.2) return 3
  if (share >= 0.1) return 2
  if (share >= 0.05) return 1
  return 0
}

function describe(frame: FlameNode): string {
  const extra = props.detail?.(frame)
  const base = `${frame.key}: ${frame.value} ${props.unit}`
  return extra ? `${base}, ${extra}` : base
}

function onFrameClick(frame: FlameNode): void {
  // A frame with children zooms; a leaf is the thing itself, so it opens.
  if (frame.children.length > 0) {
    zoomKey.value = frame.key
    return
  }

  emit('select', frame)
}

function zoomTo(key: string): void {
  zoomKey.value = key
}

function zoomOut(): void {
  const steps = trail.value
  zoomKey.value = steps.length > 1 ? steps[steps.length - 2].key : ''
}
</script>

<style scoped>
.flame {
  display: flex;
  flex-direction: column;
  gap: 0.5rem;
}

.flame-trail {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 0.25rem;
  font-size: 0.78rem;
  color: var(--color-text-muted);
}

.trail-step {
  background: none;
  border: 0;
  padding: 0.1rem 0.2rem;
  color: var(--color-accent);
  font: inherit;
  cursor: pointer;
  border-radius: var(--radius-xs);
}

.trail-step:hover {
  text-decoration: underline;
}

.flame-canvas {
  display: flex;
  flex-direction: column;
  gap: 2px;
}

.flame-row {
  display: flex;
  gap: 2px;
  min-height: 1.45rem;
}

.flame-frame {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 0.4rem;
  overflow: hidden;
  padding: 0.15rem 0.4rem;
  border: 1px solid rgba(148, 163, 184, 0.25);
  border-radius: var(--radius-xs);
  background: rgba(148, 163, 184, 0.12);
  color: var(--color-text);
  font: inherit;
  font-size: 0.75rem;
  white-space: nowrap;
  cursor: pointer;
  transition: filter 0.12s;
}

.flame-frame:hover {
  filter: brightness(1.25);
}

.flame-frame--root {
  width: 100%;
  background: rgba(148, 163, 184, 0.2);
  font-weight: 600;
}

/* Hotter frames carry more of what is being counted. Five steps, not a gradient. */
.flame-frame--heat-1 {
  background: rgba(56, 189, 248, 0.18);
  border-color: rgba(56, 189, 248, 0.35);
}

.flame-frame--heat-2 {
  background: rgba(250, 204, 21, 0.18);
  border-color: rgba(250, 204, 21, 0.4);
}

.flame-frame--heat-3 {
  background: rgba(249, 115, 22, 0.22);
  border-color: rgba(249, 115, 22, 0.45);
}

.flame-frame--heat-4 {
  background: rgba(239, 68, 68, 0.26);
  border-color: rgba(239, 68, 68, 0.5);
}

.flame-frame--leaf {
  border-style: dashed;
}

.frame-label {
  overflow: hidden;
  text-overflow: ellipsis;
}

.frame-value {
  color: var(--color-text-muted);
  font-variant-numeric: tabular-nums;
}

.flame-empty {
  margin: 0;
  font-size: 0.85rem;
  color: var(--color-text-muted);
}
</style>
