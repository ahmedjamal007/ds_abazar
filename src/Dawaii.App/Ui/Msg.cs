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

        public static bool Confirm(string text, string caption = "تأكيد")
            => MessageBox.Show(text, caption, MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2, Rtl)
               == DialogResult.Yes;
    }
}
