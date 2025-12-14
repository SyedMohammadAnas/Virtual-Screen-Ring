using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace screenring
{
    public partial class MainWindow : Window
    {
        private int thickness = 50;

        public MainWindow()
        {
            InitializeComponent();
            ApplyThickness();
        }

        public void SetThickness(int value)
        {
            thickness = value;
            ApplyThickness();
        }

        private void ApplyThickness()
        {
            TopRect.Height = thickness;
            BottomRect.Height = thickness;
            LeftRect.Width = thickness;
            RightRect.Width = thickness;
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            Left = 0;
            Top = 0;
            Width = SystemParameters.PrimaryScreenWidth;
            Height = SystemParameters.PrimaryScreenHeight;

            MakeClickThrough();
        }

        private void MakeClickThrough()
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            int style = GetWindowLong(hwnd, GWL_EXSTYLE);
            SetWindowLong(hwnd, GWL_EXSTYLE, style | WS_EX_LAYERED | WS_EX_TRANSPARENT);
        }

        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TRANSPARENT = 0x20;
        private const int WS_EX_LAYERED = 0x80000;

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hwnd, int index);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hwnd, int index, int value);
    }
}