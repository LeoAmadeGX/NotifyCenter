export interface AdminSession {
  username: string;
  mustChangePassword: boolean;
  telegramDefaultTarget: string | null;
}

export interface NotificationItem {
  id: string;
  notificationId: string;
  dedupeKey: string;
  sourceSystem: string;
  eventType: string;
  channel: string;
  targetName: string | null;
  target: string | null;
  isTargetOverride: boolean;
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
  skippedAtUtc: string | null;
}

export interface NotificationAttempt {
  id: string;
  deliveryId: string;
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
  pendingNoTarget: number;
  sent: number;
  failed: number;
  canceled: number;
  skipped: number;
  due: number;
}

export interface UpsertResult {
  notificationId: string;
  deliveryId: string | null;
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
  messageQuery: string;
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

export interface LineSourceItem {
  id: string;
  sourceType: string;
  sourceId: string;
  displayName: string | null;
  pictureUrl: string | null;
  statusMessage: string | null;
  lastEventType: string | null;
  lastEventAtUtc: string | null;
  firstSeenAtUtc: string;
  updatedAt: string;
  metadataJson: string;
  routingTargetId: string | null;
  routingTargetName: string | null;
  routingTargetEnabled: boolean | null;
}

export interface RoutingTargetItem {
  id: string;
  channel: string;
  name: string;
  destination: string;
  isEnabled: boolean;
  sortOrder: number;
  metadataJson: string;
  createdAt: string;
  updatedAt: string;
}

export interface RoutingTargetInput {
  channel: string;
  name: string;
  destination: string;
  isEnabled: boolean;
  sortOrder: number;
  metadata?: Record<string, unknown> | null;
}
