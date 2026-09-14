-- V4: 资产台账字段 + 操作审计表
ALTER TABLE printers ADD COLUMN model VARCHAR(120) NULL;
ALTER TABLE printers ADD COLUMN serial_number VARCHAR(120) NULL;
ALTER TABLE printers ADD COLUMN asset_tag VARCHAR(64) NULL;
ALTER TABLE printers ADD COLUMN installed_at DATETIME NULL;

CREATE TABLE audit_logs (
    id BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY,
    created_at DATETIME NOT NULL,
    username VARCHAR(64) NOT NULL,
    action VARCHAR(64) NOT NULL,
    target VARCHAR(200) NULL,
    detail VARCHAR(500) NULL,
    INDEX idx_audit_created (created_at),
    INDEX idx_audit_action (action)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
