-- =====================================================================
--  دوائي (Dawaii) — SQLite schema (single-file local database).
--  Serverless: the app creates this file on first run (no MySQL, no login).
--  Conventions (see Data/Db.cs): money = REAL (2dp), timestamps = TEXT
--  "YYYY-MM-DD HH:MM:SS" local, booleans = INTEGER 0/1. FKs enforced per connection.
-- =====================================================================

PRAGMA foreign_keys = ON;

-- ---------------------------------------------------------------------
-- Users & roles (FR-USR-01/02/03, NFR-04)
-- ---------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS users (
  id            INTEGER PRIMARY KEY AUTOINCREMENT,
  username      TEXT NOT NULL UNIQUE,
  password_hash TEXT NOT NULL,                       -- v1$iterations$b64salt$b64hash (PBKDF2)
  full_name     TEXT,
  -- FullEmployee = "موظف ذو امتيازات": a cashier who may also manage the catalog and stock (V1.8).
  role          TEXT NOT NULL DEFAULT 'Cashier' CHECK (role IN ('Admin','Cashier','FullEmployee')),
  is_active     INTEGER NOT NULL DEFAULT 1,
  created_at    TEXT NOT NULL DEFAULT (datetime('now','localtime'))
);

-- ---------------------------------------------------------------------
-- Items / medicines (FR-INV-01, FR-POS-04). Prices are per SINGLE unit (D-01).
-- ---------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS items (
  id               INTEGER PRIMARY KEY AUTOINCREMENT,
  name_en          TEXT NOT NULL,                    -- trade name on the box ("polymol")
  generic_name     TEXT,                             -- scientific name ("paracetamol")
  units_per_strip  INTEGER NOT NULL DEFAULT 1,
  strips_per_box   INTEGER NOT NULL DEFAULT 1,
  purchase_price   REAL NOT NULL DEFAULT 0,          -- reference cost / single unit
  selling_price    REAL,                             -- selling price / single unit; NULL = not priced yet (hidden from POS)
  manual_price     INTEGER NOT NULL DEFAULT 0,       -- 1 = set by hand on إدارة الأسعار; a bulk multiplier skips it (V2.3)
  min_quantity     INTEGER NOT NULL DEFAULT 0,       -- low-stock threshold (single units)
  max_quantity     INTEGER NOT NULL DEFAULT 0,       -- target/ceiling stock level (V1.3 مخزون كامل)
  expiry_warn_days INTEGER,                          -- per-item override
  substitute_of    INTEGER REFERENCES items(id),     -- this drug substitutes that one (V1.2 req 4)
  is_active        INTEGER NOT NULL DEFAULT 1,
  created_at       TEXT NOT NULL DEFAULT (datetime('now','localtime')),
  updated_at       TEXT NOT NULL DEFAULT (datetime('now','localtime'))
);
CREATE INDEX IF NOT EXISTS ix_items_name_en  ON items(name_en);
CREATE INDEX IF NOT EXISTS ix_items_generic  ON items(generic_name);

-- ---------------------------------------------------------------------
-- Item codes — QR/barcode values; one code ↔ one item (FR-QRC-01/06, D-08)
-- V1.9: and one code PER item — a drug has a single barcode on its box, so the
-- unique index below makes "one row per item" the database's rule, not the app's.
-- ---------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS item_codes (
  id      INTEGER PRIMARY KEY AUTOINCREMENT,
  item_id INTEGER NOT NULL REFERENCES items(id) ON DELETE CASCADE,
  code    TEXT NOT NULL UNIQUE
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_item_codes_item ON item_codes(item_id);

-- ---------------------------------------------------------------------
-- Stock batches — quantity in SINGLE units, per expiry (FR-INV-02/03, FEFO D-05)
-- ---------------------------------------------------------------------
-- Prices are entered per BOX only. Strip and single-unit prices are ALWAYS
-- derived (box / strips_per_box, then / units_per_strip) and never stored,
-- so a strip price can never disagree with the box price it came from.
CREATE TABLE IF NOT EXISTS stock_batches (
  id                 INTEGER PRIMARY KEY AUTOINCREMENT,
  item_id            INTEGER NOT NULL REFERENCES items(id) ON DELETE CASCADE,
  quantity_units     INTEGER NOT NULL DEFAULT 0,
  expiry_date        TEXT,                            -- "YYYY-MM-DD" or NULL
  batch_number       TEXT,                            -- supplier lot number as printed
  strips_per_box     INTEGER NOT NULL DEFAULT 1,      -- packaging for THIS shipment
  units_per_strip    INTEGER NOT NULL DEFAULT 1,
  box_purchase_price REAL NOT NULL DEFAULT 0,         -- entered by the user
  box_selling_price  REAL NOT NULL DEFAULT 0,         -- entered by the user
  received_at        TEXT NOT NULL DEFAULT (datetime('now','localtime')),
  is_disposed        INTEGER NOT NULL DEFAULT 0
);
CREATE INDEX IF NOT EXISTS ix_batches_fefo ON stock_batches(item_id, is_disposed, expiry_date);
-- FEFO reads only batches with stock left, soonest expiry first with undated ones last. The index
-- carries the "(expiry_date IS NULL)" expression the ORDER BY leads with: without it SQLite can find
-- the rows but not the order, and builds a temporary B-tree to sort them on every single sale line.
-- Partial, so it stays the size of the working shelf rather than of the whole delivery history.
CREATE INDEX IF NOT EXISTS ix_batches_sellable
  ON stock_batches(item_id, (expiry_date IS NULL), expiry_date, id)
  WHERE is_disposed = 0 AND quantity_units > 0;

-- ---------------------------------------------------------------------
-- Customers & debt ledger (FR-DBT-01..04)
-- ---------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS customers (
  id         INTEGER PRIMARY KEY AUTOINCREMENT,
  name       TEXT NOT NULL,
  phone      TEXT,
  balance    REAL NOT NULL DEFAULT 0,                 -- cached running debt (>0 = owes), D-06
  created_at TEXT NOT NULL DEFAULT (datetime('now','localtime'))
);
CREATE INDEX IF NOT EXISTS ix_customers_name ON customers(name);

-- ---------------------------------------------------------------------
-- Sales & lines (FR-POS-05, immutable price/cost snapshots)
-- ---------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS sales (
  id          INTEGER PRIMARY KEY AUTOINCREMENT,
  -- The invoice number printed on the receipt and typed into the return screen. A plain integer
  -- equal to the row id: anything composite renders reversed on these right-to-left screens and
  -- staff then cannot type back the number they are holding.
  sale_number INTEGER UNIQUE,
  user_id     INTEGER NOT NULL REFERENCES users(id),
  customer_id INTEGER REFERENCES customers(id),
  sale_type   TEXT NOT NULL DEFAULT 'Cash' CHECK (sale_type IN ('Cash','Credit')),
  payment_method TEXT,                                 -- Cash | Bankak | Fawry (V1.3); null for credit
  subtotal    REAL NOT NULL DEFAULT 0,
  discount    REAL NOT NULL DEFAULT 0,
  total       REAL NOT NULL DEFAULT 0,
  cost_total  REAL NOT NULL DEFAULT 0,                -- profit = total - cost_total
  -- Running totals of what has been refunded off this invoice (V1.8 partial returns), kept in the
  -- same transaction as the return itself so reports never have to re-aggregate return_lines.
  returned_total REAL NOT NULL DEFAULT 0,
  returned_cost  REAL NOT NULL DEFAULT 0,
  status      TEXT NOT NULL DEFAULT 'Completed' CHECK (status IN ('Completed','PartiallyReturned','Returned')),
  terminal    TEXT,
  created_at  TEXT NOT NULL DEFAULT (datetime('now','localtime'))
);
CREATE INDEX IF NOT EXISTS ix_sales_created  ON sales(created_at);
CREATE INDEX IF NOT EXISTS ix_sales_customer ON sales(customer_id);

CREATE TABLE IF NOT EXISTS sale_lines (
  id         INTEGER PRIMARY KEY AUTOINCREMENT,
  sale_id    INTEGER NOT NULL REFERENCES sales(id) ON DELETE CASCADE,
  item_id    INTEGER NOT NULL REFERENCES items(id),
  unit_type  TEXT NOT NULL DEFAULT 'Unit' CHECK (unit_type IN ('Box','Strip','Unit')),
  quantity   INTEGER NOT NULL,
  units_each INTEGER NOT NULL,
  unit_price REAL NOT NULL,                           -- price / single unit at sale time (FR-PRC-01)
  line_total REAL NOT NULL,
  cost_total REAL NOT NULL DEFAULT 0
);
CREATE INDEX IF NOT EXISTS ix_lines_sale ON sale_lines(sale_id);
CREATE INDEX IF NOT EXISTS ix_lines_item ON sale_lines(item_id);

-- Which batches each line consumed — exact returns + per-line profit (FR-PRC-03, D-04)
CREATE TABLE IF NOT EXISTS sale_line_allocations (
  id           INTEGER PRIMARY KEY AUTOINCREMENT,
  sale_line_id INTEGER NOT NULL REFERENCES sale_lines(id) ON DELETE CASCADE,
  batch_id     INTEGER NOT NULL REFERENCES stock_batches(id),
  units        INTEGER NOT NULL,
  unit_cost    REAL NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_alloc_line  ON sale_line_allocations(sale_line_id);
CREATE INDEX IF NOT EXISTS ix_alloc_batch ON sale_line_allocations(batch_id);

-- ---------------------------------------------------------------------
-- Returns (FR-POS-08)
-- ---------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS returns (
  id         INTEGER PRIMARY KEY AUTOINCREMENT,
  sale_id    INTEGER NOT NULL REFERENCES sales(id),
  user_id    INTEGER NOT NULL REFERENCES users(id),
  reason     TEXT,
  total      REAL NOT NULL DEFAULT 0,
  created_at TEXT NOT NULL DEFAULT (datetime('now','localtime'))
);
CREATE INDEX IF NOT EXISTS ix_returns_sale ON returns(sale_id);

-- What each return actually took back (V1.8). A customer may return 5 of the 50 boxes they bought,
-- so a return is per line and per quantity; the sale line keeps its original sold quantity and the
-- rows here accumulate against it.
CREATE TABLE IF NOT EXISTS return_lines (
  id           INTEGER PRIMARY KEY AUTOINCREMENT,
  return_id    INTEGER NOT NULL REFERENCES returns(id) ON DELETE CASCADE,
  sale_line_id INTEGER NOT NULL REFERENCES sale_lines(id),
  item_id      INTEGER NOT NULL REFERENCES items(id),
  quantity     INTEGER NOT NULL,                      -- in the sale line's unit type (5 boxes)
  units        INTEGER NOT NULL,                      -- single units put back into stock
  amount       REAL NOT NULL DEFAULT 0,               -- money refunded for this line
  cost         REAL NOT NULL DEFAULT 0                -- cost of the goods that came back
);
CREATE INDEX IF NOT EXISTS ix_return_lines_return ON return_lines(return_id);
CREATE INDEX IF NOT EXISTS ix_return_lines_line   ON return_lines(sale_line_id);

-- ---------------------------------------------------------------------
-- Debt transactions (FR-DBT-02/03)
-- ---------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS debt_transactions (
  id          INTEGER PRIMARY KEY AUTOINCREMENT,
  customer_id INTEGER NOT NULL REFERENCES customers(id),
  type        TEXT NOT NULL CHECK (type IN ('Charge','Payment')),
  amount      REAL NOT NULL,
  sale_id     INTEGER REFERENCES sales(id),
  user_id     INTEGER NOT NULL REFERENCES users(id),
  note        TEXT,
  created_at  TEXT NOT NULL DEFAULT (datetime('now','localtime'))
);
CREATE INDEX IF NOT EXISTS ix_debt_customer ON debt_transactions(customer_id, created_at);

-- ---------------------------------------------------------------------
-- Manual stock adjustments (FR-INV-04)
-- ---------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS stock_adjustments (
  id          INTEGER PRIMARY KEY AUTOINCREMENT,
  item_id     INTEGER NOT NULL REFERENCES items(id),
  batch_id    INTEGER,
  delta_units INTEGER NOT NULL,
  reason      TEXT NOT NULL,
  user_id     INTEGER NOT NULL REFERENCES users(id),
  created_at  TEXT NOT NULL DEFAULT (datetime('now','localtime'))
);
CREATE INDEX IF NOT EXISTS ix_adj_item ON stock_adjustments(item_id);

-- ---------------------------------------------------------------------
-- Audit log (FR-USR-03, D-11)
-- ---------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS audit_log (
  id         INTEGER PRIMARY KEY AUTOINCREMENT,
  user_id    INTEGER,
  action     TEXT NOT NULL,
  entity     TEXT,
  entity_id  INTEGER,
  details    TEXT,
  terminal   TEXT,
  created_at TEXT NOT NULL DEFAULT (datetime('now','localtime'))
);
CREATE INDEX IF NOT EXISTS ix_audit_created ON audit_log(created_at);

-- ---------------------------------------------------------------------
-- Settings (key/value, D-12)
-- ---------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS settings (
  key_name TEXT PRIMARY KEY,
  value    TEXT
);

-- ---------------------------------------------------------------------
-- Backup history (FR-BAK-01/03 — warn if none in 3 days)
-- ---------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS backups (
  id         INTEGER PRIMARY KEY AUTOINCREMENT,
  file_path  TEXT NOT NULL,
  size_bytes INTEGER NOT NULL DEFAULT 0,
  status     TEXT NOT NULL DEFAULT 'Success' CHECK (status IN ('Success','Failed')),
  created_at TEXT NOT NULL DEFAULT (datetime('now','localtime'))
);
CREATE INDEX IF NOT EXISTS ix_backups_created ON backups(created_at);

-- ---------------------------------------------------------------------
-- V1.2 — Employee management (attendance, salary, leaves, deductions,
-- expenses) and substitute drugs. Existing databases get these via the
-- startup migrations in DatabaseInitializer (ALTER/CREATE IF NOT EXISTS).
-- ---------------------------------------------------------------------

-- Attendance: a row per login (FR: auto-record employee login time).
CREATE TABLE IF NOT EXISTS attendance (
  id         INTEGER PRIMARY KEY AUTOINCREMENT,
  user_id    INTEGER NOT NULL REFERENCES users(id),
  login_at   TEXT NOT NULL DEFAULT (datetime('now','localtime')),
  terminal   TEXT
);
CREATE INDEX IF NOT EXISTS ix_attendance_user_day ON attendance(user_id, login_at);

-- Per-employee HR profile (salary is monthly, in SDG).
CREATE TABLE IF NOT EXISTS employee_profiles (
  user_id        INTEGER PRIMARY KEY REFERENCES users(id),
  monthly_salary REAL NOT NULL DEFAULT 0,
  notes          TEXT
);

-- Leaves / vacations.
CREATE TABLE IF NOT EXISTS leaves (
  id         INTEGER PRIMARY KEY AUTOINCREMENT,
  user_id    INTEGER NOT NULL REFERENCES users(id),
  from_date  TEXT NOT NULL,             -- "YYYY-MM-DD"
  to_date    TEXT NOT NULL,
  reason     TEXT,
  created_by INTEGER NOT NULL REFERENCES users(id),
  created_at TEXT NOT NULL DEFAULT (datetime('now','localtime'))
);
CREATE INDEX IF NOT EXISTS ix_leaves_user ON leaves(user_id, from_date);

-- Salary deductions.
CREATE TABLE IF NOT EXISTS deductions (
  id         INTEGER PRIMARY KEY AUTOINCREMENT,
  user_id    INTEGER NOT NULL REFERENCES users(id),
  amount     REAL NOT NULL,
  reason     TEXT NOT NULL,
  created_by INTEGER NOT NULL REFERENCES users(id),
  created_at TEXT NOT NULL DEFAULT (datetime('now','localtime'))
);
CREATE INDEX IF NOT EXISTS ix_deductions_user ON deductions(user_id, created_at);

-- Employee expenses: money taken or medicine dispensed (shows on the daily report).
CREATE TABLE IF NOT EXISTS employee_expenses (
  id         INTEGER PRIMARY KEY AUTOINCREMENT,
  user_id    INTEGER NOT NULL REFERENCES users(id),
  type       TEXT NOT NULL CHECK (type IN ('Money','Medicine')),
  amount     REAL NOT NULL DEFAULT 0,   -- money value (for Medicine: units*selling price)
  item_id    INTEGER REFERENCES items(id),
  units      INTEGER,                   -- single units, when type='Medicine'
  note       TEXT,
  created_at TEXT NOT NULL DEFAULT (datetime('now','localtime'))
);
CREATE INDEX IF NOT EXISTS ix_emp_exp_user_day ON employee_expenses(user_id, created_at);

-- ---------------------------------------------------------------------
-- V1.4 — Purchases ("المشتريات"): goods bought from people who come to
-- sell (bags/أكياس and other supplies). Standalone spend log, not tied to
-- the medicine catalog or stock. Cash purchases reduce the expected cash
-- in the drawer on the shift report.
-- ---------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS purchases (
  id            INTEGER PRIMARY KEY AUTOINCREMENT,
  supplier_name TEXT,                                 -- who it was bought from (optional)
  description   TEXT NOT NULL,                         -- what was bought (البيان)
  quantity      INTEGER NOT NULL DEFAULT 1,
  unit_price    REAL NOT NULL DEFAULT 0,               -- price per unit
  amount        REAL NOT NULL DEFAULT 0,               -- total paid = quantity * unit_price
  note          TEXT,
  user_id       INTEGER NOT NULL REFERENCES users(id), -- who recorded it
  created_at    TEXT NOT NULL DEFAULT (datetime('now','localtime'))
);
CREATE INDEX IF NOT EXISTS ix_purchases_created ON purchases(created_at);

-- ---------------------------------------------------------------------
-- Suppliers, their invoices and what is still owed on them (V2.1)
-- ---------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS suppliers (
  id         INTEGER PRIMARY KEY AUTOINCREMENT,
  name       TEXT NOT NULL,
  created_at TEXT NOT NULL DEFAULT (datetime('now','localtime'))
);
CREATE INDEX IF NOT EXISTS ix_suppliers_name ON suppliers(name);

-- One delivery. total is the sum of the lines and is never typed; amount_paid is moved only by
-- supplier_payments, so "outstanding" is always total - amount_paid and needs no column of its own.
CREATE TABLE IF NOT EXISTS purchase_invoices (
  id             INTEGER PRIMARY KEY AUTOINCREMENT,
  supplier_id    INTEGER NOT NULL REFERENCES suppliers(id),
  representative TEXT,                                  -- distributor/rep who brought it: name or phone
  invoice_number TEXT,                                  -- the supplier's own number, as printed
  invoice_date   TEXT NOT NULL,                         -- "YYYY-MM-DD"
  total          REAL NOT NULL DEFAULT 0,
  amount_paid    REAL NOT NULL DEFAULT 0,
  user_id        INTEGER NOT NULL REFERENCES users(id),
  created_at     TEXT NOT NULL DEFAULT (datetime('now','localtime'))
);
CREATE INDEX IF NOT EXISTS ix_pinv_supplier ON purchase_invoices(supplier_id, invoice_date);

-- A line becomes a stock batch when the invoice is saved; stock_batch_id is the link to it.
CREATE TABLE IF NOT EXISTS purchase_invoice_lines (
  id                 INTEGER PRIMARY KEY AUTOINCREMENT,
  invoice_id         INTEGER NOT NULL REFERENCES purchase_invoices(id) ON DELETE CASCADE,
  item_id            INTEGER NOT NULL REFERENCES items(id),
  item_name          TEXT NOT NULL,                     -- snapshot: renaming a drug must not rewrite paperwork
  quantity_boxes     INTEGER NOT NULL DEFAULT 0,
  strips_per_box     INTEGER NOT NULL DEFAULT 1,
  box_purchase_price REAL NOT NULL DEFAULT 0,
  box_selling_price  REAL NOT NULL DEFAULT 0,
  expiry_date        TEXT,
  batch_number       TEXT,
  stock_batch_id     INTEGER REFERENCES stock_batches(id)
);
CREATE INDEX IF NOT EXISTS ix_pinvline_invoice ON purchase_invoice_lines(invoice_id);

CREATE TABLE IF NOT EXISTS supplier_payments (
  id          INTEGER PRIMARY KEY AUTOINCREMENT,
  supplier_id INTEGER NOT NULL REFERENCES suppliers(id),
  invoice_id  INTEGER NOT NULL REFERENCES purchase_invoices(id),
  amount      REAL NOT NULL,
  user_id     INTEGER NOT NULL REFERENCES users(id),
  note        TEXT,
  payment_method TEXT,                              -- Cash | Bank; only cash leaves the drawer
  created_at  TEXT NOT NULL DEFAULT (datetime('now','localtime'))
);
CREATE INDEX IF NOT EXISTS ix_supplier_payments ON supplier_payments(supplier_id, created_at);
