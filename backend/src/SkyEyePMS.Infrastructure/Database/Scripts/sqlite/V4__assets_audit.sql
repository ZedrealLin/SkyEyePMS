-- V4: 资产台账字段 + 操作审计表
ALTER TABLE printers ADD COLUMN model TEXT NULL;
ALTER TABLE printers ADD COLUMN serial_number TEXT NULL;
ALTER TABLE printers ADD COLUMN asset_tag TEXT NULL;
ALTER TABLE printers ADD COLUMN installed_at TEXT NULL;

CREATE TABLE audit_logs (
    id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
    created_at TEXT NOT NULL,
    username TEXT NOT NULL,
    action TEXT NOT NULL,
    target TEXT NULL,
    detail TEXT NULL
);
CREATE INDEX idx_audit_created ON audit_logs (created_at);
