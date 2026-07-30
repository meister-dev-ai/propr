<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->
<!-- This file implements commercial-only functionality. A commercial license is required to activate or use that functionality. -->

<template>
    <div class="section-card" data-testid="code-insights-taxonomy">
        <div class="section-card-header">
            <h3>Finding-type taxonomy</h3>
            <button class="btn-primary btn-sm" :disabled="saving" @click="beginCreate">
                <i class="fi fi-rr-plus"></i> Add custom tag
            </button>
        </div>

        <div class="section-card-body">
            <p class="taxonomy-intro">
                Findings are tagged by type so quality can be analysed by the kind of problem found. The
                core set below is fixed for this installation: it is what makes numbers comparable
                between clients and over time. Custom tags are this client's own and never appear in a
                cross-client comparison.
            </p>

            <p v-if="loadError" class="error" data-testid="taxonomy-load-error">{{ loadError }}</p>
            <p v-else-if="loading" class="loading">Loading…</p>

            <template v-else>
                <!-- Editor: create, or edit an existing custom tag. -->
                <div v-if="showCreateForm" class="taxonomy-editor" data-testid="taxonomy-editor">
                    <h4>{{ editingTagId ? "Edit custom tag" : "New custom tag" }}</h4>
                    <div class="form-field">
                        <label for="taxonomySlug">Slug
                            <span class="field-hint-inline">(lower-kebab-case; cannot match a core type)</span>
                            <input id="taxonomySlug" v-model="draft.slug" class="form-input" type="text"
                                placeholder="e.g. domain-rule" />
                        </label>
                    </div>
                    <div class="form-field">
                        <label for="taxonomyDisplayName">Display name
                            <input id="taxonomyDisplayName" v-model="draft.displayName" class="form-input"
                                type="text" placeholder="e.g. Domain rule" />
                        </label>
                    </div>
                    <div class="form-field">
                        <label for="taxonomyDefinition">Definition
                            <span class="field-hint-inline">(the classifier uses this to decide when the tag applies)</span>
                            <textarea id="taxonomyDefinition" v-model="draft.definition" class="form-input" rows="2"
                                placeholder="One sentence describing what this type of finding is." />
                        </label>
                    </div>
                    <span v-if="saveError" class="error" data-testid="taxonomy-save-error">{{ saveError }}</span>
                    <div class="form-actions">
                        <button class="btn-primary" :disabled="saving || !isDraftComplete" @click="save">
                            {{ saving ? "Saving…" : "Save" }}
                        </button>
                        <button class="btn-secondary" :disabled="saving" @click="cancelEdit">Cancel</button>
                    </div>
                </div>

                <span v-else-if="saveError" class="error" data-testid="taxonomy-save-error">{{ saveError }}</span>

                <!-- Custom tags -->
                <h4 class="taxonomy-group-heading">Custom tags</h4>
                <p v-if="activeCustomTags.length === 0" class="muted-inline">
                    No custom tags yet. The core set alone is enough to get useful analysis.
                </p>
                <table v-else class="taxonomy-table" data-testid="custom-tag-table">
                    <thead>
                        <tr>
                            <th>Tag</th>
                            <th>Definition</th>
                            <th class="taxonomy-actions-header">Actions</th>
                        </tr>
                    </thead>
                    <tbody>
                        <tr v-for="tag in activeCustomTags" :key="tag.id">
                            <td>
                                <strong>{{ tag.displayName }}</strong>
                                <code class="taxonomy-slug">{{ tag.slug }}</code>
                            </td>
                            <td>{{ tag.definition }}</td>
                            <td class="taxonomy-actions">
                                <button class="btn-secondary btn-sm" :disabled="saving" @click="beginEdit(tag)">
                                    Edit
                                </button>
                                <button class="btn-secondary btn-sm" :disabled="saving" @click="retire(tag)">
                                    Retire
                                </button>
                            </td>
                        </tr>
                    </tbody>
                </table>

                <details v-if="retiredCustomTags.length > 0" class="taxonomy-retired">
                    <summary>{{ retiredCustomTags.length }} retired tag(s)</summary>
                    <p class="muted-inline">
                        Retired tags are no longer applied to new findings. Findings already tagged with them
                        keep their label, which is why a retired slug cannot be reused.
                    </p>
                    <ul class="taxonomy-retired-list">
                        <li v-for="tag in retiredCustomTags" :key="tag.id">
                            <strong>{{ tag.displayName }}</strong>
                            <code class="taxonomy-slug">{{ tag.slug }}</code>
                        </li>
                    </ul>
                </details>

                <!-- Core set, read-only -->
                <h4 class="taxonomy-group-heading">
                    Core set
                    <span class="field-hint-inline">(version {{ taxonomyVersion }}, read-only)</span>
                </h4>
                <table class="taxonomy-table" data-testid="core-tag-table">
                    <thead>
                        <tr>
                            <th>Type</th>
                            <th>Definition</th>
                            <th>Quality characteristic</th>
                        </tr>
                    </thead>
                    <tbody>
                        <tr v-for="tag in coreTags" :key="tag.slug">
                            <td>
                                <strong>{{ tag.displayName }}</strong>
                                <code class="taxonomy-slug">{{ tag.slug }}</code>
                            </td>
                            <td>{{ tag.definition }}</td>
                            <td>{{ characteristicLabel(tag.characteristic) }}</td>
                        </tr>
                    </tbody>
                </table>
            </template>
        </div>
    </div>
</template>

<script lang="ts" setup>
import { onMounted } from "vue";
import { useClientCodeInsightsTaxonomy } from "@/features/clients/components/useClientCodeInsightsTaxonomy";
import type { CodeInsightQualityCharacteristic } from "@/services/codeInsightTaxonomyService";

const props = defineProps<{ clientId: string }>();

const {
    coreTags,
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
} = useClientCodeInsightsTaxonomy(() => props.clientId);

const CHARACTERISTIC_LABELS: Record<CodeInsightQualityCharacteristic, string> = {
    reliability: "Reliability",
    security: "Security",
    performanceEfficiency: "Performance efficiency",
    maintainability: "Maintainability",
};

function characteristicLabel(characteristic: CodeInsightQualityCharacteristic): string {
    return CHARACTERISTIC_LABELS[characteristic] ?? characteristic;
}

onMounted(load);
</script>

<style scoped>
.taxonomy-intro {
    margin-bottom: 1rem;
}

.taxonomy-editor {
    border: 1px solid var(--color-border);
    border-radius: var(--radius-md);
    padding: 1rem;
    margin-bottom: 1rem;
}

.taxonomy-editor h4 {
    margin-top: 0;
}

.taxonomy-group-heading {
    margin-top: 1.5rem;
    margin-bottom: 0.5rem;
}

.taxonomy-table {
    width: 100%;
    border-collapse: collapse;
}

.taxonomy-table th,
.taxonomy-table td {
    text-align: left;
    padding: 0.5rem 0.75rem;
    border-bottom: 1px solid var(--color-border);
    vertical-align: top;
}

.taxonomy-actions-header {
    width: 12rem;
}

.taxonomy-actions {
    display: flex;
    gap: 0.5rem;
}

.taxonomy-slug {
    display: block;
    color: var(--color-text-muted);
    font-size: 0.85em;
}

.taxonomy-retired {
    margin-top: 1rem;
}

.taxonomy-retired-list {
    margin: 0.5rem 0 0;
    padding-left: 1.25rem;
}

.muted-inline {
    color: var(--color-text-muted);
    font-style: italic;
}
</style>
