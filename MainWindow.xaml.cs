using System;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
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

        private int _captureWidth;
        private int _captureHeight;

        // Reusable capture surfaces — allocated once, never per-frame
        private readonly object _bufferLock = new object();
        private Bitmap _captureBitmap;
        private Graphics _captureGraphics;

        // DPI cache variables
        private double _dpiX = 1.0;
        private double _dpiY = 1.0;
        private int _windowPxW;
        private int _windowPxH;

        // Frame-rate throttle
        private readonly Stopwatch _frameClock = Stopwatch.StartNew();
        private long _lastFrameMs;

        public MainWindow()
        {
            InitializeComponent();

            Loaded += MainWindow_Loaded;
            Closed += MainWindow_Closed;
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            // Use physical pixels directly for everything since we set SetProcessDPIAware()
            PresentationSource ps = PresentationSource.FromVisual(this);
            _dpiX = ps?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
            _dpiY = ps?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;

            // Physical size of our window on the screen
            _windowPxW = (int)(Width * _dpiX);
            _windowPxH = (int)(Height * _dpiY);

            // Set up initial buffer sizes
            UpdateZoomBuffers();

            // Get the window handle (hwnd)
            IntPtr hwnd = new WindowInteropHelper(this).Handle;

            // --- Position the window exactly in the physical screen center ---
            int screenW = GetSystemMetrics(SM_CXSCREEN);
            int screenH = GetSystemMetrics(SM_CYSCREEN);
            int left = (screenW - _windowPxW) / 2;
            int top = (screenH - _windowPxH) / 2;

            // Set screen coordinates using Win32 to bypass any WPF DPI-scaling layout issues
            SetWindowPos(hwnd, new IntPtr(-1), (int)(left / _dpiX), (int)(top / _dpiY), (int)Width, (int)Height, 0x0040);

            // --- Apply click-through so the overlay never eats game input ---
            int style = GetWindowLong(hwnd, GWL_EXSTYLE);
            SetWindowLong(hwnd, GWL_EXSTYLE, style | WS_EX_TRANSPARENT | WS_EX_LAYERED);

            // --- Exclude this window from screen capture to prevent recursive feedback zooming ---
            SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE);

            // --- Register Global Hotkeys (PageUp & PageDown / NumPad Plus & Minus) ---
            // Modifiers: 0x0000 = None, 0x0001 = Alt, 0x0002 = Control, 0x0004 = Shift, 0x0008 = Windows
            // Registering hotkeys globally so they work while gaming
            RegisterHotKey(hwnd, HOTKEY_ZOOM_UP_PGUP, 0x0000, VK_PRIOR);      // Page Up
            RegisterHotKey(hwnd, HOTKEY_ZOOM_DOWN_PGDN, 0x0000, VK_NEXT);    // Page Down
            RegisterHotKey(hwnd, HOTKEY_ZOOM_UP_NUM, 0x0000, VK_ADD);        // Numpad +
            RegisterHotKey(hwnd, HOTKEY_ZOOM_DOWN_NUM, 0x0000, VK_SUBTRACT); // Numpad -

            // Intercept messages to process hotkeys
            HwndSource source = HwndSource.FromHwnd(hwnd);
            source?.AddHook(HwndMessageHook);

            // --- Register Global Low-Level Mouse Hook for Middle Click Toggle ---
            _mouseHookProcedure = MouseHookCallback;
            using (Process currentProcess = Process.GetCurrentProcess())
            using (ProcessModule currentModule = currentProcess.MainModule)
            {
                _mouseHookId = SetWindowsHookEx(
                    WH_MOUSE_LL,
                    _mouseHookProcedure,
                    GetModuleHandle(currentModule.ModuleName),
                    0);
            }

            // --- Start the render-synced capture loop ---
            CompositionTarget.Rendering += OnRenderFrame;
        }

        // --- Win32 Global Hotkeys & Mouse Hooks ---
        [DllImport("user32.dll")]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

        private LowLevelMouseProc _mouseHookProcedure;
        private IntPtr _mouseHookId = IntPtr.Zero;

        private const int WH_MOUSE_LL = 14;
        private const int WM_MBUTTONDOWN = 0x0207;

        private const int HOTKEY_ZOOM_UP_PGUP = 9001;
        private const int HOTKEY_ZOOM_DOWN_PGDN = 9002;
        private const int HOTKEY_ZOOM_UP_NUM = 9003;
        private const int HOTKEY_ZOOM_DOWN_NUM = 9004;

        private const uint VK_PRIOR = 0x21;    // Page Up
        private const uint VK_NEXT = 0x22;     // Page Down
        private const uint VK_ADD = 0x6B;      // Numpad +
        private const uint VK_SUBTRACT = 0x6D; // Numpad -
        private const int WM_HOTKEY = 0x0312;

        private IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && wParam.ToInt32() == WM_MBUTTONDOWN)
            {
                // Toggle overlay visibility on middle mouse button down
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (this.Visibility == Visibility.Visible)
                    {
                        this.Visibility = Visibility.Collapsed;
                    }
                    else
                    {
                        this.Visibility = Visibility.Visible;
                    }
                }));
            }
            return CallNextHookEx(_mouseHookId, nCode, wParam, lParam);
        }

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
                    UpdateZoomBuffers();
                }
                handled = true;
            }
            return IntPtr.Zero;
        }

        private void UpdateZoomBuffers()
        {
            lock (_bufferLock)
            {
                // To zoom by _zoomFactor, capture a smaller screen region
                _captureWidth = _windowPxW / _zoomFactor;
                _captureHeight = _windowPxH / _zoomFactor;

                // Ensure widths/heights are even numbers for GDI compatibility
                if (_captureWidth % 2 != 0) _captureWidth++;
                if (_captureHeight % 2 != 0) _captureHeight++;

                // Dispose old objects
                _captureGraphics?.Dispose();
                _captureBitmap?.Dispose();

                // Allocate new buffers with the updated size
                _captureBitmap = new Bitmap(_captureWidth, _captureHeight,
                    System.Drawing.Imaging.PixelFormat.Format32bppRgb);
                _captureGraphics = Graphics.FromImage(_captureBitmap);
            }
        }

        private void MainWindow_Closed(object sender, EventArgs e)
        {
            CompositionTarget.Rendering -= OnRenderFrame;

            // Clean up low-level mouse hook
            if (_mouseHookId != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_mouseHookId);
                _mouseHookId = IntPtr.Zero;
            }
            
            // Clean up global hotkeys
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            UnregisterHotKey(hwnd, HOTKEY_ZOOM_UP_PGUP);
            UnregisterHotKey(hwnd, HOTKEY_ZOOM_DOWN_PGDN);
            UnregisterHotKey(hwnd, HOTKEY_ZOOM_UP_NUM);
            UnregisterHotKey(hwnd, HOTKEY_ZOOM_DOWN_NUM);

            lock (_bufferLock)
            {
                _captureGraphics?.Dispose();
                _captureBitmap?.Dispose();
            }
        }

        private void OnRenderFrame(object sender, EventArgs e)
        {
            // Throttle to support up to 144Hz (1000ms / 144fps ≈ 6.94ms)
            long now = _frameClock.ElapsedMilliseconds;
            if (now - _lastFrameMs < 6)
                return;
            _lastFrameMs = now;

            lock (_bufferLock)
            {
                if (_captureBitmap == null || _captureGraphics == null)
                    return;

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

                    // Convert GDI HBITMAP → WPF BitmapSource directly (no MemoryStream!)
                    IntPtr hBitmap = _captureBitmap.GetHbitmap();
                    try
                    {
                    BitmapSource source = Imaging.CreateBitmapSourceFromHBitmap(
                        hBitmap,
                        IntPtr.Zero,
                        Int32Rect.Empty,
                        BitmapSizeOptions.FromEmptyOptions());
                    source.Freeze(); // Required for cross-thread WPF perf
                    ZoomDisplay.Source = source;
                }
                finally
                {
                    // Always release the GDI handle to avoid a handle leak
                    DeleteObject(hBitmap);
                }
                }
                catch
                {
                    // Guard against transient failures: resolution changes, UAC prompts, alt-tabs
                }
            }
        }
    }
}