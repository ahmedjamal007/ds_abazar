using System;
using System.Drawing;
using System.Windows.Forms;
using Dawaii.App.Ui;
using Dawaii.Core;
using Dawaii.Core.Models;

namespace Dawaii.App.Forms
{
    /// <summary>
    /// The item's barcode/QR code (FR-QRC-01/06). One code per item (V1.9): typing or scanning a new
    /// one replaces what was there, so there is no list to manage. A drug with no manufacturer barcode
    /// gets an internal QR code from "طباعة QR" — generated, saved into the same single slot, and
    /// printed to stick on the box.
    /// </summary>
    public class CodesForm : BaseForm
    {
        private readonly Item _item;
        private Label _code;

        public CodesForm(Item item)
        {
            _item = item;
            Text = "رمز الصنف: " + item.NameEn;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            ClientSize = new Size(420, 300);
            RightToLeft = RightToLeft.Yes;

            var caption = new Label
            {
                Text = "الرمز الحالي", Location = new Point(20, 20), Size = new Size(380, 24),
                Font = Theme.Base(10.5f), ForeColor = Theme.TextMuted, TextAlign = ContentAlignment.MiddleRight
            };
            _code = new Label
            {
                Location = new Point(20, 48), Size = new Size(380, 46), Font = Theme.Base(15f, FontStyle.Bold),
                ForeColor = Theme.Primary, TextAlign = ContentAlignment.MiddleRight
            };
            var hint = new Label
            {
                Text = "امسح الباركود أو رمز QR بالماسح، أو أدخله يدوياً. الرمز الجديد يحل محل السابق.",
                Location = new Point(20, 96), Size = new Size(380, 40), Font = Theme.Base(9f),
                ForeColor = Theme.TextMuted, TextAlign = ContentAlignment.MiddleRight
            };

            var scan = new Button { Text = "مسح / إدخال الرمز", Size = new Size(185, 40), Location = new Point(215, 146) };
            var remove = new Button { Text = "حذف الرمز", Size = new Size(185, 40), Location = new Point(20, 146) };
            var qr = new Button { Text = "طباعة QR", Size = new Size(185, 40), Location = new Point(215, 196) };
            var close = new Button { Text = "إغلاق", DialogResult = DialogResult.OK, Size = new Size(185, 40), Location = new Point(20, 196) };
            Theme.StyleSecondaryButton(scan); Theme.StyleSecondaryButton(remove); Theme.StyleSecondaryButton(qr);
            Theme.StylePrimaryButton(close);

            scan.Click += (s, e) => SetCode(Prompt.Show("امسح الباركود / رمز QR أو أدخله يدوياً", "رمز الصنف"));
            remove.Click += (s, e) => RemoveCode();
            qr.Click += (s, e) => PrintQr();

            Controls.AddRange(new Control[] { caption, _code, hint, scan, remove, qr, close });
            AcceptButton = close;
            Reload();
        }

        private void Reload()
        {
            string code = Session.Services.Codes.GetCode(_item.Id);
            bool has = !string.IsNullOrEmpty(code);
            _code.Text = has ? code : "— لا يوجد رمز —";
            _code.ForeColor = has ? Theme.Primary : Theme.TextMuted;
        }

        private void SetCode(string code)
        {
            if (string.IsNullOrWhiteSpace(code)) return;
            try { Session.Services.Codes.SetCode(Session.CurrentUser, _item.Id, code); Reload(); }
            catch (DomainException ex) { Msg.Error(ex.Message); }
        }

        private void RemoveCode()
        {
            if (string.IsNullOrEmpty(Session.Services.Codes.GetCode(_item.Id))) { Msg.Info("لا يوجد رمز لحذفه."); return; }
            if (!Msg.Confirm("حذف رمز هذا الصنف؟ لن يعود المسح الضوئي يجده حتى تضيف رمزاً آخر.")) return;
            try { Session.Services.Codes.RemoveCode(Session.CurrentUser, _item.Id); Reload(); }
            catch (DomainException ex) { Msg.Error(ex.Message); }
        }

        private void PrintQr()
        {
            try
            {
                // Prints whatever the item already carries; only an item with no code at all gets an
                // internal one generated here, so a manufacturer barcode is never overwritten.
                string code = Session.Services.Codes.EnsureInternalCode(Session.CurrentUser, _item.Id);
                Reload();
                using (var f = new QrLabelForm(_item, code)) f.ShowDialog(this);
            }
            catch (DomainException ex) { Msg.Error(ex.Message); }
        }
    }
}
