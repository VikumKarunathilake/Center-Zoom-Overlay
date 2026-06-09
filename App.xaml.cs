using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;

namespace Center_Zoom_Overlay
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

        private System.Windows.Forms.NotifyIcon _notifyIcon;
        private MainWindow _mainWindow;
        private SettingsWindow _settingsWindow;

        protected override void OnStartup(StartupEventArgs e)
        {
            // Must be called before any window is created so that
            // GetSystemMetrics / CopyFromScreen use physical pixels.
            SetProcessDPIAware();
            base.OnStartup(e);

            // Create window instances
            _mainWindow = new MainWindow();
            _settingsWindow = new SettingsWindow(_mainWindow);

            // Show zoom overlay
            _mainWindow.Show();

            // Set up System Tray NotifyIcon
            SetupTrayIcon();
        }

        private void SetupTrayIcon()
        {
            _notifyIcon = new System.Windows.Forms.NotifyIcon();

            try
            {
                // Create a beautiful custom 16x16 icon in-memory (green circle with red center)
                using (Bitmap bmp = new Bitmap(16, 16))
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

                    // Draw outer scope circle (Electric Teal/Cyan)
                    using (Pen p = new Pen(Color.FromArgb(0, 210, 255), 2f))
                    {
                        g.DrawEllipse(p, 1, 1, 13, 13);
                    }

                    // Draw center dot (Crimson Red)
                    g.FillEllipse(Brushes.Crimson, 6, 6, 4, 4);

                    IntPtr hIcon = bmp.GetHicon();
                    _notifyIcon.Icon = Icon.FromHandle(hIcon);
                }
            }
            catch
            {
                // Fallback to system application icon
                _notifyIcon.Icon = SystemIcons.Application;
            }

            _notifyIcon.Text = "Zoom Overlay (Ctrl+Shift+S for Settings)";
            _notifyIcon.Visible = true;

            // Setup Context Menu for tray icon
            var contextMenu = new System.Windows.Forms.ContextMenu();
            contextMenu.MenuItems.Add("Settings...", (s, ev) => ShowSettingsWindow());
            contextMenu.MenuItems.Add("-"); // Separator
            contextMenu.MenuItems.Add("Toggle Zoom", (s, ev) => _mainWindow.ToggleZoomState());
            contextMenu.MenuItems.Add("Exit", (s, ev) => ShutdownApp());

            _notifyIcon.ContextMenu = contextMenu;

            // Left-click on tray icon toggles settings panel visibility
            _notifyIcon.Click += (s, ev) =>
            {
                if (ev is System.Windows.Forms.MouseEventArgs mouseEv && mouseEv.Button == System.Windows.Forms.MouseButtons.Left)
                {
                    ToggleSettingsWindow();
                }
            };
        }

        public void ShowSettingsWindow()
        {
            if (_settingsWindow != null)
            {
                _settingsWindow.Show();
                _settingsWindow.Activate();
                _settingsWindow.WindowState = WindowState.Normal;
            }
        }

        public void ToggleSettingsWindow()
        {
            if (_settingsWindow != null)
            {
                if (_settingsWindow.IsVisible)
                {
                    _settingsWindow.Hide();
                }
                else
                {
                    ShowSettingsWindow();
                }
            }
        }

        public void ShutdownApp()
        {
            // Explicitly clean up notify icon to prevent it hanging in the tray on close
            if (_notifyIcon != null)
            {
                _notifyIcon.Visible = false;
                _notifyIcon.Dispose();
                _notifyIcon = null;
            }

            // Exit application immediately, bypassing window closing cancels
            Environment.Exit(0);
        }

        protected override void OnExit(ExitEventArgs e)
        {
            if (_notifyIcon != null)
            {
                _notifyIcon.Visible = false;
                _notifyIcon.Dispose();
            }
            base.OnExit(e);
        }
    }
}
