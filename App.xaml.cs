using System.Windows;

namespace screenring
{
    public partial class ThicknessWindow : Window
    {
        private readonly MainWindow overlay;

        public ThicknessWindow(MainWindow overlayWindow)
        {
            InitializeComponent();
            overlay = overlayWindow;
        }

        private void Slider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            overlay.SetThickness((int)e.NewValue);
        }
    }
}
