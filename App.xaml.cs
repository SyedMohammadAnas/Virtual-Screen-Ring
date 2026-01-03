using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Interop;

namespace screenring
{
    public partial class App : System.Windows.Application
    {
        public static bool IsExiting { get; private set; }

        private NotifyIcon trayIcon;
        private System.Drawing.Icon trayIconImage;
        private MainWindow overlay;
        private ThicknessWindow thicknessWindow;

        private const int HOTKEY_ID = 9000;
        private const int MOD_WIN = 0x0008;
        private const int MOD_CONTROL = 0x0002;
        private const int VK_R = 0x52;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            IsExiting = false;

            overlay = new MainWindow();
            overlay.Show();

            thicknessWindow = new ThicknessWindow(overlay);

            // Attempt to register hotkey; subscribe to message hook only if registration succeeds.
            try
            {
                if (RegisterHotKey(IntPtr.Zero, HOTKEY_ID, MOD_WIN | MOD_CONTROL, VK_R))
                {
                    ComponentDispatcher.ThreadPreprocessMessage += HotkeyHandler;
                }
            }
            catch
            {
                // swallow: don't let hotkey registration break startup
            }

            // Load tray icon: prefer file in output folder, then pack URI, then system fallback.
            try
            {
                var exeIconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "screenring.ico");
                if (File.Exists(exeIconPath))
                {
                    trayIconImage = new System.Drawing.Icon(exeIconPath);
                }
                else
                {
                    // Try pack URI (requires the .ico to be included as Resource)
                    try
                    {
                        var uri = new Uri("pack://application:,,,/screenring.ico", UriKind.Absolute);
                        var streamInfo = System.Windows.Application.GetResourceStream(uri);
                        if (streamInfo != null)
                        {
                            using var s = streamInfo.Stream;
                            trayIconImage = new System.Drawing.Icon(s);
                        }
                    }
                    catch
                    {
                        // ignore pack URI failures
                    }
                }
            }
            catch
            {
                // ignore icon loading failures
            }

            trayIcon = new NotifyIcon
            {
                Icon = trayIconImage ?? System.Drawing.SystemIcons.Application,
                Visible = true,
                Text = "screenring"
            };

            var menu = new ContextMenuStrip();

            var toggleItem = new ToolStripMenuItem("Toggle Ring (Win + Ctrl + R)");
            toggleItem.Click += (_, _) => ToggleRing();

            var thicknessItem = new ToolStripMenuItem("Ring Thickness");
            thicknessItem.Click += (_, _) =>
            {
                try
                {
                    if (thicknessWindow == null || !thicknessWindow.IsLoaded)
                        thicknessWindow = new ThicknessWindow(overlay);

                    thicknessWindow.Show();
                    thicknessWindow.Activate();
                }
                catch
                {
                    // swallow — tray should not crash the app
                }
            };

            var exitItem = new ToolStripMenuItem("Exit");
            exitItem.Click += (_, _) =>
            {
                IsExiting = true;

                try
                {
                    trayIcon.Visible = false;
                    trayIcon.Dispose();
                }
                catch { }

                try
                {
                    trayIconImage?.Dispose();
                    trayIconImage = null;
                }
                catch { }

                Shutdown();
            };

            menu.Items.Add(toggleItem);
            menu.Items.Add(thicknessItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(exitItem);

            trayIcon.ContextMenuStrip = menu;
        }

        protected override void OnExit(ExitEventArgs e)
        {
            IsExiting = true;

            try
            {
                ComponentDispatcher.ThreadPreprocessMessage -= HotkeyHandler;
            }
            catch { }

            try
            {
                UnregisterHotKey(IntPtr.Zero, HOTKEY_ID);
            }
            catch { }

            try
            {
                if (trayIcon != null)
                {
                    trayIcon.Visible = false;
                    trayIcon.Dispose();
                    trayIcon = null;
                }
            }
            catch { }

            try
            {
                trayIconImage?.Dispose();
                trayIconImage = null;
            }
            catch { }

            base.OnExit(e);
        }

        private void ToggleRing()
        {
            try
            {
                if (overlay == null || !overlay.IsLoaded)
                {
                    overlay = new MainWindow();
                    if (thicknessWindow == null)
                        thicknessWindow = new ThicknessWindow(overlay);
                    else
                        thicknessWindow = new ThicknessWindow(overlay);

                    overlay.Show();
                    return;
                }

                if (overlay.IsVisible)
                    overlay.Hide();
                else
                    overlay.Show();
            }
            catch
            {
                // Do not let exceptions bubble from tray/hotkey handling.
            }
        }

        // Matches ComponentDispatcher.ThreadPreprocessMessage delegate
        private void HotkeyHandler(ref MSG msg, ref bool handled)
        {
            const int WM_HOTKEY = 0x0312;
            if (msg.message == WM_HOTKEY && msg.wParam.ToInt32() == HOTKEY_ID)
            {
                ToggleRing();
                handled = true;
            }
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, int fsModifiers, int vk);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    }
}
