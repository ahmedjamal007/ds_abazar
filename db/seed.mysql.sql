-- =====================================================================
-- MySQL essential seed (network mode): default settings.
-- Idempotent (INSERT IGNORE) — runs on every startup, so it must only
-- contain rows the app cannot live without. Demo/sample data lives in
-- seed.demo.mysql.sql and is applied ONCE by DatabaseInitializer, so
-- anything the user deletes stays deleted.
-- =====================================================================

-- ---------- Settings (D-12) ----------
INSERT IGNORE INTO settings (key_name, value) VALUES
  ('pharmacy_name',        'صيدلية دوائي'),
  ('currency',             'ج.س'),
  ('expiry_warn_days',     '90'),
  ('pos_expiry_warn_days', '30'),
  ('low_stock_default',    '10'),
  ('backup_folder',        ''),
  ('backup_keep_last',     '14'),
  ('receipt_printer_name', ''),
  ('receipt_width',        '32'),
  ('price_rounding_step',  '0'),
  ('terminal_name',        'main');

-- The default admin is created by DatabaseInitializer.EnsureDefaultAdmin
-- (only when the users table is empty), NOT here — seeding it every run
-- would re-create the admin/admin123 account even after it was removed.
