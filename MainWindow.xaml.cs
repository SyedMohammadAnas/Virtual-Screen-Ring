using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace screenring
{
    public partial class MainWindow : Window
    {
        private int thickness = 50;

        // Reused geometry objects to avoid allocations each frame
        private readonly RectangleGeometry _fullRectGeom = new RectangleGeometry();
        private readonly EllipseGeometry _holeGeom = new EllipseGeometry();
        private readonly CombinedGeometry _combinedClip;
        private System.Windows.Point _lastCursor = new System.Windows.Point(double.NaN, double.NaN);

        // clear radius (pixels in WPF units)
        private const double ClearRadius = 100.0;

        public MainWindow()
        {
            InitializeComponent();

            // Combined geometry built once and updated by changing _holeGeom.Center
            _combinedClip = new CombinedGeometry(GeometryCombineMode.Exclude, _fullRectGeom, _holeGeom);

            IsVisibleChanged += MainWindow_IsVisibleChanged;
            SizeChanged += MainWindow_SizeChanged;
        }

        // Allow external code to read current thickness so sliders can initialize correctly
        public int GetThickness() => thickness;

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

            // ensure we unsubscribe from rendering when app is closing
            CompositionTarget.Rendering -= CompositionTarget_Rendering;
            base.OnClosing(e);
        }

        private void MainWindow_IsVisibleChanged(object? sender, DependencyPropertyChangedEventArgs e)
        {
            if (IsVisible)
                StartRendering();
            else
                StopRendering();
        }

        private void MainWindow_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            // update full rect geometry to current window size (WPF units)
            _fullRectGeom.Rect = new Rect(0, 0, ActualWidth, ActualHeight);
        }

        private void StartRendering()
        {
            // Hook into per-frame rendering to get smooth updates (vs. DispatcherTimer)
            CompositionTarget.Rendering += CompositionTarget_Rendering;
            // ensure geometry rect matches
            _fullRectGeom.Rect = new Rect(0, 0, ActualWidth, ActualHeight);
        }

        private void StopRendering()
        {
            CompositionTarget.Rendering -= CompositionTarget_Rendering;
            RingGrid.Clip = null;
            _lastCursor = new System.Windows.Point(double.NaN, double.NaN);
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            Left = 0;
            Top = 0;
            Width = SystemParameters.PrimaryScreenWidth;
            Height = SystemParameters.PrimaryScreenHeight;

            MakeClickThrough();

            // apply current thickness to the rectangles initially
            ApplyThickness();

            if (IsVisible)
                StartRendering();
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

            // Update full rect in case layout changed
            _fullRectGeom.Rect = new Rect(0, 0, ActualWidth, ActualHeight);
        }

        private void CompositionTarget_Rendering(object? sender, EventArgs e)
        {
            // Get cursor in screen (device) pixels
            if (!GetCursorPos(out POINT pt))
            {
                RingGrid.Clip = null;
                return;
            }

            // Convert device pixels to WPF units
            var source = PresentationSource.FromVisual(this) as HwndSource;
            if (source == null || source.CompositionTarget == null)
            {
                RingGrid.Clip = null;
                return;
            }

            var transform = source.CompositionTarget.TransformFromDevice;
            var cursor = transform.Transform(new System.Windows.Point(pt.X, pt.Y));

            // small threshold to avoid updating when cursor hasn't moved much (reduces jitter)
            const double moveThreshold = 0.5;
            if (!double.IsNaN(_lastCursor.X) &&
                Math.Abs(cursor.X - _lastCursor.X) < moveThreshold &&
                Math.Abs(cursor.Y - _lastCursor.Y) < moveThreshold)
            {
                // no meaningful movement, skip heavy work
                return;
            }

            _lastCursor = cursor;

            // Check if cursor is over any ring rectangle (use thickness and current Actual sizes)
            bool overTop = cursor.Y <= thickness;
            bool overBottom = cursor.Y >= (ActualHeight - thickness);
            bool overLeft = cursor.X <= thickness;
            bool overRight = cursor.X >= (ActualWidth - thickness);

            if (overTop || overBottom || overLeft || overRight)
            {
                // update hole center and radius (update existing geometry instances only)
                _holeGeom.Center = cursor;
                _holeGeom.RadiusX = ClearRadius;
                _holeGeom.RadiusY = ClearRadius;

                // assign combined clip (same object instance — cheap)
                RingGrid.Clip = _combinedClip;
            }
            else
            {
                // not over ring: clear clip
                RingGrid.Clip = null;
            }
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
