using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Structure;
using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Controls;

#nullable enable

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

        // --- Face detection / capture fields ---
        private VideoCapture? _capture;
        private CascadeClassifier? _faceCascade;
        private CancellationTokenSource? _captureCts;
        private readonly object _faceLock = new object();

        // Target and current positions in WPF units (logical pixels)
        private System.Windows.Point _faceTarget = new System.Windows.Point(double.NaN, double.NaN);
        private System.Windows.Point _faceCurrent = new System.Windows.Point(double.NaN, double.NaN);

        // Face proximity (0..1) target and current (smoothed)
        private double _faceProximity = double.NaN;
        private double _faceProximityCurrent = double.NaN;

        // Smoothing factor for lerp per CompositionTarget frame (0..1)
        private const double LerpFactor = 0.20;

        // Visual tuning for ring glow (we will lerp the ring rectangles toward white)
        private readonly Color _baseRingColor = Color.FromArgb(0xCC, 0x00, 0x00, 0x00); // default dark semi-transparent
        private readonly Color _highlightColor = Colors.White;
        private const double MaxProximityRange = 300.0; // pixels from ring edge where glow starts

        // Brushes for rectangles (so updating is cheap)
        private SolidColorBrush? _topBrush;
        private SolidColorBrush? _bottomBrush;
        private SolidColorBrush? _leftBrush;
        private SolidColorBrush? _rightBrush;

        // The yellow ellipse remains a test indicator and is NOT modified for brightness.
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

            // ensure we unsubscribe from rendering and stop capture when app is closing
            CompositionTarget.Rendering -= CompositionTarget_Rendering;
            StopFaceCapture();
            base.OnClosing(e);
        }

        private void MainWindow_IsVisibleChanged(object? sender, DependencyPropertyChangedEventArgs e)
        {
            if (IsVisible)
            {
                StartRendering();
                StartFaceCapture();
            }
            else
            {
                StopRendering();
                StopFaceCapture();
                FaceHighlight.Visibility = Visibility.Collapsed;
            }
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

            // create and assign brushes for rectangles so we can update their color to white as proximity increases
            _topBrush = new SolidColorBrush(_baseRingColor);
            _bottomBrush = new SolidColorBrush(_baseRingColor);
            _leftBrush = new SolidColorBrush(_baseRingColor);
            _rightBrush = new SolidColorBrush(_baseRingColor);

            TopRect.Fill = _topBrush;
            BottomRect.Fill = _bottomBrush;
            LeftRect.Fill = _leftBrush;
            RightRect.Fill = _rightBrush;

            // Ensure the test ellipse (FaceHighlight) is the yellow indicator and keep it unchanged by proximity logic.
            if (!(FaceHighlight.Fill is SolidColorBrush))
            {
                FaceHighlight.Fill = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xD5, 0x4F));
            }
            if (!(FaceHighlight.Stroke is SolidColorBrush))
            {
                FaceHighlight.Stroke = new SolidColorBrush(Color.FromArgb(0x80, 0xFF, 0xD5, 0x4F));
            }
            // Do NOT change the ellipse opacity or strokeThickness here — it's only a test indicator.

            if (IsVisible)
            {
                StartRendering();
                StartFaceCapture();
            }
        }

        public void SetThickness(int value)
        {
            thickness = value;
            ApplyThickness();
            // keep ellipse test size separate; this does not control ring brightness
            FaceHighlight.Width = value * 2;
            FaceHighlight.Height = value * 2;
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
            }
            else
            {
                _lastCursor = cursor;
            }

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

            // --- Face highlight smoothing & update (runs on UI thread) ---
            System.Windows.Point target;
            double targetProximity;
            lock (_faceLock)
            {
                target = _faceTarget;
                targetProximity = _faceProximity;
            }

            if (!double.IsNaN(target.X) && !double.IsNaN(target.Y))
            {
                // if first time, snap current to target
                if (double.IsNaN(_faceCurrent.X))
                {
                    _faceCurrent = target;
                    _faceProximityCurrent = targetProximity;
                }
                else
                {
                    // lerp toward target for smoothing
                    _faceCurrent.X = _faceCurrent.X + (target.X - _faceCurrent.X) * LerpFactor;
                    _faceCurrent.Y = _faceCurrent.Y + (target.Y - _faceCurrent.Y) * LerpFactor;
                    _faceProximityCurrent = _faceProximityCurrent + (targetProximity - _faceProximityCurrent) * LerpFactor;
                }

                // position the ellipse centered at _faceCurrent (test indicator)
                var left = _faceCurrent.X - (FaceHighlight.Width / 2.0);
                var top = _faceCurrent.Y - (FaceHighlight.Height / 2.0);

                Canvas.SetLeft(FaceHighlight, left);
                Canvas.SetTop(FaceHighlight, top);

                // compute proximity to ring rectangles on UI (distance from face center to inner edge of each rectangle)
                // positive distances mean face is outside the band; negative means overlapping band
                double distTop = _faceCurrent.Y - thickness;
                double distBottom = (ActualHeight - thickness) - _faceCurrent.Y;
                double distLeft = _faceCurrent.X - thickness;
                double distRight = (ActualWidth - thickness) - _faceCurrent.X;

                double proxTop = DistanceToProximity(distTop, MaxProximityRange);
                double proxBottom = DistanceToProximity(distBottom, MaxProximityRange);
                double proxLeft = DistanceToProximity(distLeft, MaxProximityRange);
                double proxRight = DistanceToProximity(distRight, MaxProximityRange);

                // strongest proximity among sides controls the glow
                double proximity = Math.Max(Math.Max(proxTop, proxBottom), Math.Max(proxLeft, proxRight));
                proximity = Math.Clamp(proximity, 0.0, 1.0);

                // quadratic curve as requested (emphasize near-edge)
                double q = proximity * proximity;

                // update rectangle colors by lerping from base to white using quadratic proximity
                UpdateRectangleBrush(_topBrush, q);
                UpdateRectangleBrush(_bottomBrush, q);
                UpdateRectangleBrush(_leftBrush, q);
                UpdateRectangleBrush(_rightBrush, q);

                if (FaceHighlight.Visibility != Visibility.Visible)
                    FaceHighlight.Visibility = Visibility.Visible;
            }
            else
            {
                // no face — hide indicator and reset ring to base color
                if (FaceHighlight.Visibility == Visibility.Visible)
                    FaceHighlight.Visibility = Visibility.Collapsed;
                _faceCurrent = new System.Windows.Point(double.NaN, double.NaN);
                _faceProximityCurrent = double.NaN;

                UpdateRectangleBrush(_topBrush, 0.0);
                UpdateRectangleBrush(_bottomBrush, 0.0);
                UpdateRectangleBrush(_leftBrush, 0.0);
                UpdateRectangleBrush(_rightBrush, 0.0);
            }
        }

        // Convert a signed distance to 0..1 proximity where <=0 -> 1 (inside band), and maxRange away -> 0.
        private static double DistanceToProximity(double signedDistance, double maxRange)
        {
            if (signedDistance <= 0)
                return 1.0;
            return Math.Clamp(1.0 - (signedDistance / maxRange), 0.0, 1.0);
        }

        // Lerp rectangle brush color toward white according to t (0..1). If brush is null nothing happens.
        private void UpdateRectangleBrush(SolidColorBrush? brush, double t)
        {
            if (brush == null)
                return;

            // square t already applied by caller if desired; this method just lerps
            var c = LerpColor(_baseRingColor, _highlightColor, t);
            // update brush color on UI thread — we're on UI thread here.
            brush.Color = c;
        }

        // Linear interpolation of ARGB colors
        private static Color LerpColor(Color a, Color b, double t)
        {
            byte A = (byte)Math.Round(a.A + (b.A - a.A) * t);
            byte R = (byte)Math.Round(a.R + (b.R - a.R) * t);
            byte G = (byte)Math.Round(a.G + (b.G - a.G) * t);
            byte B = (byte)Math.Round(a.B + (b.B - a.B) * t);
            return Color.FromArgb(A, R, G, B);
        }

        private void StartFaceCapture()
        {
            // already running?
            if (_capture != null || _captureCts != null)
                return;

            try
            {
                // Create classifier from a file shipped with app output folder.
                // Ensure "haarcascade_frontalface_default.xml" is copied to output.
                var cascadePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "haarcascade_frontalface_default.xml");
                if (File.Exists(cascadePath))
                {
                    _faceCascade = new CascadeClassifier(cascadePath);
                }
                else
                {
                    // If cascade not found, skip starting capture to avoid exceptions.
                    _faceCascade = null;
                }

                _capture = new VideoCapture(0, VideoCapture.API.DShow);
                // prefer a reasonable, not-too-large frame size to reduce CPU
                _capture.Set(CapProp.FrameWidth, 320);
                _capture.Set(CapProp.FrameHeight, 240);
                _capture.ImageGrabbed += Capture_ImageGrabbed;

                _captureCts = new CancellationTokenSource();
                // start async grabbing loop
                _capture.Start();
            }
            catch
            {
                // best-effort: if camera can't be opened, ensure we don't crash
                StopFaceCapture();
            }
        }

        private void Capture_ImageGrabbed(object? sender, EventArgs e)
        {
            // This handler runs on a background thread inside Emgu's capture loop.
            try
            {
                if (_capture == null)
                    return;

                using var mat = new Mat();
                if (!_capture.Retrieve(mat) || mat.IsEmpty)
                    return;

                // convert to gray and detect faces
                using var gray = new Mat();
                CvInvoke.CvtColor(mat, gray, ColorConversion.Bgr2Gray);
                CvInvoke.EqualizeHist(gray, gray);

                // use fully-qualified System.Drawing.Rectangle to avoid ambiguity with WPF shapes
                var faces = Array.Empty<System.Drawing.Rectangle>();
                if (_faceCascade != null)
                {
                    using var u = gray.ToImage<Gray, byte>();
                    var rects = _faceCascade.DetectMultiScale(u, 1.1, 4, System.Drawing.Size.Empty);
                    faces = rects.ToArray();
                }

                if (faces.Length == 0)
                {
                    lock (_faceLock)
                    {
                        _faceTarget = new System.Windows.Point(double.NaN, double.NaN);
                        _faceProximity = double.NaN;
                    }
                    return;
                }

                // choose largest face (most likely primary)
                var face = faces.OrderByDescending(r => r.Width * r.Height).First();

                // Map face center from capture frame -> overlay window (WPF units)
                var frameWidth = mat.Width;
                var frameHeight = mat.Height;

                // center point in frame pixels
                double cx = face.X + face.Width * 0.5;
                double cy = face.Y + face.Height * 0.5;

                // INVERT horizontal mapping: camera is mirrored relative to screen movement
                double targetX = ((frameWidth - cx) / frameWidth) * SystemParameters.PrimaryScreenWidth;
                double targetY = (cy / frameHeight) * SystemParameters.PrimaryScreenHeight;

                // compute proximity to camera edge (0 = center, 1 = on an edge)
                double minDistX = Math.Min(cx, frameWidth - cx);
                double minDistY = Math.Min(cy, frameHeight - cy);

                double normalizedX = 1.0 - (minDistX / (frameWidth * 0.5));
                double normalizedY = 1.0 - (minDistY / (frameHeight * 0.5));

                normalizedX = Math.Clamp(normalizedX, 0.0, 1.0);
                normalizedY = Math.Clamp(normalizedY, 0.0, 1.0);

                double proximity = Math.Max(normalizedX, normalizedY); // stronger when near any edge

                lock (_faceLock)
                {
                    _faceTarget = new System.Windows.Point(targetX, targetY);
                    _faceProximity = proximity;
                }
            }
            catch
            {
                // ignore processing errors — don't crash capture loop
            }
        }

        private void StopFaceCapture()
        {
            try
            {
                if (_capture != null)
                {
                    _capture.ImageGrabbed -= Capture_ImageGrabbed;
                    _capture.Stop();
                    _capture.Dispose();
                    _capture = null;
                }
            }
            catch
            {
                // ignore
                _capture = null;
            }

            try
            {
                _captureCts?.Cancel();
                _captureCts?.Dispose();
            }
            catch { }
            _captureCts = null;

            try
            {
                _faceCascade?.Dispose();
            }
            catch { }
            _faceCascade = null;

            lock (_faceLock)
            {
                _faceTarget = new System.Windows.Point(double.NaN, double.NaN);
                _faceProximity = double.NaN;
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
