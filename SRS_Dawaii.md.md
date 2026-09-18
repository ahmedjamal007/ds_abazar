# Software Requirements Specification (SRS)
## دوائي (Dawaii) — Offline Pharmacy Management System

**Version:** 1.0 Draft
**Date:** July 2026
**Target market:** Single, independent pharmacies in Sudan

---

## 1. Introduction

### 1.1 Purpose
This document specifies the requirements for **دوائي (Dawaii)**, an offline pharmacy management system for a single pharmacy. It is intended for the development team, testers, and stakeholders (pharmacy owners) to agree on what the system must do before development begins.

### 1.2 Scope
Dawaii manages the daily operations of one pharmacy: selling medicines, tracking inventory and expiry dates, managing prices, recording customer debts, and producing daily reports. The system operates **fully offline** with no internet dependency, on one main computer with the option of one or more additional counter computers connected over the pharmacy's local network.

Out of scope for version 1.0: multi-branch synchronization, online/cloud features, supplier purchase orders, insurance claims, and mobile applications.

### 1.3 Definitions and Abbreviations
| Term | Definition |
|---|---|
| Main computer | The PC that hosts the MySQL database. |
| Counter / terminal | Any PC running the Dawaii client, including the main computer. |
| Item / medicine | Any product sold by the pharmacy (medicines, cosmetics, supplies). |
| Strip (شريط) | A sub-unit of a medicine box (blister strip). |
| Credit sale | A sale recorded against a customer's debt account instead of cash. |
| SDG | Sudanese Pound. |
| SRS | Software Requirements Specification. |

### 1.4 Operating Environment
- **OS:** Windows 7 SP1 or later (32/64-bit) — chosen because older machines are common in Sudanese pharmacies.
- **Framework:** .NET Framework 4.8 (WinForms or WPF desktop client).
- **Database:** MySQL Server (local instance on the main computer).
- **Network:** Optional local LAN/Wi-Fi (no internet) for additional counters.
- **Hardware (minimum):** 2 GB RAM, dual-core CPU, 10 GB free disk.
- **Optional peripherals:** USB receipt printer (ESC/POS), USB barcode/QR scanner (keyboard-emulation type), UPS.

### 1.5 Constraints and Assumptions
- C-1: The system must function with **zero internet connectivity**, permanently.
- C-2: Frequent, unannounced power cuts must never corrupt data or lose a committed sale.
- C-3: The primary interface language is **Arabic (RTL)**; English is secondary.
- C-4: Users may have minimal computer literacy; core workflows must be learnable in under one hour.
- C-5: Prices change frequently due to currency fluctuation; bulk price updates must be fast.
- A-1: Exactly one main computer hosts the database; additional counters depend on it being powered on.
- A-2: The pharmacy owner is responsible for keeping at least one recent backup copy (USB).

---

## 2. Overall Description

### 2.1 User Classes
| Role | Description | Typical permissions |
|---|---|---|
| Owner / Admin | Pharmacy owner or manager | Everything: stock, prices, users, reports, backups, settings |
| Pharmacist / Cashier | Counter staff | Sell, view stock, record debts; cannot change prices, delete records, or view profit reports |

### 2.2 Major Functions (summary)
1. Point of sale (fast selling screen)
2. Inventory and stock management
3. Expiry-date tracking and alerts
4. Pricing management (single and bulk updates)
5. Customer debt (credit) ledger
6. Reports (daily sales, profit, low stock, near-expiry, debts)
7. Optional QR/barcode identification of items
8. User accounts and roles
9. Backup and restore
10. Multi-terminal operation on a local network

---

## 3. Functional Requirements

### 3.1 Point of Sale (POS)
- **FR-POS-01:** The system shall allow searching items by name as the user types (incremental search), matching both Arabic and English names.
- **FR-POS-02:** The system shall display, for each search result: item name, selling price, available quantity, and nearest expiry date.
- **FR-POS-03:** The system shall allow adding multiple items to one sale, editing quantities, and removing lines before completing the sale.
- **FR-POS-04:** The system shall support selling by **box, strip, or single unit**, according to the unit configuration of each item (e.g., 1 box = 10 strips = 100 tablets).
- **FR-POS-05:** On completing a sale, the system shall atomically save the sale and reduce stock in a single database transaction.
- **FR-POS-06:** The system shall support cash sales and credit sales (see 3.5).
- **FR-POS-07:** The system shall optionally print a receipt on an ESC/POS receipt printer; sales must be completable without a printer.
- **FR-POS-08:** The system shall allow an authorized user (Admin) to cancel/return a sale, restoring stock and recording the return.
- **FR-POS-09:** The system shall warn (but not block) when selling an item whose nearest expiry is within the configured warning period.
- **FR-POS-10:** A complete cash sale of one item shall be achievable using keyboard only (search → Enter → quantity → Enter).

### 3.2 Inventory Management
- **FR-INV-01:** The system shall allow creating items with: Arabic name, English/trade name, generic name (optional), category, unit structure (box/strip/unit), purchase price, selling price, and optional QR/barcode.
- **FR-INV-02:** The system shall record stock entries (received shipments) with quantity, batch expiry date, and purchase price per batch.
- **FR-INV-03:** The system shall track stock quantity per item and per expiry batch, deducting from the nearest-expiry batch first (FEFO) by default.
- **FR-INV-04:** The system shall allow manual stock adjustment (damage, loss, correction) with a mandatory reason, restricted to Admin.
- **FR-INV-05:** The system shall flag items at or below their configurable minimum-quantity threshold.
- **FR-INV-06:** The system shall provide a bulk stock-entry screen optimized for first-time data entry (keyboard-only flow, duplicate-name detection).

### 3.3 Expiry Management
- **FR-EXP-01:** The system shall list items expiring within a configurable window (default 90 days), sorted by date.
- **FR-EXP-02:** The system shall show an expiry alert summary on the home screen at login.
- **FR-EXP-03:** The system shall allow marking expired batches as disposed, removing them from sellable stock and recording the loss value.

### 3.4 Pricing
- **FR-PRC-01:** The system shall allow Admin to edit an item's selling price at any time; historical sales keep the price at time of sale.
- **FR-PRC-02:** The system shall support bulk price updates: by percentage across all items or a category, and by editable list.
- **FR-PRC-03:** The system shall record purchase price per batch so profit per sale line is computable.

### 3.5 Customer Debts (Credit Ledger)
- **FR-DBT-01:** The system shall maintain customer records: name, phone (optional), running balance.
- **FR-DBT-02:** The system shall allow completing a sale as credit, linked to a customer, increasing their balance.
- **FR-DBT-03:** The system shall record partial or full payments against a customer balance.
- **FR-DBT-04:** The system shall display a statement per customer (sales, payments, balance) and a total-outstanding-debts report.

### 3.6 QR / Barcode Support (Optional Module)
This module is optional: every function in the system must remain fully usable without a scanner or codes.

- **FR-QRC-01:** The system shall allow associating each item with one or more codes (QR code or standard barcode value) stored as text.
- **FR-QRC-02:** The system shall accept input from keyboard-emulation USB scanners in any search field without special drivers or configuration.
- **FR-QRC-03 (search by code):** When a code is scanned or typed in the POS or inventory search, the system shall instantly find and select the matching item everywhere search is available.
- **FR-QRC-04 (unknown code → set details):** When a scanned code matches no item, the system shall offer to open the item screen with the code pre-filled, so the user can set the item's details (name, prices, units, expiry) and save it — creating a new item or attaching the code to an existing item.
- **FR-QRC-05:** The system shall be able to generate and print QR code labels for items that have no manufacturer barcode, using the item's internal code.
- **FR-QRC-06:** The system shall prevent assigning the same code to two different items and shall warn on the attempt.
- **FR-QRC-07:** Scanning an item's code at the POS shall add it directly to the current sale with quantity 1 (subsequent scans increment quantity).

### 3.7 Users and Roles
- **FR-USR-01:** The system shall require login with username and password.
- **FR-USR-02:** The system shall support the two roles in 2.1 with the stated permission boundaries.
- **FR-USR-03:** Every sale, return, stock adjustment, and price change shall record which user performed it and when.

### 3.8 Reports
- **FR-RPT-01:** Daily report: total sales, total profit, number of transactions, cash vs credit totals, per selected date.
- **FR-RPT-02:** Best-selling items and dead stock (no sales in N days) reports.
- **FR-RPT-03:** Low-stock report and near-expiry report (printable).
- **FR-RPT-04:** Outstanding debts report per customer and in total.
- **FR-RPT-05:** Profit reports shall be visible to Admin only.

### 3.9 Backup and Restore
- **FR-BAK-01:** The system shall perform an automatic daily backup of the database to a configurable folder (e.g., a USB drive), keeping the last N copies.
- **FR-BAK-02:** The system shall allow manual one-click backup and Admin-only restore.
- **FR-BAK-03:** The system shall warn on the home screen if no successful backup exists in the last 3 days.

### 3.10 Multi-Terminal Operation
- **FR-NET-01:** The installer shall ask whether the machine is the **main computer** (installs MySQL + app) or an **additional counter** (installs app only, prompts for main computer address).
- **FR-NET-02:** All terminals shall operate on the single shared database in real time; a sale on one terminal is immediately reflected on others.
- **FR-NET-03:** If a counter loses connection to the main computer, it shall display a clear Arabic message and retry automatically; it shall not crash or corrupt data.

---

## 4. Non-Functional Requirements

- **NFR-01 (Performance):** Item search results shall appear within 300 ms for a catalog of up to 20,000 items on minimum hardware.
- **NFR-02 (Reliability):** A power loss at any moment shall never leave a partially committed sale; on restart the system shall recover to a consistent state (transactional writes, InnoDB).
- **NFR-03 (Usability):** Full Arabic RTL interface; all POS actions available via keyboard; fonts and layouts legible on 1024×768 screens.
- **NFR-04 (Security):** Passwords stored hashed; profit data and destructive actions restricted to Admin; database not accessible without credentials.
- **NFR-05 (Data retention):** All sales and stock history retained for at least 5 years without archiving.
- **NFR-06 (Installability):** A non-technical user shall be able to install the main computer setup in under 30 minutes using a single installer.
- **NFR-07 (Offline):** No feature may require internet access; the application shall never block or degrade due to absent connectivity.

---

## 5. Data Entities (High Level)
Items, ItemCodes (QR/barcode values), StockBatches, Sales, SaleLines, Returns, Customers, DebtTransactions, Users, PriceHistory, AuditLog, Settings.

---

## 6. Acceptance Criteria Highlights
1. Sell 3 items including one by strip, keyboard-only, in under 30 seconds.
2. Pull power from the main PC mid-sale; on reboot, no partial sale exists and stock is consistent.
3. Scan an unknown QR code → item creation screen opens with the code pre-filled → save → rescan finds the item instantly.
4. Update all prices in a category by +15% in one operation.
5. Second counter sells the last unit of an item; first counter immediately shows zero stock.
6. Restore yesterday's backup on a fresh machine and resume selling.
