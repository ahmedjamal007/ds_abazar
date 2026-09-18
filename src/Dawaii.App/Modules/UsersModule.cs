using System;
using System.Drawing;
using System.Windows.Forms;
using Dawaii.App.Forms;
using Dawaii.App.Ui;
using Dawaii.Core;
using Dawaii.Core.Models;

namespace Dawaii.App.Modules
{
    /// <summary>Admin-only user management (FR-USR-02): list, add, rename, delete, enable/disable,
    /// reset password.</summary>
    public class UsersModule : ModuleControl
    {
        private DataGridView _grid;

        public UsersModule()
        {
            var title = Theme.PageHeader("إدارة الموظفين", "حسابات الدخول وصلاحيات الموظفين");

            var toolbar = new FlowLayoutPanel
            {
                Dock = DockStyle.Top, Height = 52, FlowDirection = FlowDirection.RightToLeft, WrapContents = false
            };
            toolbar.Controls.Add(MakeButton("موظف جديد", AddUser));
            toolbar.Controls.Add(MakeButton("تعديل الاسم", RenameUser));
            toolbar.Controls.Add(MakeButton("حذف الموظف", DeleteUser));
            toolbar.Controls.Add(MakeButton("تغيير الصلاحية", ChangeRole));
            toolbar.Controls.Add(MakeButton("تفعيل / تعطيل", ToggleActive));
            toolbar.Controls.Add(MakeButton("تعيين كلمة مرور", ResetPassword));
            toolbar.Controls.Add(MakeButton("تحديث", () => Reload()));

            _grid = new DataGridView { Dock = DockStyle.Fill, AutoGenerateColumns = false };
            Theme.StyleGrid(_grid);
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "المعرّف", DataPropertyName = "Id", Width = 70 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "اسم المستخدم", DataPropertyName = "Username", Width = 160 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "الاسم", DataPropertyName = "FullName", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "الصلاحية", DataPropertyName = "RoleText", Width = 120 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "الحالة", DataPropertyName = "StatusText", Width = 100 });

            Controls.Add(_grid);
            Controls.Add(toolbar);
            Controls.Add(title);
        }

        public override void OnActivated() => Reload();

        private void Reload()
        {
            try
            {
                var rows = new System.Collections.Generic.List<object>();
                foreach (User u in Services.UserService.ListUsers(Session.CurrentUser))
                    rows.Add(new
                    {
                        u.Id,
                        u.Username,
                        u.FullName,
                        RoleText = RoleLabels.Of(u.Role),
                        StatusText = u.IsActive ? "مفعّل" : "معطّل"
                    });
                _grid.DataSource = rows;
            }
            catch (Exception ex) { Msg.Error(ex.Message); }
        }

        private int? SelectedUserId()
        {
            if (_grid.CurrentRow == null) return null;
            return Convert.ToInt32(_grid.CurrentRow.Cells[0].Value);
        }

        private void AddUser()
        {
            using (var dlg = new NewUserForm())
                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    try
                    {
                        Services.UserService.CreateUser(Session.CurrentUser,
                            dlg.Username, dlg.Password, dlg.FullName, dlg.SelectedRole);
                        Reload();
                    }
                    catch (DomainException ex) { Msg.Error(ex.Message); }
                }
        }

        /// <summary>Corrects an employee's login name and display name — a misspelling on the day the
        /// account was created shouldn't need the account deleting and re-made (V1.9).</summary>
        private void RenameUser()
        {
            int? id = SelectedUserId();
            if (id == null) { Msg.Info("اختر مستخدماً أولاً."); return; }

            string[] entered = Prompt.ShowTwo("اسم المستخدم (للدخول)", "الاسم", "تعديل بيانات الموظف",
                Convert.ToString(_grid.CurrentRow.Cells[1].Value),
                Convert.ToString(_grid.CurrentRow.Cells[2].Value));
            if (entered == null) return;

            try
            {
                Services.UserService.Rename(Session.CurrentUser, id.Value, entered[0], entered[1]);
                Msg.Info("تم حفظ البيانات. اسم الدخول الجديد يُستعمل من تسجيل الدخول التالي.");
                Reload();
            }
            catch (DomainException ex) { Msg.Error(ex.Message); }
        }

        /// <summary>
        /// Deletes an employee's account. An employee who has sold, refunded, adjusted stock or touched
        /// the ledger is kept — that history has to keep naming who did it — and the manager is offered
        /// deactivation instead, which is what removes them from the login screen either way.
        /// </summary>
        private void DeleteUser()
        {
            int? id = SelectedUserId();
            if (id == null) { Msg.Info("اختر مستخدماً أولاً."); return; }
            string name = Convert.ToString(_grid.CurrentRow.Cells[2].Value);
            if (!Msg.Confirm($"حذف حساب \"{name}\" نهائياً؟ لا يمكن التراجع.")) return;

            try
            {
                if (Services.UserService.DeleteUser(Session.CurrentUser, id.Value))
                {
                    Msg.Info("تم حذف الموظف.");
                    Reload();
                    return;
                }

                if (Msg.Confirm("لا يمكن حذف هذا الموظف لوجود مبيعات أو حركات مسجلة باسمه.\n" +
                                "هل تريد تعطيل حسابه بدلاً من الحذف؟"))
                {
                    Services.UserService.SetActive(Session.CurrentUser, id.Value, false);
                    Reload();
                }
            }
            catch (DomainException ex) { Msg.Error(ex.Message); }
        }

        /// <summary>Grants or withdraws stockroom access on an account that already exists — the manager
        /// shouldn't have to delete and re-create an employee to promote them (V1.8).</summary>
        private void ChangeRole()
        {
            int? id = SelectedUserId();
            if (id == null) { Msg.Info("اختر مستخدماً أولاً."); return; }

            string currentText = Convert.ToString(_grid.CurrentRow.Cells[3].Value);
            int current = Array.IndexOf(RoleLabels.Choices, currentText);
            int picked = Prompt.Choose("الصلاحية الجديدة", "تغيير الصلاحية",
                RoleLabels.Choices, current < 0 ? 0 : current);
            if (picked < 0) return;

            try
            {
                Services.UserService.SetRole(Session.CurrentUser, id.Value, RoleLabels.At(picked));
                Msg.Info("تم تحديث الصلاحية. تُطبَّق عند تسجيل دخول الموظف التالي.");
                Reload();
            }
            catch (DomainException ex) { Msg.Error(ex.Message); }
        }

        private void ToggleActive()
        {
            int? id = SelectedUserId();
            if (id == null) { Msg.Info("اختر مستخدماً أولاً."); return; }
            bool currentlyActive = _grid.CurrentRow.Cells[4].Value?.ToString() == "مفعّل";
            try
            {
                Services.UserService.SetActive(Session.CurrentUser, id.Value, !currentlyActive);
                Reload();
            }
            catch (DomainException ex) { Msg.Error(ex.Message); }
        }

        private void ResetPassword()
        {
            int? id = SelectedUserId();
            if (id == null) { Msg.Info("اختر مستخدماً أولاً."); return; }
            string pw = Prompt.Show("كلمة المرور الجديدة", "تعيين كلمة مرور", isPassword: true);
            if (string.IsNullOrEmpty(pw)) return;
            try
            {
                Services.UserService.ResetPassword(Session.CurrentUser, id.Value, pw);
                Msg.Info("تم تحديث كلمة المرور.");
            }
            catch (DomainException ex) { Msg.Error(ex.Message); }
        }

        private static Button MakeButton(string text, Action onClick)
            => Theme.ActionButton(text, onClick);
    }
}
