-- V3: 单台打印机耗材低阈值覆盖（NULL = 使用全局阈值）
ALTER TABLE printers ADD COLUMN supply_low_percent_override INTEGER NULL;
