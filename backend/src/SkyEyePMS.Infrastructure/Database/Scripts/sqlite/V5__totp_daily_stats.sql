-- V5: TOTP 两步验证 + 日粒度统计表（年度趋势）
ALTER TABLE users ADD COLUMN totp_secret TEXT NULL;
ALTER TABLE users ADD COLUMN totp_enabled INTEGER NOT NULL DEFAULT 0;

CREATE TABLE daily_stats (
    printer_id INTEGER NOT NULL,
    stat_date TEXT NOT NULL,
    pages_printed INTEGER NOT NULL DEFAULT 0,
    online_samples INTEGER NOT NULL DEFAULT 0,
    total_samples INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY (printer_id, stat_date)
);
