using System;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;
using Dawaii.App.Ui;
using Dawaii.Core.Services;

namespace Dawaii.App.Forms
{
    /// <summary>Login / splash screen (FR-USR-01) — two-panel design (branding + login card).</summary>
    public class LoginForm : BaseForm
    {
        private readonly AppServices _services;
        private RoundedTextField _username, _password;
        private CheckBox _remember;
        private Label _error;
        private PillButton _loginButton;

        private static readonly string RememberFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Dawaii", "last_user.txt");

        // Only the USERNAME is remembered (V2.3). Until then the password was stored too, DPAPI-encrypted
        // under the CURRENT WINDOWS USER — which protects it from other Windows accounts and from nobody
        // else. A pharmacy counter runs one Windows account shared by every member of staff, so the next
        // person to sit down got the previous one's password typed in for them; if the manager had ever
        // ticked the box, every employee had the manager's login and the whole role model was decorative.

        public LoginForm(AppServices services)
        {
            _services = services;
            BuildUi();
            LoadRememberedUser();
        }

        private void BuildUi()
        {
            Text = "دوائي — تسجيل الدخول";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            ClientSize = new Size(940, 660);
            BackColor = Theme.Background;

            BuildBrandingPanel();
            BuildLoginCard();

            var support = new Label
            {
                Text = "هل تواجه مشكلة؟ تواصل مع الدعم الفني",
                Font = Theme.Base(9.5f),
                ForeColor = Theme.Primary,
                AutoSize = false,
                Cursor = Cursors.Hand,
                TextAlign = ContentAlignment.MiddleCenter,
                Size = new Size(ClientSize.Width, 22),
                Location = new Point(0, 628)
            };
            support.Click += (s, e) => ShowSupportInfo();
            Controls.Add(support);
        }

        // ---------------- branding (left) ----------------

        private void BuildBrandingPanel()
        {
            var brand = new RoundedPanel
            {
                UseGradient = true,
                GradientTop = Theme.GradientTop,
                GradientBottom = Theme.GradientBottom,
                CornerRadius = 20,
                Inset = 12,
                Location = new Point(540, 150),   // mirrored by RTL layout -> renders on the LEFT
                Size = new Size(330, 400)
            };
            brand.Paint += BrandingPaint;
            Controls.Add(brand);
        }

        private void BrandingPaint(object sender, PaintEventArgs e)
        {
            var panel = (RoundedPanel)sender;
            var g = e.Graphics;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
            Rectangle c = panel.CardBounds;
            var rtl = new StringFormat(StringFormatFlags.DirectionRightToLeft) { Alignment = StringAlignment.Near }; // Near = right
            var center = new StringFormat { Alignment = StringAlignment.Center };

            // Wordmark + the logo as a white rounded chip beside it.
            using (var wf = new Font(Theme.FontFamily, 30, FontStyle.Bold))
            {
                float wy = c.Y + 140;
                SizeF wsz = g.MeasureString("دوائي", wf);
                float wx = c.Right - 34 - wsz.Width;
                g.DrawString("دوائي", wf, Brushes.White, new PointF(wx, wy));

                Image logo = AppImages.Logo;
                if (logo != null)
                {
                    float chipSize = 58;
                    var chip = new RectangleF(wx - chipSize - 16, wy + (wsz.Height - chipSize) / 2f, chipSize, chipSize);
                    Gfx.FillRounded(g, chip, 14, Color.White);
                    var inner = new RectangleF(chip.X + 3, chip.Y + 3, chip.Width - 6, chip.Height - 6);
                    using (var clip = Gfx.RoundedRect(inner, 10))
                    {
                        var old = g.Clip;
                        g.SetClip(clip, System.Drawing.Drawing2D.CombineMode.Intersect);
                        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                        g.DrawImage(logo, inner);
                        g.Clip = old;
                    }
                }
            }

            // Tagline block, lower area
            float y = c.Y + c.Height - 150;
            using (var small = new Font(Theme.FontFamily, 10.5f))
            using (var head = new Font(Theme.FontFamily, 15f, FontStyle.Bold))
            using (var body = new Font(Theme.FontFamily, 10f))
            using (var faint = new SolidBrush(Color.FromArgb(210, 255, 255, 255)))
            using (var soft = new SolidBrush(Color.FromArgb(180, 255, 255, 255)))
            {
                var box = new RectangleF(c.X + 20, y, c.Width - 40, 22);
                g.DrawString("معتمد طبياً", small, faint, box, rtl);
                y += 28;
                g.DrawString("الدقة في كل صرف", head, Brushes.White, new RectangleF(c.X + 20, y, c.Width - 40, 26), rtl);
                y += 34;
                g.DrawString("نضمن لك سرعة الأداء ودقة البيانات لإدارة صيدليتك باحترافية.",
                    body, soft, new RectangleF(c.X + 20, y, c.Width - 40, 60), rtl);
            }
        }

        // ---------------- login card (right) ----------------

        private void BuildLoginCard()
        {
            var card = new RoundedPanel
            {
                FillColor = Theme.Surface,
                BorderColor = Theme.CardBorder,
                CornerRadius = 18,
                Inset = 14,
                Location = new Point(60, 40),     // mirrored by RTL layout -> renders on the RIGHT
                Size = new Size(410, 580)
            };
            Controls.Add(card);

            int cw = card.Width;
            int contentX = 46, contentW = cw - contentX * 2;

            var logo = new LogoChip
            {
                Size = new Size(66, 66),
                Location = new Point(cw / 2 - 33, 30)
            };

            var title = new Label
            {
                Text = "دوائي", Font = Theme.Title(26f), ForeColor = Theme.Primary, BackColor = Theme.Surface,
                AutoSize = false, TextAlign = ContentAlignment.MiddleCenter, Size = new Size(cw - 28, 42), Location = new Point(14, 100)
            };
            var subtitle = new Label
            {
                Text = "نظام إدارة الصيدلية المتكامل", Font = Theme.Base(11.5f), ForeColor = Theme.TextMuted, BackColor = Theme.Surface,
                AutoSize = false, TextAlign = ContentAlignment.MiddleCenter, Size = new Size(cw - 28, 22), Location = new Point(14, 144)
            };

            var lblUser = FieldLabel("اسم المستخدم", contentX, 186, contentW);
            _username = new RoundedTextField(FieldIcon.Person) { Location = new Point(contentX, 208), Size = new Size(contentW, 52), Placeholder = "أدخل اسم المستخدم" };

            var lblPass = FieldLabel("كلمة المرور", contentX, 274, contentW);
            _password = new RoundedTextField(FieldIcon.Lock, password: true) { Location = new Point(contentX, 296), Size = new Size(contentW, 52), Placeholder = "أدخل كلمة المرور" };
            _password.KeyDownEx += (s, e) => { if (e.KeyCode == Keys.Enter) TryLogin(); };

            _remember = new CheckBox
            {
                Text = "تذكر اسم المستخدم", Font = Theme.Base(10f), ForeColor = Theme.TextPrimary, BackColor = Theme.Surface,
                AutoSize = true, RightToLeft = RightToLeft.Yes,
                Location = new Point(contentX + contentW - 90, 360)
            };
            var forgot = new LinkLabel
            {
                Text = "نسيت كلمة المرور؟", Font = Theme.Base(10f), BackColor = Theme.Surface,
                LinkColor = Theme.Primary, ActiveLinkColor = Theme.PrimaryDark, AutoSize = true,
                Location = new Point(contentX, 361)
            };
            forgot.LinkClicked += (s, e) => Msg.Info(
                "لإعادة تعيين كلمة المرور، اطلب من المدير فتح «إدارة الموظفين» ثم «تعيين كلمة مرور».\n" +
                "إذا نسي المدير كلمته، استعد نسخة احتياطية سابقة.", "نسيت كلمة المرور");

            _error = new Label
            {
                ForeColor = Theme.Danger, BackColor = Theme.Surface, AutoSize = false, TextAlign = ContentAlignment.MiddleCenter,
                Size = new Size(contentW, 20), Location = new Point(contentX, 388), Font = Theme.Base(9.5f)
            };

            _loginButton = new PillButton
            {
                Text = "تسجيل الدخول", Location = new Point(contentX, 410), Size = new Size(contentW, 50)
            };
            _loginButton.Click += (s, e) => TryLogin();

            var divider = new Label
            {
                Text = "———  الوصول الآمن  ———", Font = Theme.Base(9f), ForeColor = Color.FromArgb(200, 208, 205), BackColor = Theme.Surface,
                AutoSize = false, TextAlign = ContentAlignment.MiddleCenter, Size = new Size(cw - 28, 20), Location = new Point(14, 482)
            };
            var footer = new Label
            {
                Text = "الإصدار v" + Application.ProductVersion, Font = Theme.Base(9f), ForeColor = Theme.TextMuted, BackColor = Theme.Surface,
                AutoSize = false, TextAlign = ContentAlignment.MiddleCenter, Size = new Size(cw - 28, 20), Location = new Point(14, 512)
            };

            card.Controls.AddRange(new Control[] { logo, title, subtitle, lblUser, _username, lblPass, _password,
                _remember, forgot, _error, _loginButton, divider, footer });

            AcceptButton = _loginButton;
            ActiveControl = _username;
        }

        private static Label FieldLabel(string text, int x, int y, int w) => new Label
        {
            Text = text, Font = Theme.Base(10f), ForeColor = Theme.TextMuted, BackColor = Theme.Surface,
            AutoSize = false, TextAlign = ContentAlignment.MiddleRight, Size = new Size(w, 18), Location = new Point(x, y)
        };

        // ---------------- behaviour ----------------

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (string.IsNullOrEmpty(_username.Text)) _username.FocusInput();
            else if (string.IsNullOrEmpty(_password.Text)) _password.FocusInput();
            else _loginButton.Focus();   // remembered user + password: one Enter to sign in

        }

        private void TryLogin()
        {
            _error.Text = "";
            _loginButton.Enabled = false;
            try
            {
                AuthResult result = _services.Auth.Authenticate(_username.Text, _password.Text);
                if (!result.Success)
                {
                    _error.Text = result.Message;
                    _password.SelectAllInput();
                    _password.FocusInput();
                    return;
                }

                SaveRememberedUser();
                Session.CurrentUser = result.User;
                DialogResult = DialogResult.OK;
                Close();
            }
            catch (Exception ex)
            {
                _error.Text = "تعذّر الاتصال بقاعدة البيانات.";
                Msg.Error("تعذّر الاتصال بقاعدة البيانات:\n" + ex.Message);
            }
            finally
            {
                _loginButton.Enabled = true;
            }
        }

        private void ShowSupportInfo()
        {
            Msg.Info(
                "تطبيق دوائي\n\n" +
                "صُنع بواسطة: JK software\n" +
                "المطوّر: أحمد جمال\n" +
                "للتواصل: +249911757214",
                "الدعم الفني");
        }

        /// <summary>
        /// Restores the remembered username. The password box is deliberately left empty — see the note
        /// on <see cref="RememberFile"/>.
        ///
        /// A file written by an older version has the encrypted password on line 1. It is ignored and
        /// the file is rewritten without it, so the stored secret is cleared the first time this build
        /// runs rather than lingering on disk for whoever looks.
        /// </summary>
        private void LoadRememberedUser()
        {
            try
            {
                if (!File.Exists(RememberFile)) return;
                string[] lines = File.ReadAllLines(RememberFile);
                string user = lines.Length > 0 ? lines[0].Trim() : "";
                if (user.Length == 0) return;

                _username.Text = user;
                _remember.Checked = true;

                if (lines.Length > 1) SaveRememberedUser();   // drop a password left by an older build
            }
            catch (Exception ex) { Log.Error("LoadRememberedUser", ex); }
        }

        private void SaveRememberedUser()
        {
            try
            {
                if (_remember.Checked)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(RememberFile));
                    File.WriteAllLines(RememberFile, new[] { _username.Text.Trim() });
                }
                else if (File.Exists(RememberFile)) File.Delete(RememberFile);
            }
            catch (Exception ex) { Log.Error("SaveRememberedUser", ex); }
        }
    }
}
