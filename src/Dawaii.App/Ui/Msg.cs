using System.Windows.Forms;

namespace Dawaii.App.Ui
{
    /// <summary>RTL-aware message boxes.</summary>
    public static class Msg
    {
        private const MessageBoxOptions Rtl =
            MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign;

        public static void Info(string text, string caption = "دوائي")
            => MessageBox.Show(text, caption, MessageBoxButtons.OK, MessageBoxIcon.Information, MessageBoxDefaultButton.Button1, Rtl);

        public static void Error(string text, string caption = "خطأ")
            => MessageBox.Show(text, caption, MessageBoxButtons.OK, MessageBoxIcon.Error, MessageBoxDefaultButton.Button1, Rtl);

        public static void Warn(string text, string caption = "تنبيه")
            => MessageBox.Show(text, caption, MessageBoxButtons.OK, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button1, Rtl);

        /// <summary>
        /// A three-way question: Yes / No / Cancel. For the case where "no" and "don't do anything"
        /// are genuinely different answers — closing an invoice with unsaved lines means keep it,
        /// discard it, or go back and carry on typing, and collapsing that into Yes/No loses the
        /// third, which is the safe one.
        /// </summary>
        public static DialogResult Ask(string text, string caption = "تأكيد")
            => MessageBox.Show(text, caption, MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button1, Rtl);

        public static bool Confirm(string text, string caption = "تأكيد")
            => MessageBox.Show(text, caption, MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2, Rtl)
               == DialogResult.Yes;
    }
}
