using Dawaii.Core.Models;

namespace Dawaii.App.Ui
{
    /// <summary>The Arabic names of the three roles, in one place so the user-management grid, the
    /// new-employee form and the sidebar card can never drift apart (V1.8).</summary>
    public static class RoleLabels
    {
        public const string Cashier = "موظف";
        public const string FullEmployee = "موظف ذو امتيازات";
        public const string Admin = "مدير";

        /// <summary>Combo-box order, least privileged first — the index maps to <see cref="At"/>.</summary>
        public static readonly object[] Choices = { Cashier, FullEmployee, Admin };

        /// <summary>The role a <see cref="Choices"/> index stands for. Anything unexpected is the
        /// safest role, a plain cashier.</summary>
        public static Role At(int index)
        {
            switch (index)
            {
                case 1: return Role.FullEmployee;
                case 2: return Role.Admin;
                default: return Role.Cashier;
            }
        }

        /// <summary>The <see cref="Choices"/> index a role sits at, for pre-selecting the current role.</summary>
        public static int IndexOf(Role role)
        {
            switch (role)
            {
                case Role.FullEmployee: return 1;
                case Role.Admin: return 2;
                default: return 0;
            }
        }

        public static string Of(Role role)
        {
            switch (role)
            {
                case Role.Admin: return Admin;
                case Role.FullEmployee: return FullEmployee;
                default: return Cashier;
            }
        }
    }
}
