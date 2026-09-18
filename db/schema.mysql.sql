-- =====================================================================
--  دوائي (Dawaii) — MySQL schema for NETWORK mode (V1.2 req 6).
--  MySQL runs as a Windows service on the manager's PC (auto-starts at boot);
--  counter PCs connect over the LAN. Money = DECIMAL(12,2), timestamps = DATETIME,
--  booleans = TINYINT(1). InnoDB + utf8mb4. Idempotent (CREATE TABLE IF NOT EXISTS).
--  The `dawaii` database itself is created by the installer before this runs.
-- =====================================================================

SET NAMES utf8mb4;

CREATE TABLE IF NOT EXISTS users (
  id            INT UNSIGNED NOT NULL AUTO_INCREMENT PRIMARY KEY,
  username      VARCHAR(50)  NOT NULL UNIQUE,
  password_hash VARCHAR(255) NOT NULL,
  full_name     VARCHAR(100) NULL,
  -- FullEmployee = "موظف ذو امتيازات": a cashier who may also manage the catalog and stock (V1.8).
  role          ENUM('Admin','Cashier','FullEmployee') NOT NULL DEFAULT 'Cashier',
  is_active     TINYINT(1)   NOT NULL DEFAULT 1,
  created_at    DATETIME     NOT NULL DEFAULT CURRENT_TIMESTAMP
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE IF NOT EXISTS items (
  id               INT UNSIGNED NOT NULL AUTO_INCREMENT PRIMARY KEY,
  name_en          VARCHAR(200) NOT NULL,            -- trade name on the box ("polymol")
  generic_name     VARCHAR(200) NULL,                -- scientific name ("paracetamol")
  units_per_strip  INT NOT NULL DEFAULT 1,
  strips_per_box   INT NOT NULL DEFAULT 1,
  purchase_price   DECIMAL(12,2) NOT NULL DEFAULT 0,
  selling_price    DECIMAL(12,2) NULL DEFAULT NULL,  -- NULL = not priced yet (hidden from POS)
  manual_price     TINYINT(1) NOT NULL DEFAULT 0,
  min_quantity     INT NOT NULL DEFAULT 0,
  max_quantity     INT NOT NULL DEFAULT 0,
  expiry_warn_days INT NULL,
  substitute_of    INT NULL,
  is_active        TINYINT(1) NOT NULL DEFAULT 1,
  created_at       DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
  updated_at       DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
  KEY ix_items_name_en (name_en),
  KEY ix_items_generic (generic_name)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE IF NOT EXISTS item_codes (
  id      INT UNSIGNED NOT NULL AUTO_INCREMENT PRIMARY KEY,
  item_id INT UNSIGNED NOT NULL,
  code    VARCHAR(100) NOT NULL UNIQUE,
  UNIQUE KEY ux_item_codes_item (item_id),
  CONSTRAINT fk_item_codes_item FOREIGN KEY (item_id) REFERENCES items(id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

-- Prices are entered per BOX only; strip/unit prices are always derived.
CREATE TABLE IF NOT EXISTS stock_batches (
  id                 INT UNSIGNED NOT NULL AUTO_INCREMENT PRIMARY KEY,
  item_id            INT UNSIGNED NOT NULL,
  quantity_units     INT NOT NULL DEFAULT 0,
  expiry_date        DATE NULL,
  batch_number       VARCHAR(100) NULL,
  strips_per_box     INT NOT NULL DEFAULT 1,
  units_per_strip    INT NOT NULL DEFAULT 1,
  box_purchase_price DECIMAL(12,2) NOT NULL DEFAULT 0,
  box_selling_price  DECIMAL(12,2) NOT NULL DEFAULT 0,
  received_at        DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
  is_disposed        TINYINT(1) NOT NULL DEFAULT 0,
  KEY ix_batches_fefo (item_id, is_disposed, expiry_date),
  -- MySQL has no partial indexes, so quantity_units joins the key instead.
  KEY ix_batches_sellable (item_id, is_disposed, quantity_units, expiry_date),
  CONSTRAINT fk_batches_item FOREIGN KEY (item_id) REFERENCES items(id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE IF NOT EXISTS customers (
  id         INT UNSIGNED NOT NULL AUTO_INCREMENT PRIMARY KEY,
  name       VARCHAR(150) NOT NULL,
  phone      VARCHAR(30)  NULL,
  balance    DECIMAL(12,2) NOT NULL DEFAULT 0,
  created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
  KEY ix_customers_name (name)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE IF NOT EXISTS sales (
  id          INT UNSIGNED NOT NULL AUTO_INCREMENT PRIMARY KEY,
  -- Printed invoice number: a plain integer equal to the row id, so staff can type back exactly
  -- the number on the receipt (a composite number renders reversed on right-to-left screens).
  sale_number INT UNSIGNED NULL UNIQUE,
  user_id     INT UNSIGNED NOT NULL,
  customer_id INT UNSIGNED NULL,
  sale_type   ENUM('Cash','Credit') NOT NULL DEFAULT 'Cash',
  payment_method VARCHAR(20) NULL,
  subtotal    DECIMAL(12,2) NOT NULL DEFAULT 0,
  discount    DECIMAL(12,2) NOT NULL DEFAULT 0,
  total       DECIMAL(12,2) NOT NULL DEFAULT 0,
  cost_total  DECIMAL(12,2) NOT NULL DEFAULT 0,
  -- Running totals of what has been refunded off this invoice (V1.8 partial returns).
  returned_total DECIMAL(12,2) NOT NULL DEFAULT 0,
  returned_cost  DECIMAL(12,2) NOT NULL DEFAULT 0,
  status      ENUM('Completed','PartiallyReturned','Returned') NOT NULL DEFAULT 'Completed',
  terminal    VARCHAR(64) NULL,
  created_at  DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
  KEY ix_sales_created (created_at),
  KEY ix_sales_customer (customer_id),
  CONSTRAINT fk_sales_user     FOREIGN KEY (user_id)     REFERENCES users(id),
  CONSTRAINT fk_sales_customer FOREIGN KEY (customer_id) REFERENCES customers(id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE IF NOT EXISTS sale_lines (
  id         INT UNSIGNED NOT NULL AUTO_INCREMENT PRIMARY KEY,
  sale_id    INT UNSIGNED NOT NULL,
  item_id    INT UNSIGNED NOT NULL,
  unit_type  ENUM('Box','Strip','Unit') NOT NULL DEFAULT 'Unit',
  quantity   INT NOT NULL,
  units_each INT NOT NULL,
  unit_price DECIMAL(12,2) NOT NULL,
  line_total DECIMAL(12,2) NOT NULL,
  cost_total DECIMAL(12,2) NOT NULL DEFAULT 0,
  KEY ix_lines_sale (sale_id),
  KEY ix_lines_item (item_id),
  CONSTRAINT fk_lines_sale FOREIGN KEY (sale_id) REFERENCES sales(id) ON DELETE CASCADE,
  CONSTRAINT fk_lines_item FOREIGN KEY (item_id) REFERENCES items(id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE IF NOT EXISTS sale_line_allocations (
  id           INT UNSIGNED NOT NULL AUTO_INCREMENT PRIMARY KEY,
  sale_line_id INT UNSIGNED NOT NULL,
  batch_id     INT UNSIGNED NOT NULL,
  units        INT NOT NULL,
  unit_cost    DECIMAL(12,2) NOT NULL,
  KEY ix_alloc_line  (sale_line_id),
  KEY ix_alloc_batch (batch_id),
  CONSTRAINT fk_alloc_line  FOREIGN KEY (sale_line_id) REFERENCES sale_lines(id) ON DELETE CASCADE,
  CONSTRAINT fk_alloc_batch FOREIGN KEY (batch_id)     REFERENCES stock_batches(id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE IF NOT EXISTS returns (
  id         INT UNSIGNED NOT NULL AUTO_INCREMENT PRIMARY KEY,
  sale_id    INT UNSIGNED NOT NULL,
  user_id    INT UNSIGNED NOT NULL,
  reason     VARCHAR(255) NULL,
  total      DECIMAL(12,2) NOT NULL DEFAULT 0,
  created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
  KEY ix_returns_sale (sale_id),
  CONSTRAINT fk_returns_sale FOREIGN KEY (sale_id) REFERENCES sales(id),
  CONSTRAINT fk_returns_user FOREIGN KEY (user_id) REFERENCES users(id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

-- What each return actually took back (V1.8): a customer may return 5 of the 50 boxes they bought,
-- so a return is recorded per line and per quantity.
CREATE TABLE IF NOT EXISTS return_lines (
  id           INT UNSIGNED NOT NULL AUTO_INCREMENT PRIMARY KEY,
  return_id    INT UNSIGNED NOT NULL,
  sale_line_id INT UNSIGNED NOT NULL,
  item_id      INT UNSIGNED NOT NULL,
  quantity     INT NOT NULL,                      -- in the sale line's unit type (5 boxes)
  units        INT NOT NULL,                      -- single units put back into stock
  amount       DECIMAL(12,2) NOT NULL DEFAULT 0,  -- money refunded for this line
  cost         DECIMAL(12,2) NOT NULL DEFAULT 0,  -- cost of the goods that came back
  KEY ix_return_lines_return (return_id),
  KEY ix_return_lines_line (sale_line_id),
  CONSTRAINT fk_return_lines_return FOREIGN KEY (return_id)    REFERENCES returns(id) ON DELETE CASCADE,
  CONSTRAINT fk_return_lines_line   FOREIGN KEY (sale_line_id) REFERENCES sale_lines(id),
  CONSTRAINT fk_return_lines_item   FOREIGN KEY (item_id)      REFERENCES items(id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE IF NOT EXISTS debt_transactions (
  id          INT UNSIGNED NOT NULL AUTO_INCREMENT PRIMARY KEY,
  customer_id INT UNSIGNED NOT NULL,
  type        ENUM('Charge','Payment') NOT NULL,
  amount      DECIMAL(12,2) NOT NULL,
  sale_id     INT UNSIGNED NULL,
  user_id     INT UNSIGNED NOT NULL,
  note        VARCHAR(255) NULL,
  created_at  DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
  KEY ix_debt_customer (customer_id, created_at),
  CONSTRAINT fk_debt_customer FOREIGN KEY (customer_id) REFERENCES customers(id),
  CONSTRAINT fk_debt_sale     FOREIGN KEY (sale_id)     REFERENCES sales(id),
  CONSTRAINT fk_debt_user     FOREIGN KEY (user_id)     REFERENCES users(id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE IF NOT EXISTS stock_adjustments (
  id          INT UNSIGNED NOT NULL AUTO_INCREMENT PRIMARY KEY,
  item_id     INT UNSIGNED NOT NULL,
  batch_id    INT UNSIGNED NULL,
  delta_units INT NOT NULL,
  reason      VARCHAR(255) NOT NULL,
  user_id     INT UNSIGNED NOT NULL,
  created_at  DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
  KEY ix_adj_item (item_id),
  CONSTRAINT fk_adj_item FOREIGN KEY (item_id) REFERENCES items(id),
  CONSTRAINT fk_adj_user FOREIGN KEY (user_id) REFERENCES users(id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE IF NOT EXISTS audit_log (
  id         BIGINT UNSIGNED NOT NULL AUTO_INCREMENT PRIMARY KEY,
  user_id    INT UNSIGNED NULL,
  action     VARCHAR(100) NOT NULL,
  entity     VARCHAR(50)  NULL,
  entity_id  INT UNSIGNED NULL,
  details    TEXT NULL,
  terminal   VARCHAR(64) NULL,
  created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
  KEY ix_audit_created (created_at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE IF NOT EXISTS settings (
  key_name VARCHAR(64)  NOT NULL PRIMARY KEY,
  value    VARCHAR(255) NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE IF NOT EXISTS backups (
  id         INT UNSIGNED NOT NULL AUTO_INCREMENT PRIMARY KEY,
  file_path  VARCHAR(500) NOT NULL,
  size_bytes BIGINT UNSIGNED NOT NULL DEFAULT 0,
  status     ENUM('Success','Failed') NOT NULL DEFAULT 'Success',
  created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
  KEY ix_backups_created (created_at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

-- ---------- Employee system (V1.2 req 2+5) ----------
CREATE TABLE IF NOT EXISTS attendance (
  id       INT UNSIGNED NOT NULL AUTO_INCREMENT PRIMARY KEY,
  user_id  INT UNSIGNED NOT NULL,
  login_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
  terminal VARCHAR(64) NULL,
  KEY ix_attendance_user_day (user_id, login_at),
  CONSTRAINT fk_attendance_user FOREIGN KEY (user_id) REFERENCES users(id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE IF NOT EXISTS employee_profiles (
  user_id        INT UNSIGNED NOT NULL PRIMARY KEY,
  monthly_salary DECIMAL(12,2) NOT NULL DEFAULT 0,
  notes          VARCHAR(255) NULL,
  CONSTRAINT fk_emp_profile_user FOREIGN KEY (user_id) REFERENCES users(id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE IF NOT EXISTS leaves (
  id         INT UNSIGNED NOT NULL AUTO_INCREMENT PRIMARY KEY,
  user_id    INT UNSIGNED NOT NULL,
  from_date  DATE NOT NULL,
  to_date    DATE NOT NULL,
  reason     VARCHAR(255) NULL,
  created_by INT UNSIGNED NOT NULL,
  created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
  KEY ix_leaves_user (user_id, from_date),
  CONSTRAINT fk_leaves_user FOREIGN KEY (user_id) REFERENCES users(id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE IF NOT EXISTS deductions (
  id         INT UNSIGNED NOT NULL AUTO_INCREMENT PRIMARY KEY,
  user_id    INT UNSIGNED NOT NULL,
  amount     DECIMAL(12,2) NOT NULL,
  reason     VARCHAR(255) NOT NULL,
  created_by INT UNSIGNED NOT NULL,
  created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
  KEY ix_deductions_user (user_id, created_at),
  CONSTRAINT fk_deductions_user FOREIGN KEY (user_id) REFERENCES users(id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE IF NOT EXISTS employee_expenses (
  id         INT UNSIGNED NOT NULL AUTO_INCREMENT PRIMARY KEY,
  user_id    INT UNSIGNED NOT NULL,
  type       ENUM('Money','Medicine') NOT NULL,
  amount     DECIMAL(12,2) NOT NULL DEFAULT 0,
  item_id    INT UNSIGNED NULL,
  units      INT NULL,
  note       VARCHAR(255) NULL,
  created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
  KEY ix_emp_exp_user_day (user_id, created_at),
  CONSTRAINT fk_emp_exp_user FOREIGN KEY (user_id) REFERENCES users(id),
  CONSTRAINT fk_emp_exp_item FOREIGN KEY (item_id) REFERENCES items(id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

-- V1.4 — Purchases ("المشتريات"): goods bought from people who come to sell
-- (bags/أكياس and other supplies). Standalone spend log, not tied to stock.
CREATE TABLE IF NOT EXISTS purchases (
  id            INT UNSIGNED NOT NULL AUTO_INCREMENT PRIMARY KEY,
  supplier_name VARCHAR(150) NULL,
  description   VARCHAR(255) NOT NULL,
  quantity      INT NOT NULL DEFAULT 1,
  unit_price    DECIMAL(12,2) NOT NULL DEFAULT 0,
  amount        DECIMAL(12,2) NOT NULL DEFAULT 0,
  note          VARCHAR(255) NULL,
  user_id       INT UNSIGNED NOT NULL,
  created_at    DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
  KEY ix_purchases_created (created_at),
  CONSTRAINT fk_purchases_user FOREIGN KEY (user_id) REFERENCES users(id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

-- ---------------------------------------------------------------------
-- Suppliers, their invoices and what is still owed on them (V2.1)
-- ---------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS suppliers (
  id         INT UNSIGNED NOT NULL AUTO_INCREMENT PRIMARY KEY,
  name       VARCHAR(200) NOT NULL,
  created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
  KEY ix_suppliers_name (name)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE IF NOT EXISTS purchase_invoices (
  id             INT UNSIGNED NOT NULL AUTO_INCREMENT PRIMARY KEY,
  supplier_id    INT UNSIGNED NOT NULL,
  representative VARCHAR(200) NULL,
  invoice_number VARCHAR(100) NULL,
  invoice_date   DATE NOT NULL,
  total          DECIMAL(12,2) NOT NULL DEFAULT 0,
  amount_paid    DECIMAL(12,2) NOT NULL DEFAULT 0,
  user_id        INT UNSIGNED NOT NULL,
  created_at     DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
  KEY ix_pinv_supplier (supplier_id, invoice_date),
  CONSTRAINT fk_pinv_supplier FOREIGN KEY (supplier_id) REFERENCES suppliers(id),
  CONSTRAINT fk_pinv_user     FOREIGN KEY (user_id)     REFERENCES users(id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE IF NOT EXISTS purchase_invoice_lines (
  id                 INT UNSIGNED NOT NULL AUTO_INCREMENT PRIMARY KEY,
  invoice_id         INT UNSIGNED NOT NULL,
  item_id            INT UNSIGNED NOT NULL,
  item_name          VARCHAR(200) NOT NULL,
  quantity_boxes     INT NOT NULL DEFAULT 0,
  strips_per_box     INT NOT NULL DEFAULT 1,
  box_purchase_price DECIMAL(12,2) NOT NULL DEFAULT 0,
  box_selling_price  DECIMAL(12,2) NOT NULL DEFAULT 0,
  expiry_date        DATE NULL,
  batch_number       VARCHAR(100) NULL,
  stock_batch_id     INT UNSIGNED NULL,
  KEY ix_pinvline_invoice (invoice_id),
  CONSTRAINT fk_pinvline_invoice FOREIGN KEY (invoice_id) REFERENCES purchase_invoices(id) ON DELETE CASCADE,
  CONSTRAINT fk_pinvline_item    FOREIGN KEY (item_id)    REFERENCES items(id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE IF NOT EXISTS supplier_payments (
  id          INT UNSIGNED NOT NULL AUTO_INCREMENT PRIMARY KEY,
  supplier_id INT UNSIGNED NOT NULL,
  invoice_id  INT UNSIGNED NOT NULL,
  amount      DECIMAL(12,2) NOT NULL,
  user_id     INT UNSIGNED NOT NULL,
  note        VARCHAR(400) NULL,
  payment_method VARCHAR(20) NULL,                 -- Cash | Bank; only cash leaves the drawer
  created_at  DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
  KEY ix_supplier_payments (supplier_id, created_at),
  CONSTRAINT fk_spay_supplier FOREIGN KEY (supplier_id) REFERENCES suppliers(id),
  CONSTRAINT fk_spay_invoice  FOREIGN KEY (invoice_id)  REFERENCES purchase_invoices(id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
