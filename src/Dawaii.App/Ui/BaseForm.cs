using System.Windows.Forms;

namespace Dawaii.App.Ui
{
    /// <summary>
    /// Base for every window: enforces Arabic RTL layout (NFR-03) and the shared font/icon.
    /// FR requirement: all forms set RightToLeft = Yes and RightToLeftLayout = true.
    /// </summary>
    public class BaseForm : Form
    {
        public BaseForm()
        {
            RightToLeft = RightToLeft.Yes;
            RightToLeftLayout = true;
            Font = Theme.Base();
            BackColor = Theme.Background;
            ForeColor = Theme.TextPrimary;
            StartPosition = FormStartPosition.CenterScreen;
            if (AppImages.AppIcon != null) Icon = AppImages.AppIcon;
        }
    }
}
