using System;
using System.Windows;
using System.Drawing;
using System.Windows.Forms;

namespace AnvilDepth
{
    public partial class App : System.Windows.Application
    {
        private NotifyIcon? _trayIcon;

        protected override void OnStartup(StartupEventArgs e)
        {
            AppDomain.CurrentDomain.UnhandledException += (s, ex) =>
            {
                System.Windows.MessageBox.Show($"CRASH: {ex.ExceptionObject}");
            };
            base.OnStartup(e);
            SetupTrayIcon();
        }

        private void SetupTrayIcon()
        {
            try
            {
                string iconPath = System.IO.Path.Combine(AppContext.BaseDirectory, "system_tray.ico");
                var icon = System.IO.File.Exists(iconPath)
                    ? new Icon(iconPath)
                    : SystemIcons.Application;

                var menu = new ContextMenuStrip();
                menu.Items.Add("Show AnvilDepth", null, (s, e) => ShowMainWindow());
                menu.Items.Add(new ToolStripSeparator());
                menu.Items.Add("Exit", null, (s, e) => System.Windows.Application.Current.Shutdown());

                _trayIcon = new NotifyIcon
                {
                    Icon = icon,
                    Text = "AnvilDepth",
                    Visible = true,
                    ContextMenuStrip = menu
                };
                _trayIcon.DoubleClick += (s, e) => ShowMainWindow();
            }
            catch
            {
                // Tray icon is a nicety, not load-bearing — don't let a missing/bad icon file crash startup.
            }
        }

        private void ShowMainWindow()
        {
            if (MainWindow == null) return;
            MainWindow.Show();
            MainWindow.WindowState = WindowState.Normal;
            MainWindow.Activate();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            if (_trayIcon != null)
            {
                _trayIcon.Visible = false;
                _trayIcon.Dispose();
            }
            base.OnExit(e);
        }
    }
}
