-- V2: 告警确认（人工认领处理）
ALTER TABLE alerts ADD COLUMN acknowledged_at DATETIME NULL;
ALTER TABLE alerts ADD COLUMN acknowledged_by VARCHAR(64) NULL;
