using System.Windows;

namespace screenring
{
    public partial class ThicknessWindow : Window
    {
        private readonly MainWindow _overlayWindow;
        private bool _suppressSliderEvent;

        public ThicknessWindow(MainWindow overlayWindow)
        {
            _overlayWindow = overlayWindow;

            // suppress slider events while initializing and syncing value
            _suppressSliderEvent = true;
            InitializeComponent();

            // sync slider to current overlay thickness so opening the window doesn't reset it
            ThicknessSlider.Value = _overlayWindow.GetThickness();

            _suppressSliderEvent = false;
        }

        private void OnThicknessSliderValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressSliderEvent)
                return;

            _overlayWindow.SetThickness((int)e.NewValue);
        }
    }
}
