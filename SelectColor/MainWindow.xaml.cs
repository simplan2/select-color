using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ColorDialog = System.Windows.Forms.ColorDialog;

namespace SelectColor
{
    public partial class MainWindow : Window
    {
        private const int MaxHistory = 24;
        private const int MaxFavorites = 24;
        private const string DefaultStatus = "Listo";

        private static readonly Brush SuccessBrush = Freeze(Color.FromRgb(95, 191, 127));
        private static readonly Brush ErrorBrush = Freeze(Color.FromRgb(224, 108, 108));
        private static readonly Brush WarningBrush = Freeze(Color.FromRgb(224, 178, 95));
        private static readonly Brush InfoBrush = Freeze(Color.FromRgb(158, 158, 158));

        private static Brush Freeze(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }

        private readonly ObservableCollection<ColorItem> _favorites = new ObservableCollection<ColorItem>();
        private readonly ObservableCollection<ColorItem> _history = new ObservableCollection<ColorItem>();
        private readonly DispatcherTimer _toastTimer = new DispatcherTimer();

        private bool _suppressHexSync;
        private bool _ready;
        private string _pendingFeedback;
        private FeedbackKind _pendingKind;
        private ColorPickerSession _picker;
        private Color _currentColor = Colors.White;

        public MainWindow()
        {
            InitializeComponent();

            FavoritesPanel.ItemsSource = _favorites;
            HistoryPanel.ItemsSource = _history;

            _toastTimer.Interval = TimeSpan.FromSeconds(3.5);

            ApplyColor(Colors.White, addToHistory: false);
            LoadState();

            Loaded += (s, e) =>
            {
                _ready = true;

                if (_pendingFeedback != null)
                {
                    var msg = _pendingFeedback;
                    var kind = _pendingKind;
                    _pendingFeedback = null;
                    ShowFeedback(msg, kind);
                }
            };
        }

        #region Color Sync

        private void Slider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressHexSync || !_ready) return;

            var color = Color.FromRgb(
                (byte)SliderR.Value,
                (byte)SliderG.Value,
                (byte)SliderB.Value);

            ApplyColor(color, addToHistory: true);
        }

        private void HexTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_suppressHexSync || !_ready) return;

            string raw = HexTextBox.Text.Trim();
            if (raw.StartsWith("#")) raw = raw.Substring(1);

            if (raw.Length == 3)
            {
                raw = new string(new[] { raw[0], raw[0], raw[1], raw[1], raw[2], raw[2] });
            }

            if (raw.Length == 6 && IsHex(raw))
            {
                ResetStatus();
                var color = (Color)ColorConverter.ConvertFromString("#" + raw);
                ApplyColor(color, addToHistory: true);
            }
            else if (raw.Length > 0)
            {
                ShowFeedback("Formato inválido: usa 3 o 6 dígitos hexadecimales.", FeedbackKind.Error);
            }
        }

        private void HexTextBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            if (!Regex.IsMatch(e.Text, "^[0-9A-Fa-f#]$"))
                e.Handled = true;
        }

        private void ApplyColor(Color color, bool addToHistory)
        {
            _currentColor = color;

            if (!_ready)
            {
                // La ventana aun no esta cargada: solo se actualiza el estado,
                // la interfaz se sincronizara en el evento Loaded.
                return;
            }

            _suppressHexSync = true;
            try
            {
                SliderR.Value = color.R;
                SliderG.Value = color.G;
                SliderB.Value = color.B;
                HexTextBox.Text = ToHex(color);
            }
            finally
            {
                _suppressHexSync = false;
            }

            ColorPreviewBrush.Color = color;
            PreviewLabel.Text = ToHex(color);

            double luminance = (0.2126 * color.R + 0.7152 * color.G + 0.0722 * color.B) / 255.0;
            PreviewLabel.Foreground = luminance > 0.55
                ? new SolidColorBrush(Color.FromRgb(20, 20, 30))
                : Brushes.White;

            HexValue.Text = ToHex(color);
            RgbValue.Text = string.Format("{0}, {1}, {2}", color.R, color.G, color.B);
            HslValue.Text = ToHslString(color);

            if (addToHistory)
                PushHistory(color);
        }

        #endregion

        #region HSL

        private static string ToHslString(Color c)
        {
            double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
            double max = Math.Max(r, Math.Max(g, b));
            double min = Math.Min(r, Math.Min(g, b));
            double delta = max - min;
            double l = (max + min) / 2.0;
            double h = 0, s = 0;

            if (delta > 0)
            {
                s = l > 0.5 ? delta / (2.0 - max - min) : delta / (max + min);
                if (max == r) h = (g - b) / delta + (g < b ? 6.0 : 0.0);
                else if (max == g) h = (b - r) / delta + 2.0;
                else h = (r - g) / delta + 4.0;
                h *= 60.0;
            }

            return string.Format("{0}°, {1}%, {2}%", Math.Round(h), Math.Round(s * 100), Math.Round(l * 100));
        }

        #endregion

        #region Actions

        private void CopyHex_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Clipboard.SetText(ToHex(_currentColor));
                ShowFeedback("Copiado " + ToHex(_currentColor) + " al portapapeles", FeedbackKind.Success);
            }
            catch
            {
                ShowFeedback("No se pudo copiar al portapapeles", FeedbackKind.Error);
            }
        }

        /// <summary>
        /// Pega el color del portapapeles. Acepta 3 o 6 digitos hexadecimales,
        /// con o sin almohadilla. Si falta la almohadilla se anade al campo.
        /// </summary>
        private void PasteHex_Click(object sender, RoutedEventArgs e)
        {
            string text;
            try
            {
                text = Clipboard.GetText();
            }
            catch
            {
                ShowFeedback("No se pudo leer el portapapeles", FeedbackKind.Error);
                return;
            }

            if (string.IsNullOrWhiteSpace(text))
            {
                ShowFeedback("El portapapeles está vacío", FeedbackKind.Warning);
                return;
            }

            string original = text.Trim();
            string core = original.TrimStart('#');

            if ((core.Length != 3 && core.Length != 6) || !Regex.IsMatch(core, "^[0-9A-Fa-f]+$"))
            {
                ShowFeedback(
                    "'" + Truncate(original) + "' no es un color hexadecimal válido",
                    FeedbackKind.Error);
                return;
            }

            // Normaliza: siempre con almohadilla y en mayusculas.
            string nota = original.StartsWith("#") ? "" : "  ·  se añadió la #";
            HexTextBox.Text = "#" + core.ToUpperInvariant();

            // _currentColor ya trae la forma canonica (3 digitos se expanden a 6).
            ShowFeedback("Pegado " + ToHex(_currentColor) + nota, FeedbackKind.Success);
        }

        private static string Truncate(string s, int max = 18) =>
            s.Length <= max ? s : s.Substring(0, max) + "…";

        private void ColorDialog_Click(object sender, RoutedEventArgs e)
        {
            using (var dialog = new ColorDialog())
            {
                dialog.FullOpen = true;
                dialog.AnyColor = true;
                dialog.Color = System.Drawing.Color.FromArgb(
                    _currentColor.A, _currentColor.R, _currentColor.G, _currentColor.B);

                if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    var d = dialog.Color;
                    ApplyColor(Color.FromRgb(d.R, d.G, d.B), addToHistory: true);
                }
            }
        }

        private void Eyedropper_Click(object sender, RoutedEventArgs e)
        {
            EndPicker();

            try
            {
                _picker = new ColorPickerSession();
                _picker.Picked += OnPickerFinished;
                _picker.Start();
            }
            catch (Exception ex)
            {
                _picker = null;
                ShowFeedback("Eluentagotas falló: " + ex.Message, FeedbackKind.Error);
            }
        }

        private void OnPickerFinished(Color? color)
        {
            _picker = null;

            if (color.HasValue)
            {
                ApplyColor(color.Value, addToHistory: true);
                ShowFeedback("Color capturado: " + ToHex(_currentColor), FeedbackKind.Success);
            }
            else
            {
                ShowFeedback("Selección cancelada", FeedbackKind.Info);
            }
        }

        private void EndPicker()
        {
            if (_picker == null) return;

            var p = _picker;
            _picker = null;
            p.Picked -= OnPickerFinished;
            p.Dispose();
        }

        private void RandomColor_Click(object sender, RoutedEventArgs e)
        {
            var rnd = new Random();
            ApplyColor(
                Color.FromRgb((byte)rnd.Next(256), (byte)rnd.Next(256), (byte)rnd.Next(256)),
                addToHistory: true);
        }

        private void AddFavorite_Click(object sender, RoutedEventArgs e)
        {
            string hex = ToHex(_currentColor);
            if (_favorites.Any(f => f.Hex == hex))
            {
                ShowFeedback("Este color ya está en favoritos", FeedbackKind.Warning);
                return;
            }

            // El limite es intentional: una paleta sin fin deja de ser util.
            if (_favorites.Count >= MaxFavorites)
            {
                ShowFeedback(
                    "Favoritos lleno (" + MaxFavorites + "/" + MaxFavorites +
                    "). Quita alguno con clic derecho para agregar otro.",
                    FeedbackKind.Warning);
                return;
            }

            _favorites.Add(new ColorItem { Color = _currentColor, Hex = hex });
            UpdateCounts();
            SaveState();
            ShowFeedback(
                "Agregado a favoritos (" + _favorites.Count + "/" + MaxFavorites + ")",
                FeedbackKind.Success);
        }

        /// <summary>Actualiza los contadores de favoritos e historial.</summary>
        private void UpdateCounts()
        {
            if (!_ready) return;

            if (FavCount != null)
            {
                FavCount.Text = _favorites.Count + "/" + MaxFavorites;
                bool full = _favorites.Count >= MaxFavorites;
                FavCount.Foreground = full
                    ? new SolidColorBrush(Color.FromRgb(224, 178, 95))
                    : (Brush)FindResource("TextMutedBrush");
            }

            if (HistCount != null)
                HistCount.Text = _history.Count + "/" + MaxHistory;
        }

        #endregion

        #region Collections

        private void PushHistory(Color color)
        {
            string hex = ToHex(color);
            var existing = _history.FirstOrDefault(h => h.Hex == hex);
            if (existing != null) _history.Remove(existing);

            _history.Insert(0, new ColorItem { Color = color, Hex = hex });
            while (_history.Count > MaxHistory)
                _history.RemoveAt(_history.Count - 1);

            UpdateCounts();
        }

        private void FavoriteColor_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed) return;
            var item = (sender as FrameworkElement)?.DataContext as ColorItem;
            if (item != null) ApplyColor(item.Color, addToHistory: true);
        }

        private void FavoriteColor_Remove(object sender, MouseButtonEventArgs e)
        {
            var item = (sender as FrameworkElement)?.DataContext as ColorItem;
            if (item == null) return;

            _favorites.Remove(item);
            UpdateCounts();
            SaveState();
            ShowFeedback("Quitado de favoritos", FeedbackKind.Success);
        }

        private void HistoryColor_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed) return;
            var item = (sender as FrameworkElement)?.DataContext as ColorItem;
            if (item != null) ApplyColor(item.Color, addToHistory: true);
        }

        #endregion

        #region Persistence

        private void LoadState()
        {
            ColorStore.Load();

            foreach (var hex in ColorStore.Favorites)
            {
                if (TryParseHex(hex, out var color))
                    _favorites.Add(new ColorItem { Color = color, Hex = ToHex(color) });
            }

            foreach (var hex in ColorStore.History)
            {
                if (TryParseHex(hex, out var color))
                    _history.Add(new ColorItem { Color = color, Hex = ToHex(color) });
            }

            if (TryParseHex(ColorStore.LastColor, out var last))
                ApplyColor(last, addToHistory: false);

            UpdateCounts();
        }

        private void SaveState()
        {
            ColorStore.Save(
                ToHex(_currentColor),
                _favorites.Select(f => f.Hex),
                _history.Select(h => h.Hex));
        }

        #endregion

        #region Helpers

        private void ShowFeedback(string message, FeedbackKind kind = FeedbackKind.Info)
        {
            // Durante InitializeComponent, TextChanged puede dispararse antes de que
            // StatusText exista en el arbol visual. En ese caso se difiere el mensaje.
            if (StatusText == null)
            {
                _pendingFeedback = message;
                _pendingKind = kind;
                return;
            }

            if (string.IsNullOrEmpty(message))
            {
                ResetStatus();
                return;
            }

            StatusText.Text = message;
            StatusText.Foreground = BrushFor(kind);
            StatusDot.Fill = BrushFor(kind);

            // El mensaje se oculta solo y la barra vuelve a "Listo".
            _toastTimer.Stop();
            _toastTimer.Tick -= OnStatusTimeout;
            _toastTimer.Tick += OnStatusTimeout;
            _toastTimer.Start();
        }

        private void ResetStatus()
        {
            _toastTimer.Stop();
            _toastTimer.Tick -= OnStatusTimeout;

            if (StatusText != null)
            {
                StatusText.Text = DefaultStatus;
                StatusText.Foreground = BrushFor(FeedbackKind.Info);
                StatusDot.Fill = BrushFor(FeedbackKind.Info);
            }
        }

        private void OnStatusTimeout(object sender, EventArgs e)
        {
            _toastTimer.Stop();
            _toastTimer.Tick -= OnStatusTimeout;
            ResetStatus();
        }

        private static Brush BrushFor(FeedbackKind kind)
        {
            switch (kind)
            {
                case FeedbackKind.Success: return SuccessBrush;
                case FeedbackKind.Error: return ErrorBrush;
                case FeedbackKind.Warning: return WarningBrush;
                default: return InfoBrush;
            }
        }

        private static bool TryParseHex(string hex, out Color color)
        {
            color = Colors.Black;
            if (string.IsNullOrWhiteSpace(hex)) return false;

            hex = hex.Trim();
            if (!hex.StartsWith("#")) hex = "#" + hex;
            if (hex.Length == 4)
                hex = "#" + hex[1] + hex[1] + hex[2] + hex[2] + hex[3] + hex[3];

            try
            {
                color = (Color)ColorConverter.ConvertFromString(hex);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static string ToHex(Color c) =>
            string.Format("#{0:X2}{1:X2}{2:X2}", c.R, c.G, c.B);

        private static bool IsHex(string s) => Regex.IsMatch(s, "^[0-9A-Fa-f]{6}$");

        #endregion

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            EndPicker();
            SaveState();
            base.OnClosing(e);
        }
    }

    public class ColorItem
    {
        public Color Color { get; set; }
        public string Hex { get; set; }
    }

    /// <summary>Tipo de aviso, determina el color del punto y del texto en la barra de estado.</summary>
    public enum FeedbackKind
    {
        Info,
        Success,
        Warning,
        Error
    }
}
