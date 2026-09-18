using System.Windows.Forms;

namespace Dawaii.App.Ui
{
    /// <summary>Base for a screen hosted inside the main window's content area (RTL).</summary>
    public class ModuleControl : UserControl
    {
        protected AppServices Services => Session.Services;

        public ModuleControl()
        {
            RightToLeft = RightToLeft.Yes;
            Dock = DockStyle.Fill;
            BackColor = Theme.Background;
            Font = Theme.Base();
            Padding = new Padding(16);
        }

        /// <summary>Called by the host when the module becomes visible (refresh data here).</summary>
        public virtual void OnActivated() { }
    }
}
