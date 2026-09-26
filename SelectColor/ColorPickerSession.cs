using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace SelectColor
{
    /// <summary>
    /// Seleccion de color con captura global del raton y mira de aumento.
    ///
    /// La mira se coloca SIEMPRE desplazada del cursor y es mas pequena que el
    /// area que captura, de modo que nunca se solapa con el punto muestreado.
    /// Gracias a eso la captura se hace directamente, sin tener que ocultar la
    /// ventana: no hay parpadeo y el color es exacto.
    ///
    ///   Mover raton .......... la mira sigue al cursor
    ///   Rueda ................ cambia el aumento
    ///   Clic izquierdo ...... elige el color
    ///   Clic derecho / ESC ... cancela
    /// </summary>
    internal sealed class ColorPickerSession : IDisposable
    {
        #region Constantes Win32

        private const int WH_MOUSE_LL = 14;
        private const int WH_KEYBOARD_LL = 13;

        private const int WM_MOUSEMOVE = 0x0200;
        private const int WM_LBUTTONDOWN = 0x0201;
        private const int WM_RBUTTONDOWN = 0x0204;
        private const int WM_MOUSEWHEEL = 0x020A;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_SYSKEYDOWN = 0x0104;

        private const int VK_ESCAPE = 0x1B;

        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint SWP_SHOWWINDOW = 0x0040;

        #endregion

        #region Parametros de la mira

        /// <summary>Radio de la abertura. Pequeno para no tapar el objetivo.</summary>
        private const double Radius = 58;

        /// <summary>Margen del bisel alrededor de la abertura.</summary>
        private const double Pad = 16;

        /// <summary>Separacion entre el cursor y el borde de la mira.</summary>
        private const double Gap = 16;

        /// <summary>Regiones muestreadas (impares, para un centro exacto).</summary>
        private static readonly int[] Regions = { 25, 21, 17, 15, 13, 11, 9, 7, 5, 3 };

        private const int DefaultRegionIndex = 4;   // 13 px

        private const double WindowSide = 2 * (Radius + Pad);

        #endregion

        #region Importaciones

        private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int nIndex);

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
            int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr hObject);

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X; public int Y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct MSLLHOOKSTRUCT
        {
            public POINT pt;
            public uint mouseData;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        #endregion

        private readonly HookProc _mouseProc;   // se mantienen vivos frente al GC
        private readonly HookProc _keyProc;

        private IntPtr _mouseHook;
        private IntPtr _keyHook;
        private bool _disposed;

        private readonly ScopeWindow _scope;
        private int _regionIndex = DefaultRegionIndex;
        private int _lastX = int.MinValue;
        private int _lastY = int.MinValue;

        /// <summary>Se dispara con el color elegido, o null si el usuario cancelo.</summary>
        public event Action<Color?> Picked;

        public ColorPickerSession()
        {
            _mouseProc = MouseProc;
            _keyProc = KeyProc;
            _scope = new ScopeWindow();
        }

        public void Start()
        {
            _scope.Show();

            IntPtr module = GetModuleHandle(null);
            _mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, module, 0);
            _keyHook = SetWindowsHookEx(WH_KEYBOARD_LL, _keyProc, module, 0);
        }

        #region Hooks

        private IntPtr MouseProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && !_disposed)
            {
                int msg = wParam.ToInt32();

                try
                {
                    switch (msg)
                    {
                        case WM_MOUSEMOVE:
                            {
                                var d = ReadStruct(lParam);
                                OnMouseMove(d.pt.X, d.pt.Y);
                                break;
                            }
                        case WM_LBUTTONDOWN:
                            {
                                var d = ReadStruct(lParam);
                                OnLeftButtonDown(d.pt.X, d.pt.Y);
                                break;
                            }
                        case WM_RBUTTONDOWN:
                            Cancel();
                            break;
                        case WM_MOUSEWHEEL:
                            {
                                int delta = (short)((wParam.ToInt32() >> 16) & 0xFFFF);
                                Zoom(delta > 0 ? 1 : -1);
                                break;
                            }
                    }
                }
                catch
                {
                    // Un hook nunca debe propagar excepciones.
                }
            }

            return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
        }

        private static MSLLHOOKSTRUCT ReadStruct(IntPtr lParam)
        {
            return (MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(MSLLHOOKSTRUCT));
        }

        private IntPtr KeyProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && !_disposed)
            {
                try
                {
                    int msg = wParam.ToInt32();
                    int vk = Marshal.ReadInt32(lParam);

                    if ((msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN) && vk == VK_ESCAPE)
                        Cancel();
                }
                catch
                {
                }
            }

            return CallNextHookEx(_keyHook, nCode, wParam, lParam);
        }

        #endregion

        #region Reacciones

        private void OnMouseMove(int x, int y)
        {
            if (x == _lastX && y == _lastY) return;
            _lastX = x;
            _lastY = y;

            _scope.Update(x, y);
        }

        private void Zoom(int direction)
        {
            int next = _regionIndex - direction;   // las regiones van de mayor a menor
            if (next < 0) next = 0;
            if (next >= Regions.Length) next = Regions.Length - 1;
            if (next == _regionIndex) return;

            _regionIndex = next;
            _scope.SetRegion(Regions[_regionIndex]);

            // Repintar con la nueva magnificacion en la posicion actual.
            int x = _lastX, y = _lastY;
            if (x != int.MinValue)
            {
                _lastX = int.MinValue;
                OnMouseMove(x, y);
            }
        }

        private void OnLeftButtonDown(int x, int y)
        {
            Color picked;
            try
            {
                int size = Regions[_regionIndex];
                using (var bmp = CaptureRegion(x, y, size))
                {
                    var c = bmp.GetPixel(size / 2, size / 2);
                    picked = Color.FromRgb(c.R, c.G, c.B);
                }
            }
            catch
            {
                picked = Colors.Black;
            }

            Finish(picked);
        }

        private void Cancel()
        {
            Finish(null);
        }

        private void Finish(Color? color)
        {
            if (_disposed) return;

            var handler = Picked;
            Dispose();
            handler?.Invoke(color);
        }

        #endregion

        #region Captura

        private static System.Drawing.Bitmap CaptureRegion(int cx, int cy, int size)
        {
            int half = size / 2;
            var bmp = new System.Drawing.Bitmap(size, size);
            using (var g = System.Drawing.Graphics.FromImage(bmp))
            {
                // La mira esta desplazada del cursor, asi que nunca se solapa
                // con esta region: se puede capturar directamente, sin parpadeo.
                g.CopyFromScreen(
                    cx - half, cy - half, 0, 0,
                    new System.Drawing.Size(size, size),
                    System.Drawing.CopyPixelOperation.SourceCopy);
            }
            return bmp;
        }

        #endregion

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            if (_mouseHook != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_mouseHook);
                _mouseHook = IntPtr.Zero;
            }
            if (_keyHook != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_keyHook);
                _keyHook = IntPtr.Zero;
            }

            try { _scope.Hide(); } catch { }
        }

        #region Mira

        /// <summary>
        /// Ventana compacta con bisel de mira. Todos los elementos se colocan
        /// una sola vez y no se mueven nunca: la unica operacion por evento es
        /// actualizar la imagen y mover la ventana, lo que evita saltos.
        /// </summary>
        private sealed class ScopeWindow : Window
        {
            private readonly Image _image;
            private readonly Border _hint;

            private IntPtr _hwnd;
            private int _region = 13;

            // Lado elegido por eje, con histeresis para que no oscile.
            private int _sideX = 1;
            private int _sideY = 1;
            private bool _sideFixed;

            private int _vsLeft, _vsTop, _vsW, _vsH;
            private int _winW, _winH;

            private static readonly double C = WindowSide / 2;   // centro de la ventana

            public ScopeWindow()
            {
                _image = new Image { Stretch = Stretch.Fill, IsHitTestVisible = false };
                RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.NearestNeighbor);

                var aperture = new Border
                {
                    Width = Radius * 2,
                    Height = Radius * 2,
                    Background = Brushes.Black,
                    BorderBrush = Brushes.Black,
                    BorderThickness = new Thickness(1),
                    Child = _image,
                    IsHitTestVisible = false,
                };
                aperture.Clip = new EllipseGeometry(new Point(Radius, Radius), Radius, Radius);
                Place(aperture, C - Radius, C - Radius);

                // Bisel exterior oscuro.
                var outer = new Ellipse
                {
                    Width = Radius * 2,
                    Height = Radius * 2,
                    Stroke = new SolidColorBrush(Color.FromArgb(240, 10, 10, 14)),
                    StrokeThickness = Pad - 2,
                    IsHitTestVisible = false,
                };
                Place(outer, C - Radius, C - Radius);

                // Filete interior claro.
                var inner = new Ellipse
                {
                    Width = Radius * 2,
                    Height = Radius * 2,
                    Stroke = new SolidColorBrush(Color.FromArgb(130, 255, 255, 255)),
                    StrokeThickness = 1,
                    IsHitTestVisible = false,
                };
                Place(inner, C - Radius, C - Radius);

                // Retícula.
                var h = new Rectangle
                {
                    Width = Radius * 2, Height = 1,
                    Fill = new SolidColorBrush(Color.FromArgb(120, 255, 255, 255)),
                    IsHitTestVisible = false,
                };
                Place(h, C - Radius, C);

                var v = new Rectangle
                {
                    Width = 1, Height = Radius * 2,
                    Fill = new SolidColorBrush(Color.FromArgb(120, 255, 255, 255)),
                    IsHitTestVisible = false,
                };
                Place(v, C, C - Radius);

                // Marca del pixel central.
                var mark = new Ellipse
                {
                    Width = 13, Height = 13,
                    Stroke = new SolidColorBrush(Color.FromArgb(255, 0, 220, 255)),
                    StrokeThickness = 1.6,
                    IsHitTestVisible = false,
                };
                Place(mark, C - 6.5, C - 6.5);

                // Muescas de mira.
                var ticks = new Path
                {
                    Stroke = new SolidColorBrush(Color.FromArgb(190, 255, 255, 255)),
                    StrokeThickness = 1.2,
                    Data = BuildTicks(),
                    IsHitTestVisible = false,
                };
                Place(ticks, 0, 0);

                // Indicador de esquina: señala que lado esta el cursor.
                _hint = new Border
                {
                    Background = new SolidColorBrush(Color.FromArgb(185, 12, 12, 20)),
                    CornerRadius = new CornerRadius(12),
                    Padding = new Thickness(12, 4, 12, 4),
                    Child = new TextBlock
                    {
                        Text = "Clic: elegir   ·   ESC: cancelar   ·   Rueda: zoom",
                        FontFamily = new FontFamily("Segoe UI"),
                        FontSize = 10,
                        Foreground = new SolidColorBrush(Color.FromRgb(200, 200, 215)),
                    },
                    IsHitTestVisible = false,
                };
                Place(_hint, 0, 0);

                var canvas = new Canvas();
                canvas.Children.Add(outer);
                canvas.Children.Add(aperture);
                canvas.Children.Add(inner);
                canvas.Children.Add(h);
                canvas.Children.Add(v);
                canvas.Children.Add(ticks);
                canvas.Children.Add(mark);

                Content = new Grid { Width = WindowSide, Height = WindowSide, Children = { canvas } };

                WindowStyle = WindowStyle.None;
                AllowsTransparency = true;
                Background = Brushes.Transparent;
                ShowInTaskbar = false;
                ShowActivated = false;
                Topmost = true;
                ResizeMode = ResizeMode.NoResize;
                Focusable = false;
                Width = WindowSide;
                Height = WindowSide;

                // La entrada la controlan los hooks: los clics pasan al fondo.
                IsHitTestVisible = false;

                _vsLeft = GetSystemMetrics(76);
                _vsTop = GetSystemMetrics(77);
                _vsW = GetSystemMetrics(78);
                _vsH = GetSystemMetrics(79);
                _winW = (int)Math.Round(WindowSide);
                _winH = (int)Math.Round(WindowSide);
            }

            private static void Place(UIElement el, double x, double y)
            {
                Canvas.SetLeft(el, x);
                Canvas.SetTop(el, y);
            }

            private static Geometry BuildTicks()
            {
                var group = new GeometryGroup();
                for (int i = 0; i < 16; i++)
                {
                    double a = i * Math.PI / 8.0;
                    bool major = (i % 4) == 0;
                    double inner = Radius + (major ? 4 : 7);
                    double outer = Radius + (major ? 11 : 9);

                    group.Children.Add(new LineGeometry(
                        new Point(C + Math.Cos(a) * inner, C + Math.Sin(a) * inner),
                        new Point(C + Math.Cos(a) * outer, C + Math.Sin(a) * outer)));
                }
                return group;
            }

            public void SetRegion(int size)
            {
                if (size % 2 == 0) size++;
                _region = size;
            }

            public void Update(int deviceX, int deviceY)
            {
                if (_hwnd == IntPtr.Zero) return;

                Move(deviceX, deviceY);

                try
                {
                    using (var bmp = CaptureRegion(deviceX, deviceY, _region))
                    {
                        DrawOverlay(bmp, _region);

                        IntPtr hBitmap = IntPtr.Zero;
                        try
                        {
                            hBitmap = bmp.GetHbitmap();
                            _image.Source = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
                                hBitmap, IntPtr.Zero, Int32Rect.Empty,
                                BitmapSizeOptions.FromEmptyOptions());
                        }
                        finally
                        {
                            if (hBitmap != IntPtr.Zero) DeleteObject(hBitmap);
                        }
                    }
                }
                catch
                {
                    // Se mantiene la ultima imagen valida.
                }
            }

            /// <summary>
            /// Coloca la mira junto al cursor. El lado se decide una sola vez y
            /// solo cambia si de verdad no cabe, evitando el vaiven en los bordes.
            /// </summary>
            private void Move(int cx, int cy)
            {
                double half = WindowSide / 2 + Gap;
                int right = _vsLeft + _vsW;
                int bottom = _vsTop + _vsH;

                if (!_sideFixed)
                {
                    _sideX = (cx + half + _winW <= right) ? 1 : -1;
                    _sideY = (cy + half + _winH <= bottom) ? 1 : -1;
                    _sideFixed = true;
                }
                else
                {
                    // Histeresis: 28 px de margen antes de cambiar de lado.
                    const int margin = 28;
                    if (_sideX > 0 && cx + half + _winW > right - margin) _sideX = -1;
                    else if (_sideX < 0 && cx - half < _vsLeft + margin) _sideX = 1;

                    if (_sideY > 0 && cy + half + _winH > bottom - margin) _sideY = -1;
                    else if (_sideY < 0 && cy - half < _vsTop + margin) _sideY = 1;
                }

                int x = cx + _sideX * (int)half - (_sideX > 0 ? 0 : _winW);
                int y = cy + _sideY * (int)half - (_sideY > 0 ? 0 : _winH);

                if (x < _vsLeft) x = _vsLeft;
                if (y < _vsTop) y = _vsTop;
                if (x + _winW > right) x = right - _winW;
                if (y + _winH > bottom) y = bottom - _winH;

                SetWindowPos(_hwnd, IntPtr.Zero, x, y, 0, 0,
                    SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
            }

            protected override void OnSourceInitialized(EventArgs e)
            {
                base.OnSourceInitialized(e);
                _hwnd = new WindowInteropHelper(this).Handle;

                // Situar sin destellar y ocultar el recordatorio a los 2.5 s.
                POINT p;
                if (GetCursorPos(out p))
                {
                    Move(p.X, p.Y);
                    LayoutHint();
                    SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, 0, 0,
                        SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_SHOWWINDOW);

                    var hide = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
                    hide.Tick += (s, a) =>
                    {
                        hide.Stop();
                        _hint.Visibility = Visibility.Collapsed;
                    };
                    hide.Start();
                }
            }

            /// <summary>
            /// El recordatorio se coloca en la esquina opuesta a la mira, o sea
            /// del lado donde esta el cursor, para no quedar debajo.
            /// </summary>
            private void LayoutHint()
            {
                double w = _hint.ActualWidth;
                if (w <= 0) return;

                double hx = _sideX > 0 ? 6 : WindowSide - w - 6;
                double hy = _sideY > 0 ? 6 : WindowSide - 26;
                Place(_hint, hx, hy);
            }

            /// <summary>Cuadricula, ejes y marca del pixel central.</summary>
            private static void DrawOverlay(System.Drawing.Bitmap bmp, int size)
            {
                int half = size / 2;

                using (var g = System.Drawing.Graphics.FromImage(bmp))
                {
                    if (size <= 15)
                    {
                        using (var grid = new System.Drawing.Pen(
                            System.Drawing.Color.FromArgb(70, 255, 255, 255)))
                        {
                            for (int i = 1; i < size; i++)
                            {
                                g.DrawLine(grid, i, 0, i, size);
                                g.DrawLine(grid, 0, i, size, i);
                            }
                        }
                    }

                    using (var cross = new System.Drawing.Pen(
                        System.Drawing.Color.FromArgb(140, 255, 90, 90), 1))
                    {
                        g.DrawLine(cross, half, 0, half, half);
                        g.DrawLine(cross, half, half + 1, half, size);
                        g.DrawLine(cross, 0, half, half, half);
                        g.DrawLine(cross, half + 1, half, size, half);
                    }

                    using (var fill = new System.Drawing.SolidBrush(
                        System.Drawing.Color.FromArgb(60, 0, 210, 255)))
                    {
                        g.FillRectangle(fill, half, half, 1, 1);
                    }

                    using (var box = new System.Drawing.Pen(
                        System.Drawing.Color.FromArgb(255, 0, 210, 255), 1))
                    {
                        g.DrawRectangle(box, half, half, 1, 1);
                    }
                }
            }
        }

        #endregion
    }
}
