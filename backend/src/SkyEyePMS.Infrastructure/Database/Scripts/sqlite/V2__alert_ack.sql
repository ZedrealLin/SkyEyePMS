-- V2: 告警确认（人工认领处理）
ALTER TABLE alerts ADD COLUMN acknowledged_at TEXT NULL;
ALTER TABLE alerts ADD COLUMN acknowledged_by TEXT NULL;
