<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->
<!-- This file implements commercial-only functionality. A commercial license is required to activate or use that functionality. -->

<template>
    <PageWithSidebar>
        <template #sidebar>
            <AppNavDrawer>
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
                        <button class="sidebar-nav-link"
                            :class="{ active: activeTab === 'procursor' }" @click="activeTab = 'procursor'">
                            <i class="fi fi-rr-books"></i> ProCursor
                        </button>
                        <button class="sidebar-nav-link" :class="{ active: activeTab === 'ai' }"
                            @click="activeTab = 'ai'">
                            <i class="fi fi-rr-robot"></i> AI Providers
                        </button>
                        <button v-if="isBudgetingAvailable" class="sidebar-nav-link"
                            :class="{ active: activeTab === 'budget' }" @click="activeTab = 'budget'">
                            <i class="fi fi-rr-badge-dollar"></i> Budget
                        </button>
                    </div>

                    <div class="sidebar-nav-group">
                        <h4>{{ canManageClient ? "Reviews & Overrides" : "Reviews" }}</h4>
                        <button class="sidebar-nav-link" :class="{ active: activeTab === 'history' }"
                            @click="activeTab = 'history'">
                            <i class="fi fi-rr-time-past"></i> Review History
                        </button>
                        <button v-if="canManageClient" class="sidebar-nav-link"
                            :class="{ active: activeTab === 'dismissals' }"
                            @click="activeTab = 'dismissals'">
                            <i class="fi fi-rr-ban"></i> Dismissed Findings
                        </button>
                        <button v-if="canManageClient" class="sidebar-nav-link"
                            :class="{ active: activeTab === 'prompt-overrides' }"
                            @click="activeTab = 'prompt-overrides'">
                            <i class="fi fi-rr-code-simple"></i> Prompt Overrides
                        </button>
                    </div>

                    <div v-if="isUsageTabAvailable || (isBudgetingAvailable && canManageClient)" class="sidebar-nav-group">
                        <h4>Analytics</h4>
                        <button v-if="isUsageTabAvailable" class="sidebar-nav-link" :class="{ active: activeTab === 'usage' }"
                            @click="activeTab = 'usage'">
                            <i class="fi fi-rr-chart-histogram"></i> Tokens & Usage
                        </button>
                        <button v-if="isBudgetingAvailable && canManageClient" class="sidebar-nav-link"
                            :class="{ active: activeTab === 'spend' }" @click="activeTab = 'spend'">
                            <i class="fi fi-rr-chart-line-up"></i> Spend &amp; Budget
                        </button>
                    </div>
                </div>
            </div>
            </AppNavDrawer>
        </template>

            <p v-if="notFound" class="error" style="padding-top: 1rem">
                Client not found.
            </p>
            <p v-else-if="loadError" class="error" style="padding-top: 1rem">
                Failed to load client. Please try again.
            </p>
            <p v-else-if="loading" class="loading" style="padding-top: 1rem">
                Loading…
            </p>

            <template v-else-if="client">
                <!-- Tab: Configuration -->
                <div v-if="canManageClient" v-show="activeTab === 'config'">
                    <ClientSystemTab />
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

                <!-- Tab: Spend & Budget (FinOps). Content host mirrors the Budget tab: rendered for managers so a
                     deep link on an unlicensed install shows the upgrade note rather than a blank panel; the nav
                     entry stays hidden until Budgeting is licensed. -->
                <div v-if="canManageClient" v-show="activeTab === 'spend'">
                    <ClientSpendTab />
                </div>

                <!-- Tab: Review History -->
                <div v-show="activeTab === 'history'">
                    <ReviewHistorySection :clientId="client.id" />
                </div>

                <!-- Tab: Dismissed Findings -->
                <div v-if="canManageClient" v-show="activeTab === 'dismissals'">
                    <ClientDismissalsTab :client-id="client.id" />
                </div>

                <!-- Tab: Prompt Overrides -->
                <div v-if="canManageClient" v-show="activeTab === 'prompt-overrides'">
                    <ClientPromptOverridesTab :client-id="client.id" :active="activeTab === 'prompt-overrides'" />
                </div>

                <!-- Tab: AI Connections -->
                <div v-if="canManageClient" v-show="activeTab === 'ai'">
                    <ClientAiConnectionsTab :client-id="client.id" />
                </div>

                <!-- Tab: Budget -->
                <div v-if="canManageClient" v-show="activeTab === 'budget'">
                    <ClientBudgetTab />
                </div>
            </template>

        <TextViewerModal :isOpen="isTextViewerOpen" @update:isOpen="isTextViewerOpen = $event" :title="textViewerTitle"
            :text="textViewerContent" plain-text />
    </PageWithSidebar>
</template>

<script lang="ts" setup>
import { provide, ref } from "vue";
import { RouterLink } from "vue-router";
import { AppNavDrawer, PageWithSidebar } from "@/components";
import ClientSystemTab from "@/features/clients/components/ClientSystemTab.vue";
import ClientBudgetTab from "@/features/clients/components/ClientBudgetTab.vue";
import ClientSpendTab from "@/features/clients/components/ClientSpendTab.vue";
import ClientCrawlConfigsTab from "@/features/clients/components/ClientCrawlConfigsTab.vue";
import ClientWebhookConfigsTab from "@/features/clients/components/ClientWebhookConfigsTab.vue";
import ClientProviderConnectionsTab from "@/features/clients/components/ClientProviderConnectionsTab.vue";
import ClientAiConnectionsTab from "@/features/clients/components/ClientAiConnectionsTab.vue";
import ClientProCursorTab from "@/features/clients/components/ClientProCursorTab.vue";
import ClientDismissalsTab from "@/features/clients/components/ClientDismissalsTab.vue";
import ClientPromptOverridesTab from "@/features/clients/components/ClientPromptOverridesTab.vue";
import UsageDashboard from "@/components/UsageDashboard.vue";
import ReviewHistorySection from "@/features/reviews/components/ReviewHistorySection.vue";
import TextViewerModal from "@/components/text/TextViewerModal.vue";
import { ClientDetailVmKey, useClientDetailViewModel } from "@/features/clients/view-models/useClientDetailViewModel";

const vm = useClientDetailViewModel();
// Shared with sub-tab components (e.g. ClientSystemTab) via inject.
provide(ClientDetailVmKey, vm);
const {
    isProviderDetailOpen,
    isWebhookDetailOpen,
    client,
    loading,
    notFound,
    loadError,
    canManageClient,
    activeTab,
    providerUpgradeMessage,
    isCrawlConfigsAvailable,
    isBudgetingAvailable,
    isUsageTabAvailable,
} = vm;

// Text Viewer Modal
const isTextViewerOpen = ref(false);
const textViewerTitle = ref("");
const textViewerContent = ref("");
</script>

<style scoped>
.provider-operations-tab {
    display: flex;
    flex-direction: column;
    gap: 1rem;
}

.muted {
    color: var(--color-text-muted);
    font-style: italic;
    padding: 1rem 1.25rem;
}
</style>
