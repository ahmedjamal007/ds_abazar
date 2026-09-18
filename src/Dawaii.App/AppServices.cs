using Dawaii.Core.Abstractions;
using Dawaii.Core.Data;
using Dawaii.Core.Printing;
using Dawaii.Core.Services;

namespace Dawaii.App
{
    /// <summary>
    /// Composition root: builds the SQLite connection factory, repositories and services once and
    /// hands them to the forms. Kept deliberately simple (no DI container) for a small desktop app.
    /// </summary>
    public class AppServices
    {
        public IDbConnectionFactory Db { get; }
        public DatabaseInitializer Initializer { get; }

        // Repositories
        public IUserRepository Users { get; }
        public ISettingsRepository Settings { get; }
        public IAuditRepository Audit { get; }
        public IItemRepository Items { get; }
        public IStockRepository Stock { get; }
        public ICustomerRepository Customers { get; }
        public ISaleStore Sales { get; }
        public IDebtStore DebtStore { get; }
        public IItemCodeRepository ItemCodes { get; }
        public IReportRepository ReportRepo { get; }
        public IBackupRepository BackupRepo { get; }
        public IEmployeeRepository EmployeeRepo { get; }
        public IPurchaseRepository PurchaseRepo { get; }
        public ISupplierRepository SupplierRepo { get; }

        // Services
        public AuthService Auth { get; }
        public UserService UserService { get; }
        public InventoryService Inventory { get; }

        /// <summary>Profit multiplier, practical rounding and hand-set prices (V2.3 إدارة الأسعار).</summary>
        public PricingService Pricing { get; }
        public PosService Pos { get; }
        public DebtService Debts { get; }
        public CodeService Codes { get; }
        public ReportService Reports { get; }
        public BackupService Backup { get; }
        public EmployeeService Employees { get; }
        public PurchaseService Purchases { get; }
        public SupplierService Suppliers { get; }

        public AppServices(IDbConnectionFactory db)
        {
            Db = db;
            Initializer = new DatabaseInitializer(Db);

            Users = new SqliteUserRepository(Db);
            Settings = new SqliteSettingsRepository(Db);
            Audit = new SqliteAuditRepository(Db);
            Items = new SqliteItemRepository(Db);
            Stock = new SqliteStockRepository(Db);
            Customers = new SqliteCustomerRepository(Db);
            Sales = new SqliteSaleStore(Db);
            DebtStore = new SqliteDebtStore(Db);
            ItemCodes = new SqliteItemCodeRepository(Db);
            ReportRepo = new SqliteReportRepository(Db);
            BackupRepo = new SqliteBackupRepository(Db);
            EmployeeRepo = new SqliteEmployeeRepository(Db);
            PurchaseRepo = new SqlitePurchaseRepository(Db);
            SupplierRepo = new SqliteSupplierRepository(Db);

            Auth = new AuthService(Users);
            UserService = new UserService(Users);
            Inventory = new InventoryService(Items, Stock, Settings, Audit, ItemCodes);
            Pricing = new PricingService(Items, Settings, Audit);
            Pos = new PosService(Items, Stock, Sales, Customers, Settings, Audit);
            Debts = new DebtService(Customers, DebtStore, Audit);
            Codes = new CodeService(ItemCodes, Items, Audit);
            Reports = new ReportService(Sales, ReportRepo);
            Backup = new BackupService(Db, Settings, BackupRepo);
            Employees = new EmployeeService(EmployeeRepo, Users, Items, Stock, Sales, Audit);
            Purchases = new PurchaseService(PurchaseRepo, Audit);
            Suppliers = new SupplierService(SupplierRepo, Items, Audit);
        }

        /// <summary>The printer the shop named for receipts, or null to use Windows' default.</summary>
        public string ReceiptPrinterName => SafeGet("receipt_printer_name");

        /// <summary>Builds a receipt printer from settings: ESC/POS if a printer name is set, else no-op.</summary>
        public IReceiptPrinter CreateReceiptPrinter()
        {
            string name = SafeGet("receipt_printer_name");
            return string.IsNullOrWhiteSpace(name) ? (IReceiptPrinter)new NullReceiptPrinter() : new EscPosReceiptPrinter(name);
        }

        public ReceiptInfo CreateReceiptInfo(string cashierName)
        {
            int width = int.TryParse(SafeGet("receipt_width"), out int w) ? w : 32;
            return new ReceiptInfo
            {
                PharmacyName = SafeGet("pharmacy_name") ?? "دوائي",
                Currency = SafeGet("currency") ?? "ج.س",
                CashierName = cashierName,
                Width = width
            };
        }

        private string SafeGet(string key)
        {
            try { return Settings.Get(key); } catch { return null; }
        }
    }
}
