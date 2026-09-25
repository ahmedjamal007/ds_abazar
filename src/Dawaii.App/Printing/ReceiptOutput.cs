using System;
using System.Windows.Forms;
using Dawaii.App.Ui;
using Dawaii.Core.Models;
using Dawaii.Core.Printing;

namespace Dawaii.App.Printing
{
    /// <summary>
    /// Getting a sale onto paper (V2.4).
    ///
    /// This is the one route from a <see cref="Sale"/> to a printer. It used to live inside the POS
    /// screen, which was fine while the POS was the only thing that printed; now the reprint module
    /// prints too, and "the reprint must look exactly like the original" is a promise that is only
    /// worth anything if both go through the same code rather than through two copies of it that can
    /// drift apart.
    ///
    /// Nothing here touches the database. Printing a sale — the first time or the tenth — changes no
    /// stock, no total and no balance; it draws what is already on file.
    /// </summary>
    public static class ReceiptOutput
    {
        /// <summary>
        /// Prints a sale.
        ///
        /// Role decides the format on the system's default printer (V1.3): the manager gets a full A4
        /// invoice, the cashier a narrow 80mm receipt.
        /// </summary>
        /// <param name="owner">The window to parent any dialog to.</param>
        /// <param name="sale">The sale to print. It is only read.</param>
        /// <param name="ask">
        /// True to let the user see or choose before paper is spent — a print dialog for the manager,
        /// an on-screen preview for the cashier. False sends it straight to the configured receipt
        /// printer, which is what completing a sale does.
        /// </param>
        public static void Print(IWin32Window owner, Sale sale, bool ask)
        {
            try
            {
                ReceiptInfo info = Session.Services.CreateReceiptInfo(
                    Session.CurrentUser.FullName ?? Session.CurrentUser.Username);

                if (Session.IsAdmin)
                {
                    InvoicePrinter.Print(owner, sale, info, showDialog: ask);
                    return;
                }

                if (ask)
                {
                    // Checked on screen first, rather than finding out what came off the roll after
                    // the paper is spent.
                    ThermalReceipt.ShowPreview(owner, sale, info);
                    return;
                }

                // The counter receipt goes to the head as a rasterised image (V2.2). Only if that
                // cannot be done — no printer, a PDF writer, a spooler that refuses — does it fall
                // back to drawing a page through the driver, which is what used to lose the labels.
                string reason;
                string configured = Session.Services.ReceiptPrinterName;
                if (ThermalReceipt.TryPrint(sale, info, configured, out reason)) return;

                // A virtual printer returns false with no reason — that is the intended fallback. A
                // real fault returns false WITH one, and used to be treated the same way: the receipt
                // silently went to the driver path, and the cashier was never told the thermal head
                // had refused. The fault is logged and the fallback is still attempted, so a receipt
                // still comes out when it can — but the cashier now knows to look at the printer.
                if (!string.IsNullOrEmpty(reason))
                {
                    Log.Error("Thermal receipt", new Exception(reason));
                    Msg.Warn("تعذّرت الطباعة على طابعة الإيصالات:\n" + reason +
                             "\n\nسيتم محاولة الطباعة عبر تعريف الطابعة. تحقق من الطابعة.");
                }
                ReceiptDocumentPrinter.Print(owner, sale, info, showDialog: false);
            }
            catch (Exception ex)
            {
                // The sale is already committed; a printer that is off or unplugged must not look like
                // a failed sale. Say so, and say what to do.
                Log.Error("Receipt print", ex);
                Msg.Warn("تعذّرت الطباعة: " + ex.Message +
                         "\n\nالفاتورة محفوظة. يمكن إعادة طباعتها من شاشة \"إعادة طباعة فاتورة\" بعد فحص الطابعة.");
            }
        }
    }
}
