using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Center_Zoom_Overlay
{
    public partial class SettingsWindow : Window
    {
        private readonly MainWindow _mainWindow;
        private AppSettings _settings;
        private bool _isInitializing = true;

        public SettingsWindow(MainWindow mainWindow)
        {
            InitializeComponent();
            _mainWindow = mainWindow;
            
            // Subscribe to external zoom changes (from PgUp/PgDn hotkeys)
            _mainWindow.ZoomFactorChangedExternally += OnZoomChangedExternally;

            LoadSettingsIntoUi();
        }

        private void LoadSettingsIntoUi()
        {
            _isInitializing = true;
            _settings = _mainWindow.CurrentSettings;

            if (_settings == null)
            {
                _settings = new AppSettings();
            }

            SldZoom.Value = _settings.ZoomFactor;
            TxtZoom.Text = $"{_settings.ZoomFactor}x";

            SldSize.Value = _settings.ScopeSize;
            TxtSize.Text = $"{(int)_settings.ScopeSize}";

            ChkBorder.IsChecked = _settings.ShowBorder;
            ChkExcludeCapture.IsChecked = _settings.ExcludeFromCapture;

            // Select ComboBox item matching style Tag
            foreach (ComboBoxItem item in CboStyle.Items)
            {
                if (item.Tag?.ToString() == _settings.CrosshairStyle)
                {
                    CboStyle.SelectedItem = item;
                    break;
                }
            }

            SldDotSize.Value = _settings.DotSize;
            TxtDotSize.Text = $"{_settings.DotSize}";

            SldColorR.Value = _settings.CrosshairColorR;
            SldColorG.Value = _settings.CrosshairColorG;
            SldColorB.Value = _settings.CrosshairColorB;

            UpdateColorPreview();

            _isInitializing = false;
        }

        private void SettingsChanged(object sender, RoutedEventArgs e)
        {
            ApplySettingsFromUi();
        }

        private void SettingsChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            ApplySettingsFromUi();
        }

        private void SettingsChanged(object sender, SelectionChangedEventArgs e)
        {
            ApplySettingsFromUi();
        }

        private void ApplySettingsFromUi()
        {
            if (_isInitializing || _settings == null || _mainWindow == null) return;

            _settings.ZoomFactor = (int)SldZoom.Value;
            TxtZoom.Text = $"{_settings.ZoomFactor}x";

            _settings.ScopeSize = SldSize.Value;
            TxtSize.Text = $"{(int)SldSize.Value}";

            _settings.ShowBorder = ChkBorder.IsChecked == true;
            _settings.ExcludeFromCapture = ChkExcludeCapture.IsChecked == true;

            if (CboStyle.SelectedItem is ComboBoxItem selectedItem)
            {
                _settings.CrosshairStyle = selectedItem.Tag?.ToString() ?? "Dot";
            }

            _settings.DotSize = SldDotSize.Value;
            TxtDotSize.Text = $"{_settings.DotSize}";

            _settings.CrosshairColorR = (int)SldColorR.Value;
            _settings.CrosshairColorG = (int)SldColorG.Value;
            _settings.CrosshairColorB = (int)SldColorB.Value;

            UpdateColorPreview();

            // Apply immediately to MainWindow
            _mainWindow.ApplySettings(_settings);

            // Save settings in the background
            SettingsManager.Save(_settings);
        }

        private void UpdateColorPreview()
        {
            if (BdrColorPreview != null)
            {
                Color color = Color.FromRgb(
                    (byte)SldColorR.Value,
                    (byte)SldColorG.Value,
                    (byte)SldColorB.Value);
                BdrColorPreview.Background = new SolidColorBrush(color);
            }
        }

        private void OnZoomChangedExternally(int newZoom)
        {
            if (_isInitializing) return;

            // Update UI thread-safely
            Dispatcher.Invoke(() =>
            {
                _isInitializing = true;
                SldZoom.Value = newZoom;
                TxtZoom.Text = $"{newZoom}x";
                _isInitializing = false;
            });
        }

        private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            DragMove();
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            Hide();
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            e.Cancel = true;
            Hide();
        }
    }
}
