-- V5: TOTP 两步验证 + 日粒度统计表（年度趋势）
ALTER TABLE users ADD COLUMN totp_secret VARCHAR(400) NULL;
ALTER TABLE users ADD COLUMN totp_enabled TINYINT NOT NULL DEFAULT 0;

CREATE TABLE daily_stats (
    printer_id INT NOT NULL,
    stat_date DATE NOT NULL,
    pages_printed BIGINT NOT NULL DEFAULT 0,
    online_samples INT NOT NULL DEFAULT 0,
    total_samples INT NOT NULL DEFAULT 0,
    PRIMARY KEY (printer_id, stat_date)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
