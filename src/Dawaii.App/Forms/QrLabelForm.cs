using System;
using System.Drawing;
using System.Drawing.Printing;
using System.IO;
using System.Windows.Forms;
using Dawaii.App.Ui;
using Dawaii.Core.Models;
using Dawaii.Core.Printing;

namespace Dawaii.App.Forms
{
    /// <summary>Shows and prints a QR label for an item's code (FR-QRC-05).</summary>
    public class QrLabelForm : BaseForm
    {
        private readonly Item _item;
        private readonly string _code;
        private Bitmap _qr;

        public QrLabelForm(Item item, string code)
        {
            _item = item;
            _code = code;
            _qr = LoadQr(code);

            Text = "رمز QR للصنف";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            ClientSize = new Size(340, 420);

            var pic = new PictureBox { Image = _qr, SizeMode = PictureBoxSizeMode.Zoom, Size = new Size(240, 240), Location = new Point(50, 20) };
            var name = new Label { Text = item.NameEn, Font = Theme.Base(14f, FontStyle.Bold), AutoSize = false, TextAlign = ContentAlignment.MiddleCenter, Size = new Size(300, 30), Location = new Point(20, 270) };
            var codeLbl = new Label { Text = code + "   •   " + BoxPriceText(item), Font = Theme.Base(11f), ForeColor = Theme.TextMuted, AutoSize = false, TextAlign = ContentAlignment.MiddleCenter, Size = new Size(300, 24), Location = new Point(20, 302) };

            var print = new Button { Text = "طباعة", Size = new Size(140, 40), Location = new Point(30, 350) };
            var close = new Button { Text = "إغلاق", DialogResult = DialogResult.Cancel, Size = new Size(140, 40), Location = new Point(180, 350) };
            Theme.StylePrimaryButton(print);
            Theme.StyleSecondaryButton(close);
            print.Click += (s, e) => Print();

            Controls.Add(pic);
            Controls.Add(name);
            Controls.Add(codeLbl);
            Controls.Add(print);
            Controls.Add(close);
        }

        private static string BoxPriceText(Item item)
            => item.SellingPrice.HasValue
                ? Fmt.Money(item.SellingPrice.Value * item.UnitsPerBox) + " / علبة"
                : "بدون سعر";

        private static Bitmap LoadQr(string code)
        {
            byte[] png = QrCodeGenerator.PngBytes(code, 10);
            using (var ms = new MemoryStream(png))
                return new Bitmap(ms);
        }

        private void Print()
        {
            try
            {
                using (var doc = new PrintDocument())
                {
                    doc.PrintPage += (s, e) =>
                    {
                        var g = e.Graphics;
                        int size = 180;
                        int x = e.MarginBounds.Left;
                        int y = e.MarginBounds.Top;
                        g.DrawImage(_qr, x, y, size, size);
                        using (var f = new Font(Theme.FontFamily, 12, FontStyle.Bold))
                            g.DrawString(_item.NameEn, f, Brushes.Black, x, y + size + 6);
                        using (var f = new Font(Theme.FontFamily, 10))
                            g.DrawString(_code + "  " + BoxPriceText(_item), f, Brushes.Black, x, y + size + 28);
                    };
                    using (var dlg = new PrintDialog { Document = doc })
                        if (dlg.ShowDialog(this) == DialogResult.OK) doc.Print();
                }
            }
            catch (Exception ex) { Msg.Error("تعذّرت الطباعة: " + ex.Message); }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _qr?.Dispose();
            base.Dispose(disposing);
        }
    }
}
