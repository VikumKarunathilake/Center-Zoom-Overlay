using System;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Center_Zoom_Overlay
{
    public partial class MainWindow : Window
    {
        // --- Win32 Interop: click-through ---
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TRANSPARENT = 0x00000020;
        private const int WS_EX_LAYERED = 0x00080000;

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        // --- Win32 Interop: GDI cleanup ---
        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr hObject);

        // --- Win32 Interop: physical screen resolution ---
        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int nIndex);

        private const int SM_CXSCREEN = 0;
        private const int SM_CYSCREEN = 1;

        // --- Win32 Interop: Window Positioning & Exclusion ---
        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll")]
        private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);

        private const uint WDA_NONE = 0x00000000;
        private const uint WDA_MONITOR = 0x00000001;
        private const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;

        // --- Zoom Configuration ---
        private int _zoomFactor = 2; // Default 2x magnification (range: 2 to 8)
        private const int MinZoom = 2;
        private const int MaxZoom = 8;
        private bool _isZoomToggled = true; // True = zoomed, False = 1x (no zoom)

        public AppSettings CurrentSettings { get; private set; }
        public event Action<int> ZoomFactorChangedExternally;

        private int _captureWidth;
        private int _captureHeight;

        // Reusable capture surfaces — allocated once, never per-frame
        private readonly object _bufferLock = new object();
        private Bitmap _captureBitmap;
        private Graphics _captureGraphics;
        private WriteableBitmap _writeableBitmap;
        private byte[] _pixelBuffer;
        private int _pixelBufferStride;

        // DPI cache variables
        private double _dpiX = 1.0;
        private double _dpiY = 1.0;
        private int _windowPxW;
        private int _windowPxH;

        // Background capture thread
        private Thread _captureThread;
        private volatile bool _isRunning;
        private volatile bool _isUpdatePending;

        // Frame-rate throttle
        private readonly Stopwatch _frameClock = Stopwatch.StartNew();

        public MainWindow()
        {
            InitializeComponent();

            Loaded += MainWindow_Loaded;
            Closed += MainWindow_Closed;
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            // Set timer resolution to 1ms to allow precise sleeping (prevent ~15.6ms sleep rounding)
            timeBeginPeriod(1);

            // Use physical pixels directly for everything since we set SetProcessDPIAware()
            PresentationSource ps = PresentationSource.FromVisual(this);
            _dpiX = ps?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
            _dpiY = ps?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;

            // Get the window handle (hwnd)
            IntPtr hwnd = new WindowInteropHelper(this).Handle;

            // --- Apply click-through so the overlay never eats game input ---
            int style = GetWindowLong(hwnd, GWL_EXSTYLE);
            SetWindowLong(hwnd, GWL_EXSTYLE, style | WS_EX_TRANSPARENT | WS_EX_LAYERED);

            // Load and apply settings
            AppSettings settings = SettingsManager.Load();
            ApplySettings(settings);

            // --- Register Global Hotkeys (PageUp & PageDown / NumPad Plus & Minus) ---
            // Modifiers: 0x0000 = None, 0x0001 = Alt, 0x0002 = Control, 0x0004 = Shift, 0x0008 = Windows
            // Registering hotkeys globally so they work while gaming
            RegisterHotKey(hwnd, HOTKEY_ZOOM_UP_PGUP, 0x0000, VK_PRIOR);      // Page Up
            RegisterHotKey(hwnd, HOTKEY_ZOOM_DOWN_PGDN, 0x0000, VK_NEXT);    // Page Down
            RegisterHotKey(hwnd, HOTKEY_ZOOM_UP_NUM, 0x0000, VK_ADD);        // Numpad +
            RegisterHotKey(hwnd, HOTKEY_ZOOM_DOWN_NUM, 0x0000, VK_SUBTRACT); // Numpad -
            RegisterHotKey(hwnd, HOTKEY_TOGGLE_SETTINGS, 0x0006, VK_S);      // Ctrl + Shift + S

            // Intercept messages to process hotkeys
            HwndSource source = HwndSource.FromHwnd(hwnd);
            source?.AddHook(HwndMessageHook);

            // --- Start the background capture thread ---
            _isRunning = true;
            _captureThread = new Thread(CaptureLoop)
            {
                IsBackground = true,
                Name = "ScreenCaptureThread",
                Priority = ThreadPriority.AboveNormal
            };
            _captureThread.Start();
        }

        // --- Win32 Global Hotkeys & Mouse Input State ---
        [DllImport("user32.dll")]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
        private static extern uint timeBeginPeriod(uint uPeriod);

        [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
        private static extern uint timeEndPeriod(uint uPeriod);

        private const int VK_MBUTTON = 0x04; // Middle mouse button
        private bool _wasMButtonDown = false;

        private const int HOTKEY_ZOOM_UP_PGUP = 9001;
        private const int HOTKEY_ZOOM_DOWN_PGDN = 9002;
        private const int HOTKEY_ZOOM_UP_NUM = 9003;
        private const int HOTKEY_ZOOM_DOWN_NUM = 9004;
        private const int HOTKEY_TOGGLE_SETTINGS = 9005;

        private const uint VK_PRIOR = 0x21;    // Page Up
        private const uint VK_NEXT = 0x22;     // Page Down
        private const uint VK_ADD = 0x6B;      // Numpad +
        private const uint VK_SUBTRACT = 0x6D; // Numpad -
        private const uint VK_S = 0x53;        // S key
        private const int WM_HOTKEY = 0x0312;

        private IntPtr HwndMessageHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_HOTKEY)
            {
                int id = wParam.ToInt32();
                bool zoomChanged = false;

                if (id == HOTKEY_ZOOM_UP_PGUP || id == HOTKEY_ZOOM_UP_NUM)
                {
                    if (_zoomFactor < MaxZoom)
                    {
                        _zoomFactor++;
                        zoomChanged = true;
                    }
                }
                else if (id == HOTKEY_ZOOM_DOWN_PGDN || id == HOTKEY_ZOOM_DOWN_NUM)
                {
                    if (_zoomFactor > MinZoom)
                    {
                        _zoomFactor--;
                        zoomChanged = true;
                    }
                }

                if (zoomChanged)
                {
                    if (CurrentSettings != null)
                    {
                        CurrentSettings.ZoomFactor = _zoomFactor;
                        SettingsManager.Save(CurrentSettings);
                    }
                    UpdateZoomBuffers();
                    ZoomFactorChangedExternally?.Invoke(_zoomFactor);
                }
                else if (id == HOTKEY_TOGGLE_SETTINGS)
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        ((App)Application.Current).ToggleSettingsWindow();
                    }));
                }
                handled = true;
            }
            return IntPtr.Zero;
        }

        public void ApplySettings(AppSettings settings)
        {
            CurrentSettings = settings;

            // Exclude from captures if configured
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero)
            {
                SetWindowDisplayAffinity(hwnd, settings.ExcludeFromCapture ? WDA_EXCLUDEFROMCAPTURE : WDA_NONE);
            }

            // Border visibility & styling
            if (ScopeBorder != null)
            {
                ScopeBorder.BorderBrush = settings.ShowBorder
                    ? new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x80, 0x80, 0x80, 0x80))
                    : new SolidColorBrush(System.Windows.Media.Colors.Transparent);
            }

            // Scope Size (Window dimensions)
            double size = settings.ScopeSize;
            this.Width = size;
            this.Height = size;

            // Clip geometry centering & corner radius
            double halfSize = size / 2;
            if (ScopeClipGeometry != null)
            {
                ScopeClipGeometry.Center = new System.Windows.Point(halfSize, halfSize);
                ScopeClipGeometry.RadiusX = halfSize;
                ScopeClipGeometry.RadiusY = halfSize;
            }
            if (ScopeBorder != null)
            {
                ScopeBorder.CornerRadius = new CornerRadius(halfSize);
            }

            // Zoom level
            _zoomFactor = settings.ZoomFactor;

            // Crosshair update
            System.Windows.Media.Color color = System.Windows.Media.Color.FromRgb(
                (byte)settings.CrosshairColorR,
                (byte)settings.CrosshairColorG,
                (byte)settings.CrosshairColorB);
            SolidColorBrush brush = new SolidColorBrush(color);

            CrosshairDot.Visibility = Visibility.Collapsed;
            CrosshairCircle.Visibility = Visibility.Collapsed;
            CrosshairCross.Visibility = Visibility.Collapsed;

            double dotSize = settings.DotSize;
            string style = settings.CrosshairStyle;

            if (style == "Dot")
            {
                CrosshairDot.Visibility = Visibility.Visible;
                CrosshairDot.Width = dotSize;
                CrosshairDot.Height = dotSize;
                CrosshairDot.Fill = brush;
            }
            else if (style == "Circle")
            {
                CrosshairCircle.Visibility = Visibility.Visible;
                CrosshairCircle.Width = dotSize * 2.5;
                CrosshairCircle.Height = dotSize * 2.5;
                CrosshairCircle.Stroke = brush;
                CrosshairCircle.StrokeThickness = Math.Max(1.0, dotSize / 4);
            }
            else if (style == "Cross")
            {
                CrosshairCross.Visibility = Visibility.Visible;
                double crossSize = dotSize * 3;
                CrosshairCross.Width = crossSize;
                CrosshairCross.Height = crossSize;
                LineH.X1 = 0; LineH.Y1 = crossSize / 2; LineH.X2 = crossSize; LineH.Y2 = crossSize / 2;
                LineV.X1 = crossSize / 2; LineV.Y1 = 0; LineV.X2 = crossSize / 2; LineV.Y2 = crossSize;
                LineH.Stroke = brush;
                LineH.StrokeThickness = Math.Max(1.0, dotSize / 4);
                LineV.Stroke = brush;
                LineV.StrokeThickness = Math.Max(1.0, dotSize / 4);
            }
            else if (style == "DotCircle")
            {
                CrosshairDot.Visibility = Visibility.Visible;
                CrosshairDot.Width = dotSize;
                CrosshairDot.Height = dotSize;
                CrosshairDot.Fill = brush;

                CrosshairCircle.Visibility = Visibility.Visible;
                CrosshairCircle.Width = dotSize * 2.5;
                CrosshairCircle.Height = dotSize * 2.5;
                CrosshairCircle.Stroke = brush;
                CrosshairCircle.StrokeThickness = Math.Max(1.0, dotSize / 4);
            }

            // Re-calculate window physical pixels based on current DPI
            _windowPxW = (int)(this.Width * _dpiX);
            _windowPxH = (int)(this.Height * _dpiY);

            // Re-center window using Win32 API
            if (hwnd != IntPtr.Zero)
            {
                int screenW = GetSystemMetrics(SM_CXSCREEN);
                int screenH = GetSystemMetrics(SM_CYSCREEN);
                int left = (screenW - _windowPxW) / 2;
                int top = (screenH - _windowPxH) / 2;
                SetWindowPos(hwnd, new IntPtr(-1), (int)(left / _dpiX), (int)(top / _dpiY), (int)this.Width, (int)this.Height, 0x0040);
            }

            UpdateZoomBuffers();
        }

        public void ToggleZoomState()
        {
            _isZoomToggled = !_isZoomToggled;
            UpdateZoomBuffers();
        }

        private void UpdateZoomBuffers()
        {
            lock (_bufferLock)
            {
                // Active zoom factor (always configure buffers for the active zoom factor)
                int activeZoom = _zoomFactor;

                _captureWidth = _windowPxW / activeZoom;
                _captureHeight = _windowPxH / activeZoom;

                // Ensure widths/heights are even numbers for GDI compatibility
                if (_captureWidth % 2 != 0) _captureWidth++;
                if (_captureHeight % 2 != 0) _captureHeight++;

                // Dispose old GDI objects
                _captureGraphics?.Dispose();
                _captureBitmap?.Dispose();

                // Allocate GDI buffer
                _captureBitmap = new Bitmap(_captureWidth, _captureHeight,
                    System.Drawing.Imaging.PixelFormat.Format32bppRgb);
                _captureGraphics = Graphics.FromImage(_captureBitmap);

                // Allocate pixel buffer for background-thread-to-UI copy
                _pixelBufferStride = _captureWidth * 4;
                _pixelBuffer = new byte[_pixelBufferStride * _captureHeight];

                Action updateAction = () =>
                {
                    _writeableBitmap = new WriteableBitmap(
                        _captureWidth,
                        _captureHeight,
                        96 * _dpiX,
                        96 * _dpiY,
                        PixelFormats.Bgr32,
                        null);
                    ZoomDisplay.Source = _writeableBitmap;

                    // Show or hide the zoom scope border while keeping the Window and the central red dot visible
                    if (ScopeBorder != null)
                    {
                        ScopeBorder.Visibility = _isZoomToggled ? Visibility.Visible : Visibility.Collapsed;
                    }
                };

                if (Dispatcher.CheckAccess())
                {
                    updateAction();
                }
                else
                {
                    Dispatcher.Invoke(updateAction);
                }
            }
        }

        private void MainWindow_Closed(object sender, EventArgs e)
        {
            _isRunning = false;
            if (_captureThread != null && _captureThread.IsAlive)
            {
                _captureThread.Join(500);
            }

            // Restore timer resolution
            timeEndPeriod(1);

            // Clean up global hotkeys
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            UnregisterHotKey(hwnd, HOTKEY_ZOOM_UP_PGUP);
            UnregisterHotKey(hwnd, HOTKEY_ZOOM_DOWN_PGDN);
            UnregisterHotKey(hwnd, HOTKEY_ZOOM_UP_NUM);
            UnregisterHotKey(hwnd, HOTKEY_ZOOM_DOWN_NUM);
            UnregisterHotKey(hwnd, HOTKEY_TOGGLE_SETTINGS);

            lock (_bufferLock)
            {
                _captureGraphics?.Dispose();
                _captureBitmap?.Dispose();
            }
        }        private void CaptureLoop()
        {
            while (_isRunning)
            {
                long startTime = _frameClock.ElapsedMilliseconds;

                // Check middle mouse button state to toggle zoom active state (1x <=> zoomed)
                bool isMButtonDown = (GetAsyncKeyState(VK_MBUTTON) & 0x8000) != 0;
                if (isMButtonDown && !_wasMButtonDown)
                {
                    _isZoomToggled = !_isZoomToggled;
                    UpdateZoomBuffers();
                }
                _wasMButtonDown = isMButtonDown;

                lock (_bufferLock)
                {
                    // Skip capture loop processing if zoom is toggled off
                    if (!_isZoomToggled)
                        goto LoopDelay;

                    if (_captureBitmap == null || _captureGraphics == null || _pixelBuffer == null || _writeableBitmap == null)
                        goto LoopDelay;

                    try
                    {
                        // Physical screen dimensions (DPI-aware via SetProcessDPIAware)
                        int screenW = GetSystemMetrics(SM_CXSCREEN);
                        int screenH = GetSystemMetrics(SM_CYSCREEN);

                        int srcX = (screenW / 2) - (_captureWidth / 2);
                        int srcY = (screenH / 2) - (_captureHeight / 2);

                        // BitBlt the center region into our reusable bitmap
                        _captureGraphics.CopyFromScreen(
                            srcX, srcY, 0, 0,
                            new System.Drawing.Size(_captureWidth, _captureHeight),
                            CopyPixelOperation.SourceCopy);

                        // Lock GDI bitmap bits and copy to pre-allocated byte array
                        var rect = new System.Drawing.Rectangle(0, 0, _captureWidth, _captureHeight);
                        var bmpData = _captureBitmap.LockBits(
                            rect,
                            System.Drawing.Imaging.ImageLockMode.ReadOnly,
                            _captureBitmap.PixelFormat);
                        try
                        {
                            Marshal.Copy(bmpData.Scan0, _pixelBuffer, 0, _pixelBuffer.Length);
                        }
                        finally
                        {
                            _captureBitmap.UnlockBits(bmpData);
                        }

                        // Dispatch update to UI thread if no update is currently pending
                        if (!_isUpdatePending)
                        {
                            _isUpdatePending = true;

                            // Capture references for the UI thread closure
                            byte[] bufferRef = _pixelBuffer;
                            int stride = _pixelBufferStride;
                            int w = _captureWidth;
                            int h = _captureHeight;
                            WriteableBitmap wbmp = _writeableBitmap;

                            Dispatcher.BeginInvoke(new Action(() =>
                            {
                                try
                                {
                                    if (wbmp != null && wbmp.PixelWidth == w && wbmp.PixelHeight == h)
                                    {
                                        wbmp.WritePixels(
                                            new Int32Rect(0, 0, w, h),
                                            bufferRef,
                                            stride,
                                            0);
                                    }
                                }
                                finally
                                {
                                    _isUpdatePending = false;
                                }
                            }), System.Windows.Threading.DispatcherPriority.Render);
                        }
                    }
                    catch
                    {
                        // Guard against transient failures: resolution changes, UAC prompts, alt-tabs
                    }
                }

            LoopDelay:
                long elapsed = _frameClock.ElapsedMilliseconds - startTime;
                int sleepMs = (int)(7 - elapsed); // Target ~144Hz (7ms per frame)
                if (sleepMs > 0)
                {
                    Thread.Sleep(sleepMs);
                }
                else
                {
                    Thread.Sleep(1);
                }
            }
        }
    }
}