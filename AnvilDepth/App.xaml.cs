using System;
using System.Windows;
using System.Windows.Threading;
using System.Drawing;
using System.Windows.Forms;
using AnvilDepth.Services;

namespace AnvilDepth
{
    public partial class App : System.Windows.Application
    {
        private NotifyIcon? _trayIcon;

        protected override void OnStartup(StartupEventArgs e)
        {
            // OpenCV disabled the OpenEXR codec by default a few versions back (a handful of CVEs
            // were found in the upstream OpenEXR library), so writing .exr now throws
            // "OpenEXR codec is disabled" unless this opt-in is set. Must happen before ANY
            // OpenCvSharp/Cv2 call touches image codecs — first line of OnStartup is the safest
            // place, well before DepthEngine/ImageProcessor ever run.
            Environment.SetEnvironmentVariable("OPENCV_IO_ENABLE_OPENEXR", "1");

            // Three separate hooks because .NET has three separate places an unhandled exception
            // can surface: AppDomain catches anything (any thread, but NOT native-code corrupted-
            // state exceptions like an access-violation crash — those bypass this entirely, which
            // is exactly why Logger.cs writes to disk immediately rather than only at the end);
            // DispatcherUnhandledException catches exceptions specifically on the UI thread and
            // lets you mark them Handled to keep the app running instead of tearing down; and
            // TaskScheduler.UnobservedTaskException catches exceptions from fire-and-forget
            // Tasks that nobody awaited (e.g. an async void event handler if it didn't await
            // everything inside it).
            AppDomain.CurrentDomain.UnhandledException += (s, ex) =>
            {
                Logger.Log($"AppDomain.UnhandledException (IsTerminating={ex.IsTerminating}): {ex.ExceptionObject}");
                System.Windows.MessageBox.Show($"CRASH: {ex.ExceptionObject}\n\nDetails were written to:\n{Logger.LogFolderPath}");
            };
            DispatcherUnhandledException += (s, ex) =>
            {
                Logger.LogException("DispatcherUnhandledException", ex.Exception);
            };
            System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (s, ex) =>
            {
                Logger.LogException("UnobservedTaskException", ex.Exception);
                ex.SetObserved();
            };

            Logger.Log("App starting up.");
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
