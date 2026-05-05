<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->

<template>
    <div class="page-with-sidebar">
        <!-- Sidebar Navigation -->
        <aside class="page-sidebar">
            <div id="provider-sidebar-target"></div>

            <div v-show="!isProviderDetailOpen && !isWebhookDetailOpen" class="default-sidebar-content">
                <RouterLink class="back-link" :to="{ name: 'clients' }" style="margin-bottom: 0">
                    <i class="fi fi-rr-arrow-left"></i> Back to clients
                </RouterLink>

                <div v-if="!notFound && !loading && client" class="detail-page-title" style="margin-bottom: 1.5rem">
                    <h2 style="font-size: 1.25rem">{{ client.displayName }}</h2>
                    <p class="detail-page-subtitle">Client Configuration</p>
                </div>

                <div v-if="!notFound && !loading && client" class="sidebar-nav">
                    <div v-if="canManageClient" class="sidebar-nav-group">
                        <h4>Configuration</h4>
                        <button class="sidebar-nav-link" :class="{ active: activeTab === 'config' }"
                            @click="activeTab = 'config'">
                            <i class="fi fi-rr-settings"></i> System
                        </button>
                        <button v-if="isCrawlConfigsAvailable" class="sidebar-nav-link"
                            :class="{ active: activeTab === 'crawl-configs' }" @click="activeTab = 'crawl-configs'">
                            <i class="fi fi-rr-spider"></i> Crawl Configs
                        </button>
                        <button class="sidebar-nav-link" :class="{ active: activeTab === 'webhooks' }"
                            @click="activeTab = 'webhooks'">
                            <i class="fi fi-rr-link-alt"></i> Webhooks
                        </button>
                        <button class="sidebar-nav-link" :class="{ active: activeTab === 'providers' }"
                            @click="activeTab = 'providers'">
                            <i class="fi fi-rr-plug-connection"></i> SCM Providers
                        </button>
                        <button v-if="isProCursorAvailable" class="sidebar-nav-link"
                            :class="{ active: activeTab === 'procursor' }" @click="activeTab = 'procursor'">
                            <i class="fi fi-rr-books"></i> ProCursor
                        </button>
                        <button class="sidebar-nav-link" :class="{ active: activeTab === 'ai' }"
                            @click="activeTab = 'ai'">
                            <i class="fi fi-rr-robot"></i> AI Providers
                        </button>
                    </div>

                    <div class="sidebar-nav-group">
                        <h4>{{ canManageClient ? "Reviews & Overrides" : "Reviews" }}</h4>
                        <button class="sidebar-nav-link" :class="{ active: activeTab === 'history' }"
                            @click="activeTab = 'history'">
                            <i class="fi fi-rr-time-past"></i> Review History
                        </button>
                        <button v-if="canManageClient" class="sidebar-nav-link"
                            :class="{ active: activeTab === 'dismissals' }" @click="
                                activeTab = 'dismissals';
                            loadDismissals();
                            ">
                            <i class="fi fi-rr-ban"></i> Dismissed Findings
                        </button>
                        <button v-if="canManageClient" class="sidebar-nav-link"
                            :class="{ active: activeTab === 'prompt-overrides' }" @click="
                                activeTab = 'prompt-overrides';
                            loadPromptOverrides();
                            ">
                            <i class="fi fi-rr-code-simple"></i> Prompt Overrides
                        </button>
                    </div>

                    <div v-if="isUsageTabAvailable" class="sidebar-nav-group">
                        <h4>Analytics</h4>
                        <button class="sidebar-nav-link" :class="{ active: activeTab === 'usage' }"
                            @click="activeTab = 'usage'">
                            <i class="fi fi-rr-chart-histogram"></i> Tokens & Usage
                        </button>
                    </div>
                </div>
            </div>
        </aside>

        <!-- Main Content Area -->
        <main class="page-main-content">
            <p v-if="notFound" class="error" style="padding-top: 1rem">
                Client not found.
            </p>
            <p v-else-if="loading" class="loading" style="padding-top: 1rem">
                Loading…
            </p>

            <template v-else-if="client">
                <!-- Tab: Configuration -->
                <div v-if="canManageClient" v-show="activeTab === 'config'">
                    <!-- Section 1: Client Identity -->
                    <div class="section-card">
                        <div class="section-card-header">
                            <h3>Client Identity</h3>
                            <div class="section-card-header-actions">
                                <span :class="client.isActive ? 'chip chip-success' : 'chip chip-muted'
                                    ">
                                    <i :class="client.isActive ? 'fi fi-rr-check-circle' : 'fi fi-rr-ban'
                                        "></i>
                                    {{ client.isActive ? "Active" : "Inactive" }}
                                </span>
                                <button :disabled="saving" :class="client.isActive
                                        ? 'btn-danger toggle-status-btn'
                                        : 'btn-primary toggle-status-btn'
                                    " @click="toggleStatus">
                                    {{ client.isActive ? "Disable" : "Enable" }}
                                </button>
                            </div>
                        </div>
                        <div class="section-card-body section-card-body--compact">
                            <div class="inline-field-row">
                                <div class="form-field flex-1">
                                    <label for="displayName">Display Name</label>
                                    <input id="displayName" v-model="editedDisplayName" name="displayName"
                                        type="text" />
                                </div>
                                <button :disabled="saving" class="btn-primary inline-save-btn save-btn"
                                    @click="saveDisplayName">
                                    Save
                                </button>
                            </div>
                            <span v-if="saveError" class="error">{{ saveError }}</span>
                        </div>
                    </div>

                    <div class="section-card">
                        <div class="section-card-header">
                            <h3>Overview</h3>
                        </div>
                        <div class="section-card-body section-card-body--compact">
                            <ClientOverview :client-id="clientId" @navigate="handleOverviewNavigate" />
                        </div>
                    </div>

                    <div class="section-card">
                        <div class="section-card-header">
                            <h3>Advanced Settings</h3>
                        </div>
                        <div class="section-card-body section-card-body--compact">
                            <div class="inline-field-row review-publication-row">
                                <div class="form-field flex-1 review-publication-field">
                                    <label class="checkbox-field" for="scmCommentPostingEnabled">
                                        <input id="scmCommentPostingEnabled" v-model="editedScmCommentPostingEnabled"
                                            name="scmCommentPostingEnabled" type="checkbox" />
                                        <strong>Post review comments to SCM</strong>
                                        <p class="muted review-publication-copy">
                                            Disable this to keep reviews running in the background
                                            without posting new pull request comments.
                                        </p>
                                    </label>
                                </div>
                                <button :disabled="!isAdvancedSettingsButtonEnabled()"
                                    class="btn-primary inline-save-btn scm-advanced-settings-save-btn"
                                    @click="saveAdvancedSettings">
                                    Save
                                </button>
                            </div>
                            <span v-if="saveError" class="error">{{ saveError }}</span>
                        </div>
                    </div>

                    <!-- Danger Zone -->
                    <div class="danger-zone-card">
                        <div class="danger-zone-info">
                            <i class="fi fi-rr-triangle-warning"></i>
                            <div>
                                <h3>Danger Zone</h3>
                                <p>Deleting this client is permanent and cannot be undone.</p>
                            </div>
                        </div>
                        <button class="btn-danger" @click="showDeleteDialog = true">
                            <i class="fi fi-rr-trash"></i> Delete Client
                        </button>
                        <ConfirmDialog :open="showDeleteDialog" message="Delete this client permanently?"
                            @cancel="showDeleteDialog = false" @confirm="handleDelete" />
                    </div>
                </div>

                <div v-if="canManageClient" v-show="activeTab === 'crawl-configs'">
                    <ClientCrawlConfigsTab :clientId="client.id" />
                </div>

                <div v-if="canManageClient" v-show="activeTab === 'webhooks'">
                    <ClientWebhookConfigsTab :clientId="client.id"
                        @update:isDetailOpen="isWebhookDetailOpen = $event" />
                </div>

                <div v-if="canManageClient" v-show="activeTab === 'providers'" class="provider-operations-tab">
                    <div v-if="providerUpgradeMessage" class="section-card provider-upgrade-note">
                        <div class="section-card-body">
                            <p class="muted">{{ providerUpgradeMessage }}</p>
                        </div>
                    </div>
                    <ClientProviderConnectionsTab :clientId="client.id"
                        @update:isDetailOpen="isProviderDetailOpen = $event" />
                </div>

                <div v-if="canManageClient" v-show="activeTab === 'procursor'">
                    <ClientProCursorTab :clientId="client.id" />
                </div>

                <!-- Tab: Usage -->
                <div v-if="isUsageTabAvailable" v-show="activeTab === 'usage'">
                    <UsageDashboard :clientId="client.id" />
                </div>

                <div v-else-if="activeTab === 'usage'" class="section-card premium-unavailable-card">
                    <div class="section-card-header">
                        <h3>Tokens & Usage</h3>
                    </div>
                    <div class="section-card-body">
                        <p class="premium-unavailable-copy">
                            {{ usageUnavailableMessage }}
                        </p>
                    </div>
                </div>

                <!-- Tab: Review History -->
                <div v-show="activeTab === 'history'">
                    <ReviewHistorySection :clientId="client.id" />
                </div>

                <!-- Tab: Dismissed Findings -->
                <div v-if="canManageClient" v-show="activeTab === 'dismissals'">
                    <div class="section-card">
                        <div class="section-card-header">
                            <h3>Dismiss Finding</h3>
                            <button class="btn-primary btn-sm" @click="showDismissalForm = !showDismissalForm">
                                <i class="fi fi-rr-plus"></i> Dismiss Finding
                            </button>
                        </div>

                        <div class="section-card-body">
                            <p class="muted" style="margin-bottom: 1rem">
                                Dismissed findings are stored as admin memory records. The AI
                                memory reconsideration pipeline will suppress similar findings
                                in future reviews. Dismissed patterns can be viewed and managed
                                in the <strong>Memory</strong> tab.
                            </p>

                            <!-- Dismiss form -->
                            <div v-if="showDismissalForm">
                                <div class="form-field">
                                    <label>Finding Message
                                        <span class="field-hint-inline">(exact AI finding text)</span></label>
                                    <textarea v-model="newDismissal.originalMessage" rows="3"
                                        placeholder="Paste the exact finding message to suppress" class="form-input" />
                                </div>
                                <div class="form-field">
                                    <label>Label
                                        <span class="field-hint-inline">(optional — why it's dismissed)</span></label>
                                    <input v-model="newDismissal.label" type="text"
                                        placeholder="e.g. False positive: naming style" class="form-input" />
                                </div>
                                <span v-if="dismissalCreateError" class="error">{{
                                    dismissalCreateError
                                    }}</span>
                                <div class="form-actions">
                                    <button :disabled="dismissalSaving || !newDismissal.originalMessage.trim()
                                        " class="btn-primary" @click="handleCreateDismissal">
                                        {{ dismissalSaving ? "Saving…" : "Dismiss Finding" }}
                                    </button>
                                    <button class="btn-secondary" @click="showDismissalForm = false">
                                        Cancel
                                    </button>
                                </div>
                            </div>

                            <p v-if="dismissalSuccess" class="success-hint">
                                <i class="fi fi-rr-check-circle"></i> Finding dismissed and
                                stored as a memory record.
                            </p>
                        </div>
                    </div>
                </div>

                <!-- Tab: Prompt Overrides -->
                <div v-if="canManageClient" v-show="activeTab === 'prompt-overrides'">
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
                                <label>Prompt Key</label>
                                <select v-model="newOverride.promptKey" class="form-input">
                                    <option value="">— select —</option>
                                    <option value="SystemPrompt">SystemPrompt</option>
                                    <option value="AgenticLoopGuidance">
                                        AgenticLoopGuidance
                                    </option>
                                    <option value="SynthesisSystemPrompt">
                                        SynthesisSystemPrompt
                                    </option>
                                    <option value="QualityFilterSystemPrompt">
                                        QualityFilterSystemPrompt
                                    </option>
                                    <option value="PerFileContextPrompt">
                                        PerFileContextPrompt
                                    </option>
                                </select>
                            </div>
                            <div class="form-field">
                                <label>Override Text
                                    <span class="field-hint-inline">(full replacement for the prompt
                                        segment)</span></label>
                                <textarea v-model="newOverride.overrideText" rows="6"
                                    placeholder="Enter the full replacement prompt text…" class="form-input" />
                            </div>
                            <span v-if="overrideCreateError" class="error">{{
                                overrideCreateError
                                }}</span>
                            <div class="form-actions">
                                <button :disabled="overrideSaving ||
                                    !newOverride.promptKey ||
                                    !newOverride.overrideText.trim()
                                    " class="btn-primary" @click="handleCreateOverride">
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
                                    <tr v-for="o in clientScopedOverrides" :key="o.id">
                                        <td class="font-semibold">{{ o.promptKey }}</td>
                                        <td class="dismissal-pattern-cell">
                                            <div class="pattern-text-wrapper" :title="o.overrideText">
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
                </div>

                <!-- Tab: AI Connections -->
                <div v-if="canManageClient" v-show="activeTab === 'ai'">
                    <ClientAiConnectionsTab :client-id="client.id" />
                </div>
            </template>
        </main>

        <TextViewerModal :isOpen="isTextViewerOpen" @update:isOpen="isTextViewerOpen = $event" :title="textViewerTitle"
            :text="textViewerContent" plain-text />
    </div>
</template>

<script lang="ts" setup>
import { onMounted, ref, reactive, computed, watch } from "vue";
import { RouterLink, useRoute, useRouter } from "vue-router";
import ClientCrawlConfigsTab from "@/components/ClientCrawlConfigsTab.vue";
import ClientWebhookConfigsTab from "@/components/ClientWebhookConfigsTab.vue";
import ClientProviderConnectionsTab from "@/components/ClientProviderConnectionsTab.vue";
import ClientAiConnectionsTab from "@/components/ClientAiConnectionsTab.vue";
import ClientOverview from "@/components/ClientOverview.vue";
import ClientProCursorTab from "@/components/ClientProCursorTab.vue";
import UsageDashboard from "@/components/UsageDashboard.vue";
import ConfirmDialog from "@/components/ConfirmDialog.vue";
import ReviewHistorySection from "@/components/ReviewHistorySection.vue";
import TextViewerModal from "@/components/TextViewerModal.vue";
import { createAdminClient } from "@/services/api";
import { dismissFinding } from "@/services/findingDismissalsService";
import {
    listOverrides,
    createOverride,
    deleteOverride,
} from "@/services/promptOverridesService";
import type { components } from "@/services/generated/openapi";
import { useSession } from "@/composables/useSession";
type PromptOverrideDto = components["schemas"]["PromptOverrideDto"];

const detailTabs = [
    "config",
    "crawl-configs",
    "webhooks",
    "providers",
    "procursor",
    "ai",
    "history",
    "dismissals",
    "prompt-overrides",
    "usage",
] as const;
type DetailTab = (typeof detailTabs)[number];

interface Client {
    id: string;
    displayName: string;
    isActive: boolean;
    createdAt: string;
    scmCommentPostingEnabled: boolean;
}

const router = useRouter();
const route = useRoute();
const { getCapability, hasClientRole } = useSession();
const clientId = route.params.id as string;
const isProviderDetailOpen = ref(false);
const isWebhookDetailOpen = ref(false);

const client = ref<Client | null>(null);
const loading = ref(false);
const notFound = ref(false);
const saving = ref(false);
const saveError = ref("");
const showDeleteDialog = ref(false);
const editedDisplayName = ref("");
const editedScmCommentPostingEnabled = ref(true);
const canManageClient = computed(() => hasClientRole(clientId, 1));
const canViewClient = computed(() => hasClientRole(clientId, 0));
const availableTabs = computed<DetailTab[]>(() => {
    const tabs: DetailTab[] = ["history"];

    if (canManageClient.value) {
        return [...detailTabs];
    }

    if (isUsageTabAvailable.value) {
        tabs.push("usage");
    }

    return tabs;
});
const defaultDetailTab = computed<DetailTab>(() =>
    canManageClient.value ? "config" : "history"
);
const activeTab = ref<DetailTab>(defaultDetailTab.value);
const isProCursorTokenUsageReportingEnabled =
    import.meta.env.VITE_FEATURE_PROCURSOR_TOKEN_USAGE_REPORTING !== "false";
const providerUpgradeMessage = computed(
    () => getCapability("multiple-scm-providers")?.message ?? ""
);
const crawlConfigsCapability = computed(() => getCapability("crawl-configs"));
const proCursorCapability = computed(() => getCapability("procursor"));
const isCrawlConfigsAvailable = computed(
    () => crawlConfigsCapability.value?.isAvailable === true
);
const isProCursorAvailable = computed(
    () => proCursorCapability.value?.isAvailable === true
);
const isUsageTabAvailable = computed(
    () => isProCursorTokenUsageReportingEnabled && isProCursorAvailable.value
);
const usageUnavailableMessage = computed(() => {
    if (!isProCursorTokenUsageReportingEnabled) {
        return "ProCursor usage reporting is disabled in this environment.";
    }

    return (
        proCursorCapability.value?.message ??
        "Commercial edition is required to use ProCursor knowledge sources, indexing, and usage reporting."
    );
});

// Text Viewer Modal
const isTextViewerOpen = ref(false);
const textViewerTitle = ref("");
const textViewerContent = ref("");

// Dismissed Findings tab — dismissals are now stored as memory records
const dismissalsLoading = ref(false);
const dismissalsError = ref("");
const newDismissal = reactive({ originalMessage: "", label: "" });
const dismissalCreateError = ref("");
const dismissalSaving = ref(false);
const showDismissalForm = ref(false);
const dismissalSuccess = ref(false);

// Prompt Overrides tab
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

onMounted(async () => {
    syncActiveTabFromRoute();
    loading.value = true;
    try {
        const { data, response } = await createAdminClient().GET(
            "/clients/{clientId}",
            {
                params: { path: { clientId } },
            }
        );
        if (response && (response as Response).status === 404) {
            notFound.value = true;
            router.push({ name: "clients" });
            return;
        }
        client.value = data as Client;
        editedDisplayName.value = (data as Client).displayName;
        editedScmCommentPostingEnabled.value = Boolean(
            (data as Client).scmCommentPostingEnabled
        );
    } catch {
        notFound.value = true;
        router.push({ name: "clients" });
    } finally {
        loading.value = false;
    }
});

watch(
    () => route.query?.tab,
    () => {
        syncActiveTabFromRoute();
    }
);

watch(availableTabs, () => {
    if (!availableTabs.value.includes(activeTab.value)) {
        activeTab.value = defaultDetailTab.value;
    }
});

watch(activeTab, (tab) => {
    const nextTab = tab === "config" ? undefined : tab;
    const currentTab =
        typeof route.query?.tab === "string" ? route.query.tab : undefined;

    if (currentTab === nextTab) {
        return;
    }

    const nextQuery = { ...(route.query ?? {}) };
    if (nextTab) {
        nextQuery.tab = nextTab;
    } else {
        delete nextQuery.tab;
    }

    if (typeof router.replace === "function") {
        router.replace({ query: nextQuery });
        return;
    }

    router.push({ query: nextQuery });
});

function syncActiveTabFromRoute() {
    const requestedTab =
        typeof route.query?.tab === "string" ? route.query.tab : null;
    if (
        requestedTab &&
        isDetailTab(requestedTab) &&
        availableTabs.value.includes(requestedTab)
    ) {
        activeTab.value = requestedTab;
        return;
    }

    activeTab.value = defaultDetailTab.value;
}

function handleOverviewNavigate(tab: string) {
    if (!isDetailTab(tab)) {
        return;
    }

    if (
        !availableTabs.value.includes(tab) ||
        (tab === "usage" && !isUsageTabAvailable.value)
    ) {
        return;
    }

    activeTab.value = tab;
}

function isDetailTab(value: string): value is DetailTab {
    return (detailTabs as readonly string[]).includes(value);
}

async function saveDisplayName() {
    if (!canManageClient.value) return;
    if (!client.value) return;
    saving.value = true;
    saveError.value = "";
    try {
        const { data } = await createAdminClient().PATCH("/clients/{clientId}", {
            params: { path: { clientId } },
            body: { displayName: editedDisplayName.value },
        });
        client.value = data as Client;
        editedScmCommentPostingEnabled.value =
            client.value.scmCommentPostingEnabled;
    } catch {
        saveError.value = "Failed to save.";
    } finally {
        saving.value = false;
    }
}

async function toggleStatus() {
    if (!canManageClient.value) return;
    if (!client.value) return;
    saving.value = true;
    try {
        const { data } = await createAdminClient().PATCH("/clients/{clientId}", {
            params: { path: { clientId } },
            body: { isActive: !client.value.isActive },
        });
        client.value = data as Client;
        editedScmCommentPostingEnabled.value =
            client.value.scmCommentPostingEnabled;
    } catch {
        saveError.value = "Failed to update status.";
    } finally {
        saving.value = false;
    }
}

async function saveAdvancedSettings() {
    if (!canManageClient.value) return;
    if (!client.value) return;
    saving.value = true;
    saveError.value = "";
    try {
        const { data } = await createAdminClient().PATCH("/clients/{clientId}", {
            params: { path: { clientId } },
            body: { scmCommentPostingEnabled: editedScmCommentPostingEnabled.value },
        });
        client.value = data as Client;
        editedScmCommentPostingEnabled.value =
            client.value.scmCommentPostingEnabled;
    } catch {
        saveError.value = "Failed to save review publication setting.";
    } finally {
        saving.value = false;
    }
}

function isAdvancedSettingsButtonEnabled(): boolean {
    return (
        !saving.value &&
        client.value !== null &&
        editedScmCommentPostingEnabled.value !==
        Boolean(client.value.scmCommentPostingEnabled)
    );
}

async function handleDelete() {
    if (!canManageClient.value) return;
    try {
        await createAdminClient().DELETE("/clients/{clientId}", {
            params: { path: { clientId } },
        });
        router.push({ name: "clients" });
    } catch {
        router.push({ name: "clients" });
    }
}

// ─── Dismissed Findings handlers ─────────────────────────────────────────────

async function loadDismissals() {
    if (!canManageClient.value) return;
    // No-op: dismissals are now stored as memory records and viewed through the Memory tab.
}

async function handleCreateDismissal() {
    if (!canManageClient.value) return;
    dismissalCreateError.value = "";
    dismissalSaving.value = true;
    dismissalSuccess.value = false;
    try {
        await dismissFinding(clientId, {
            findingMessage: newDismissal.originalMessage,
            label: newDismissal.label || null,
        });
        newDismissal.originalMessage = "";
        newDismissal.label = "";
        showDismissalForm.value = false;
        dismissalSuccess.value = true;
        setTimeout(() => {
            dismissalSuccess.value = false;
        }, 3000);
    } catch {
        dismissalCreateError.value = "Failed to dismiss finding.";
    } finally {
        dismissalSaving.value = false;
    }
}

async function loadPromptOverrides() {
    if (!canManageClient.value) return;
    if (overridesLoading.value) return;
    overridesLoading.value = true;
    overridesError.value = "";
    try {
        promptOverrides.value = await listOverrides(clientId);
    } catch {
        overridesError.value = "Failed to load prompt overrides.";
    } finally {
        overridesLoading.value = false;
    }
}

async function handleCreateOverride() {
    if (!canManageClient.value) return;
    overrideCreateError.value = "";
    overrideSaving.value = true;
    try {
        const o = await createOverride(clientId, {
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
    if (!canManageClient.value) return;
    try {
        await deleteOverride(clientId, id);
        promptOverrides.value = promptOverrides.value.filter((o) => o.id !== id);
    } catch {
        overridesError.value = "Failed to delete override.";
    }
}
</script>

<style scoped>
.success {
    color: var(--color-success);
    margin-left: 0.5rem;
    font-weight: 500;
}

.toggle-status-btn {
    font-size: 0.8rem;
    padding: 0.35rem 0.85rem;
}

/* Compact section body variant */
.section-card-body--compact {
    padding: 1rem 1.25rem;
}

/* Inline field row — input + button on same line */
.inline-field-row {
    display: flex;
    align-items: flex-end;
    gap: 0.75rem;
}

.flex-1 {
    flex: 1;
}

.inline-save-btn {
    flex-shrink: 0;
    align-self: flex-end;
    margin-bottom: 0;
}

/* 2-column grid for reviewer fields */
.reviewer-fields-grid {
    display: grid;
    grid-template-columns: 1fr 1fr;
    gap: 0.75rem;
    margin-bottom: 0.75rem;
}

.identity-save-actions {
    margin-top: 1rem;
    border-top: 1px solid var(--color-border);
    padding-top: 1rem;
}

/* Locked section state */
.section-card--locked .section-card-header {
    opacity: 0.65;
}

.section-locked-notice {
    display: flex;
    align-items: center;
    gap: 0.75rem;
    padding: 1rem 1.25rem;
    color: var(--color-text-muted);
    font-size: 0.875rem;
    border-top: 1px solid var(--color-border);
}

.section-locked-notice .fi {
    font-size: 0.9rem;
    flex-shrink: 0;
}

.section-locked-notice p {
    margin: 0;
}

.identity-list {
    list-style: none;
    padding: 0;
    margin: 0.75rem 0 0;
}

.identity-list li {
    padding: 0.6rem 0.875rem;
    border: 1px solid var(--color-border);
    border-radius: 8px;
    margin-bottom: 0.4rem;
    cursor: pointer;
    display: flex;
    flex-direction: column;
    background: var(--color-bg);
    transition: all 0.2s;
}

.identity-list li:hover {
    border-color: var(--color-text-muted);
}

.identity-list li.selected {
    border-color: var(--color-accent);
    background: rgba(34, 211, 238, 0.04);
}

.guid {
    font-size: 0.78rem;
    color: var(--color-text-muted);
    font-family: monospace;
    margin-top: 0.2rem;
}

.btn-sm {
    font-size: 0.8rem;
    padding: 0.3rem 0.7rem;
}

.checkbox-field {
    display: flex;
    align-items: center;
    gap: 0.55rem;
}

.checkbox-field span {
    font-weight: 500;
}

.ai-create-form {
    border-top: 1px solid var(--color-border);
    padding: 1rem 1.25rem;
    display: flex;
    flex-direction: column;
    gap: 1rem;
}

.ai-form-grid {
    display: grid;
    grid-template-columns: 1fr 1fr;
    gap: 0.75rem;
}

.ai-form-grid .full-col {
    grid-column: span 2;
}

.ai-capability-panel {
    grid-column: span 2;
    border: 1px solid var(--color-border);
    border-radius: 10px;
    background: rgba(255, 255, 255, 0.03);
    padding: 1rem;
    display: flex;
    flex-direction: column;
    gap: 0.85rem;
}

.ai-capability-panel-header {
    display: flex;
    align-items: flex-start;
    justify-content: space-between;
    gap: 1rem;
}

.ai-capability-panel-header h4 {
    margin: 0;
    font-size: 0.92rem;
}

.ai-capability-panel-header p {
    margin: 0.25rem 0 0;
    color: var(--color-text-muted);
    font-size: 0.82rem;
}

.ai-capability-empty,
.ai-capability-warning {
    margin: 0;
    padding: 0;
}

.ai-capability-list {
    display: flex;
    flex-direction: column;
    gap: 0.75rem;
}

.ai-capability-row {
    display: grid;
    grid-template-columns: minmax(0, 1.2fr) 1fr 1fr 1fr 1fr 1fr;
    gap: 0.75rem;
    align-items: end;
    padding: 0.9rem;
    border: 1px solid var(--color-border);
    border-radius: 10px;
    background: rgba(255, 255, 255, 0.02);
}

.ai-capability-row .form-field {
    margin-bottom: 0;
}

.ai-capability-name {
    font-family: monospace;
    font-size: 0.84rem;
    word-break: break-word;
    padding-bottom: 0.8rem;
}

.capability-select {
    width: 100%;
    min-height: 42px;
}

.field-hint-inline {
    font-weight: normal;
    font-size: 0.78rem;
    color: var(--color-text-muted);
    text-transform: none;
    letter-spacing: 0;
}

.ai-connections-list {
    list-style: none;
    margin: 0;
    padding: 0;
    border-top: 1px solid var(--color-border);
}

.ai-connection-item {
    display: flex;
    align-items: center;
    justify-content: space-between;
    padding: 0.85rem 1.25rem;
    border-bottom: 1px solid var(--color-border);
    gap: 1rem;
}

.ai-connection-item:last-child {
    border-bottom: none;
}

.ai-conn-main {
    flex: 1;
    display: flex;
    flex-direction: column;
    gap: 0.25rem;
}

.ai-conn-main--clickable {
    cursor: pointer;
    border-radius: 10px;
    padding: 0.25rem 0.35rem;
    margin: -0.25rem -0.35rem;
    transition: background 0.2s ease, border-color 0.2s ease;
}

.ai-conn-main--clickable:hover,
.ai-conn-main--clickable:focus-visible {
    background: rgba(34, 211, 238, 0.06);
    outline: none;
}

.ai-conn-header {
    display: flex;
    align-items: center;
    gap: 0.6rem;
    flex-wrap: wrap;
}

.ai-conn-name {
    font-weight: 600;
    font-size: 0.95rem;
}

.chip-sm {
    font-size: 0.72rem;
    padding: 0.15rem 0.5rem;
}

.active-model-label {
    font-weight: normal;
    opacity: 0.8;
}

.ai-conn-meta {
    display: flex;
    flex-wrap: wrap;
    gap: 0.75rem;
    font-size: 0.78rem;
    color: var(--color-text-muted);
    font-family: monospace;
}

.ai-conn-edit-hint {
    font-family: inherit;
    letter-spacing: 0.02em;
}

.ai-conn-models {
    display: flex;
    flex-wrap: wrap;
    gap: 0.3rem;
    margin-top: 0.25rem;
}

.model-chip {
    font-size: 0.75rem;
    background: rgba(255, 255, 255, 0.07);
    border: 1px solid var(--color-border);
    border-radius: 4px;
    padding: 0.1rem 0.45rem;
    font-family: monospace;
}

.ai-conn-actions {
    display: flex;
    align-items: center;
    gap: 0.5rem;
    flex-shrink: 0;
}

.muted {
    color: var(--color-text-muted);
    font-style: italic;
    padding: 1rem 1.25rem;
}

/* Modal */
.modal-backdrop {
    position: fixed;
    inset: 0;
    background: rgba(0, 0, 0, 0.55);
    display: flex;
    align-items: center;
    justify-content: center;
    z-index: 1000;
}

.modal-card {
    background: var(--color-surface);
    border: 1px solid var(--color-border);
    border-radius: 12px;
    padding: 1.5rem;
    width: 360px;
    max-width: 90vw;
    display: flex;
    flex-direction: column;
    gap: 0.75rem;
    box-shadow: 0 8px 32px rgba(0, 0, 0, 0.4);
}

.modal-card h3 {
    margin: 0;
    font-size: 1.05rem;
}

.modal-select {
    width: 100%;
}

.modal-actions {
    margin-top: 0.25rem;
}

.ai-edit-form-grid {
    margin-bottom: 0.5rem;
}

.ai-edit-form-grid-full {
    grid-column: span 2;
}

.readonly-value {
    min-height: 42px;
    display: flex;
    align-items: center;
    padding: 0.65rem 0.8rem;
    border: 1px solid var(--color-border);
    border-radius: 8px;
    background: var(--color-bg);
    color: var(--color-text-muted);
}

/* Dismissals Table Layout */
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

.muted-hint {
    color: var(--color-text-muted);
    font-size: 0.8rem;
    font-style: italic;
}

.text-right {
    text-align: right;
}

.muted-text {
    color: var(--color-text-muted);
}

.small-text {
    font-size: 0.85rem;
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

.cursor-pointer {
    cursor: pointer;
}

.hover-accent:hover {
    color: var(--color-accent);
}

.provider-operations-tab {
    display: flex;
    flex-direction: column;
    gap: 1rem;
}

.premium-unavailable-card .section-card-body {
    padding-top: 0.5rem;
}

.premium-unavailable-copy {
    margin: 0;
    color: var(--color-text-muted);
}

.provider-operations-stack {
    display: flex;
    flex-direction: column;
    gap: 1rem;
}

@media (max-width: 960px) {
    .provider-operations-stack {
        grid-template-columns: 1fr;
    }

    .ai-capability-row {
        grid-template-columns: 1fr 1fr;
    }

    .ai-capability-name {
        grid-column: 1 / -1;
    }
}

@media (max-width: 720px) {
    .ai-form-grid {
        grid-template-columns: 1fr;
    }

    .ai-form-grid .full-col,
    .ai-edit-form-grid-full,
    .ai-capability-panel {
        grid-column: span 1;
    }

    .ai-connection-item {
        flex-direction: column;
        align-items: stretch;
    }

    .ai-conn-actions {
        justify-content: flex-end;
        flex-wrap: wrap;
    }

    .ai-capability-row {
        grid-template-columns: 1fr;
    }
}
</style>
