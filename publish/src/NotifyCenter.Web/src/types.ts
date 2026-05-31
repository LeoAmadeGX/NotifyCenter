export interface AdminSession {
  username: string;
  mustChangePassword: boolean;
  telegramDefaultTarget: string | null;
}

export interface NotificationItem {
  id: string;
  dedupeKey: string;
  sourceSystem: string;
  eventType: string;
  channel: string;
  target: string;
  title: string;
  body: string;
  scheduledAtUtc: string;
  status: string;
  metadataJson: string;
  lastError: string | null;
  createdAt: string;
  updatedAt: string;
  sentAtUtc: string | null;
  canceledAtUtc: string | null;
}

export interface NotificationAttempt {
  id: string;
  notificationId: string;
  attemptedAtUtc: string;
  status: string;
  httpStatus: number | null;
  responseBody: string | null;
  error: string | null;
}

export interface NotificationStats {
  total: number;
  pending: number;
  sent: number;
  failed: number;
  canceled: number;
  due: number;
}

export interface UpsertResult {
  id: string;
  dedupeKey: string;
  action: string;
  status: string;
}

export interface BulkResult {
  accepted: number;
  created: number;
  updated: number;
  skipped: number;
  items: UpsertResult[];
}

export interface NotificationFilterOptions {
  channels: string[];
  sourceSystems: string[];
  eventTypes: string[];
}

export interface NotificationFilters {
  status: string;
  channel: string;
  sourceSystem: string;
  eventType: string;
  scheduledFrom: string;
  scheduledTo: string;
  limit: number;
}

export interface NotificationCreateInput {
  dedupeKey?: string | null;
  sourceSystem?: string | null;
  eventType?: string | null;
  channel?: string | null;
  target?: string | null;
  title: string;
  body: string;
  scheduledAtUtc: string;
  metadata?: Record<string, unknown> | null;
}
