using System;
using System.Drawing;
using System.Windows.Forms;

namespace FxDbg.Debuggees.WinFormsX64
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            bool scenario = args.Length == 3 && args[0] == "--scenario";
            if (scenario) Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            var form = new MainForm();
            if (scenario)
            {
                form.ShowInTaskbar = false;
                form.Opacity = 0;
                form.Shown += (_, __) => form.BeginInvoke(new Action(() =>
                {
                    if (args[1] == "exception") new System.Threading.Thread(() => EndToEndScenarios.Run(args[1], args[2])).Start();
                    else { EndToEndScenarios.Run(args[1], args[2]); form.Close(); }
                }));
            }
            Application.Run(form);
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
