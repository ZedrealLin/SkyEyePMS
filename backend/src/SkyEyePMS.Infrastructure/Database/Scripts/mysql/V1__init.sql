-- SkyEyePMS schema v1 (MySQL 5.7+)
CREATE TABLE IF NOT EXISTS users (
  id INT AUTO_INCREMENT PRIMARY KEY,
  username VARCHAR(64) NOT NULL,
  password_hash VARCHAR(100) NOT NULL,
  role VARCHAR(16) NOT NULL DEFAULT 'viewer',
  created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
  last_login_at DATETIME NULL,
  CONSTRAINT uq_users_username UNIQUE (username)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS printers (
  id INT AUTO_INCREMENT PRIMARY KEY,
  name VARCHAR(100) NOT NULL,
  ip VARCHAR(45) NOT NULL,
  port INT NOT NULL DEFAULT 161,
  community_cipher TEXT NOT NULL,
  snmp_version INT NOT NULL DEFAULT 2,
  location VARCHAR(100) NULL,
  department VARCHAR(100) NULL,
  enabled TINYINT(1) NOT NULL DEFAULT 1,
  consecutive_failures INT NOT NULL DEFAULT 0,
  last_poll_at DATETIME NULL,
  last_success_at DATETIME NULL,
  last_online TINYINT(1) NULL,
  hr_status INT NULL,
  status_changed_at DATETIME NULL,
  page_count BIGINT NULL,
  sys_name VARCHAR(100) NULL,
  sys_descr VARCHAR(255) NULL,
  created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
  updated_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
  CONSTRAINT uq_printers_endpoint UNIQUE (ip, port)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
CREATE INDEX idx_printers_enabled ON printers (enabled);

CREATE TABLE IF NOT EXISTS status_samples (
  id INT AUTO_INCREMENT PRIMARY KEY,
  printer_id INT NOT NULL,
  collected_at DATETIME NOT NULL,
  online TINYINT(1) NOT NULL,
  hr_status INT NULL,
  page_count BIGINT NULL,
  KEY idx_samples_printer_time (printer_id, collected_at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS supplies (
  id INT AUTO_INCREMENT PRIMARY KEY,
  printer_id INT NOT NULL,
  supply_index INT NOT NULL,
  name VARCHAR(100) NULL,
  supply_type INT NULL,
  unit INT NULL,
  max_capacity BIGINT NULL,
  level BIGINT NULL,
  percent DOUBLE NULL,
  low_percent_threshold INT NULL,
  updated_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
  CONSTRAINT uq_supplies UNIQUE (printer_id, supply_index)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS supply_samples (
  id INT AUTO_INCREMENT PRIMARY KEY,
  printer_id INT NOT NULL,
  supply_index INT NOT NULL,
  percent DOUBLE NULL,
  level BIGINT NULL,
  collected_at DATETIME NOT NULL,
  KEY idx_supply_samples (printer_id, supply_index, collected_at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS alerts (
  id INT AUTO_INCREMENT PRIMARY KEY,
  printer_id INT NOT NULL,
  alert_key VARCHAR(100) NOT NULL,
  type VARCHAR(32) NOT NULL,
  severity VARCHAR(16) NOT NULL,
  message VARCHAR(500) NOT NULL,
  status VARCHAR(16) NOT NULL DEFAULT 'active',
  first_triggered_at DATETIME NOT NULL,
  last_notified_at DATETIME NULL,
  notify_count INT NOT NULL DEFAULT 0,
  resolved_at DATETIME NULL,
  KEY idx_alerts_status (status),
  KEY idx_alerts_printer (printer_id),
  KEY idx_alerts_printer_key (printer_id, alert_key, status)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS notification_log (
  id INT AUTO_INCREMENT PRIMARY KEY,
  alert_id INT NOT NULL,
  channel VARCHAR(32) NOT NULL,
  success TINYINT(1) NOT NULL,
  error VARCHAR(500) NULL,
  sent_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
  KEY idx_notify_alert (alert_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS app_settings (
  skey VARCHAR(64) PRIMARY KEY,
  svalue TEXT NOT NULL,
  updated_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
