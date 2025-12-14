using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace screenring
{
    public partial class MainWindow : Window
    {
        private int thickness = 50;
        private readonly DispatcherTimer _cursorTimer;
        private const int _clearRadius = 100;

        public MainWindow()
        {
            InitializeComponent();

            // Poll global cursor position while the overlay is visible
            _cursorTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(33) // ~30Hz
            };
            _cursorTimer.Tick += CursorTimer_Tick;

            IsVisibleChanged += MainWindow_IsVisibleChanged;
        }

        // Prevent user from actually closing the window so tray/hotkey logic can Show() it later.
        // When the application is exiting (App.IsExiting == true) allow the close to proceed.
        protected override void OnClosing(CancelEventArgs e)
        {
            if (!App.IsExiting)
            {
                e.Cancel = true;
                Hide();
                return;
            }

            StopCursorTimer();
            base.OnClosing(e);
        }

        private void MainWindow_IsVisibleChanged(object? sender, DependencyPropertyChangedEventArgs e)
        {
            if (IsVisible)
                StartCursorTimer();
            else
                StopCursorTimer();
        }

        private void StartCursorTimer()
        {
            if (!_cursorTimer.IsEnabled && IsLoaded)
                _cursorTimer.Start();
        }

        private void StopCursorTimer()
        {
            if (_cursorTimer.IsEnabled)
                _cursorTimer.Stop();

            // remove any temporary hole
            RingGrid.Clip = null;
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            Left = 0;
            Top = 0;
            Width = SystemParameters.PrimaryScreenWidth;
            Height = SystemParameters.PrimaryScreenHeight;

            MakeClickThrough();

            // start timer if window is visible after loaded
            if (IsVisible)
                StartCursorTimer();
        }

        public void SetThickness(int value)
        {
            thickness = value;
            ApplyThickness();

            // immediately refresh the hole so it respects new thickness
            RefreshClip();
        }

        private void ApplyThickness()
        {
            TopRect.Height = thickness;
            BottomRect.Height = thickness;
            LeftRect.Width = thickness;
            RightRect.Width = thickness;
        }

        private void CursorTimer_Tick(object? sender, EventArgs e)
        {
            RefreshClip();
        }

        private void RefreshClip()
        {
            // only update when the window has size and is visible
            if (!IsVisible || !IsLoaded || ActualWidth <= 0 || ActualHeight <= 0)
            {
                RingGrid.Clip = null;
                return;
            }

            if (!GetCursorPos(out POINT pt))
            {
                RingGrid.Clip = null;
                return;
            }

            // Convert device (physical) pixels to WPF device-independent units
            var source = PresentationSource.FromVisual(this) as HwndSource;
            if (source == null || source.CompositionTarget == null)
            {
                RingGrid.Clip = null;
                return;
            }

            var transform = source.CompositionTarget.TransformFromDevice;
            var cursor = transform.Transform(new System.Windows.Point(pt.X, pt.Y));

            // Check if cursor is over any of the ring rectangles
            bool overTop = cursor.Y <= thickness;
            bool overBottom = cursor.Y >= (ActualHeight - thickness);
            bool overLeft = cursor.X <= thickness;
            bool overRight = cursor.X >= (ActualWidth - thickness);

            if (overTop || overBottom || overLeft || overRight)
            {
                UpdateClipWithHole(cursor, _clearRadius);
            }
            else
            {
                // no hole, ensure normal rendering
                RingGrid.Clip = null;
            }
        }

        private void UpdateClipWithHole(System.Windows.Point center, double radius)
        {
            // Full window rectangle geometry
            var fullRect = new Rect(0, 0, ActualWidth, ActualHeight);
            var rectGeom = new RectangleGeometry(fullRect);

            // Ellipse (hole) centered at cursor position
            var hole = new EllipseGeometry(center, radius, radius);

            // Exclude (rectangle minus ellipse) so the ellipse becomes transparent
            var combined = new CombinedGeometry(GeometryCombineMode.Exclude, rectGeom, hole);

            RingGrid.Clip = combined;
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

        // Get global cursor position in screen (device) pixels
        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT lpPoint);

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }
    }
}
