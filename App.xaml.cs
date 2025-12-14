using System;
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
        private MainWindow overlay;
        private ThicknessWindow thicknessWindow;

        private const int HOTKEY_ID = 9000;
        private const int MOD_WIN = 0x0008;
        private const int MOD_CONTROL = 0x0002; // changed from MOD_SHIFT to MOD_CONTROL
        private const int VK_R = 0x52;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            IsExiting = false;

            overlay = new MainWindow();
            overlay.Show();

            thicknessWindow = new ThicknessWindow(overlay);

            // Register global hotkey for the current thread (hWnd = IntPtr.Zero)
            RegisterHotKey(IntPtr.Zero, HOTKEY_ID, MOD_WIN | MOD_CONTROL, VK_R);
            ComponentDispatcher.ThreadPreprocessMessage += HotkeyHandler;

            trayIcon = new NotifyIcon
            {
                Icon = System.Drawing.SystemIcons.Application,
                Visible = true,
                Text = "screenring"
            };

            var menu = new ContextMenuStrip();

            var toggleItem = new ToolStripMenuItem("Toggle Ring (Win + Ctrl + R)"); // updated text
            toggleItem.Click += (_, _) => ToggleRing();

            var thicknessItem = new ToolStripMenuItem("Ring Thickness");
            thicknessItem.Click += (_, _) =>
            {
                try
                {
                    // guard against the thickness window having been closed unexpectedly
                    if (thicknessWindow == null || !thicknessWindow.IsLoaded)
                    {
                        thicknessWindow = new ThicknessWindow(overlay);
                    }

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
                // mark exiting so MainWindow.OnClosing allows close
                IsExiting = true;

                trayIcon.Visible = false;
                trayIcon.Dispose();

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
            // mark exiting so windows can close normally
            IsExiting = true;

            // Clean up hotkey and message hook
            try
            {
                ComponentDispatcher.ThreadPreprocessMessage -= HotkeyHandler;
                UnregisterHotKey(IntPtr.Zero, HOTKEY_ID);
            }
            catch
            {
                // ignore cleanup exceptions
            }

            if (trayIcon != null)
            {
                trayIcon.Visible = false;
                trayIcon.Dispose();
            }

            base.OnExit(e);
        }

        private void ToggleRing()
        {
            try
            {
                if (overlay == null || !overlay.IsLoaded)
                {
                    // recreate overlay if it was somehow closed; ensure thicknessWindow references it
                    overlay = new MainWindow();
                    if (thicknessWindow == null)
                        thicknessWindow = new ThicknessWindow(overlay);
                    else
                    {
                        // if thicknessWindow was created earlier with a different overlay instance,
                        // recreate it so it references the current overlay (ThicknessWindow stores overlay readonly).
                        thicknessWindow = new ThicknessWindow(overlay);
                    }

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

        private void HotkeyHandler(ref MSG msg, ref bool handled)
        {
            const int WM_HOTKEY = 0x0312;
            if (msg.message == WM_HOTKEY && msg.wParam.ToInt32() == HOTKEY_ID)
            {
                ToggleRing();
                handled = true;
            }
        }

        [DllImport("user32.dll")]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, int fsModifiers, int vk);

        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    }
}
