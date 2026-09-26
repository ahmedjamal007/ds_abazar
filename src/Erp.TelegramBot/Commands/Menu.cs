using Erp.TelegramBot.Telegram;

namespace Erp.TelegramBot.Commands;

/// <summary>
/// The menus, in Arabic.
///
/// The bot exists for a pharmacy owner standing somewhere that is not their office, wanting one
/// number. Typing "/report sales week" is a thing a developer does; pressing تقارير then مبيعات
/// اليوم is a thing anybody does. So the commands stay — they are faster once learned — but nobody
/// has to know one to use this.
///
/// Every screen is reachable in at most two presses from the main menu, and every screen has a way
/// back. A menu you can get lost in on a phone is a menu people stop opening.
///
/// The callback tokens are deliberately terse. Telegram caps callback data at 64 BYTES, and Arabic
/// is two to three bytes a character — a token carrying a readable Arabic label would breach that
/// silently, and the button would simply fail to work.
/// </summary>
public static class Menu
{
    // Screens.
    public const string Main = "m:main";
    public const string Reports = "m:rep";
    public const string People = "m:ppl";
    public const string Search = "m:find";

    // Actions. Short for the 64-byte limit; never shown to anybody.
    public const string SalesToday = "r:today";
    public const string SalesByEmployee = "r:emp";
    public const string Shifts = "r:shift";
    public const string Purchases = "r:buy";
    public const string PriceList = "r:prices";
    public const string Expiring = "r:exp";
    public const string LowStock = "r:low";

    public const string Customers = "r:cust";
    public const string Suppliers = "r:supp";
    public const string CustomerDebt = "r:custdebt";
    public const string SupplierDebt = "r:suppdebt";

    /// <summary>True for a token this menu owns, so the router can tell it from a paging button.</summary>
    public static bool Owns(string? data)
        => data != null && (data.StartsWith("m:", StringComparison.Ordinal) ||
                            data.StartsWith("r:", StringComparison.Ordinal));

    /// <summary>
    /// The welcome. Introduces what this is, because a bot that opens with a bare menu leaves the
    /// owner guessing whether it can change anything — and it cannot.
    /// </summary>
    public static Reply Welcome(string pharmacyName) => new(
        "مرحباً بك في مساعد " + pharmacyName + "\n" +
        "\n" +
        "أنا المساعد الذكي لبرنامج دوائي لإدارة الصيدلية.\n" +
        "أعرض لك المبيعات والمخزون والعملاء والموردين من هاتفك، دون الحاجة لفتح جهاز الصيدلية.\n" +
        "\n" +
        "🔒 للاطّلاع فقط — لا يمكنني تعديل أي بيانات.\n" +
        "\n" +
        "اختر من القائمة:",
        MainButtons);

    public static Reply MainMenu() => new("القائمة الرئيسية — اختر القسم:", MainButtons);

    private static IReadOnlyList<Button> MainButtons =>
    [
        new Button("📊 التقارير", Reports),
        new Button("👥 العملاء والموردون", People),
        new Button("🔎 البحث", Search),
        new Button("❓ مساعدة", "m:help"),
    ];

    public static Reply ReportsMenu() => new(
        "📊 التقارير — اختر التقرير:",
        [
            new Button("مبيعات اليوم", SalesToday),
            new Button("مبيعات حسب الموظف", SalesByEmployee),
            new Button("تقرير الورديات", Shifts),
            new Button("تقرير المشتريات", Purchases),
            new Button("المخزون المنخفض", LowStock),
            new Button("قرب الانتهاء", Expiring),
            new Button("⬅️ رجوع", Main),
        ]);

    public static Reply PeopleMenu() => new(
        "👥 العملاء والموردون:",
        [
            new Button("مديونية العملاء", CustomerDebt),
            new Button("مديونية الموردين", SupplierDebt),
            new Button("قائمة العملاء", Customers),
            new Button("قائمة الموردين", Suppliers),
            new Button("⬅️ رجوع", Main),
        ]);

    public static Reply SearchMenu() => new(
        "🔎 البحث\n" +
        "\n" +
        "اكتب مباشرةً:\n" +
        "• اسم الصنف أو الباركود — للسعر والمتوفر\n" +
        "• العلامة *  — لقائمة الأسعار كاملة\n" +
        "\n" +
        "أو اختر:",
        [
            new Button("قائمة الأسعار كاملة", PriceList),
            new Button("المخزون المنخفض", LowStock),
            new Button("⬅️ رجوع", Main),
        ]);

    /// <summary>
    /// Help. Lists the typed commands as a shortcut, not as the only way in — the menu above is the
    /// way in, and somebody who never reads this should still be able to use the bot.
    /// </summary>
    public static Reply Help() => new(
        "❓ المساعدة\n" +
        "\n" +
        "الأسرع: اكتب اسم الصنف أو امسح الباركود مباشرةً، وسيصلك السعر والمتوفر.\n" +
        "\n" +
        "الأوامر المختصرة:\n" +
        "/stock صنف — المتوفر وتفاصيل الدفعات\n" +
        "/low — الأصناف عند حد الطلب\n" +
        "/sales today أو week أو month — ملخص المبيعات\n" +
        "/report — تقرير كملف\n" +
        "/menu — القائمة الرئيسية\n" +
        "\n" +
        "🔒 هذا المساعد للاطّلاع فقط ولا يعدّل أي بيانات، وهو متاح لمدير الصيدلية وحده.",
        [new Button("⬅️ القائمة الرئيسية", Main)]);

    /// <summary>A row with just a way back, put under every report.</summary>
    public static IReadOnlyList<Button> BackTo(string screen) =>
        [new Button("⬅️ رجوع", screen), new Button("🏠 الرئيسية", Main)];
}
