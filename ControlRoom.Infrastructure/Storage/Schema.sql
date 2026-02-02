PRAGMA foreign_keys = ON;

CREATE TABLE IF NOT EXISTS things (
  thing_id TEXT PRIMARY KEY,
  name TEXT NOT NULL,
  kind INTEGER NOT NULL,
  config_json TEXT NOT NULL,
  created_at TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS runs (
  run_id TEXT PRIMARY KEY,
  thing_id TEXT NOT NULL,
  started_at TEXT NOT NULL,
  ended_at TEXT NULL,
  status INTEGER NOT NULL,
  exit_code INTEGER NULL,
  summary TEXT NULL,
  FOREIGN KEY (thing_id) REFERENCES things(thing_id)
);

-- Event log backbone
CREATE TABLE IF NOT EXISTS run_events (
  seq INTEGER PRIMARY KEY AUTOINCREMENT,
  run_id TEXT NOT NULL,
  at TEXT NOT NULL,
  kind INTEGER NOT NULL,
  payload_json TEXT NOT NULL,
  FOREIGN KEY (run_id) REFERENCES runs(run_id)
);

CREATE INDEX IF NOT EXISTS idx_run_events_run_id_seq ON run_events(run_id, seq);
CREATE INDEX IF NOT EXISTS idx_runs_started_at ON runs(started_at DESC);

CREATE TABLE IF NOT EXISTS artifacts (
  artifact_id TEXT PRIMARY KEY,
  run_id TEXT NOT NULL,
  media_type TEXT NOT NULL,
  locator TEXT NOT NULL,
  sha256_hex TEXT NULL,
  created_at TEXT NOT NULL,
  FOREIGN KEY (run_id) REFERENCES runs(run_id)
);

-- App settings (key-value store)
CREATE TABLE IF NOT EXISTS settings (
  key TEXT PRIMARY KEY,
  value_json TEXT NOT NULL,
  updated_at TEXT NOT NULL
);

-- =====================================================
-- RUNBOOK AUTOMATION ENGINE (Phase 2)
-- =====================================================

-- Runbooks: Multi-step workflow definitions
CREATE TABLE IF NOT EXISTS runbooks (
  runbook_id TEXT PRIMARY KEY,
  name TEXT NOT NULL,
  description TEXT NOT NULL DEFAULT '',
  config_json TEXT NOT NULL,  -- RunbookConfig serialized
  is_enabled INTEGER NOT NULL DEFAULT 1,
  created_at TEXT NOT NULL,
  updated_at TEXT NOT NULL,
  version INTEGER NOT NULL DEFAULT 1
);

CREATE INDEX IF NOT EXISTS idx_runbooks_name ON runbooks(name);
CREATE INDEX IF NOT EXISTS idx_runbooks_enabled ON runbooks(is_enabled);

-- Runbook executions: Each time a runbook runs
CREATE TABLE IF NOT EXISTS runbook_executions (
  execution_id TEXT PRIMARY KEY,
  runbook_id TEXT NOT NULL,
  status INTEGER NOT NULL,  -- RunbookExecutionStatus
  started_at TEXT NOT NULL,
  ended_at TEXT NULL,
  trigger_info TEXT NULL,   -- JSON: what triggered this execution
  error_message TEXT NULL,
  FOREIGN KEY (runbook_id) REFERENCES runbooks(runbook_id)
);

CREATE INDEX IF NOT EXISTS idx_runbook_executions_runbook_id ON runbook_executions(runbook_id);
CREATE INDEX IF NOT EXISTS idx_runbook_executions_started_at ON runbook_executions(started_at DESC);
CREATE INDEX IF NOT EXISTS idx_runbook_executions_status ON runbook_executions(status);

-- Step executions: Each step within a runbook execution
CREATE TABLE IF NOT EXISTS step_executions (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  execution_id TEXT NOT NULL,
  step_id TEXT NOT NULL,
  step_name TEXT NOT NULL,
  run_id TEXT NULL,           -- Links to runs table if a script was executed
  status INTEGER NOT NULL,    -- StepExecutionStatus
  started_at TEXT NULL,
  ended_at TEXT NULL,
  attempt INTEGER NOT NULL DEFAULT 1,
  error_message TEXT NULL,
  output TEXT NULL,           -- Captured output/result
  FOREIGN KEY (execution_id) REFERENCES runbook_executions(execution_id),
  FOREIGN KEY (run_id) REFERENCES runs(run_id)
);

CREATE INDEX IF NOT EXISTS idx_step_executions_execution_id ON step_executions(execution_id);
CREATE INDEX IF NOT EXISTS idx_step_executions_step_id ON step_executions(step_id);

-- Trigger history: When triggers fire
CREATE TABLE IF NOT EXISTS trigger_history (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  runbook_id TEXT NOT NULL,
  trigger_type INTEGER NOT NULL,  -- TriggerType
  fired_at TEXT NOT NULL,
  execution_id TEXT NULL,         -- Resulting execution (if created)
  payload_json TEXT NULL,         -- Trigger-specific data (webhook body, file changes, etc.)
  FOREIGN KEY (runbook_id) REFERENCES runbooks(runbook_id),
  FOREIGN KEY (execution_id) REFERENCES runbook_executions(execution_id)
);

CREATE INDEX IF NOT EXISTS idx_trigger_history_runbook_id ON trigger_history(runbook_id);
CREATE INDEX IF NOT EXISTS idx_trigger_history_fired_at ON trigger_history(fired_at DESC);

-- =====================================================
-- OBSERVABILITY & SELF-HEALING (Phase 3)
-- =====================================================

-- Metrics: Time-series data points
CREATE TABLE IF NOT EXISTS metrics (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  name TEXT NOT NULL,
  type INTEGER NOT NULL,          -- MetricType
  value REAL NOT NULL,
  timestamp TEXT NOT NULL,
  tags_json TEXT NOT NULL DEFAULT '{}'
);

CREATE INDEX IF NOT EXISTS idx_metrics_name_ts ON metrics(name, timestamp DESC);
CREATE INDEX IF NOT EXISTS idx_metrics_timestamp ON metrics(timestamp DESC);

-- Metric aggregates: Pre-computed rollups for faster queries
CREATE TABLE IF NOT EXISTS metric_aggregates (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  name TEXT NOT NULL,
  window_start TEXT NOT NULL,
  window_end TEXT NOT NULL,
  resolution TEXT NOT NULL,       -- e.g., '1m', '5m', '1h', '1d'
  count INTEGER NOT NULL,
  min REAL NOT NULL,
  max REAL NOT NULL,
  sum REAL NOT NULL,
  avg REAL NOT NULL,
  p50 REAL NOT NULL,
  p90 REAL NOT NULL,
  p99 REAL NOT NULL,
  variance REAL NOT NULL DEFAULT 0,
  tags_json TEXT NOT NULL DEFAULT '{}'
);

CREATE INDEX IF NOT EXISTS idx_metric_aggregates_name_window ON metric_aggregates(name, window_start DESC);
CREATE UNIQUE INDEX IF NOT EXISTS idx_metric_aggregates_unique ON metric_aggregates(name, window_start, resolution, tags_json);

-- Alert rules: Define conditions that trigger alerts
CREATE TABLE IF NOT EXISTS alert_rules (
  rule_id TEXT PRIMARY KEY,
  name TEXT NOT NULL,
  description TEXT NOT NULL DEFAULT '',
  metric_name TEXT NOT NULL,
  condition INTEGER NOT NULL,     -- AlertCondition
  threshold REAL NOT NULL,
  evaluation_window_ms INTEGER NOT NULL,
  cooldown_ms INTEGER NOT NULL,
  severity INTEGER NOT NULL,      -- AlertSeverity
  is_enabled INTEGER NOT NULL DEFAULT 1,
  tags_json TEXT NOT NULL DEFAULT '{}',
  actions_json TEXT NOT NULL DEFAULT '[]',
  created_at TEXT NOT NULL,
  updated_at TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_alert_rules_metric ON alert_rules(metric_name);
CREATE INDEX IF NOT EXISTS idx_alert_rules_enabled ON alert_rules(is_enabled);

-- Alerts: Fired alert instances
CREATE TABLE IF NOT EXISTS alerts (
  alert_id TEXT PRIMARY KEY,
  rule_id TEXT NOT NULL,
  rule_name TEXT NOT NULL,
  severity INTEGER NOT NULL,
  message TEXT NOT NULL,
  current_value REAL NOT NULL,
  threshold REAL NOT NULL,
  fired_at TEXT NOT NULL,
  resolved_at TEXT NULL,
  status INTEGER NOT NULL,        -- AlertStatus
  tags_json TEXT NOT NULL DEFAULT '{}',
  FOREIGN KEY (rule_id) REFERENCES alert_rules(rule_id)
);

CREATE INDEX IF NOT EXISTS idx_alerts_rule_id ON alerts(rule_id);
CREATE INDEX IF NOT EXISTS idx_alerts_status ON alerts(status);
CREATE INDEX IF NOT EXISTS idx_alerts_fired_at ON alerts(fired_at DESC);
CREATE INDEX IF NOT EXISTS idx_alerts_severity ON alerts(severity);

-- Health checks: Service/endpoint health monitoring
CREATE TABLE IF NOT EXISTS health_checks (
  check_id TEXT PRIMARY KEY,
  name TEXT NOT NULL,
  description TEXT NOT NULL DEFAULT '',
  type INTEGER NOT NULL,          -- HealthCheckType
  config_json TEXT NOT NULL,
  interval_ms INTEGER NOT NULL,
  timeout_ms INTEGER NOT NULL,
  is_enabled INTEGER NOT NULL DEFAULT 1,
  created_at TEXT NOT NULL,
  updated_at TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_health_checks_enabled ON health_checks(is_enabled);

-- Health check results: History of check executions
CREATE TABLE IF NOT EXISTS health_check_results (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  check_id TEXT NOT NULL,
  check_name TEXT NOT NULL,
  status INTEGER NOT NULL,        -- HealthStatus
  checked_at TEXT NOT NULL,
  response_time_ms INTEGER NOT NULL,
  message TEXT NULL,
  details_json TEXT NULL,
  FOREIGN KEY (check_id) REFERENCES health_checks(check_id)
);

CREATE INDEX IF NOT EXISTS idx_health_check_results_check_id ON health_check_results(check_id, checked_at DESC);
CREATE INDEX IF NOT EXISTS idx_health_check_results_checked_at ON health_check_results(checked_at DESC);

-- Self-healing rules: Automated remediation
CREATE TABLE IF NOT EXISTS self_healing_rules (
  rule_id TEXT PRIMARY KEY,
  name TEXT NOT NULL,
  description TEXT NOT NULL DEFAULT '',
  trigger_condition TEXT NOT NULL,  -- Expression to evaluate
  remediation_runbook_id TEXT NOT NULL,
  max_executions_per_hour INTEGER NOT NULL DEFAULT 3,
  cooldown_ms INTEGER NOT NULL,
  requires_approval INTEGER NOT NULL DEFAULT 0,
  is_enabled INTEGER NOT NULL DEFAULT 1,
  created_at TEXT NOT NULL,
  updated_at TEXT NOT NULL,
  FOREIGN KEY (remediation_runbook_id) REFERENCES runbooks(runbook_id)
);

CREATE INDEX IF NOT EXISTS idx_self_healing_rules_enabled ON self_healing_rules(is_enabled);

-- Self-healing executions: Record of remediation attempts
CREATE TABLE IF NOT EXISTS self_healing_executions (
  execution_id TEXT PRIMARY KEY,
  rule_id TEXT NOT NULL,
  triggering_alert_id TEXT NULL,
  remediation_execution_id TEXT NULL,
  status INTEGER NOT NULL,        -- SelfHealingStatus
  started_at TEXT NOT NULL,
  completed_at TEXT NULL,
  result TEXT NULL,
  FOREIGN KEY (rule_id) REFERENCES self_healing_rules(rule_id),
  FOREIGN KEY (triggering_alert_id) REFERENCES alerts(alert_id),
  FOREIGN KEY (remediation_execution_id) REFERENCES runbook_executions(execution_id)
);

CREATE INDEX IF NOT EXISTS idx_self_healing_executions_rule_id ON self_healing_executions(rule_id);
CREATE INDEX IF NOT EXISTS idx_self_healing_executions_started_at ON self_healing_executions(started_at DESC);

-- ============================================================================
-- TEAM COLLABORATION (Phase 4)
-- ============================================================================

-- Users table
CREATE TABLE IF NOT EXISTS users (
  id TEXT PRIMARY KEY,
  username TEXT NOT NULL UNIQUE,
  display_name TEXT NOT NULL,
  email TEXT NOT NULL,
  role TEXT NOT NULL DEFAULT 'User',
  created_at TEXT NOT NULL,
  last_login_at TEXT NULL,
  preferences TEXT NOT NULL DEFAULT '{}'
);

CREATE INDEX IF NOT EXISTS idx_users_username ON users(username);
CREATE INDEX IF NOT EXISTS idx_users_email ON users(email);

-- Teams table
CREATE TABLE IF NOT EXISTS teams (
  id TEXT PRIMARY KEY,
  name TEXT NOT NULL,
  description TEXT NULL,
  owner_id TEXT NOT NULL,
  created_at TEXT NOT NULL,
  updated_at TEXT NULL,
  settings TEXT NOT NULL DEFAULT '{}',
  FOREIGN KEY (owner_id) REFERENCES users(id)
);

CREATE INDEX IF NOT EXISTS idx_teams_owner ON teams(owner_id);
CREATE INDEX IF NOT EXISTS idx_teams_name ON teams(name);

-- Team memberships
CREATE TABLE IF NOT EXISTS team_memberships (
  id TEXT PRIMARY KEY,
  team_id TEXT NOT NULL,
  user_id TEXT NOT NULL,
  role TEXT NOT NULL DEFAULT 'Member',
  added_by TEXT NOT NULL,
  joined_at TEXT NOT NULL,
  FOREIGN KEY (team_id) REFERENCES teams(id),
  FOREIGN KEY (user_id) REFERENCES users(id),
  FOREIGN KEY (added_by) REFERENCES users(id),
  UNIQUE(team_id, user_id)
);

CREATE INDEX IF NOT EXISTS idx_memberships_team ON team_memberships(team_id);
CREATE INDEX IF NOT EXISTS idx_memberships_user ON team_memberships(user_id);

-- Team invitations
CREATE TABLE IF NOT EXISTS team_invitations (
  id TEXT PRIMARY KEY,
  team_id TEXT NOT NULL,
  email TEXT NOT NULL,
  invited_user_id TEXT NULL,
  role TEXT NOT NULL DEFAULT 'Member',
  invited_by TEXT NOT NULL,
  created_at TEXT NOT NULL,
  expires_at TEXT NOT NULL,
  status TEXT NOT NULL DEFAULT 'Pending',
  FOREIGN KEY (team_id) REFERENCES teams(id),
  FOREIGN KEY (invited_user_id) REFERENCES users(id),
  FOREIGN KEY (invited_by) REFERENCES users(id)
);

CREATE INDEX IF NOT EXISTS idx_invitations_team ON team_invitations(team_id);
CREATE INDEX IF NOT EXISTS idx_invitations_email ON team_invitations(email);
CREATE INDEX IF NOT EXISTS idx_invitations_status ON team_invitations(status);

-- Shared resources
CREATE TABLE IF NOT EXISTS shared_resources (
  id TEXT PRIMARY KEY,
  resource_type TEXT NOT NULL,
  resource_id TEXT NOT NULL,
  owner_id TEXT NOT NULL,
  shared_with_team_id TEXT NULL,
  shared_with_user_id TEXT NULL,
  created_at TEXT NOT NULL,
  FOREIGN KEY (owner_id) REFERENCES users(id),
  FOREIGN KEY (shared_with_team_id) REFERENCES teams(id),
  FOREIGN KEY (shared_with_user_id) REFERENCES users(id)
);

CREATE INDEX IF NOT EXISTS idx_shared_resources_owner ON shared_resources(owner_id);
CREATE INDEX IF NOT EXISTS idx_shared_resources_team ON shared_resources(shared_with_team_id);
CREATE INDEX IF NOT EXISTS idx_shared_resources_user ON shared_resources(shared_with_user_id);
CREATE INDEX IF NOT EXISTS idx_shared_resources_type ON shared_resources(resource_type, resource_id);

-- Resource permissions
CREATE TABLE IF NOT EXISTS resource_permissions (
  shared_resource_id TEXT NOT NULL,
  user_id TEXT NOT NULL,
  permission_level TEXT NOT NULL,
  granted_at TEXT NOT NULL,
  granted_by TEXT NOT NULL,
  PRIMARY KEY (shared_resource_id, user_id),
  FOREIGN KEY (shared_resource_id) REFERENCES shared_resources(id),
  FOREIGN KEY (user_id) REFERENCES users(id),
  FOREIGN KEY (granted_by) REFERENCES users(id)
);

CREATE INDEX IF NOT EXISTS idx_permissions_user ON resource_permissions(user_id);

-- Activity log (audit trail)
CREATE TABLE IF NOT EXISTS activity_log (
  id TEXT PRIMARY KEY,
  user_id TEXT NOT NULL,
  activity_type TEXT NOT NULL,
  description TEXT NOT NULL,
  team_id TEXT NULL,
  resource_id TEXT NULL,
  target_user_id TEXT NULL,
  comment_id TEXT NULL,
  occurred_at TEXT NOT NULL,
  metadata TEXT NULL,
  FOREIGN KEY (user_id) REFERENCES users(id),
  FOREIGN KEY (team_id) REFERENCES teams(id),
  FOREIGN KEY (target_user_id) REFERENCES users(id)
);

CREATE INDEX IF NOT EXISTS idx_activity_user ON activity_log(user_id);
CREATE INDEX IF NOT EXISTS idx_activity_team ON activity_log(team_id);
CREATE INDEX IF NOT EXISTS idx_activity_occurred ON activity_log(occurred_at DESC);
CREATE INDEX IF NOT EXISTS idx_activity_type ON activity_log(activity_type);

-- Comments on resources
CREATE TABLE IF NOT EXISTS comments (
  id TEXT PRIMARY KEY,
  resource_id TEXT NOT NULL,
  author_id TEXT NOT NULL,
  content TEXT NOT NULL,
  created_at TEXT NOT NULL,
  edited_at TEXT NULL,
  parent_id TEXT NULL,
  mentions TEXT NOT NULL DEFAULT '[]',
  reactions TEXT NOT NULL DEFAULT '{}',
  FOREIGN KEY (resource_id) REFERENCES shared_resources(id),
  FOREIGN KEY (author_id) REFERENCES users(id),
  FOREIGN KEY (parent_id) REFERENCES comments(id)
);

CREATE INDEX IF NOT EXISTS idx_comments_resource ON comments(resource_id);
CREATE INDEX IF NOT EXISTS idx_comments_author ON comments(author_id);
CREATE INDEX IF NOT EXISTS idx_comments_parent ON comments(parent_id);
CREATE INDEX IF NOT EXISTS idx_comments_created ON comments(created_at DESC);

-- Annotations (highlights, bookmarks, etc.)
CREATE TABLE IF NOT EXISTS annotations (
  id TEXT PRIMARY KEY,
  resource_id TEXT NOT NULL,
  author_id TEXT NOT NULL,
  annotation_type TEXT NOT NULL,
  content TEXT NOT NULL,
  start_position INTEGER NOT NULL,
  length INTEGER NOT NULL,
  created_at TEXT NOT NULL,
  updated_at TEXT NULL,
  color TEXT NULL,
  metadata TEXT NULL,
  FOREIGN KEY (resource_id) REFERENCES shared_resources(id),
  FOREIGN KEY (author_id) REFERENCES users(id)
);

CREATE INDEX IF NOT EXISTS idx_annotations_resource ON annotations(resource_id);
CREATE INDEX IF NOT EXISTS idx_annotations_author ON annotations(author_id);
CREATE INDEX IF NOT EXISTS idx_annotations_type ON annotations(annotation_type);

-- Notifications
CREATE TABLE IF NOT EXISTS notifications (
  id TEXT PRIMARY KEY,
  user_id TEXT NOT NULL,
  notification_type TEXT NOT NULL,
  message TEXT NOT NULL,
  is_read INTEGER NOT NULL DEFAULT 0,
  team_id TEXT NULL,
  resource_id TEXT NULL,
  comment_id TEXT NULL,
  created_at TEXT NOT NULL,
  read_at TEXT NULL,
  metadata TEXT NULL,
  FOREIGN KEY (user_id) REFERENCES users(id),
  FOREIGN KEY (team_id) REFERENCES teams(id),
  FOREIGN KEY (comment_id) REFERENCES comments(id)
);

CREATE INDEX IF NOT EXISTS idx_notifications_user ON notifications(user_id);
CREATE INDEX IF NOT EXISTS idx_notifications_unread ON notifications(user_id, is_read);
CREATE INDEX IF NOT EXISTS idx_notifications_created ON notifications(created_at DESC);

-- ============================================================================
-- INTEGRATION HUB (Phase 5)
-- ============================================================================

-- Integration definitions (templates/blueprints)
CREATE TABLE IF NOT EXISTS integrations (
  id TEXT PRIMARY KEY,
  name TEXT NOT NULL UNIQUE,
  display_name TEXT NOT NULL,
  description TEXT NOT NULL DEFAULT '',
  category TEXT NOT NULL,
  auth_method TEXT NOT NULL,
  icon_url TEXT NULL,
  documentation_url TEXT NULL,
  is_built_in INTEGER NOT NULL DEFAULT 0,
  is_enabled INTEGER NOT NULL DEFAULT 1,
  capabilities_json TEXT NOT NULL DEFAULT '{}',
  config_json TEXT NOT NULL DEFAULT '{}',
  created_at TEXT NOT NULL,
  updated_at TEXT NULL
);

CREATE INDEX IF NOT EXISTS idx_integrations_category ON integrations(category);
CREATE INDEX IF NOT EXISTS idx_integrations_enabled ON integrations(is_enabled);

-- Integration instances (connected accounts)
CREATE TABLE IF NOT EXISTS integration_instances (
  id TEXT PRIMARY KEY,
  integration_id TEXT NOT NULL,
  owner_id TEXT NOT NULL,
  team_id TEXT NULL,
  name TEXT NOT NULL,
  display_name TEXT NOT NULL,
  status TEXT NOT NULL DEFAULT 'Pending',
  health TEXT NOT NULL DEFAULT 'Unknown',
  configuration_json TEXT NOT NULL DEFAULT '{}',
  credentials_json TEXT NULL,
  created_at TEXT NOT NULL,
  connected_at TEXT NULL,
  last_sync_at TEXT NULL,
  last_health_check_at TEXT NULL,
  last_error TEXT NULL,
  metadata_json TEXT NULL,
  FOREIGN KEY (integration_id) REFERENCES integrations(id),
  FOREIGN KEY (owner_id) REFERENCES users(id),
  FOREIGN KEY (team_id) REFERENCES teams(id)
);

CREATE INDEX IF NOT EXISTS idx_instances_integration ON integration_instances(integration_id);
CREATE INDEX IF NOT EXISTS idx_instances_owner ON integration_instances(owner_id);
CREATE INDEX IF NOT EXISTS idx_instances_team ON integration_instances(team_id);
CREATE INDEX IF NOT EXISTS idx_instances_status ON integration_instances(status);

-- Webhook endpoints
CREATE TABLE IF NOT EXISTS webhook_endpoints (
  id TEXT PRIMARY KEY,
  instance_id TEXT NOT NULL,
  url TEXT NOT NULL,
  secret TEXT NOT NULL,
  subscribed_events TEXT NOT NULL DEFAULT '[]',
  is_active INTEGER NOT NULL DEFAULT 1,
  created_at TEXT NOT NULL,
  last_received_at TEXT NULL,
  total_received INTEGER NOT NULL DEFAULT 0,
  total_processed INTEGER NOT NULL DEFAULT 0,
  total_failed INTEGER NOT NULL DEFAULT 0,
  FOREIGN KEY (instance_id) REFERENCES integration_instances(id)
);

CREATE INDEX IF NOT EXISTS idx_webhooks_instance ON webhook_endpoints(instance_id);
CREATE INDEX IF NOT EXISTS idx_webhooks_active ON webhook_endpoints(is_active);

-- Webhook events (incoming)
CREATE TABLE IF NOT EXISTS webhook_events (
  id TEXT PRIMARY KEY,
  webhook_id TEXT NOT NULL,
  instance_id TEXT NOT NULL,
  event_type TEXT NOT NULL,
  raw_payload TEXT NOT NULL,
  headers_json TEXT NOT NULL DEFAULT '{}',
  received_at TEXT NOT NULL,
  processed_at TEXT NULL,
  is_processed INTEGER NOT NULL DEFAULT 0,
  error TEXT NULL,
  FOREIGN KEY (webhook_id) REFERENCES webhook_endpoints(id),
  FOREIGN KEY (instance_id) REFERENCES integration_instances(id)
);

CREATE INDEX IF NOT EXISTS idx_webhook_events_webhook ON webhook_events(webhook_id);
CREATE INDEX IF NOT EXISTS idx_webhook_events_instance ON webhook_events(instance_id);
CREATE INDEX IF NOT EXISTS idx_webhook_events_received ON webhook_events(received_at DESC);
CREATE INDEX IF NOT EXISTS idx_webhook_events_processed ON webhook_events(is_processed);

-- API keys
CREATE TABLE IF NOT EXISTS api_keys (
  id TEXT PRIMARY KEY,
  owner_id TEXT NOT NULL,
  team_id TEXT NULL,
  name TEXT NOT NULL,
  key_prefix TEXT NOT NULL,
  hashed_key TEXT NOT NULL,
  scopes TEXT NOT NULL DEFAULT '[]',
  created_at TEXT NOT NULL,
  expires_at TEXT NULL,
  last_used_at TEXT NULL,
  is_active INTEGER NOT NULL DEFAULT 1,
  allowed_ips TEXT NULL,
  usage_count INTEGER NOT NULL DEFAULT 0,
  FOREIGN KEY (owner_id) REFERENCES users(id),
  FOREIGN KEY (team_id) REFERENCES teams(id)
);

CREATE INDEX IF NOT EXISTS idx_api_keys_owner ON api_keys(owner_id);
CREATE INDEX IF NOT EXISTS idx_api_keys_team ON api_keys(team_id);
CREATE INDEX IF NOT EXISTS idx_api_keys_prefix ON api_keys(key_prefix);
CREATE INDEX IF NOT EXISTS idx_api_keys_active ON api_keys(is_active);

-- Sync jobs
CREATE TABLE IF NOT EXISTS sync_jobs (
  id TEXT PRIMARY KEY,
  instance_id TEXT NOT NULL,
  direction TEXT NOT NULL,
  resource_type TEXT NOT NULL,
  status TEXT NOT NULL DEFAULT 'Pending',
  started_at TEXT NOT NULL,
  completed_at TEXT NULL,
  total_records INTEGER NOT NULL DEFAULT 0,
  processed_records INTEGER NOT NULL DEFAULT 0,
  failed_records INTEGER NOT NULL DEFAULT 0,
  error TEXT NULL,
  metadata_json TEXT NULL,
  FOREIGN KEY (instance_id) REFERENCES integration_instances(id)
);

CREATE INDEX IF NOT EXISTS idx_sync_jobs_instance ON sync_jobs(instance_id);
CREATE INDEX IF NOT EXISTS idx_sync_jobs_status ON sync_jobs(status);
CREATE INDEX IF NOT EXISTS idx_sync_jobs_started ON sync_jobs(started_at DESC);

-- Integration events (audit log)
CREATE TABLE IF NOT EXISTS integration_events (
  id TEXT PRIMARY KEY,
  instance_id TEXT NOT NULL,
  event_type TEXT NOT NULL,
  description TEXT NOT NULL,
  triggered_by TEXT NULL,
  occurred_at TEXT NOT NULL,
  data_json TEXT NULL,
  FOREIGN KEY (instance_id) REFERENCES integration_instances(id),
  FOREIGN KEY (triggered_by) REFERENCES users(id)
);

CREATE INDEX IF NOT EXISTS idx_integration_events_instance ON integration_events(instance_id);
CREATE INDEX IF NOT EXISTS idx_integration_events_type ON integration_events(event_type);
CREATE INDEX IF NOT EXISTS idx_integration_events_occurred ON integration_events(occurred_at DESC);
