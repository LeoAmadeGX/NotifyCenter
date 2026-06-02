import type {
  AdminSession,
  BulkResult,
  NotificationFilterOptions,
  NotificationAttempt,
  NotificationCreateInput,
  NotificationFilters,
  NotificationItem,
  LineSourceItem,
  RoutingTargetInput,
  RoutingTargetItem,
  NotificationStats,
  UpsertResult,
} from "./types";

const apiBase = import.meta.env.BASE_URL.replace(/\/$/, "");

export const adminEventsUrl = `${apiBase}/api/admin/events`;

async function request<T>(path: string, init?: RequestInit): Promise<T> {
  const response = await fetch(apiBase + path, {
    credentials: "include",
    headers: {
      "Content-Type": "application/json",
      ...(init?.headers ?? {}),
    },
    ...init,
  });

  if (response.status === 204) {
    return undefined as T;
  }

  const text = await response.text();
  const body = text ? JSON.parse(text) : null;

  if (!response.ok) {
    throw new Error(body?.error ?? `Request failed with ${response.status}`);
  }

  return body as T;
}

export function login(username: string, password: string) {
  return request<AdminSession>("/api/admin/login", {
    method: "POST",
    body: JSON.stringify({ username, password }),
  });
}

export function logout() {
  return request<void>("/api/admin/logout", { method: "POST" });
}

export function getSession() {
  return request<AdminSession>("/api/admin/session");
}

export function changePassword(
  currentPassword: string,
  newPassword: string,
  confirmPassword: string,
) {
  return request<AdminSession>("/api/admin/change-password", {
    method: "POST",
    body: JSON.stringify({ currentPassword, newPassword, confirmPassword }),
  });
}

export function getNotifications(filters: NotificationFilters) {
  const params = new URLSearchParams();
  if (filters.status) params.set("status", filters.status);
  if (filters.channel) params.set("channel", filters.channel);
  if (filters.sourceSystem) params.set("sourceSystem", filters.sourceSystem);
  if (filters.eventType) params.set("eventType", filters.eventType);
  if (filters.messageQuery) params.set("messageQuery", filters.messageQuery);
  if (filters.scheduledFrom)
    params.set(
      "scheduledFromUtc",
      new Date(filters.scheduledFrom).toISOString(),
    );
  if (filters.scheduledTo)
    params.set("scheduledToUtc", new Date(filters.scheduledTo).toISOString());
  params.set("limit", String(filters.limit));
  return request<{ items: NotificationItem[] }>(
    `/api/notifications?${params.toString()}`,
  );
}

export function getFilterOptions() {
  return request<NotificationFilterOptions>(
    "/api/notifications/filter-options",
  );
}

export function getNotification(id: string) {
  return request<NotificationItem>(`/api/notifications/${id}`);
}

export function getAttempts(id: string) {
  return request<{ items: NotificationAttempt[] }>(
    `/api/notifications/${id}/attempts`,
  );
}

export function getStats() {
  return request<NotificationStats>("/api/notifications/stats");
}

export function createNotification(input: NotificationCreateInput) {
  return request<UpsertResult>("/api/notifications", {
    method: "POST",
    body: JSON.stringify(input),
  });
}

export function createNotificationsBulk(
  notifications: NotificationCreateInput[],
) {
  return request<BulkResult>("/api/notifications/bulk", {
    method: "POST",
    body: JSON.stringify({ notifications }),
  });
}

export function cancelNotification(id: string) {
  return request<{ canceled: boolean }>(`/api/notifications/${id}/cancel`, {
    method: "POST",
  });
}

export function retryNotification(id: string) {
  return request<{ queued: boolean; skipped: boolean; status: string }>(
    `/api/notifications/${id}/retry`,
    {
      method: "POST",
    },
  );
}

export function getRoutingTargets() {
  return request<{ items: RoutingTargetItem[] }>("/api/routing-targets");
}

export function getLineSources() {
  return request<{ items: LineSourceItem[] }>("/api/line-sources");
}

export function createRoutingTarget(input: RoutingTargetInput) {
  return request<RoutingTargetItem>("/api/routing-targets", {
    method: "POST",
    body: JSON.stringify(input),
  });
}

export function updateRoutingTarget(id: string, input: RoutingTargetInput) {
  return request<RoutingTargetItem>(`/api/routing-targets/${id}`, {
    method: "PATCH",
    body: JSON.stringify(input),
  });
}

export function deleteRoutingTarget(id: string) {
  return request<void>(`/api/routing-targets/${id}`, {
    method: "DELETE",
  });
}
