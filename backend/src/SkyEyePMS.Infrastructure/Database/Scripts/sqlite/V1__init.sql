-- SkyEyePMS schema v1 (SQLite)
CREATE TABLE IF NOT EXISTS users (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  username TEXT NOT NULL,
  password_hash TEXT NOT NULL,
  role TEXT NOT NULL DEFAULT 'viewer',
  created_at TEXT NOT NULL,
  last_login_at TEXT NULL,
  CONSTRAINT uq_users_username UNIQUE (username)
);

CREATE TABLE IF NOT EXISTS printers (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  name TEXT NOT NULL,
  ip TEXT NOT NULL,
  port INTEGER NOT NULL DEFAULT 161,
  community_cipher TEXT NOT NULL,
  snmp_version INTEGER NOT NULL DEFAULT 2,
  location TEXT NULL,
  department TEXT NULL,
  enabled INTEGER NOT NULL DEFAULT 1,
  consecutive_failures INTEGER NOT NULL DEFAULT 0,
  last_poll_at TEXT NULL,
  last_success_at TEXT NULL,
  last_online INTEGER NULL,
  hr_status INTEGER NULL,
  status_changed_at TEXT NULL,
  page_count INTEGER NULL,
  sys_name TEXT NULL,
  sys_descr TEXT NULL,
  created_at TEXT NOT NULL,
  updated_at TEXT NOT NULL,
  CONSTRAINT uq_printers_endpoint UNIQUE (ip, port)
);
CREATE INDEX IF NOT EXISTS idx_printers_enabled ON printers (enabled);

CREATE TABLE IF NOT EXISTS status_samples (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  printer_id INTEGER NOT NULL,
  collected_at TEXT NOT NULL,
  online INTEGER NOT NULL,
  hr_status INTEGER NULL,
  page_count INTEGER NULL
);
CREATE INDEX IF NOT EXISTS idx_samples_printer_time ON status_samples (printer_id, collected_at);

CREATE TABLE IF NOT EXISTS supplies (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  printer_id INTEGER NOT NULL,
  supply_index INTEGER NOT NULL,
  name TEXT NULL,
  supply_type INTEGER NULL,
  unit INTEGER NULL,
  max_capacity INTEGER NULL,
  level INTEGER NULL,
  percent REAL NULL,
  low_percent_threshold INTEGER NULL,
  updated_at TEXT NOT NULL,
  CONSTRAINT uq_supplies UNIQUE (printer_id, supply_index)
);

CREATE TABLE IF NOT EXISTS supply_samples (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  printer_id INTEGER NOT NULL,
  supply_index INTEGER NOT NULL,
  percent REAL NULL,
  level INTEGER NULL,
  collected_at TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_supply_samples ON supply_samples (printer_id, supply_index, collected_at);

CREATE TABLE IF NOT EXISTS alerts (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  printer_id INTEGER NOT NULL,
  alert_key TEXT NOT NULL,
  type TEXT NOT NULL,
  severity TEXT NOT NULL,
  message TEXT NOT NULL,
  status TEXT NOT NULL DEFAULT 'active',
  first_triggered_at TEXT NOT NULL,
  last_notified_at TEXT NULL,
  notify_count INTEGER NOT NULL DEFAULT 0,
  resolved_at TEXT NULL
);
CREATE INDEX IF NOT EXISTS idx_alerts_status ON alerts (status);
CREATE INDEX IF NOT EXISTS idx_alerts_printer ON alerts (printer_id);
CREATE INDEX IF NOT EXISTS idx_alerts_printer_key ON alerts (printer_id, alert_key, status);

CREATE TABLE IF NOT EXISTS notification_log (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  alert_id INTEGER NOT NULL,
  channel TEXT NOT NULL,
  success INTEGER NOT NULL,
  error TEXT NULL,
  sent_at TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_notify_alert ON notification_log (alert_id);

CREATE TABLE IF NOT EXISTS app_settings (
  skey TEXT PRIMARY KEY,
  svalue TEXT NOT NULL,
  updated_at TEXT NOT NULL
);
