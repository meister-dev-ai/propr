<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->

<template>
    <div class="section-card">
        <div class="section-card-header">
            <h3>Prompt Overrides</h3>
            <button class="btn-primary btn-sm" @click="showOverrideForm = !showOverrideForm">
                <i class="fi fi-rr-plus"></i> Add Override
            </button>
        </div>

        <!-- Create form -->
        <div v-if="showOverrideForm" class="section-card-body">
            <div class="form-field">
                <label>Prompt Key
                    <select v-model="newOverride.promptKey" class="form-input">
                        <option value="">— select —</option>
                        <option value="SystemPrompt">SystemPrompt</option>
                        <option value="AgenticLoopGuidance">AgenticLoopGuidance</option>
                        <option value="SynthesisSystemPrompt">SynthesisSystemPrompt</option>
                        <option value="QualityFilterSystemPrompt">QualityFilterSystemPrompt</option>
                        <option value="PerFileContextPrompt">PerFileContextPrompt</option>
                    </select>
                </label>
            </div>
            <div class="form-field">
                <label>Override Text
                    <span class="field-hint-inline">(full replacement for the prompt
                        segment)</span>
                    <textarea v-model="newOverride.overrideText" rows="6"
                        placeholder="Enter the full replacement prompt text…" class="form-input" />
                </label>
            </div>
            <span v-if="overrideCreateError" class="error">{{ overrideCreateError }}</span>
            <div class="form-actions">
                <button :disabled="overrideSaving || !newOverride.promptKey || !newOverride.overrideText.trim()"
                    class="btn-primary" @click="handleCreateOverride">
                    {{ overrideSaving ? "Saving…" : "Save Override" }}
                </button>
                <button class="btn-secondary" @click="showOverrideForm = false">
                    Cancel
                </button>
            </div>
        </div>

        <div v-if="overridesLoading" class="section-card-body">
            <p class="muted">Loading prompt overrides…</p>
        </div>
        <div v-else-if="overridesError" class="section-card-body">
            <p class="error">{{ overridesError }}</p>
        </div>
        <div v-else-if="promptOverrides.length === 0 && !overridesLoading" class="section-card-body">
            <p class="muted">
                No prompt overrides configured for this client.
            </p>
        </div>
        <div v-else class="section-card-body--compact">
            <table class="admin-table">
                <thead>
                    <tr>
                        <th style="width: 250px">Prompt Key</th>
                        <th>Override Text</th>
                        <th style="width: 80px" class="text-right">Actions</th>
                    </tr>
                </thead>
                <tbody>
                    <tr v-for="o in clientScopedOverrides" :key="o.id ?? ''">
                        <td class="font-semibold">{{ o.promptKey }}</td>
                        <td class="dismissal-pattern-cell">
                            <div class="pattern-text-wrapper" :title="o.overrideText ?? ''">
                                {{ o.overrideText }}
                            </div>
                        </td>
                        <td class="text-right">
                            <button class="btn-danger btn-xs" title="Delete Override"
                                @click="o.id && handleDeleteOverride(o.id)">
                                <i class="fi fi-rr-trash"></i>
                            </button>
                        </td>
                    </tr>
                </tbody>
            </table>
        </div>
    </div>
</template>

<script lang="ts" setup>
import { computed, reactive, ref, watch } from "vue";
import {
    createOverride,
    deleteOverride,
    listOverrides,
} from "@/services/promptOverridesService";
import type { components } from "@/services/generated/openapi";

type PromptOverrideDto = components["schemas"]["PromptOverrideDto"];

const props = defineProps<{ clientId: string; active?: boolean }>();

const promptOverrides = ref<PromptOverrideDto[]>([]);
const overridesLoading = ref(false);
const overridesError = ref("");
const showOverrideForm = ref(false);
const overrideSaving = ref(false);
const overrideCreateError = ref("");
const newOverride = reactive({
    scope: "clientScope" as "clientScope" | "crawlConfigScope",
    promptKey: "",
    overrideText: "",
});

const clientScopedOverrides = computed(() =>
    promptOverrides.value.filter((o) => o.scope === "clientScope")
);

let hasLoaded = false;

async function loadPromptOverrides() {
    if (overridesLoading.value) return;
    overridesLoading.value = true;
    overridesError.value = "";
    try {
        promptOverrides.value = await listOverrides(props.clientId);
        hasLoaded = true;
    } catch {
        overridesError.value = "Failed to load prompt overrides.";
    } finally {
        overridesLoading.value = false;
    }
}

async function handleCreateOverride() {
    overrideCreateError.value = "";
    overrideSaving.value = true;
    try {
        const o = await createOverride(props.clientId, {
            scope: "clientScope",
            promptKey: newOverride.promptKey,
            overrideText: newOverride.overrideText,
        });
        promptOverrides.value.push(o);
        newOverride.promptKey = "";
        newOverride.overrideText = "";
        showOverrideForm.value = false;
    } catch {
        overrideCreateError.value =
            "Failed to save override. A duplicate scope+key may already exist.";
    } finally {
        overrideSaving.value = false;
    }
}

async function handleDeleteOverride(id: string) {
    try {
        await deleteOverride(props.clientId, id);
        promptOverrides.value = promptOverrides.value.filter((o) => o.id !== id);
    } catch {
        overridesError.value = "Failed to delete override.";
    }
}

// Lazy-load on first activation (preserves the parent's previous load-on-tab-open behaviour).
watch(
    () => props.active,
    (isActive) => {
        if (isActive && !hasLoaded) {
            void loadPromptOverrides();
        }
    },
    { immediate: true }
);
</script>

<style scoped>
.btn-sm {
    font-size: 0.8rem;
    padding: 0.3rem 0.7rem;
}

.field-hint-inline {
    font-weight: normal;
    font-size: 0.78rem;
    color: var(--color-text-muted);
    text-transform: none;
    letter-spacing: 0;
}

.muted {
    color: var(--color-text-muted);
    font-style: italic;
    padding: 1rem 1.25rem;
}

.section-card-body--compact {
    padding: 1rem 1.25rem;
}

.text-right {
    text-align: right;
}

.dismissal-pattern-cell {
    max-width: 0;
    /* Enable truncation in flex/grid/table context */
}

.pattern-text-wrapper {
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
    font-family: inherit;
    font-size: 0.9rem;
    color: var(--color-text);
}

.admin-table {
    width: 100%;
    border-collapse: collapse;
}

.admin-table th {
    text-align: left;
    font-size: 0.75rem;
    text-transform: uppercase;
    letter-spacing: 0.05em;
    color: var(--color-text-muted);
    padding: 0.75rem 1rem;
    border-bottom: 1px solid var(--color-border);
}

.admin-table td {
    padding: 0.875rem 1rem;
    border-bottom: 1px solid var(--color-border);
    vertical-align: middle;
}

.admin-table tr:last-child td {
    border-bottom: none;
}

.admin-table tr:hover td {
    background: rgba(255, 255, 255, 0.02);
}
</style>
