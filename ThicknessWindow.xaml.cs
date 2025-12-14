using System.Windows;

namespace screenring
{
    public partial class ThicknessWindow : Window
    {
        private readonly MainWindow _overlayWindow;

        public ThicknessWindow(MainWindow overlayWindow)
        {
            // assign before InitializeComponent so slider events during initialization
            // won't race with a null reference.
            _overlayWindow = overlayWindow;

            InitializeComponent();
        }

        private void OnThicknessSliderValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_overlayWindow is null)
                return;

            _overlayWindow.SetThickness((int)e.NewValue);
        }
    }
}
