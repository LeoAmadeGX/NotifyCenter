<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, reactive, ref, watch } from "vue";
import {
  adminEventsUrl,
  cancelNotification,
  changePassword,
  createNotification,
  createNotificationsBulk,
  createRoutingTarget,
  deleteRoutingTarget,
  getAttempts,
  getFilterOptions,
  getLineSources,
  getNotification,
  getNotifications,
  getRoutingTargets,
  getSession,
  getStats,
  login,
  logout,
  retryNotification,
  updateRoutingTarget
} from "./api";
import type {
  AdminSession,
  BulkResult,
  NotificationAttempt,
  NotificationCreateInput,
  NotificationFilterOptions,
  NotificationFilters,
  NotificationItem,
  NotificationStats,
  LineSourceItem,
  RoutingTargetInput,
  RoutingTargetItem,
  UpsertResult
} from "./types";

type WorkspacePage = "notifications" | "targets";
type SidePanel = "preview" | "create" | "batch" | "password";
type SelectOption = { label: string; value: string };
type DashboardEvent = {
  kind: string;
  deliveryId?: string | null;
  channel?: string | null;
  occurredAt: string;
};

const loading = ref(true);
const saving = ref(false);
const session = ref<AdminSession | null>(null);
const errorMessage = ref("");
const successMessage = ref("");
const activePage = ref<WorkspacePage>("notifications");
const sidePanel = ref<SidePanel>("preview");
const notifications = ref<NotificationItem[]>([]);
const attempts = ref<NotificationAttempt[]>([]);
const selectedNotification = ref<NotificationItem | null>(null);
const stats = ref<NotificationStats | null>(null);
const bulkResult = ref<BulkResult | null>(null);
const selectedIds = ref<string[]>([]);
const filterOptions = ref<NotificationFilterOptions>(getFallbackFilterOptions());
const routingTargets = ref<RoutingTargetItem[]>([]);
const lineSources = ref<LineSourceItem[]>([]);
const advancedFiltersOpen = ref(false);
const targetInputQuery = ref("");
const targetDropdownOpen = ref(false);
const targetPickedItem = ref<RoutingTargetItem | null>(null);

const loginForm = reactive({
  username: "amadegx",
  password: ""
});

const filters = reactive<NotificationFilters>({
  status: "",
  channel: "",
  sourceSystem: "",
  eventType: "",
  messageQuery: "",
  scheduledFrom: "",
  scheduledTo: "",
  limit: 100
});

const createForm = reactive({
  title: "",
  body: "",
  channel: "telegram",
  target: "",
  dedupeKey: "",
  sourceSystem: "admin-ui",
  eventType: "manual.notification",
  scheduledAtLocal: createDefaultSchedule(),
  metadataJson: ""
});

const batchForm = reactive({
  payload: ""
});

const passwordForm = reactive({
  currentPassword: "",
  newPassword: "",
  confirmPassword: ""
});

const routingTargetForm = reactive({
  id: "",
  channel: "telegram",
  name: "",
  destination: "",
  isEnabled: true,
  sortOrder: 0,
  metadataJson: ""
});

let refreshIntervalHandle: number | null = null;
let passiveRefreshHandle: number | null = null;
let passiveRefreshWantsPreview = false;
let eventSource: EventSource | null = null;

const isLoggedIn = computed(() => session.value !== null);
const canRetrySelected = computed(() => selectedNotification.value?.status === "failed");
const canCancelSelected = computed(() => canCancelStatus(selectedNotification.value?.status));
const selectedMetadata = computed(() => {
  if (!selectedNotification.value) {
    return "";
  }

  return formatJson(selectedNotification.value.metadataJson);
});
const selectedHasMetadata = computed(() =>
  hasMeaningfulMetadata(selectedNotification.value?.metadataJson ?? "")
);
const selectedCount = computed(() => selectedIds.value.length);
const allVisibleSelected = computed(
  () => notifications.value.length > 0 && notifications.value.every((item) => selectedIds.value.includes(item.id))
);
const cancelableSelectedIds = computed(() =>
  selectedIds.value.filter((id) => {
    const item = notifications.value.find((candidate) => candidate.id === id);
    return canCancelStatus(item?.status);
  })
);
const nonCancelableSelectedCount = computed(
  () => selectedCount.value - cancelableSelectedIds.value.length
);
const hasAdvancedFilters = computed(
  () =>
    Boolean(filters.channel) ||
    Boolean(filters.scheduledFrom) ||
    Boolean(filters.scheduledTo) ||
    Boolean(filters.messageQuery)
);
const statusOptions = computed(() => [
  { label: "全部", value: "", count: stats.value?.total ?? notifications.value.length },
  { label: "待派送", value: "pending", count: stats.value?.pending ?? 0 },
  { label: "待配置", value: "pending_no_target", count: stats.value?.pendingNoTarget ?? 0 },
  { label: "失敗", value: "failed", count: stats.value?.failed ?? 0 },
  { label: "已送達", value: "sent", count: stats.value?.sent ?? 0 },
  { label: "已取消", value: "canceled", count: stats.value?.canceled ?? 0 },
  { label: "已略過", value: "skipped_no_target", count: stats.value?.skipped ?? 0 }
]);
const channelOptions = computed(() =>
  buildSelectOptions(filterOptions.value.channels, ["telegram", "line"], filters.channel)
);
const notificationChannelOptions = computed(() =>
  uniqueSorted([...filterOptions.value.channels, "telegram", "line"])
    .filter((channel) => channel === "telegram" || channel === "line")
    .map((channel) => ({ label: channel, value: channel }))
);
const sourceSystemOptions = computed(() =>
  buildSelectOptions(filterOptions.value.sourceSystems, ["admin-ui", "manual"], filters.sourceSystem)
);
const eventTypeOptions = computed(() =>
  buildSelectOptions(
    filterOptions.value.eventTypes,
    ["manual.notification", "manual.batch"],
    filters.eventType
  )
);
const routingChannelOptions = [
  { label: "telegram", value: "telegram" },
  { label: "line", value: "line" },
  { label: "teams", value: "teams" }
];
const routingDestinationLabel = computed(() => {
  switch (routingTargetForm.channel) {
    case "telegram":
      return "Telegram Chat ID";
    case "teams":
      return "Webhook URL";
    case "line":
      return "Line 對象 ID";
    default:
      return "Destination";
  }
});
const routingDestinationPlaceholder = computed(() => {
  switch (routingTargetForm.channel) {
    case "telegram":
      return "例如：123456789 或 -1001234567890";
    case "teams":
      return "例如：https://outlook.office.com/webhook/...";
    case "line":
      return "例如：groupId、userId 或未來的 recipient key";
    default:
      return "";
  }
});
const routingTargetHint = computed(() => {
  switch (routingTargetForm.channel) {
    case "telegram":
      return "Telegram 的 Bot Token 仍然讀取 .env 的 TELEGRAM_BOT_TOKEN；這裡只填 chat id。預設 parse mode 現在是 HTML。";
    case "teams":
      return "Teams 目前先管理對象設定，這一輪還沒有實際 sender。";
    case "line":
      return "LINE_CHANNEL_ACCESS_TOKEN 由 .env 提供；下方 webhook 收集到的 userId、groupId 或 roomId 可以直接帶入這裡。";
    default:
      return "";
  }
});
const lineWebhookUrl = computed(() => {
  const basePath = import.meta.env.BASE_URL.replace(/\/$/, "");
  return `${window.location.origin}${basePath}/api/line/webhook`;
});
const lineSourceCounts = computed(() => ({
  total: lineSources.value.length,
  groups: lineSources.value.filter((source) => source.sourceType === "group").length,
  rooms: lineSources.value.filter((source) => source.sourceType === "room").length,
  users: lineSources.value.filter((source) => source.sourceType === "user").length
}));

const targetSuggestions = computed(() => {
  const channel = createForm.channel;
  const q = targetInputQuery.value.toLowerCase().trim();
  return routingTargets.value.filter(
    (t) =>
      t.channel === channel &&
      t.isEnabled &&
      (q === "" || t.name.toLowerCase().includes(q) || t.destination.toLowerCase().includes(q))
  );
});

watch(() => createForm.channel, () => {
  if (targetPickedItem.value && targetPickedItem.value.channel !== createForm.channel) {
    targetPickedItem.value = null;
  }
});

onMounted(async () => {
  document.addEventListener("visibilitychange", handleVisibilityChange);
  await restoreSession();
});

onBeforeUnmount(() => {
  document.removeEventListener("visibilitychange", handleVisibilityChange);
  stopAutoRefresh();
  closeEventStream();

  if (passiveRefreshHandle !== null) {
    window.clearTimeout(passiveRefreshHandle);
    passiveRefreshHandle = null;
  }
});

async function restoreSession() {
  loading.value = true;
  errorMessage.value = "";

  try {
    session.value = await getSession();
    applySessionDefaults(session.value, true);
    await loadDashboard({
      refreshFilters: true,
      refreshTargets: true,
      refreshLineSources: true,
      syncPreview: true
    });
    startAutoRefresh();
    openEventStream();
  } catch {
    session.value = null;
    resetBatchPayload(true);
    stopAutoRefresh();
    closeEventStream();
  } finally {
    loading.value = false;
  }
}

async function handleLogin() {
  loading.value = true;
  errorMessage.value = "";
  successMessage.value = "";

  try {
    session.value = await login(loginForm.username, loginForm.password);
    applySessionDefaults(session.value, true);
    loginForm.password = "";
    await loadDashboard({
      refreshFilters: true,
      refreshTargets: true,
      refreshLineSources: true,
      syncPreview: true
    });
    startAutoRefresh();
    openEventStream();
  } catch (error) {
    errorMessage.value = toMessage(error);
  } finally {
    loading.value = false;
  }
}

async function handleLogout() {
  loading.value = true;
  errorMessage.value = "";
  successMessage.value = "";

  try {
    await logout();
    stopAutoRefresh();
    closeEventStream();
    session.value = null;
    notifications.value = [];
    attempts.value = [];
    selectedNotification.value = null;
    stats.value = null;
    selectedIds.value = [];
    bulkResult.value = null;
    routingTargets.value = [];
    lineSources.value = [];
    activePage.value = "notifications";
    sidePanel.value = "preview";
    filterOptions.value = getFallbackFilterOptions();
    resetCreateForm();
    resetBatchPayload(true);
    resetRoutingTargetForm();
  } catch (error) {
    errorMessage.value = toMessage(error);
  } finally {
    loading.value = false;
  }
}

async function runManualRefresh() {
  loading.value = true;
  errorMessage.value = "";

  try {
    await loadDashboard({
      refreshFilters: true,
      refreshTargets: activePage.value === "targets",
      refreshLineSources: activePage.value === "targets",
      syncPreview: activePage.value === "notifications" && sidePanel.value === "preview"
    });
  } catch (error) {
    errorMessage.value = toMessage(error);
  } finally {
    loading.value = false;
  }
}

async function loadDashboard(options?: {
  refreshFilters?: boolean;
  refreshTargets?: boolean;
  refreshLineSources?: boolean;
  syncPreview?: boolean;
}) {
  const tasks: Promise<unknown>[] = [loadStats(), loadNotifications()];
  if (options?.refreshFilters) {
    tasks.push(loadFilterOptions());
  }
  if (options?.refreshTargets) {
    tasks.push(loadRoutingTargets());
  }
  if (options?.refreshLineSources) {
    tasks.push(loadLineSources());
  }

  await Promise.all(tasks);

  if (options?.syncPreview && activePage.value === "notifications" && sidePanel.value === "preview") {
    await refreshSelectedPreview();
  }
}

async function loadStats() {
  stats.value = await getStats();
}

async function loadFilterOptions() {
  try {
    const options = await getFilterOptions();
    filterOptions.value = normalizeFilterOptions(options);
  } catch {
    filterOptions.value = getFallbackFilterOptions();
  }
}

async function loadRoutingTargets() {
  const response = await getRoutingTargets();
  routingTargets.value = response.items;
}

async function loadLineSources() {
  const response = await getLineSources();
  lineSources.value = response.items;
}

async function loadTargetManagement() {
  await Promise.all([loadRoutingTargets(), loadLineSources()]);
}

async function loadNotifications() {
  const response = await getNotifications(filters);
  notifications.value = sortNotifications(response.items);
  selectedIds.value = selectedIds.value.filter((id) =>
    notifications.value.some((item) => item.id === id)
  );

  if (notifications.value.length === 0) {
    selectedNotification.value = null;
    attempts.value = [];
    return;
  }

  const nextSelected =
    notifications.value.find((item) => item.id === selectedNotification.value?.id) ??
    notifications.value[0];

  if (selectedNotification.value?.id !== nextSelected.id) {
    attempts.value = [];
  }

  selectedNotification.value = nextSelected;
}

async function refreshSummaryAndList(options?: { syncPreview?: boolean }) {
  try {
    await Promise.all([loadStats(), loadNotifications()]);

    if (options?.syncPreview && activePage.value === "notifications" && sidePanel.value === "preview") {
      await refreshSelectedPreview();
    }
  } catch (error) {
    errorMessage.value = toMessage(error);
  }
}

async function refreshSelectedPreview() {
  if (sidePanel.value !== "preview" || !selectedNotification.value) {
    return;
  }

  errorMessage.value = "";

  try {
    const [item, attemptResponse] = await Promise.all([
      getNotification(selectedNotification.value.id),
      getAttempts(selectedNotification.value.id)
    ]);
    selectedNotification.value = item;
    attempts.value = attemptResponse.items;
  } catch (error) {
    errorMessage.value = toMessage(error);
    attempts.value = [];
  }
}

function startAutoRefresh() {
  stopAutoRefresh();

  refreshIntervalHandle = window.setInterval(() => {
    if (!document.hidden) {
      void refreshSummaryAndList({ syncPreview: sidePanel.value === "preview" });
    }
  }, 60_000);
}

function stopAutoRefresh() {
  if (refreshIntervalHandle !== null) {
    window.clearInterval(refreshIntervalHandle);
    refreshIntervalHandle = null;
  }
}

function openEventStream() {
  closeEventStream();
  if (!session.value) {
    return;
  }

  eventSource = new EventSource(adminEventsUrl);
  eventSource.addEventListener("dashboard", (event) => {
    try {
      const payload = JSON.parse((event as MessageEvent<string>).data) as DashboardEvent;
      if (payload.kind === "line_sources_changed") {
        if (activePage.value === "targets") {
          void loadTargetManagement().catch((error) => {
            errorMessage.value = toMessage(error);
          });
        }
        return;
      }

      const shouldSyncPreview =
        activePage.value === "notifications" &&
        sidePanel.value === "preview" &&
        (!payload.deliveryId || payload.deliveryId === selectedNotification.value?.id);
      queuePassiveRefresh(shouldSyncPreview);
    } catch {
      queuePassiveRefresh(activePage.value === "notifications" && sidePanel.value === "preview");
    }
  });
  eventSource.onerror = () => {
    // Native EventSource will retry automatically; no UI action needed here.
  };
}

function closeEventStream() {
  if (eventSource) {
    eventSource.close();
    eventSource = null;
  }
}

function queuePassiveRefresh(syncPreview: boolean) {
  passiveRefreshWantsPreview = passiveRefreshWantsPreview || syncPreview;

  if (passiveRefreshHandle !== null) {
    return;
  }

  passiveRefreshHandle = window.setTimeout(() => {
    const shouldSyncPreview = passiveRefreshWantsPreview;
    passiveRefreshWantsPreview = false;
    passiveRefreshHandle = null;
    void refreshSummaryAndList({ syncPreview: shouldSyncPreview });
  }, 500);
}

function handleVisibilityChange() {
  if (!document.hidden && session.value) {
    if (activePage.value === "targets") {
      void loadTargetManagement().catch((error) => {
        errorMessage.value = toMessage(error);
      });
      return;
    }

    queuePassiveRefresh(sidePanel.value === "preview");
  }
}

async function applyFilters() {
  if (
    filters.scheduledFrom &&
    filters.scheduledTo &&
    new Date(filters.scheduledFrom).getTime() > new Date(filters.scheduledTo).getTime()
  ) {
    errorMessage.value = "起始時間不能晚於結束時間。";
    return;
  }

  loading.value = true;
  errorMessage.value = "";

  try {
    await refreshSummaryAndList({ syncPreview: sidePanel.value === "preview" });
  } finally {
    loading.value = false;
  }
}

async function setStatusFilter(value: string) {
  filters.status = value;
  await applyFilters();
}

function toggleAdvancedFilters() {
  advancedFiltersOpen.value = !advancedFiltersOpen.value;
}

function openCreatePanel() {
  activePage.value = "notifications";
  createForm.scheduledAtLocal = createDefaultSchedule();
  sidePanel.value = "create";
}

function openBatchPanel() {
  activePage.value = "notifications";
  sidePanel.value = "batch";
}

function openPasswordPanel() {
  activePage.value = "notifications";
  sidePanel.value = "password";
}

function openNotificationsPage() {
  activePage.value = "notifications";
}

async function openTargetsPage() {
  activePage.value = "targets";
  await loadTargetManagement();
}

function clearSelection() {
  selectedIds.value = [];
}

function clearDateRange() {
  filters.scheduledFrom = "";
  filters.scheduledTo = "";
}

function resetFilters() {
  filters.status = "";
  filters.channel = "";
  filters.sourceSystem = "";
  filters.eventType = "";
  filters.messageQuery = "";
  filters.scheduledFrom = "";
  filters.scheduledTo = "";
  filters.limit = 100;
  advancedFiltersOpen.value = false;
}

function changeLimit(delta: number) {
  filters.limit = Math.min(500, Math.max(1, filters.limit + delta));
}

function toggleSelection(id: string) {
  const selection = new Set(selectedIds.value);
  if (selection.has(id)) {
    selection.delete(id);
  } else {
    selection.add(id);
  }

  selectedIds.value = notifications.value
    .filter((item) => selection.has(item.id))
    .map((item) => item.id);
}

function toggleSelectAllVisible() {
  if (allVisibleSelected.value) {
    selectedIds.value = [];
    return;
  }

  selectedIds.value = notifications.value.map((item) => item.id);
}

async function selectNotification(id: string, focusPreview = true) {
  activePage.value = "notifications";
  const item = notifications.value.find((candidate) => candidate.id === id);
  if (item) {
    selectedNotification.value = item;
  }

  if (focusPreview) {
    sidePanel.value = "preview";
  }

  await refreshSelectedPreview();
}

async function performBulkCancel() {
  if (cancelableSelectedIds.value.length === 0) {
    errorMessage.value = "目前沒有可批次取消的派送單。";
    return;
  }

  saving.value = true;
  errorMessage.value = "";
  successMessage.value = "";

  let canceled = 0;
  const failures: string[] = [];

  try {
    for (const id of cancelableSelectedIds.value) {
      try {
        await cancelNotification(id);
        canceled += 1;
      } catch (error) {
        failures.push(toMessage(error));
      }
    }

    if (canceled > 0) {
      successMessage.value = `已取消 ${canceled} 筆派送單。`;
    }

    if (failures.length > 0) {
      errorMessage.value =
        failures.length === 1
          ? failures[0]
          : `有 ${failures.length} 筆取消失敗，第一個錯誤：${failures[0]}`;
    }

    await refreshSummaryAndList({ syncPreview: sidePanel.value === "preview" });
  } finally {
    saving.value = false;
  }
}

function useSelectedAsDraft() {
  if (!selectedNotification.value) {
    return;
  }

  createForm.title = selectedNotification.value.title;
  createForm.body = selectedNotification.value.body;
  createForm.target = selectedNotification.value.isTargetOverride
    ? selectedNotification.value.target ?? ""
    : "";
  const destValue = createForm.target;
  const matchedTarget = routingTargets.value.find((t) => t.destination === destValue && t.channel === selectedNotification.value!.channel);
  if (matchedTarget) {
    targetPickedItem.value = matchedTarget;
    targetInputQuery.value = matchedTarget.name;
  } else {
    targetPickedItem.value = null;
    targetInputQuery.value = destValue;
  }
  createForm.sourceSystem = selectedNotification.value.sourceSystem || "admin-ui";
  createForm.eventType = selectedNotification.value.eventType || "manual.notification";
  createForm.dedupeKey = "";
  createForm.scheduledAtLocal = createDefaultSchedule();
  createForm.metadataJson = selectedHasMetadata.value ? selectedMetadata.value : "";
  sidePanel.value = "create";
  bulkResult.value = null;
}

async function submitCreate() {
  saving.value = true;
  errorMessage.value = "";
  successMessage.value = "";

  try {
    const result = (await createNotification(buildCreatePayload())) as UpsertResult;
    successMessage.value = "通知需求已建立，派送單已重新整理。";
    resetCreateForm();
    await refreshSummaryAndList({ syncPreview: false });

    if (result.deliveryId) {
      await selectNotification(result.deliveryId, true);
    }

    sidePanel.value = "preview";
  } catch (error) {
    errorMessage.value = toMessage(error);
  } finally {
    saving.value = false;
  }
}

async function submitBatch() {
  saving.value = true;
  errorMessage.value = "";
  successMessage.value = "";

  try {
    const parsed = JSON.parse(batchForm.payload) as
      | NotificationCreateInput[]
      | { notifications: NotificationCreateInput[] };
    const notificationsInput = Array.isArray(parsed) ? parsed : parsed.notifications;
    if (!Array.isArray(notificationsInput) || notificationsInput.length === 0) {
      throw new Error("批次 JSON 內至少要有一筆通知。");
    }

    bulkResult.value = await createNotificationsBulk(notificationsInput);
    successMessage.value = "批次通知需求已送交處理。";
    await refreshSummaryAndList({ syncPreview: sidePanel.value === "preview" });
    sidePanel.value = "batch";
  } catch (error) {
    errorMessage.value = toMessage(error);
  } finally {
    saving.value = false;
  }
}

async function submitPasswordChange() {
  saving.value = true;
  errorMessage.value = "";
  successMessage.value = "";

  try {
    session.value = await changePassword(
      passwordForm.currentPassword,
      passwordForm.newPassword,
      passwordForm.confirmPassword
    );
    applySessionDefaults(session.value, false);
    passwordForm.currentPassword = "";
    passwordForm.newPassword = "";
    passwordForm.confirmPassword = "";
    successMessage.value = "密碼已更新，之後請使用新密碼登入。";
    sidePanel.value = "preview";
  } catch (error) {
    errorMessage.value = toMessage(error);
  } finally {
    saving.value = false;
  }
}

async function performCancel(id: string) {
  saving.value = true;
  errorMessage.value = "";
  successMessage.value = "";

  try {
    await cancelNotification(id);
    successMessage.value = "派送單已取消。";
    await refreshSummaryAndList({ syncPreview: sidePanel.value === "preview" });
  } catch (error) {
    errorMessage.value = toMessage(error);
  } finally {
    saving.value = false;
  }
}

async function performRetry(id: string) {
  saving.value = true;
  errorMessage.value = "";
  successMessage.value = "";

  try {
    const result = await retryNotification(id);
    successMessage.value = result.skipped
      ? "這筆失敗的派送單對應對象已不存在或停用，已直接略過。"
      : "失敗的派送單已重新排入派送。";
    await refreshSummaryAndList({ syncPreview: sidePanel.value === "preview" });
  } catch (error) {
    errorMessage.value = toMessage(error);
  } finally {
    saving.value = false;
  }
}

function startEditingRoutingTarget(item: RoutingTargetItem) {
  activePage.value = "targets";
  routingTargetForm.id = item.id;
  routingTargetForm.channel = item.channel;
  routingTargetForm.name = item.name;
  routingTargetForm.destination = item.destination;
  routingTargetForm.isEnabled = item.isEnabled;
  routingTargetForm.sortOrder = item.sortOrder;
  routingTargetForm.metadataJson = formatJson(item.metadataJson);
}

function resetRoutingTargetForm() {
  routingTargetForm.id = "";
  routingTargetForm.channel = "telegram";
  routingTargetForm.name = "";
  routingTargetForm.destination = "";
  routingTargetForm.isEnabled = true;
  routingTargetForm.sortOrder = 0;
  routingTargetForm.metadataJson = "";
}

async function submitRoutingTarget() {
  saving.value = true;
  errorMessage.value = "";
  successMessage.value = "";

  try {
    const input = buildRoutingTargetPayload();
    if (routingTargetForm.id) {
      await updateRoutingTarget(routingTargetForm.id, input);
      successMessage.value = "對象設定已更新。";
    } else {
      await createRoutingTarget(input);
      successMessage.value = "對象設定已建立。";
    }

    resetRoutingTargetForm();
    await Promise.all([
      loadRoutingTargets(),
      loadLineSources(),
      refreshSummaryAndList({ syncPreview: activePage.value === "notifications" && sidePanel.value === "preview" })
    ]);
  } catch (error) {
    errorMessage.value = toMessage(error);
  } finally {
    saving.value = false;
  }
}

async function removeRoutingTarget(id: string) {
  saving.value = true;
  errorMessage.value = "";
  successMessage.value = "";

  try {
    await deleteRoutingTarget(id);
    if (routingTargetForm.id === id) {
      resetRoutingTargetForm();
    }
    successMessage.value = "對象設定已刪除。";
    await Promise.all([
      loadRoutingTargets(),
      loadLineSources(),
      refreshSummaryAndList({ syncPreview: activePage.value === "notifications" && sidePanel.value === "preview" })
    ]);
  } catch (error) {
    errorMessage.value = toMessage(error);
  } finally {
    saving.value = false;
  }
}

function buildCreatePayload(): NotificationCreateInput {
  return {
    dedupeKey: createForm.dedupeKey || null,
    sourceSystem: createForm.sourceSystem || null,
    eventType: createForm.eventType || null,
    channel: createForm.channel,
    target: createForm.target.trim() || null,
    title: createForm.title,
    body: createForm.body,
    scheduledAtUtc: new Date(createForm.scheduledAtLocal).toISOString(),
    metadata: parseMetadata(createForm.metadataJson)
  };
}

function buildRoutingTargetPayload(): RoutingTargetInput {
  return {
    channel: routingTargetForm.channel,
    name: routingTargetForm.name,
    destination: routingTargetForm.destination,
    isEnabled: routingTargetForm.isEnabled,
    sortOrder: routingTargetForm.sortOrder,
    metadata: parseMetadata(routingTargetForm.metadataJson)
  };
}

function applySessionDefaults(currentSession: AdminSession | null, force: boolean) {
  if (!currentSession) {
    return;
  }

  if (force || !batchForm.payload.trim()) {
    resetBatchPayload(true);
  }
}

function resetCreateForm() {
  createForm.title = "";
  createForm.body = "";
  createForm.channel = "telegram";
  createForm.target = "";
  createForm.dedupeKey = "";
  createForm.sourceSystem = "admin-ui";
  createForm.eventType = "manual.notification";
  createForm.scheduledAtLocal = createDefaultSchedule();
  createForm.metadataJson = "";
  targetInputQuery.value = "";
  targetPickedItem.value = null;
  targetDropdownOpen.value = false;
}

function onTargetInput(val: string) {
  targetInputQuery.value = val;
  targetPickedItem.value = null;
  createForm.target = val;
  targetDropdownOpen.value = true;
}

function pickTarget(item: RoutingTargetItem) {
  targetPickedItem.value = item;
  targetInputQuery.value = item.name;
  createForm.target = item.destination;
  targetDropdownOpen.value = false;
}

function resetBatchPayload(force: boolean) {
  if (!force && batchForm.payload.trim()) {
    return;
  }

  batchForm.payload = createBatchExample();
}

function createBatchExample() {
  return JSON.stringify(
    [
      {
        title: "Daily report",
        body: "NotifyCenter has completed the 09:00 summary.",
        channel: "telegram",
        sourceSystem: "admin-ui",
        eventType: "manual.batch",
        scheduledAtUtc: new Date(Date.now() + 10 * 60 * 1000).toISOString()
      }
    ],
    null,
    2
  );
}

function parseMetadata(value: string) {
  if (!value.trim()) {
    return null;
  }

  return JSON.parse(value) as Record<string, unknown>;
}

function createDefaultSchedule() {
  const future = new Date(Date.now() + 5 * 60 * 1000);
  return toDateTimeLocalValue(future);
}

function toDateTimeLocalValue(value: Date) {
  const year = value.getFullYear();
  const month = String(value.getMonth() + 1).padStart(2, "0");
  const day = String(value.getDate()).padStart(2, "0");
  const hours = String(value.getHours()).padStart(2, "0");
  const minutes = String(value.getMinutes()).padStart(2, "0");
  return `${year}-${month}-${day}T${hours}:${minutes}`;
}

function formatDate(value: string | null) {
  if (!value) {
    return "—";
  }

  return new Date(value).toLocaleString("zh-TW", {
    year: "numeric",
    month: "2-digit",
    day: "2-digit",
    hour: "2-digit",
    minute: "2-digit",
    second: "2-digit"
  });
}

function formatDateCompact(value: string | null) {
  if (!value) {
    return "—";
  }

  return new Date(value).toLocaleString("zh-TW", {
    month: "2-digit",
    day: "2-digit",
    hour: "2-digit",
    minute: "2-digit"
  });
}

function formatJson(value: string) {
  try {
    return JSON.stringify(JSON.parse(value), null, 2);
  } catch {
    return value;
  }
}

function hasMeaningfulMetadata(value: string) {
  const trimmed = value.trim();
  if (!trimmed) {
    return false;
  }

  try {
    const parsed = JSON.parse(trimmed);

    if (parsed === null) {
      return false;
    }

    if (typeof parsed !== "object") {
      return true;
    }

    if (Array.isArray(parsed)) {
      return parsed.length > 0;
    }

    return Object.keys(parsed as Record<string, unknown>).length > 0;
  } catch {
    return true;
  }
}

function summarizeBody(value: string) {
  const compact = value.replace(/\s+/g, " ").trim();
  return compact.length > 88 ? `${compact.slice(0, 88)}…` : compact;
}

function statusLabel(status: string) {
  switch (status) {
    case "pending":
      return "待派送";
    case "pending_no_target":
      return "待配置對象";
    case "sent":
      return "已送達";
    case "failed":
      return "失敗";
    case "canceled":
      return "已取消";
    case "skipped_no_target":
      return "已略過";
    default:
      return status;
  }
}

function canCancelStatus(status: string | undefined) {
  return status === "pending" || status === "pending_no_target" || status === "failed";
}

function getStatusRank(status: string) {
  switch (status) {
    case "pending":
      return 0;
    case "pending_no_target":
      return 1;
    case "failed":
      return 2;
    case "sent":
      return 3;
    case "canceled":
      return 4;
    case "skipped_no_target":
      return 5;
    default:
      return 6;
  }
}

function sortNotifications(items: NotificationItem[]) {
  return [...items].sort((left, right) => {
    const rankDifference = getStatusRank(left.status) - getStatusRank(right.status);
    if (rankDifference !== 0) {
      return rankDifference;
    }

    return new Date(left.scheduledAtUtc).getTime() - new Date(right.scheduledAtUtc).getTime();
  });
}

function normalizeFilterOptions(options: NotificationFilterOptions): NotificationFilterOptions {
  return {
    channels: uniqueSorted(options.channels),
    sourceSystems: uniqueSorted(options.sourceSystems),
    eventTypes: uniqueSorted(options.eventTypes)
  };
}

function getFallbackFilterOptions(): NotificationFilterOptions {
  return {
    channels: ["telegram", "line"],
    sourceSystems: ["admin-ui", "manual"],
    eventTypes: ["manual.batch", "manual.notification"]
  };
}

function buildSelectOptions(
  remoteValues: string[],
  fallbackValues: string[],
  currentValue: string
): SelectOption[] {
  const merged = uniqueSorted([...remoteValues, ...fallbackValues, currentValue].filter(Boolean));
  return [
    { label: "全部", value: "" },
    ...merged.map((value) => ({
      label: value,
      value
    }))
  ];
}

function uniqueSorted(values: string[]) {
  return [...new Set(values.map((value) => value.trim()).filter(Boolean))].sort((left, right) =>
    left.localeCompare(right, "zh-Hant")
  );
}

function targetSummary(item: NotificationItem) {
  if (item.targetName && item.target) {
    return `${item.targetName} · ${item.target}`;
  }

  if (item.targetName) {
    return item.targetName;
  }

  if (item.target) {
    return item.target;
  }

  return "未配置對象";
}

function targetDescription(item: NotificationItem) {
  if (item.isTargetOverride) {
    return "單筆覆蓋";
  }

  if (item.targetName) {
    return item.targetName;
  }

  return "NotifyCenter 路由";
}

function useLineSourceAsRoutingTarget(source: LineSourceItem) {
  activePage.value = "targets";
  routingTargetForm.id = "";
  routingTargetForm.channel = "line";
  routingTargetForm.name = source.displayName || `${sourceTypeLabel(source.sourceType)} ${shortId(source.sourceId)}`;
  routingTargetForm.destination = source.sourceId;
  routingTargetForm.isEnabled = true;
  routingTargetForm.sortOrder = nextRoutingSortOrder("line");
  routingTargetForm.metadataJson = JSON.stringify(
    {
      lineSourceId: source.id,
      lineSourceType: source.sourceType
    },
    null,
    2
  );
}

function nextRoutingSortOrder(channel: string) {
  const currentMax = routingTargets.value
    .filter((target) => target.channel === channel)
    .reduce((max, target) => Math.max(max, target.sortOrder), 0);
  return currentMax + 10;
}

function sourceTypeLabel(sourceType: string) {
  switch (sourceType) {
    case "group":
      return "群組";
    case "room":
      return "聊天室";
    case "user":
      return "使用者";
    default:
      return sourceType;
  }
}

function lineSourceTitle(source: LineSourceItem) {
  return source.displayName || `${sourceTypeLabel(source.sourceType)} ${shortId(source.sourceId)}`;
}

function lineSourceRoutingText(source: LineSourceItem) {
  if (!source.routingTargetName) {
    return "尚未加入派送對象";
  }

  return source.routingTargetEnabled
    ? `已加入：${source.routingTargetName}`
    : `已加入但停用：${source.routingTargetName}`;
}

function shortId(value: string) {
  return value.length > 14 ? `${value.slice(0, 7)}…${value.slice(-5)}` : value;
}

function toMessage(error: unknown) {
  return error instanceof Error ? error.message : "發生未預期錯誤。";
}
</script>

<template>
  <div class="shell">
    <div class="shell__glow shell__glow--top"></div>
    <div class="shell__glow shell__glow--bottom"></div>

    <main v-if="!isLoggedIn" class="login-card">
      <p class="eyebrow">NotifyCenter Admin</p>
      <h1>登入後台</h1>
      <p class="subtitle">查看待派送、失敗與已略過的派送單，並由中心式設定決定實際通知對象。</p>

      <form class="stack" @submit.prevent="handleLogin">
        <label>
          <span>帳號</span>
          <input v-model="loginForm.username" autocomplete="username" />
        </label>
        <label>
          <span>密碼</span>
          <input v-model="loginForm.password" type="password" autocomplete="current-password" />
        </label>
        <button class="button button--primary" :disabled="loading">登入</button>
      </form>

      <p class="hint">
        首次啟動的 bootstrap 帳號為 <code>amadegx</code>。登入後系統會提醒你盡快改密碼。
      </p>
      <p v-if="errorMessage" class="message message--error">{{ errorMessage }}</p>
    </main>

    <main v-else class="workspace">
      <header class="topbar">
        <div>
          <p class="eyebrow">NotifyCenter Console</p>
          <h1>個人化通知中心</h1>
          <p class="subtitle">中心式決定誰會收到通知，主控台只局部刷新清單與統計，不打斷你手上的輸入。</p>
        </div>
        <div class="topbar__actions">
          <button class="button button--ghost button--compact" @click="runManualRefresh" :disabled="loading || saving">
            重新整理
          </button>
          <button
            class="button button--ghost button--compact"
            :class="{ 'button--selected': activePage === 'notifications' }"
            @click="openNotificationsPage"
          >
            通知管理
          </button>
          <button
            class="button button--ghost button--compact"
            :class="{ 'button--selected': activePage === 'targets' }"
            @click="openTargetsPage"
          >
            對象管理
          </button>
          <button class="button button--ghost button--compact" @click="openPasswordPanel">修改密碼</button>
          <button class="button button--primary button--compact" @click="handleLogout" :disabled="loading || saving">
            登出
          </button>
        </div>
      </header>

      <section v-if="session?.mustChangePassword" class="warning-card">
        <div>
          <p class="warning-card__title">這是首次登入，建議立即更新密碼</p>
          <p class="warning-card__body">你可以先使用主控台，但提醒會持續顯示直到密碼成功更新為止。</p>
        </div>
        <button class="button button--warning button--compact" @click="openPasswordPanel">立即改密碼</button>
      </section>

      <section class="status-stack">
        <p v-if="errorMessage" class="message message--error">{{ errorMessage }}</p>
        <p v-if="successMessage" class="message message--success">{{ successMessage }}</p>
      </section>

      <section class="summary-strip">
        <article class="summary-card summary-card--accent">
          <span>待派送</span>
          <strong>{{ stats?.pending ?? 0 }}</strong>
        </article>
        <article class="summary-card">
          <span>待配置對象</span>
          <strong>{{ stats?.pendingNoTarget ?? 0 }}</strong>
        </article>
        <article class="summary-card">
          <span>逾時待送</span>
          <strong>{{ stats?.due ?? 0 }}</strong>
        </article>
        <article class="summary-card">
          <span>失敗</span>
          <strong>{{ stats?.failed ?? 0 }}</strong>
        </article>
        <article class="summary-card">
          <span>已送達</span>
          <strong>{{ stats?.sent ?? 0 }}</strong>
        </article>
        <article class="summary-card">
          <span>總派送單</span>
          <strong>{{ stats?.total ?? 0 }}</strong>
        </article>
      </section>

      <section v-if="activePage === 'notifications'" class="dashboard">
        <section class="panel queue-panel">
          <div class="panel__header">
            <div>
              <h2>派送清單</h2>
            </div>
            <div class="panel__actions">
              <button
                class="button button--compact"
                :class="{ 'button--selected': sidePanel === 'preview' }"
                @click="sidePanel = 'preview'"
              >
                預覽
              </button>
              <button
                class="button button--compact"
                :class="{ 'button--selected': sidePanel === 'create' }"
                @click="openCreatePanel"
              >
                單筆建立
              </button>
              <button
                class="button button--compact"
                :class="{ 'button--selected': sidePanel === 'batch' }"
                @click="openBatchPanel"
              >
                批次建立
              </button>
            </div>
          </div>

          <div class="status-tabs">
            <button
              v-for="option in statusOptions"
              :key="option.value || 'all'"
              class="status-tab"
              :class="{ 'status-tab--active': filters.status === option.value }"
              @click="setStatusFilter(option.value)"
            >
              <span>{{ option.label }}</span>
              <strong>{{ option.count }}</strong>
            </button>
          </div>

          <form class="filter-shell" @submit.prevent="applyFilters">
            <div class="filter-main">
              <label class="field">
                <span>Source System</span>
                <select v-model="filters.sourceSystem">
                  <option v-for="option in sourceSystemOptions" :key="option.value || 'all'" :value="option.value">
                    {{ option.label }}
                  </option>
                </select>
              </label>

              <label class="field">
                <span>Event Type</span>
                <select v-model="filters.eventType">
                  <option v-for="option in eventTypeOptions" :key="option.value || 'all'" :value="option.value">
                    {{ option.label }}
                  </option>
                </select>
              </label>

              <label class="field field--limit">
                <span>顯示筆數</span>
                <div class="stepper">
                  <input v-model.number="filters.limit" min="1" max="500" type="number" />
                  <div class="stepper__buttons">
                    <button type="button" @click="changeLimit(10)">▴</button>
                    <button type="button" @click="changeLimit(-10)">▾</button>
                  </div>
                </div>
              </label>

              <button
                class="button button--ghost button--compact"
                type="button"
                :class="{ 'button--selected': advancedFiltersOpen || hasAdvancedFilters }"
                @click="toggleAdvancedFilters"
              >
                進階篩選
              </button>
              <button class="button button--primary button--compact" :disabled="loading || saving">套用篩選</button>
              <button
                class="button button--ghost button--compact"
                type="button"
                @click="
                  resetFilters();
                  applyFilters();
                "
              >
                重設篩選
              </button>
            </div>

            <div v-if="advancedFiltersOpen || hasAdvancedFilters" class="filter-advanced">
              <label class="field">
                <span>頻道</span>
                <select v-model="filters.channel">
                  <option v-for="option in channelOptions" :key="option.value || 'all'" :value="option.value">
                    {{ option.label }}
                  </option>
                </select>
              </label>

              <label class="field">
                <span>排程起</span>
                <input v-model="filters.scheduledFrom" type="datetime-local" />
              </label>

              <label class="field">
                <span>排程迄</span>
                <input v-model="filters.scheduledTo" type="datetime-local" />
              </label>

              <label class="field field--wide">
                <span>訊息內容</span>
                <input v-model="filters.messageQuery" placeholder="搜尋標題或內文" />
              </label>

              <button class="button button--ghost button--compact" type="button" @click="clearDateRange">
                清空時間
              </button>
            </div>
          </form>

          <div v-if="notifications.length > 0" class="bulk-bar">
            <div class="bulk-bar__group">
              <button class="button button--ghost button--compact" @click="toggleSelectAllVisible">
                {{ allVisibleSelected ? "取消全選" : "全選目前" }}
              </button>
              <span class="selection-note">已選 {{ selectedCount }} / {{ notifications.length }}</span>
            </div>
            <div class="bulk-bar__group">
              <button
                class="button button--ghost button--compact"
                @click="clearSelection"
                :disabled="selectedCount === 0 || saving"
              >
                清除
              </button>
              <button
                class="button button--warning button--compact"
                @click="performBulkCancel"
                :disabled="saving || cancelableSelectedIds.length === 0"
              >
                批次取消 {{ cancelableSelectedIds.length > 0 ? `(${cancelableSelectedIds.length})` : "" }}
              </button>
              <span v-if="nonCancelableSelectedCount > 0" class="selection-note selection-note--muted">
                不可取消 {{ nonCancelableSelectedCount }}
              </span>
            </div>
          </div>

          <div class="queue-table">
            <div v-if="notifications.length > 0" class="queue-table__head">
              <span></span>
              <span>通知</span>
              <span>排程</span>
              <span>對象</span>
              <span>狀態</span>
            </div>

            <div v-if="notifications.length > 0" class="queue-list">
              <article
                v-for="item in notifications"
                :key="item.id"
                class="queue-row"
                :class="{ 'queue-row--active': selectedNotification?.id === item.id }"
              >
                <label class="queue-check" @click.stop>
                  <input type="checkbox" :checked="selectedIds.includes(item.id)" @change="toggleSelection(item.id)" />
                </label>

                <button class="queue-row__button" @click="selectNotification(item.id, true)">
                  <div class="queue-row__title">
                    <strong>{{ item.title }}</strong>
                    <p>{{ summarizeBody(item.body) }}</p>
                    <p class="queue-row__submeta">
                      {{ item.channel }} · {{ item.sourceSystem }} · {{ item.eventType }}
                    </p>
                    <p v-if="item.status === 'failed' && item.lastError" class="queue-row__error">
                      {{ item.lastError }}
                    </p>
                  </div>
                  <div class="queue-row__meta">{{ formatDateCompact(item.scheduledAtUtc) }}</div>
                  <div class="queue-row__meta">
                    <strong>{{ targetSummary(item) }}</strong>
                    <small>{{ targetDescription(item) }}</small>
                  </div>
                  <div class="queue-row__status">
                    <span class="badge" :class="`badge--${item.status}`">{{ statusLabel(item.status) }}</span>
                  </div>
                </button>
              </article>
            </div>

            <div v-else class="empty-state">
              <p>目前沒有符合條件的派送單。你可以調整篩選、建立新通知需求，或先設定頻道對象。</p>
            </div>
          </div>
        </section>

        <aside class="side-stack">
          <section v-if="sidePanel === 'preview'" class="panel preview-panel">
            <div class="panel__header">
              <div>
                <h2>{{ selectedNotification ? selectedNotification.title : "派送預覽" }}</h2>
              </div>
              <button v-if="selectedNotification" class="button button--ghost button--compact" @click="useSelectedAsDraft">
                以此為藍本
              </button>
            </div>

            <template v-if="selectedNotification">
              <div class="preview-strip">
                <span class="badge" :class="`badge--${selectedNotification.status}`">
                  {{ statusLabel(selectedNotification.status) }}
                </span>
                <span class="mini-chip">{{ formatDateCompact(selectedNotification.scheduledAtUtc) }}</span>
                <span class="mini-chip">{{ selectedNotification.channel }}</span>
                <span class="mini-chip mono">{{ selectedNotification.target ?? "未配置對象" }}</span>
                <span v-if="selectedNotification.targetName" class="mini-chip">{{ selectedNotification.targetName }}</span>
                <span v-if="selectedNotification.isTargetOverride" class="mini-chip">override</span>
              </div>

              <article class="message-preview">
                <h3>{{ selectedNotification.title }}</h3>
                <p>{{ selectedNotification.body }}</p>
              </article>

              <p v-if="selectedNotification.lastError" class="message message--error">{{ selectedNotification.lastError }}</p>

              <div class="action-row">
                <button
                  class="button button--warning button--compact"
                  @click="performCancel(selectedNotification.id)"
                  :disabled="saving || !canCancelSelected"
                >
                  取消派送單
                </button>
                <button
                  class="button button--primary button--compact"
                  @click="performRetry(selectedNotification.id)"
                  :disabled="saving || !canRetrySelected"
                >
                  重新派送
                </button>
              </div>

              <details class="foldout" :open="attempts.length > 0">
                <summary>派送紀錄 <span>{{ attempts.length }}</span></summary>
                <div class="foldout__content">
                  <div v-if="attempts.length > 0" class="table-wrap">
                    <table>
                      <thead>
                        <tr>
                          <th>時間</th>
                          <th>狀態</th>
                          <th>HTTP</th>
                          <th>錯誤</th>
                          <th>Response</th>
                        </tr>
                      </thead>
                      <tbody>
                        <tr v-for="attempt in attempts" :key="attempt.id">
                          <td>{{ formatDate(attempt.attemptedAtUtc) }}</td>
                          <td>{{ attempt.status }}</td>
                          <td>{{ attempt.httpStatus ?? "—" }}</td>
                          <td>{{ attempt.error || "—" }}</td>
                          <td><pre class="attempt-response">{{ attempt.responseBody || "—" }}</pre></td>
                        </tr>
                      </tbody>
                    </table>
                  </div>
                  <p v-else class="mini-empty">這筆派送單目前還沒有派送紀錄。</p>
                </div>
              </details>

              <details class="foldout">
                <summary>進階資訊</summary>
                <div class="foldout__content">
                  <div class="advanced-grid">
                    <div>
                      <span class="field-label">Source System</span>
                      <strong>{{ selectedNotification.sourceSystem }}</strong>
                    </div>
                    <div>
                      <span class="field-label">Event Type</span>
                      <strong>{{ selectedNotification.eventType }}</strong>
                    </div>
                    <div>
                      <span class="field-label">Dedupe Key</span>
                      <strong class="mono">{{ selectedNotification.dedupeKey }}</strong>
                    </div>
                    <div>
                      <span class="field-label">Notification ID</span>
                      <strong class="mono">{{ selectedNotification.notificationId }}</strong>
                    </div>
                    <div>
                      <span class="field-label">Delivery ID</span>
                      <strong class="mono">{{ selectedNotification.id }}</strong>
                    </div>
                    <div>
                      <span class="field-label">最後更新</span>
                      <strong>{{ formatDate(selectedNotification.updatedAt) }}</strong>
                    </div>
                  </div>

                  <div v-if="selectedHasMetadata" class="advanced-block">
                    <span class="field-label">Metadata JSON</span>
                    <pre>{{ selectedMetadata }}</pre>
                  </div>
                </div>
              </details>
            </template>

            <div v-else class="empty-state">
              <p>從左側選一筆派送單，就能在這裡查看內容與執行操作。</p>
            </div>
          </section>

          <section v-else-if="sidePanel === 'create'" class="panel">
            <div class="panel__header">
              <div>
                <h2>單筆建立通知</h2>
              </div>
              <button class="button button--ghost button--compact" @click="sidePanel = 'preview'">返回預覽</button>
            </div>

            <form class="stack" @submit.prevent="submitCreate">
              <label>
                <span>標題</span>
                <input v-model="createForm.title" required />
              </label>
              <label>
                <span>內容</span>
                <textarea v-model="createForm.body" rows="5" required></textarea>
              </label>

              <div class="two-up">
                <label>
                  <span>排程時間</span>
                  <input v-model="createForm.scheduledAtLocal" type="datetime-local" required />
                </label>
                <label>
                  <span>頻道</span>
                  <select v-model="createForm.channel">
                    <option v-for="option in notificationChannelOptions" :key="option.value" :value="option.value">
                      {{ option.label }}
                    </option>
                  </select>
                </label>
              </div>

              <details class="foldout">
                <summary>進階欄位與路由覆蓋</summary>
                <div class="foldout__content stack">
                  <label>
                    <span>Target Override</span>
                    <div class="target-ac">
                      <input
                        :value="targetInputQuery"
                        @input="onTargetInput(($event.target as HTMLInputElement).value)"
                        @focus="targetDropdownOpen = true"
                        @blur="targetDropdownOpen = false"
                        autocomplete="off"
                        placeholder="留空時，交由 NotifyCenter 的頻道對象設定決定實際派送對象"
                      />
                      <ul v-if="targetDropdownOpen && targetSuggestions.length > 0" class="target-ac__dropdown">
                        <li
                          v-for="t in targetSuggestions"
                          :key="t.id"
                          class="target-ac__item"
                          @mousedown.prevent="pickTarget(t)"
                        >
                          <span class="target-ac__name">{{ t.name }}</span>
                          <small class="target-ac__dest">{{ t.destination }}</small>
                        </li>
                      </ul>
                    </div>
                    <p v-if="targetPickedItem" class="helper helper--picked">↳ {{ targetPickedItem.destination }}</p>
                  </label>

                  <div class="two-up">
                    <label>
                      <span>Source System</span>
                      <input v-model="createForm.sourceSystem" />
                    </label>
                    <label>
                      <span>Event Type</span>
                      <input v-model="createForm.eventType" />
                    </label>
                  </div>

                  <label>
                    <span>Dedupe Key</span>
                    <input v-model="createForm.dedupeKey" placeholder="可留空，系統會自動產生" />
                  </label>

                  <label>
                    <span>Metadata JSON</span>
                    <textarea
                      v-model="createForm.metadataJson"
                      rows="5"
                      placeholder='例如：{"campaign":"promo-a"}'
                    ></textarea>
                  </label>

                  <p class="helper">
                    Target Override 只會覆蓋這一筆通知需求。留空時，實際要送給誰完全由 NotifyCenter 的對象管理決定。
                  </p>
                </div>
              </details>

              <button class="button button--primary" :disabled="saving">建立通知</button>
            </form>
          </section>

          <section v-else-if="sidePanel === 'batch'" class="panel">
            <div class="panel__header">
              <div>
                <h2>批次建立</h2>
              </div>
              <button class="button button--ghost button--compact" @click="sidePanel = 'preview'">返回預覽</button>
            </div>

            <form class="stack" @submit.prevent="submitBatch">
              <label>
                <span>Batch JSON</span>
                <textarea v-model="batchForm.payload" rows="14"></textarea>
              </label>
              <button class="button button--primary" :disabled="saving">送出批次建立</button>
            </form>

            <article v-if="bulkResult" class="result-card">
              <div class="result-card__summary">
                <strong>已接受 {{ bulkResult.accepted }} 筆</strong>
                <span>建立 {{ bulkResult.created }} / 更新 {{ bulkResult.updated }} / 略過 {{ bulkResult.skipped }}</span>
              </div>
              <pre>{{ JSON.stringify(bulkResult, null, 2) }}</pre>
            </article>
          </section>

          <section v-else class="panel">
            <div class="panel__header">
              <div>
                <h2>密碼設定</h2>
              </div>
              <button class="button button--ghost button--compact" @click="sidePanel = 'preview'">返回預覽</button>
            </div>

            <form class="stack" @submit.prevent="submitPasswordChange">
              <label>
                <span>目前密碼</span>
                <input
                  v-model="passwordForm.currentPassword"
                  type="password"
                  autocomplete="current-password"
                />
              </label>
              <label>
                <span>新密碼</span>
                <input v-model="passwordForm.newPassword" type="password" autocomplete="new-password" />
              </label>
              <label>
                <span>確認新密碼</span>
                <input
                  v-model="passwordForm.confirmPassword"
                  type="password"
                  autocomplete="new-password"
                />
              </label>
              <button class="button button--primary" :disabled="saving">更新密碼</button>
            </form>
          </section>
        </aside>
      </section>

      <section v-else class="targets-page">
        <section class="panel targets-hero">
          <div>
            <p class="eyebrow">Routing Targets</p>
            <h2>對象管理</h2>
            <p class="subtitle">
              LINE webhook 會先收集 userId、groupId 與 roomId；確認後再加入 routing targets，通知才會實際派送給該對象。
            </p>
          </div>
          <div class="target-metrics">
            <article>
              <span>Routing targets</span>
              <strong>{{ routingTargets.length }}</strong>
            </article>
            <article>
              <span>LINE sources</span>
              <strong>{{ lineSourceCounts.total }}</strong>
            </article>
            <article>
              <span>群組 / 聊天室 / 使用者</span>
              <strong>{{ lineSourceCounts.groups }} / {{ lineSourceCounts.rooms }} / {{ lineSourceCounts.users }}</strong>
            </article>
          </div>
        </section>

        <section class="targets-grid">
          <section class="panel targets-panel">
            <div class="panel__header">
              <div>
                <h2>{{ routingTargetForm.id ? "編輯派送對象" : "新增派送對象" }}</h2>
              </div>
              <button class="button button--ghost button--compact" @click="resetRoutingTargetForm">清空表單</button>
            </div>

            <form class="stack" @submit.prevent="submitRoutingTarget">
              <div class="two-up">
                <label>
                  <span>頻道</span>
                  <select v-model="routingTargetForm.channel">
                    <option v-for="option in routingChannelOptions" :key="option.value" :value="option.value">
                      {{ option.label }}
                    </option>
                  </select>
                </label>
                <label>
                  <span>顯示名稱</span>
                  <input v-model="routingTargetForm.name" required />
                </label>
              </div>

              <div class="two-up">
                <label>
                  <span>{{ routingDestinationLabel }}</span>
                  <input v-model="routingTargetForm.destination" :placeholder="routingDestinationPlaceholder" required />
                </label>
                <label>
                  <span>排序</span>
                  <input v-model.number="routingTargetForm.sortOrder" type="number" />
                </label>
              </div>

              <p v-if="routingTargetHint" class="helper">{{ routingTargetHint }}</p>

              <label class="checkbox-field">
                <input v-model="routingTargetForm.isEnabled" type="checkbox" />
                <span>啟用這個對象</span>
              </label>

              <label>
                <span>Metadata JSON</span>
                <textarea
                  v-model="routingTargetForm.metadataJson"
                  rows="4"
                  placeholder='例如：{"team":"ops","note":"line group"}'
                ></textarea>
              </label>

              <div class="panel__actions">
                <button class="button button--primary" :disabled="saving">
                  {{ routingTargetForm.id ? "更新對象" : "新增對象" }}
                </button>
                <button class="button button--ghost" type="button" @click="resetRoutingTargetForm">重設</button>
              </div>
            </form>

            <div class="target-list">
              <article v-for="item in routingTargets" :key="item.id" class="target-card">
                <div class="target-card__head">
                  <div>
                    <strong>{{ item.name }}</strong>
                    <p>{{ item.channel }} · sort {{ item.sortOrder }}</p>
                  </div>
                  <span class="badge" :class="item.isEnabled ? 'badge--sent' : 'badge--canceled'">
                    {{ item.isEnabled ? "啟用中" : "已停用" }}
                  </span>
                </div>
                <p class="target-card__destination mono">{{ item.destination }}</p>
                <div class="target-card__actions">
                  <button class="button button--ghost button--compact" @click="startEditingRoutingTarget(item)">編輯</button>
                  <button class="button button--warning button--compact" @click="removeRoutingTarget(item.id)" :disabled="saving">
                    刪除
                  </button>
                </div>
              </article>

              <div v-if="routingTargets.length === 0" class="empty-state">
                <p>尚未設定派送對象。可以手動新增，或從右側 LINE webhook 來源帶入。</p>
              </div>
            </div>
          </section>

          <section class="panel line-sources-panel">
            <div class="panel__header">
              <div>
                <h2>LINE webhook 來源</h2>
                <p class="helper">將這個網址設定到 LINE Developers 的 Webhook URL，使用者或群組互動後就會出現在下方。</p>
              </div>
              <button class="button button--ghost button--compact" @click="loadTargetManagement" :disabled="loading || saving">
                重新載入來源
              </button>
            </div>

            <label>
              <span>Webhook URL</span>
              <input class="mono" :value="lineWebhookUrl" readonly />
            </label>

            <div class="source-list">
              <article v-for="source in lineSources" :key="source.id" class="source-card">
                <div class="source-card__head">
                  <div>
                    <span class="badge badge--line-source">{{ sourceTypeLabel(source.sourceType) }}</span>
                    <strong>{{ lineSourceTitle(source) }}</strong>
                  </div>
                  <span
                    class="badge"
                    :class="
                      source.routingTargetName
                        ? source.routingTargetEnabled
                          ? 'badge--sent'
                          : 'badge--canceled'
                        : 'badge--pending_no_target'
                    "
                  >
                    {{ lineSourceRoutingText(source) }}
                  </span>
                </div>

                <p class="source-card__id mono">{{ source.sourceId }}</p>
                <p class="helper">
                  最後事件 {{ source.lastEventType || "—" }} ·
                  {{ formatDate(source.lastEventAtUtc) }} · 首次看到 {{ formatDate(source.firstSeenAtUtc) }}
                </p>

                <div class="target-card__actions">
                  <button class="button button--primary button--compact" @click="useLineSourceAsRoutingTarget(source)">
                    {{ source.routingTargetName ? "帶入表單" : "加入派送對象" }}
                  </button>
                </div>
              </article>

              <div v-if="lineSources.length === 0" class="empty-state">
                <p>尚未收到 LINE webhook 事件。先把上方 URL 設到 LINE Developers，然後讓使用者或群組傳一則訊息給 bot。</p>
              </div>
            </div>
          </section>
        </section>
      </section>
    </main>
  </div>
</template>

<style scoped>
.shell {
  position: relative;
  min-height: 100vh;
  padding: 14px;
  overflow: hidden;
}

.shell__glow {
  position: absolute;
  width: 24rem;
  height: 24rem;
  border-radius: 50%;
  filter: blur(24px);
  opacity: 0.38;
  pointer-events: none;
}

.shell__glow--top {
  top: -10rem;
  right: -7rem;
  background: rgba(255, 177, 91, 0.24);
}

.shell__glow--bottom {
  bottom: -11rem;
  left: -8rem;
  background: rgba(33, 101, 85, 0.12);
}

.login-card,
.workspace {
  position: relative;
  z-index: 1;
}

.login-card {
  max-width: 32rem;
  margin: 8vh auto;
  padding: 1.8rem;
  border: 1px solid rgba(21, 27, 35, 0.12);
  border-radius: 1.2rem;
  background: rgba(255, 252, 245, 0.92);
  box-shadow: 0 24px 64px rgba(40, 51, 62, 0.11);
  backdrop-filter: blur(16px);
}

.workspace {
  display: grid;
  gap: 0.75rem;
}

.topbar,
.warning-card,
.summary-card,
.panel {
  border: 1px solid rgba(21, 27, 35, 0.1);
  background: rgba(255, 251, 244, 0.92);
  box-shadow: 0 14px 30px rgba(28, 35, 43, 0.06);
  backdrop-filter: blur(12px);
}

.topbar {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 0.9rem;
  padding: 1rem 1.15rem;
  border-radius: 1.2rem;
}

.topbar__actions,
.panel__actions,
.action-row,
.two-up,
.bulk-bar,
.bulk-bar__group,
.target-card__actions {
  display: flex;
  gap: 0.55rem;
}

.topbar__actions,
.panel__actions,
.bulk-bar,
.bulk-bar__group,
.target-card__actions {
  flex-wrap: wrap;
}

.eyebrow {
  margin: 0 0 0.22rem;
  color: #8a5223;
  text-transform: uppercase;
  letter-spacing: 0.16em;
  font-size: 0.74rem;
  font-weight: 700;
}

h1,
h2,
h3,
p {
  margin: 0;
}

h1 {
  font-size: clamp(1.75rem, 3vw, 2.3rem);
  line-height: 1.05;
}

h2 {
  font-size: 1.04rem;
}

h3 {
  font-size: 0.98rem;
}

.subtitle,
.helper,
.hint,
.selection-note--muted,
.queue-row__submeta,
.target-card__head p {
  color: #5d6976;
}

.subtitle {
  margin-top: 0.4rem;
  max-width: 48rem;
}

.warning-card {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 0.9rem;
  padding: 0.8rem 0.95rem;
  border-radius: 1rem;
  background: linear-gradient(135deg, rgba(255, 196, 126, 0.22), rgba(255, 246, 230, 0.92));
}

.warning-card__title {
  font-weight: 700;
}

.warning-card__body {
  color: #6e573f;
}

.status-stack {
  display: grid;
  gap: 0.55rem;
}

.message {
  padding: 0.72rem 0.88rem;
  border-radius: 0.9rem;
  border: 1px solid transparent;
}

.message--error {
  color: #982f27;
  background: rgba(255, 232, 228, 0.95);
  border-color: rgba(152, 47, 39, 0.14);
}

.message--success {
  color: #185d45;
  background: rgba(232, 250, 240, 0.95);
  border-color: rgba(24, 93, 69, 0.12);
}

.summary-strip {
  display: grid;
  grid-template-columns: repeat(6, minmax(0, 1fr));
  gap: 0.65rem;
}

.summary-card {
  display: grid;
  gap: 0.1rem;
  padding: 0.72rem 0.8rem;
  border-radius: 0.95rem;
}

.summary-card--accent {
  background: linear-gradient(135deg, rgba(29, 106, 78, 0.18), rgba(255, 251, 244, 0.96));
}

.summary-card span {
  color: #54616f;
  font-size: 0.84rem;
}

.summary-card strong {
  font-size: 1.36rem;
  line-height: 1;
}

.dashboard {
  display: grid;
  grid-template-columns: minmax(0, 1.55fr) minmax(22rem, 0.95fr);
  gap: 0.75rem;
  align-items: start;
}

.targets-page,
.targets-grid,
.target-metrics,
.source-list {
  display: grid;
  gap: 0.75rem;
}

.targets-grid {
  grid-template-columns: minmax(0, 1fr) minmax(24rem, 0.9fr);
  align-items: start;
}

.targets-hero {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 1rem;
}

.target-metrics {
  grid-template-columns: repeat(3, minmax(8rem, 1fr));
  min-width: min(42rem, 100%);
}

.target-metrics article {
  padding: 0.68rem 0.78rem;
  border: 1px solid rgba(21, 27, 35, 0.08);
  border-radius: 0.9rem;
  background: rgba(255, 255, 255, 0.58);
}

.target-metrics span {
  display: block;
  color: #54616f;
  font-size: 0.78rem;
  font-weight: 700;
}

.target-metrics strong {
  display: block;
  margin-top: 0.18rem;
  font-size: 1.16rem;
}

.panel {
  padding: 0.88rem;
  border-radius: 1.15rem;
}

.queue-panel,
.preview-panel,
.targets-panel,
.line-sources-panel {
  display: grid;
  gap: 0.72rem;
}

.side-stack {
  display: grid;
  gap: 0.75rem;
  position: sticky;
  top: 0.9rem;
}

.panel__header {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 0.7rem;
}

.status-tabs {
  display: flex;
  flex-wrap: wrap;
  gap: 0.42rem;
}

.status-tab {
  display: inline-flex;
  align-items: center;
  gap: 0.35rem;
  padding: 0.42rem 0.64rem;
  border: 1px solid rgba(21, 27, 35, 0.1);
  border-radius: 999px;
  background: rgba(255, 255, 255, 0.8);
  color: #23313d;
  font-size: 0.88rem;
}

.status-tab strong {
  color: #1f5b4c;
}

.status-tab--active {
  background: rgba(31, 91, 76, 0.12);
  border-color: rgba(31, 91, 76, 0.3);
}

.filter-shell {
  display: grid;
  gap: 0.62rem;
  padding: 0.72rem;
  border: 1px solid rgba(21, 27, 35, 0.08);
  border-radius: 1rem;
  background: rgba(255, 255, 255, 0.52);
}

.filter-main,
.filter-advanced {
  display: grid;
  gap: 0.62rem;
  align-items: end;
}

.filter-main {
  grid-template-columns: minmax(10rem, 1fr) minmax(10rem, 1fr) minmax(9rem, 10rem) auto auto auto;
}

.filter-advanced {
  grid-template-columns: repeat(3, minmax(0, 1fr)) minmax(0, 1.3fr) auto;
}

.field {
  display: grid;
  gap: 0.25rem;
}

.field span,
label span {
  color: #445261;
  font-size: 0.8rem;
  font-weight: 700;
}

.field--wide {
  min-width: 0;
}

.stepper {
  display: grid;
  grid-template-columns: minmax(0, 1fr) 2rem;
  overflow: hidden;
  border: 1px solid rgba(21, 27, 35, 0.14);
  border-radius: 0.8rem;
  background: rgba(255, 255, 255, 0.84);
}

.stepper input {
  border: 0;
  border-radius: 0;
  background: transparent;
}

.stepper__buttons {
  display: grid;
  grid-template-rows: 1fr 1fr;
  border-left: 1px solid rgba(21, 27, 35, 0.12);
}

.stepper__buttons button {
  border: 0;
  background: rgba(248, 249, 250, 0.95);
  color: #33414d;
  font-size: 0.7rem;
}

.stepper__buttons button + button {
  border-top: 1px solid rgba(21, 27, 35, 0.12);
}

.bulk-bar {
  align-items: center;
  justify-content: space-between;
  padding: 0.52rem 0.7rem;
  border: 1px solid rgba(21, 27, 35, 0.08);
  border-radius: 0.92rem;
  background: rgba(255, 255, 255, 0.62);
}

.selection-note {
  color: #2b3742;
  font-size: 0.86rem;
  font-weight: 600;
}

.queue-table {
  border: 1px solid rgba(21, 27, 35, 0.08);
  border-radius: 1rem;
  background: rgba(255, 255, 255, 0.56);
  overflow: hidden;
}

.queue-table__head {
  display: grid;
  grid-template-columns: 2.1rem minmax(0, 1.75fr) 7rem minmax(10rem, 1fr) 6rem;
  gap: 0.65rem;
  padding: 0.52rem 0.8rem;
  background: rgba(245, 248, 246, 0.96);
  color: #63707b;
  font-size: 0.76rem;
  font-weight: 700;
  text-transform: uppercase;
  letter-spacing: 0.05em;
}

.queue-list {
  max-height: calc(100vh - 22rem);
  overflow: auto;
}

.queue-row {
  display: grid;
  grid-template-columns: 2.1rem minmax(0, 1fr);
  gap: 0.65rem;
  padding: 0.16rem 0.8rem;
  border-top: 1px solid rgba(21, 27, 35, 0.07);
}

.queue-row:first-child {
  border-top: 0;
}

.queue-row--active {
  background: rgba(31, 91, 76, 0.06);
}

.queue-check {
  display: inline-flex;
  align-items: center;
  justify-content: center;
}

.queue-row__button {
  display: grid;
  grid-template-columns: minmax(0, 1.75fr) 7rem minmax(10rem, 1fr) 6rem;
  gap: 0.65rem;
  align-items: center;
  padding: 0.56rem 0;
  border: 0;
  background: transparent;
  text-align: left;
}

.queue-row__title {
  min-width: 0;
}

.queue-row__title strong {
  display: block;
  font-size: 0.96rem;
}

.queue-row__title p {
  font-size: 0.86rem;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.queue-row__submeta {
  margin-top: 0.12rem;
}

.queue-row__error {
  color: #a13c2f;
  margin-top: 0.14rem;
}

.queue-row__meta,
.queue-row__status {
  color: #42515e;
  font-size: 0.86rem;
}

.queue-row__meta strong,
.queue-row__meta small {
  display: block;
}

.queue-row__meta small {
  color: #5d6976;
  margin-top: 0.12rem;
}

.queue-row__status {
  display: flex;
  justify-content: flex-end;
}

.preview-strip {
  display: flex;
  flex-wrap: wrap;
  gap: 0.42rem;
  align-items: center;
}

.mini-chip {
  display: inline-flex;
  align-items: center;
  padding: 0.26rem 0.52rem;
  border-radius: 999px;
  background: rgba(255, 255, 255, 0.8);
  border: 1px solid rgba(21, 27, 35, 0.08);
  color: #394652;
  font-size: 0.8rem;
}

.message-preview {
  display: grid;
  gap: 0.4rem;
  padding: 0.82rem 0.92rem;
  border-radius: 0.95rem;
  background: linear-gradient(135deg, rgba(255, 255, 255, 0.96), rgba(240, 248, 244, 0.94));
  border: 1px solid rgba(31, 91, 76, 0.12);
}

.message-preview p {
  white-space: pre-wrap;
}

.badge {
  display: inline-flex;
  align-items: center;
  padding: 0.24rem 0.52rem;
  border-radius: 999px;
  font-size: 0.74rem;
  font-weight: 700;
}

.badge--pending {
  background: rgba(255, 215, 130, 0.35);
  color: #7d5204;
}

.badge--pending_no_target {
  background: rgba(147, 191, 214, 0.28);
  color: #24526d;
}

.badge--sent {
  background: rgba(131, 214, 179, 0.3);
  color: #1d6a4e;
}

.badge--failed {
  background: rgba(255, 171, 162, 0.3);
  color: #9f3028;
}

.badge--canceled {
  background: rgba(164, 178, 196, 0.28);
  color: #41505d;
}

.badge--skipped_no_target {
  background: rgba(199, 184, 137, 0.28);
  color: #725d1f;
}

.badge--line-source {
  background: rgba(18, 125, 86, 0.14);
  color: #12694c;
}

.foldout {
  border: 1px solid rgba(21, 27, 35, 0.1);
  border-radius: 1rem;
  background: rgba(255, 255, 255, 0.64);
  overflow: hidden;
}

.foldout summary {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 1rem;
  padding: 0.72rem 0.86rem;
  font-weight: 700;
  cursor: pointer;
}

.foldout__content {
  padding: 0 0.86rem 0.86rem;
}

.advanced-grid {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  gap: 0.7rem;
}

.field-label {
  display: block;
  margin-bottom: 0.2rem;
  color: #687684;
  font-size: 0.74rem;
  text-transform: uppercase;
  letter-spacing: 0.08em;
}

.advanced-block {
  margin-top: 0.82rem;
  padding: 0.86rem;
  border-radius: 0.92rem;
  background: rgba(16, 23, 31, 0.94);
  color: #f3f7fb;
}

.advanced-block pre,
.result-card pre,
.attempt-response {
  margin: 0;
  white-space: pre-wrap;
  overflow-wrap: anywhere;
  font-family: Consolas, "Cascadia Code", monospace;
}

.table-wrap {
  overflow-x: auto;
}

table {
  width: 100%;
  border-collapse: collapse;
}

th,
td {
  padding: 0.62rem 0.42rem;
  border-bottom: 1px solid rgba(21, 27, 35, 0.1);
  text-align: left;
  vertical-align: top;
  font-size: 0.86rem;
}

.stack,
label {
  display: grid;
  gap: 0.45rem;
}

.checkbox-field {
  display: flex;
  align-items: center;
  gap: 0.55rem;
}

.checkbox-field input {
  width: auto;
}

input,
select,
textarea {
  width: 100%;
  padding: 0.62rem 0.75rem;
  border: 1px solid rgba(21, 27, 35, 0.14);
  border-radius: 0.82rem;
  background: rgba(255, 255, 255, 0.84);
  color: #151b23;
}

textarea {
  resize: vertical;
}

.button {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  min-height: 2.24rem;
  padding: 0.6rem 0.9rem;
  border: 1px solid rgba(21, 27, 35, 0.12);
  border-radius: 999px;
  background: rgba(255, 255, 255, 0.8);
  color: #151b23;
}

.button--compact {
  min-height: 1.95rem;
  padding: 0.42rem 0.76rem;
  font-size: 0.86rem;
}

.button:disabled {
  opacity: 0.6;
  cursor: not-allowed;
}

.button--primary {
  background: linear-gradient(135deg, #1b6c57, #215659);
  color: #fbf7f0;
  border-color: transparent;
}

.button--ghost {
  background: rgba(255, 255, 255, 0.66);
}

.button--warning {
  background: linear-gradient(135deg, #c97c22, #a95d14);
  color: #fff8ef;
  border-color: transparent;
}

.button--selected {
  background: rgba(31, 91, 76, 0.14);
  border-color: rgba(31, 91, 76, 0.3);
  color: #194f42;
}

.empty-state,
.mini-empty {
  color: #5b6875;
}

.empty-state {
  display: grid;
  place-items: center;
  min-height: 12rem;
  text-align: center;
}

.result-card {
  display: grid;
  gap: 0.72rem;
  padding: 0.9rem;
  border-radius: 0.96rem;
  background: rgba(18, 27, 35, 0.95);
  color: #f3f7fb;
}

.result-card__summary {
  display: flex;
  flex-wrap: wrap;
  justify-content: space-between;
  gap: 0.72rem;
}

.target-list {
  display: grid;
  gap: 0.6rem;
}

.target-card {
  display: grid;
  gap: 0.55rem;
  padding: 0.8rem;
  border: 1px solid rgba(21, 27, 35, 0.08);
  border-radius: 0.94rem;
  background: rgba(255, 255, 255, 0.62);
}

.target-card__head {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 0.7rem;
}

.target-card__destination {
  color: #23313d;
}

.source-card {
  display: grid;
  gap: 0.55rem;
  padding: 0.82rem;
  border: 1px solid rgba(21, 27, 35, 0.08);
  border-radius: 0.96rem;
  background: linear-gradient(135deg, rgba(255, 255, 255, 0.72), rgba(240, 248, 244, 0.64));
}

.source-card__head {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 0.7rem;
}

.source-card__head > div {
  display: grid;
  gap: 0.28rem;
}

.source-card__id {
  color: #23313d;
  overflow-wrap: anywhere;
}

.mono {
  font-family: Consolas, "Cascadia Code", monospace;
}

code {
  padding: 0.18rem 0.38rem;
  border-radius: 0.45rem;
  background: rgba(21, 27, 35, 0.08);
}

@media (max-width: 1520px) {
  .filter-main {
    grid-template-columns: repeat(3, minmax(0, 1fr));
  }

  .filter-advanced {
    grid-template-columns: repeat(2, minmax(0, 1fr));
  }

  .summary-strip {
    grid-template-columns: repeat(3, minmax(0, 1fr));
  }
}

@media (max-width: 1280px) {
  .dashboard,
  .targets-grid {
    grid-template-columns: 1fr;
  }

  .side-stack {
    position: static;
  }
}

@media (max-width: 900px) {
  .queue-table__head {
    display: none;
  }

  .queue-list {
    max-height: none;
  }

  .queue-row,
  .queue-row__button {
    grid-template-columns: 1fr;
  }

  .queue-check {
    justify-content: flex-start;
    padding-top: 0.4rem;
  }

  .queue-row__status {
    justify-content: flex-start;
  }

  .advanced-grid {
    grid-template-columns: 1fr;
  }
}

@media (max-width: 760px) {
  .shell {
    padding: 12px;
  }

  .topbar,
  .warning-card,
  .panel__header,
  .topbar__actions,
  .panel__actions,
  .two-up,
  .action-row,
    .result-card__summary,
    .bulk-bar,
    .bulk-bar__group,
    .targets-hero,
    .target-metrics,
    .target-card__head,
    .source-card__head,
    .target-card__actions {
    display: grid;
  }

  .summary-strip,
  .filter-main,
  .filter-advanced {
    grid-template-columns: 1fr;
  }
}

.target-ac {
  position: relative;
}

.target-ac__dropdown {
  position: absolute;
  z-index: 20;
  top: calc(100% + 4px);
  left: 0;
  right: 0;
  margin: 0;
  padding: 0.35rem;
  list-style: none;
  border: 1px solid rgba(21, 27, 35, 0.12);
  border-radius: 0.82rem;
  background: rgba(255, 253, 247, 0.98);
  box-shadow: 0 8px 24px rgba(28, 35, 43, 0.13);
  backdrop-filter: blur(8px);
  max-height: 14rem;
  overflow-y: auto;
}

.target-ac__item {
  display: flex;
  justify-content: space-between;
  align-items: center;
  gap: 0.5rem;
  padding: 0.52rem 0.7rem;
  border-radius: 0.6rem;
  cursor: pointer;
}

.target-ac__item:hover {
  background: rgba(29, 106, 78, 0.08);
}

.target-ac__name {
  font-size: 0.87rem;
  font-weight: 600;
  color: #23313d;
  min-width: 0;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}

.target-ac__dest {
  font-size: 0.77rem;
  color: #7a8898;
  font-family: monospace;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
  max-width: 12rem;
  flex-shrink: 0;
}

.helper--picked {
  color: #185d45;
  font-family: monospace;
  font-size: 0.82rem;
}
</style>
