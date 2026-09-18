using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Dawaii.App.Ui
{
    public enum FieldIcon { None, Person, Lock }

    /// <summary>
    /// A rounded, RTL input field matching the design: a light fill with a border that highlights on
    /// focus, a leading icon (person/lock) on the right, a native placeholder, and — for passwords —
    /// an eye toggle on the left.
    /// </summary>
    public class RoundedTextField : UserControl
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);
        private const int EM_SETCUEBANNER = 0x1501;

        private readonly TextBox _box = new TextBox();
        private readonly FieldIcon _icon;
        private readonly bool _password;
        private string _placeholder = "";
        private bool _focused;
        private const int IconSize = 22;

        /// <summary>Colour of the card this field sits on (used to clear the rounded corners).</summary>
        public Color SurfaceColor { get; set; } = Theme.Surface;

        public event EventHandler TextChangedEx;
        public event KeyEventHandler KeyDownEx;

        public RoundedTextField(FieldIcon icon = FieldIcon.None, bool password = false)
        {
            _icon = icon;
            _password = password;

            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Height = 52;
            BackColor = Color.Transparent;

            _box.BorderStyle = BorderStyle.None;
            _box.BackColor = Theme.InputBg;
            _box.ForeColor = Theme.TextPrimary;
            _box.Font = Theme.Base(13f);
            _box.RightToLeft = RightToLeft.Yes;
            _box.UseSystemPasswordChar = password;
            _box.GotFocus += (s, e) => { _focused = true; Invalidate(); };
            _box.LostFocus += (s, e) => { _focused = false; Invalidate(); };
            _box.TextChanged += (s, e) => TextChangedEx?.Invoke(this, e);
            _box.KeyDown += (s, e) => KeyDownEx?.Invoke(this, e);
            Controls.Add(_box);
        }

        public override string Text
        {
            get => _box.Text;
            set => _box.Text = value;
        }

        public string Placeholder
        {
            get => _placeholder;
            set { _placeholder = value ?? ""; if (IsHandleCreated) ApplyCue(); }
        }

        public void FocusInput() => _box.Focus();
        public void SelectAllInput() => _box.SelectAll();

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            ApplyCue();
        }

        private void ApplyCue()
        {
            try { SendMessage(_box.Handle, EM_SETCUEBANNER, (IntPtr)1, _placeholder); } catch { }
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            int right = Width - 14 - IconSize - 6;                 // clear the leading icon (right)
            int left = _password ? 12 + IconSize + 6 : 16;         // clear the eye toggle (left)
            _box.SetBounds(left, (Height - _box.PreferredHeight) / 2, right - left, _box.PreferredHeight);
        }

        private RectangleF LeadingIconRect => new RectangleF(Width - 14 - IconSize, (Height - IconSize) / 2f, IconSize, IconSize);
        private RectangleF EyeRect => new RectangleF(12, (Height - IconSize) / 2f, IconSize, IconSize);

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (_password && EyeRect.Contains(e.Location))
            {
                _box.UseSystemPasswordChar = !_box.UseSystemPasswordChar;
                Invalidate();
                _box.Focus();
            }
            else _box.Focus();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            using (var bg = new SolidBrush(SurfaceColor))
                g.FillRectangle(bg, ClientRectangle);

            var r = new RectangleF(1, 1, Width - 2, Height - 2);
            Gfx.FillRounded(g, r, 10, Theme.InputBg);
            Gfx.DrawRoundedBorder(g, r, 10, _focused ? Theme.Primary : Theme.InputBorder, _focused ? 1.6f : 1f);

            Color ic = _focused ? Theme.Primary : Theme.TextMuted;
            if (_icon == FieldIcon.Person) Gfx.PersonIcon(g, LeadingIconRect, ic);
            else if (_icon == FieldIcon.Lock) Gfx.LockIcon(g, LeadingIconRect, ic);

            if (_password) Gfx.EyeIcon(g, EyeRect, Theme.TextMuted, !_box.UseSystemPasswordChar);
        }
    }
}
