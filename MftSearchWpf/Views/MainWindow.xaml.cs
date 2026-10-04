using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Interop;

namespace MftSearchWpf
{
    public partial class MainWindow : Window
    {
        // -----------------------------------------------------------------------
        // Win32 Hotkey API
        // -----------------------------------------------------------------------

        [DllImport("user32.dll")]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        private const int HOTKEY_ID      = 9001;
        private const uint MOD_ALT       = 0x0001;
        private const uint MOD_NOREPEAT  = 0x4000;
        private const uint VK_SPACE      = 0x20;

        // -----------------------------------------------------------------------
        // System Tray
        // -----------------------------------------------------------------------

        private NotifyIcon? _trayIcon;
        private bool _closeToTray = true;   // false only when user picks "Exit" from tray menu

        // -----------------------------------------------------------------------
        // Window lifetime
        // -----------------------------------------------------------------------

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            SetupTrayIcon();
            RegisterGlobalHotkey();
            UpdatePlaceholderVisibility();
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (_closeToTray && _trayIcon != null)
            {
                e.Cancel = true;
                Hide();
                _trayIcon.ShowBalloonTip(
                    2000,
                    "Tachyon",
                    "Still running in the background. Press Alt+Space to bring it back.",
                    ToolTipIcon.Info);
            }
            else
            {
                UnregisterGlobalHotkey();
                _trayIcon?.Dispose();
            }
        }

        // -----------------------------------------------------------------------
        // System Tray setup
        // -----------------------------------------------------------------------

        private void SetupTrayIcon()
        {
            _trayIcon = new NotifyIcon
            {
                Text    = "Tachyon - File Search",
                Visible = true,
                Icon    = SystemIcons.Application
            };

            var menu = new ContextMenuStrip();

            var showItem = new ToolStripMenuItem("Show Tachyon (Alt+Space)");
            showItem.Font = new Font(showItem.Font, System.Drawing.FontStyle.Bold);
            showItem.Click += (_, _) => BringToFront();
            menu.Items.Add(showItem);

            menu.Items.Add(new ToolStripSeparator());

            var exitItem = new ToolStripMenuItem("Exit");
            exitItem.Click += (_, _) =>
            {
                _closeToTray = false;
                System.Windows.Application.Current.Shutdown();
            };
            menu.Items.Add(exitItem);

            _trayIcon.ContextMenuStrip = menu;
            _trayIcon.DoubleClick += (_, _) => BringToFront();
        }

        private void BringToFront()
        {
            Show();
            WindowState = WindowState.Normal;
            Activate();
            Topmost = true;
            Topmost = false;
            SearchBox.Focus();
        }

        // -----------------------------------------------------------------------
        // Global Hotkey  (Alt + Space)
        // -----------------------------------------------------------------------

        private void RegisterGlobalHotkey()
        {
            var helper = new WindowInteropHelper(this);
            RegisterHotKey(helper.Handle, HOTKEY_ID, MOD_ALT | MOD_NOREPEAT, VK_SPACE);

            HwndSource source = HwndSource.FromHwnd(helper.Handle);
            source.AddHook(WndProc);
        }

        private void UnregisterGlobalHotkey()
        {
            var helper = new WindowInteropHelper(this);
            UnregisterHotKey(helper.Handle, HOTKEY_ID);
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            const int WM_HOTKEY = 0x0312;

            if (msg == WM_HOTKEY && wParam.ToInt32() == HOTKEY_ID)
            {
                if (IsVisible)
                {
                    Hide();
                }
                else
                {
                    BringToFront();
                }
                handled = true;
            }

            return IntPtr.Zero;
        }

        // -----------------------------------------------------------------------
        // Search box placeholder
        // -----------------------------------------------------------------------

        private void SearchBox_GotFocus(object sender, RoutedEventArgs e)
        {
            SearchPlaceholder.Visibility = Visibility.Collapsed;
        }

        private void SearchBox_LostFocus(object sender, RoutedEventArgs e)
        {
            UpdatePlaceholderVisibility();
        }

        private void UpdatePlaceholderVisibility()
        {
            SearchPlaceholder.Visibility =
                string.IsNullOrEmpty(SearchBox?.Text)
                    ? Visibility.Visible
                    : Visibility.Collapsed;
        }

        // -----------------------------------------------------------------------
        // Column auto-fill: Path column takes all space after the Name column
        // -----------------------------------------------------------------------

        private void FileListView_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            // 4px scrollbar + border allowance so the horizontal scrollbar never appears
            const double nameWidth     = 260;
            const double scrollPad     = 22;
            double available = FileListView.ActualWidth - nameWidth - scrollPad;
            if (available > 100)
                PathColumn.Width = available;
        }
    }
}
