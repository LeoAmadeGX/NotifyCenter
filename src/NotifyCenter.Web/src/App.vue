<script setup lang="ts">
import { computed, onMounted, reactive, ref } from "vue";
import {
  cancelNotification,
  changePassword,
  createNotification,
  createNotificationsBulk,
  getAttempts,
  getFilterOptions,
  getNotification,
  getNotifications,
  getSession,
  getStats,
  login,
  logout,
  retryNotification
} from "./api";
import type {
  AdminSession,
  BulkResult,
  NotificationAttempt,
  NotificationCreateInput,
  NotificationFilterOptions,
  NotificationFilters,
  NotificationItem,
  NotificationStats
} from "./types";

type SidePanel = "preview" | "create" | "batch" | "password";
type SelectOption = { label: string; value: string };

const loading = ref(true);
const saving = ref(false);
const session = ref<AdminSession | null>(null);
const errorMessage = ref("");
const successMessage = ref("");
const sidePanel = ref<SidePanel>("preview");
const notifications = ref<NotificationItem[]>([]);
const attempts = ref<NotificationAttempt[]>([]);
const selectedNotification = ref<NotificationItem | null>(null);
const stats = ref<NotificationStats | null>(null);
const bulkResult = ref<BulkResult | null>(null);
const selectedIds = ref<string[]>([]);
const filterOptions = ref<NotificationFilterOptions>(getFallbackFilterOptions());

const loginForm = reactive({
  username: "amadegx",
  password: ""
});

const filters = reactive<NotificationFilters>({
  status: "",
  channel: "telegram",
  sourceSystem: "",
  eventType: "",
  scheduledFrom: "",
  scheduledTo: "",
  limit: 100
});

const createForm = reactive({
  title: "",
  body: "",
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
const statusOptions = computed(() => [
  { label: "全部", value: "", count: stats.value?.total ?? notifications.value.length },
  { label: "待派送", value: "pending", count: stats.value?.pending ?? 0 },
  { label: "失敗", value: "failed", count: stats.value?.failed ?? 0 },
  { label: "已送達", value: "sent", count: stats.value?.sent ?? 0 },
  { label: "已取消", value: "canceled", count: stats.value?.canceled ?? 0 }
]);
const channelOptions = computed(() =>
  buildSelectOptions(filterOptions.value.channels, ["telegram"], filters.channel)
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

onMounted(async () => {
  await restoreSession();
});

async function restoreSession() {
  loading.value = true;
  errorMessage.value = "";

  try {
    session.value = await getSession();
    applySessionDefaults(session.value, true);
    await loadDashboard();
  } catch {
    session.value = null;
    resetBatchPayload(true);
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
    await loadDashboard();
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
    session.value = null;
    notifications.value = [];
    attempts.value = [];
    selectedNotification.value = null;
    stats.value = null;
    selectedIds.value = [];
    bulkResult.value = null;
    sidePanel.value = "preview";
    filterOptions.value = getFallbackFilterOptions();
    resetCreateForm();
    resetBatchPayload(true);
  } catch (error) {
    errorMessage.value = toMessage(error);
  } finally {
    loading.value = false;
  }
}

async function loadDashboard() {
  await Promise.all([loadStats(), loadNotifications(), loadFilterOptions()]);
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

  const preferredId =
    selectedNotification.value && notifications.value.some((item) => item.id === selectedNotification.value?.id)
      ? selectedNotification.value.id
      : notifications.value[0].id;

  await selectNotification(preferredId, false);
}

async function selectNotification(id: string, focusPreview = true) {
  if (focusPreview) {
    sidePanel.value = "preview";
  }

  errorMessage.value = "";

  try {
    const [item, attemptResponse] = await Promise.all([getNotification(id), getAttempts(id)]);
    selectedNotification.value = item;
    attempts.value = attemptResponse.items;
  } catch (error) {
    errorMessage.value = toMessage(error);
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
    await loadNotifications();
  } catch (error) {
    errorMessage.value = toMessage(error);
  } finally {
    loading.value = false;
  }
}

async function setStatusFilter(value: string) {
  filters.status = value;
  await applyFilters();
}

function openCreatePanel() {
  sidePanel.value = "create";
}

function openBatchPanel() {
  sidePanel.value = "batch";
}

function openPasswordPanel() {
  sidePanel.value = "password";
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
  filters.channel = "telegram";
  filters.sourceSystem = "";
  filters.eventType = "";
  filters.scheduledFrom = "";
  filters.scheduledTo = "";
  filters.limit = 100;
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

async function performBulkCancel() {
  if (cancelableSelectedIds.value.length === 0) {
    errorMessage.value = "目前沒有可批次取消的通知。";
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
      successMessage.value = `已取消 ${canceled} 筆通知。`;
    }

    if (failures.length > 0) {
      errorMessage.value =
        failures.length === 1
          ? failures[0]
          : `有 ${failures.length} 筆取消失敗，第一個錯誤：${failures[0]}`;
    }

    await loadDashboard();
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
  createForm.target = selectedNotification.value.target;
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
    const result = (await createNotification(buildCreatePayload())) as { id?: string };
    successMessage.value = "通知已建立並加入排程。";
    resetCreateForm();
    await loadDashboard();

    if (result.id) {
      await selectNotification(result.id, true);
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
    successMessage.value = "批次通知已送交處理。";
    await loadDashboard();
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
    successMessage.value = "通知已取消。";
    await loadDashboard();
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
    await retryNotification(id);
    successMessage.value = "通知已重新排入派送佇列。";
    await loadDashboard();
  } catch (error) {
    errorMessage.value = toMessage(error);
  } finally {
    saving.value = false;
  }
}

function buildCreatePayload(): NotificationCreateInput {
  const target = createForm.target.trim() || session.value?.telegramDefaultTarget || null;

  return {
    dedupeKey: createForm.dedupeKey || null,
    sourceSystem: createForm.sourceSystem || null,
    eventType: createForm.eventType || null,
    channel: "telegram",
    target,
    title: createForm.title,
    body: createForm.body,
    scheduledAtUtc: new Date(createForm.scheduledAtLocal).toISOString(),
    metadata: parseMetadata(createForm.metadataJson)
  };
}

function applySessionDefaults(currentSession: AdminSession | null, force: boolean) {
  if (!currentSession) {
    return;
  }

  if (force || !createForm.target.trim()) {
    createForm.target = currentSession.telegramDefaultTarget ?? "";
  }

  if (force || !batchForm.payload.trim()) {
    resetBatchPayload(true, currentSession.telegramDefaultTarget);
  }
}

function resetCreateForm() {
  createForm.title = "";
  createForm.body = "";
  createForm.target = session.value?.telegramDefaultTarget ?? "";
  createForm.dedupeKey = "";
  createForm.sourceSystem = "admin-ui";
  createForm.eventType = "manual.notification";
  createForm.scheduledAtLocal = createDefaultSchedule();
  createForm.metadataJson = "";
}

function resetBatchPayload(force: boolean, defaultTarget?: string | null) {
  if (!force && batchForm.payload.trim()) {
    return;
  }

  batchForm.payload = createBatchExample(defaultTarget ?? session.value?.telegramDefaultTarget ?? null);
}

function createBatchExample(defaultTarget: string | null) {
  return JSON.stringify(
    [
      {
        title: "Daily report",
        body: "NotifyCenter has completed the 09:00 summary.",
        channel: "telegram",
        target: defaultTarget ?? "1323447026",
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

    const keys = Object.keys(parsed as Record<string, unknown>);
    if (keys.length === 0) {
      return false;
    }

    if (keys.length === 1 && keys[0] === "priority" && (parsed as { priority?: string }).priority === "normal") {
      return false;
    }

    return true;
  } catch {
    return true;
  }
}

function summarizeBody(value: string) {
  const compact = value.replace(/\s+/g, " ").trim();
  return compact.length > 78 ? `${compact.slice(0, 78)}…` : compact;
}

function statusLabel(status: string) {
  switch (status) {
    case "pending":
      return "待派送";
    case "sent":
      return "已送達";
    case "failed":
      return "失敗";
    case "canceled":
      return "已取消";
    default:
      return status;
  }
}

function canCancelStatus(status: string | undefined) {
  return status === "pending" || status === "failed";
}

function getStatusRank(status: string) {
  switch (status) {
    case "pending":
      return 0;
    case "failed":
      return 1;
    case "sent":
      return 2;
    case "canceled":
      return 3;
    default:
      return 4;
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
    channels: ["telegram"],
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
      <p class="subtitle">先看通知清單，再決定要取消、重送或新增內容。</p>

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
          <p class="subtitle">負責提醒通知的小玩具。</p>
        </div>
        <div class="topbar__actions">
          <button class="button button--ghost button--compact" @click="loadDashboard" :disabled="loading || saving">
            重新整理
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
          <p class="warning-card__body">你現在可以先使用後台，但提醒會持續顯示，直到密碼成功變更為止。</p>
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
          <span>總筆數</span>
          <strong>{{ stats?.total ?? 0 }}</strong>
        </article>
      </section>

      <section class="dashboard">
        <section class="panel queue-panel">
          <div class="panel__header">
            <div>
              <h2>通知清單</h2>
              <p class="panel__hint">待派送會自動排前面，左側清單維持高密度顯示，方便大量資料時快速掃描。</p>
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

          <form class="filter-grid" @submit.prevent="applyFilters">
            <label class="field">
              <span>頻道</span>
              <select v-model="filters.channel">
                <option v-for="option in channelOptions" :key="option.value || 'all'" :value="option.value">
                  {{ option.label }}
                </option>
              </select>
            </label>

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

            <label class="field">
              <span>排程起</span>
              <input v-model="filters.scheduledFrom" type="datetime-local" />
            </label>

            <label class="field">
              <span>排程迄</span>
              <input v-model="filters.scheduledTo" type="datetime-local" />
            </label>

            <div class="filter-actions">
              <button class="button button--primary button--compact" :disabled="loading || saving">套用篩選</button>
              <button class="button button--ghost button--compact" type="button" @click="clearDateRange">清空時間</button>
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
              <span>Target</span>
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
                    <p v-if="item.status === 'failed' && item.lastError" class="queue-row__error">
                      {{ item.lastError }}
                    </p>
                  </div>
                  <div class="queue-row__meta">{{ formatDateCompact(item.scheduledAtUtc) }}</div>
                  <div class="queue-row__meta mono">{{ item.target }}</div>
                  <div class="queue-row__status">
                    <span class="badge" :class="`badge--${item.status}`">{{ statusLabel(item.status) }}</span>
                  </div>
                </button>
              </article>
            </div>

            <div v-else class="empty-state">
              <p>目前沒有符合條件的通知。你可以調整時間區間，或先從右側建立新通知。</p>
            </div>
          </div>
        </section>

        <aside class="side-stack">
          <section v-if="sidePanel === 'preview'" class="panel preview-panel">
            <div class="panel__header">
              <div>
                <h2>{{ selectedNotification ? selectedNotification.title : "通知預覽" }}</h2>
                <p class="panel__hint">右側只保留預覽與操作，不再用大片留白堆疊資訊。</p>
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
                <span class="mini-chip mono">{{ selectedNotification.target }}</span>
                <span class="mini-chip">{{ selectedNotification.channel }}</span>
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
                  取消通知
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
                <summary>派送記錄 <span>{{ attempts.length }}</span></summary>
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
                  <p v-else class="mini-empty">這筆通知目前還沒有派送紀錄。</p>
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
                      <span class="field-label">最後更新</span>
                      <strong>{{ formatDate(selectedNotification.updatedAt) }}</strong>
                    </div>
                  </div>

                  <div v-if="selectedHasMetadata" class="advanced-block">
                    <span class="field-label">Metadata JSON</span>
                    <pre>{{ selectedMetadata }}</pre>
                    <p class="helper">目前 Telegram v1 不會依 metadata 自動套樣式。這塊主要保留給追蹤或未來擴充。</p>
                  </div>
                </div>
              </details>
            </template>

            <div v-else class="empty-state">
              <p>從左側選一筆通知，就能在這裡查看內容與執行操作。</p>
            </div>
          </section>

          <section v-else-if="sidePanel === 'create'" class="panel">
            <div class="panel__header">
              <div>
                <h2>單筆建立通知</h2>
                <p class="panel__hint">把常用欄位放在表面，進階欄位收起來，避免干擾。</p>
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
                  <span>Target Chat ID</span>
                  <input v-model="createForm.target" placeholder="1323447026 or -1001234567890" />
                </label>
                <label>
                  <span>排程時間</span>
                  <input v-model="createForm.scheduledAtLocal" type="datetime-local" required />
                </label>
              </div>

              <details class="foldout">
                <summary>進階欄位</summary>
                <div class="foldout__content stack">
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

                  <p class="helper">Event Type 目前主要用來追蹤來源。Metadata 不會直接改變 Telegram v1 的訊息樣式，普通通知通常可以留空。</p>
                </div>
              </details>

              <button class="button button--primary" :disabled="saving">建立通知</button>
            </form>
          </section>

          <section v-else-if="sidePanel === 'batch'" class="panel">
            <div class="panel__header">
              <div>
                <h2>批次建立</h2>
                <p class="panel__hint">適合匯入多筆通知。若不需要附加資訊，metadata 可以完全省略。</p>
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
                <p class="panel__hint">密碼至少需要 8 個字元。修改成功後，首次登入提醒就會消失。</p>
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
    </main>
  </div>
</template>

<style scoped>
.shell {
  position: relative;
  min-height: 100vh;
  padding: 20px;
  overflow: hidden;
}

.shell__glow {
  position: absolute;
  width: 26rem;
  height: 26rem;
  border-radius: 50%;
  filter: blur(24px);
  opacity: 0.42;
  pointer-events: none;
}

.shell__glow--top {
  top: -11rem;
  right: -8rem;
  background: rgba(255, 177, 91, 0.24);
}

.shell__glow--bottom {
  bottom: -11rem;
  left: -9rem;
  background: rgba(33, 101, 85, 0.14);
}

.login-card,
.workspace {
  position: relative;
  z-index: 1;
}

.login-card {
  max-width: 32rem;
  margin: 8vh auto;
  padding: 2rem;
  border: 1px solid rgba(21, 27, 35, 0.12);
  border-radius: 1.35rem;
  background: rgba(255, 252, 245, 0.9);
  box-shadow: 0 28px 80px rgba(40, 51, 62, 0.11);
  backdrop-filter: blur(16px);
}

.workspace {
  display: grid;
  gap: 0.85rem;
}

.topbar,
.warning-card,
.summary-card,
.panel {
  border: 1px solid rgba(21, 27, 35, 0.1);
  background: rgba(255, 251, 244, 0.9);
  box-shadow: 0 18px 36px rgba(28, 35, 43, 0.06);
  backdrop-filter: blur(12px);
}

.topbar {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 1rem;
  padding: 1.15rem 1.35rem;
  border-radius: 1.35rem;
}

.topbar__actions,
.panel__actions,
.action-row,
.two-up,
.bulk-bar,
.bulk-bar__group,
.filter-actions {
  display: flex;
  gap: 0.6rem;
}

.topbar__actions,
.panel__actions,
.bulk-bar,
.bulk-bar__group,
.filter-actions {
  flex-wrap: wrap;
}

.eyebrow {
  margin: 0 0 0.25rem;
  color: #8a5223;
  text-transform: uppercase;
  letter-spacing: 0.16em;
  font-size: 0.76rem;
  font-weight: 700;
}

h1,
h2,
h3,
p {
  margin: 0;
}

h1 {
  font-size: clamp(1.75rem, 3vw, 2.35rem);
  line-height: 1.06;
}

h2 {
  font-size: 1.08rem;
}

h3 {
  font-size: 1rem;
}

.subtitle,
.panel__hint,
.helper,
.hint,
.selection-note--muted {
  color: #5d6976;
}

.subtitle {
  margin-top: 0.45rem;
  max-width: 45rem;
}

.warning-card {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 1rem;
  padding: 0.85rem 1rem;
  border-radius: 1.1rem;
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
  gap: 0.65rem;
}

.message {
  padding: 0.8rem 0.95rem;
  border-radius: 0.95rem;
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
  grid-template-columns: repeat(5, minmax(0, 1fr));
  gap: 0.7rem;
}

.summary-card {
  display: grid;
  gap: 0.15rem;
  padding: 0.8rem 0.9rem;
  border-radius: 1rem;
}

.summary-card--accent {
  background: linear-gradient(135deg, rgba(29, 106, 78, 0.18), rgba(255, 251, 244, 0.96));
}

.summary-card span {
  color: #54616f;
  font-size: 0.88rem;
}

.summary-card strong {
  font-size: 1.55rem;
  line-height: 1;
}

.dashboard {
  display: grid;
  grid-template-columns: minmax(0, 1.6fr) minmax(22rem, 0.95fr);
  gap: 0.85rem;
  align-items: start;
}

.panel {
  padding: 1rem;
  border-radius: 1.25rem;
}

.queue-panel {
  display: grid;
  gap: 0.8rem;
}

.side-stack {
  display: grid;
  gap: 0.85rem;
  position: sticky;
  top: 1rem;
}

.panel__header {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 0.75rem;
}

.status-tabs {
  display: flex;
  flex-wrap: wrap;
  gap: 0.45rem;
}

.status-tab {
  display: inline-flex;
  align-items: center;
  gap: 0.4rem;
  padding: 0.45rem 0.7rem;
  border: 1px solid rgba(21, 27, 35, 0.1);
  border-radius: 999px;
  background: rgba(255, 255, 255, 0.8);
  color: #23313d;
  font-size: 0.92rem;
}

.status-tab strong {
  color: #1f5b4c;
}

.status-tab--active {
  background: rgba(31, 91, 76, 0.12);
  border-color: rgba(31, 91, 76, 0.3);
}

.filter-grid {
  display: grid;
  grid-template-columns: repeat(6, minmax(0, 1fr));
  gap: 0.65rem;
  align-items: end;
}

.field {
  display: grid;
  gap: 0.28rem;
}

.field span,
label span {
  color: #445261;
  font-size: 0.82rem;
  font-weight: 700;
}

.field--limit {
  min-width: 0;
}

.filter-actions {
  align-items: flex-end;
  justify-content: flex-end;
  grid-column: 5 / span 2;
}

.stepper {
  display: grid;
  grid-template-columns: minmax(0, 1fr) 2rem;
  overflow: hidden;
  border: 1px solid rgba(21, 27, 35, 0.14);
  border-radius: 0.85rem;
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
  padding: 0.55rem 0.75rem;
  border: 1px solid rgba(21, 27, 35, 0.08);
  border-radius: 0.95rem;
  background: rgba(255, 255, 255, 0.62);
}

.selection-note {
  color: #2b3742;
  font-size: 0.88rem;
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
  grid-template-columns: 2.2rem minmax(0, 1.65fr) 8rem minmax(9rem, 1fr) 6rem;
  gap: 0.75rem;
  padding: 0.55rem 0.85rem;
  background: rgba(245, 248, 246, 0.96);
  color: #63707b;
  font-size: 0.78rem;
  font-weight: 700;
  text-transform: uppercase;
  letter-spacing: 0.05em;
}

.queue-list {
  max-height: calc(100vh - 23rem);
  overflow: auto;
}

.queue-row {
  display: grid;
  grid-template-columns: 2.2rem minmax(0, 1fr);
  gap: 0.75rem;
  padding: 0.2rem 0.85rem;
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
  grid-template-columns: minmax(0, 1.65fr) 8rem minmax(9rem, 1fr) 6rem;
  gap: 0.75rem;
  align-items: center;
  padding: 0.65rem 0;
  border: 0;
  background: transparent;
  text-align: left;
}

.queue-row__title {
  min-width: 0;
}

.queue-row__title strong {
  display: block;
  font-size: 0.98rem;
}

.queue-row__title p {
  color: #53616e;
  font-size: 0.88rem;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.queue-row__error {
  color: #a13c2f;
  margin-top: 0.16rem;
}

.queue-row__meta,
.queue-row__status {
  color: #42515e;
  font-size: 0.88rem;
}

.queue-row__status {
  display: flex;
  justify-content: flex-end;
}

.preview-panel {
  display: grid;
  gap: 0.8rem;
}

.preview-strip {
  display: flex;
  flex-wrap: wrap;
  gap: 0.45rem;
  align-items: center;
}

.mini-chip {
  display: inline-flex;
  align-items: center;
  padding: 0.28rem 0.55rem;
  border-radius: 999px;
  background: rgba(255, 255, 255, 0.8);
  border: 1px solid rgba(21, 27, 35, 0.08);
  color: #394652;
  font-size: 0.82rem;
}

.message-preview {
  display: grid;
  gap: 0.45rem;
  padding: 0.9rem 1rem;
  border-radius: 1rem;
  background: linear-gradient(135deg, rgba(255, 255, 255, 0.96), rgba(240, 248, 244, 0.94));
  border: 1px solid rgba(31, 91, 76, 0.12);
}

.message-preview p {
  white-space: pre-wrap;
}

.badge {
  display: inline-flex;
  align-items: center;
  padding: 0.26rem 0.55rem;
  border-radius: 999px;
  font-size: 0.76rem;
  font-weight: 700;
}

.badge--pending {
  background: rgba(255, 215, 130, 0.35);
  color: #7d5204;
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
  padding: 0.75rem 0.9rem;
  font-weight: 700;
  cursor: pointer;
}

.foldout__content {
  padding: 0 0.9rem 0.9rem;
}

.advanced-grid {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  gap: 0.75rem;
}

.field-label {
  display: block;
  margin-bottom: 0.22rem;
  color: #687684;
  font-size: 0.76rem;
  text-transform: uppercase;
  letter-spacing: 0.08em;
}

.advanced-block {
  margin-top: 0.85rem;
  padding: 0.9rem;
  border-radius: 0.95rem;
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
  padding: 0.65rem 0.45rem;
  border-bottom: 1px solid rgba(21, 27, 35, 0.1);
  text-align: left;
  vertical-align: top;
  font-size: 0.88rem;
}

.stack,
label {
  display: grid;
  gap: 0.5rem;
}

input,
select,
textarea {
  width: 100%;
  padding: 0.68rem 0.8rem;
  border: 1px solid rgba(21, 27, 35, 0.14);
  border-radius: 0.85rem;
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
  min-height: 2.4rem;
  padding: 0.65rem 0.95rem;
  border: 1px solid rgba(21, 27, 35, 0.12);
  border-radius: 999px;
  background: rgba(255, 255, 255, 0.8);
  color: #151b23;
}

.button--compact {
  min-height: 2rem;
  padding: 0.45rem 0.8rem;
  font-size: 0.88rem;
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
  gap: 0.75rem;
  padding: 0.95rem;
  border-radius: 1rem;
  background: rgba(18, 27, 35, 0.95);
  color: #f3f7fb;
}

.result-card__summary {
  display: flex;
  flex-wrap: wrap;
  justify-content: space-between;
  gap: 0.75rem;
}

.mono {
  font-family: Consolas, "Cascadia Code", monospace;
}

code {
  padding: 0.18rem 0.38rem;
  border-radius: 0.45rem;
  background: rgba(21, 27, 35, 0.08);
}

@media (max-width: 1440px) {
  .filter-grid {
    grid-template-columns: repeat(4, minmax(0, 1fr));
  }

  .filter-actions {
    grid-column: 1 / -1;
    justify-content: flex-start;
  }
}

@media (max-width: 1280px) {
  .dashboard {
    grid-template-columns: 1fr;
  }

  .side-stack {
    position: static;
  }

  .summary-strip {
    grid-template-columns: repeat(3, minmax(0, 1fr));
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
    padding-top: 0.45rem;
  }

  .queue-row__status {
    justify-content: flex-start;
  }
}

@media (max-width: 760px) {
  .shell {
    padding: 14px;
  }

  .topbar,
  .warning-card,
  .panel__header,
  .topbar__actions,
  .panel__actions,
  .two-up,
  .action-row,
  .advanced-grid,
  .result-card__summary,
  .bulk-bar,
  .bulk-bar__group {
    display: grid;
  }

  .summary-strip {
    grid-template-columns: repeat(2, minmax(0, 1fr));
  }

  .filter-grid {
    grid-template-columns: 1fr;
  }

  .filter-actions {
    grid-column: auto;
  }
}
</style>
