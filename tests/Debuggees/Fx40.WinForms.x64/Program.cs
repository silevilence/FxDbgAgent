using System;
using System.Drawing;
using System.Windows.Forms;

namespace FxDbg.Debuggees.WinFormsX64
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }

    internal sealed class MainForm : Form
    {
        internal MainForm()
        {
            Text = "FxDbg .NET Framework 4.0 x64 debuggee";
            ClientSize = new Size(520, 160);
            StartPosition = FormStartPosition.CenterScreen;
            Controls.Add(new Label
            {
                AutoSize = true,
                Location = new Point(24, 24),
                Text = "FxDbg WinForms x64 is running on CLR " + Environment.Version
            });
        }
    }
}
