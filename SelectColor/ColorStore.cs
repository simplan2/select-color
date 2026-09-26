using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Media;

namespace SelectColor
{
    /// <summary>
    /// Persistencia simple en un archivo de texto plano, sin dependencias de
    /// ApplicationSettingsBase ni de App.config. Formato:
    ///   last=FFFFFF
    ///   fav=FFFFFF,00B4D8
    ///   hist=112233,445566
    /// </summary>
    internal static class ColorStore
    {
        private static readonly string FilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SelectColor",
            "palette.txt");

        public static string LastColor { get; private set; } = "FFFFFF";
        public static List<string> Favorites { get; private set; } = new List<string>();
        public static List<string> History { get; private set; } = new List<string>();

        public static void Load()
        {
            LastColor = "FFFFFF";
            Favorites = new List<string>();
            History = new List<string>();

            try
            {
                if (!File.Exists(FilePath)) return;

                foreach (var line in File.ReadAllLines(FilePath, Encoding.UTF8))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    int sep = line.IndexOf('=');
                    if (sep <= 0) continue;

                    string key = line.Substring(0, sep).Trim();
                    string val = line.Substring(sep + 1).Trim();

                    switch (key.ToLowerInvariant())
                    {
                        case "last":
                            if (IsHex(val)) LastColor = val.ToUpperInvariant();
                            break;
                        case "fav":
                            Favorites = SplitHex(val);
                            break;
                        case "hist":
                            History = SplitHex(val);
                            break;
                    }
                }
            }
            catch
            {
                // Si el archivo esta corrupto se usan los valores por defecto.
            }
        }

        public static void Save(string last, IEnumerable<string> favorites, IEnumerable<string> history)
        {
            try
            {
                string dir = Path.GetDirectoryName(FilePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                var sb = new StringBuilder();
                sb.Append("last=").Append(Normalize(last)).AppendLine();

                sb.Append("fav=")
                  .Append(string.Join(",", favorites.Select(Normalize).Where(x => x.Length > 0)))
                  .AppendLine();

                sb.Append("hist=")
                  .Append(string.Join(",", history.Select(Normalize).Where(x => x.Length > 0)))
                  .AppendLine();

                File.WriteAllText(FilePath, sb.ToString(), Encoding.UTF8);
            }
            catch
            {
                // La persistencia nunca debe impedir usar la aplicacion.
            }
        }

        private static List<string> SplitHex(string csv)
        {
            if (string.IsNullOrWhiteSpace(csv)) return new List<string>();

            return csv.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                      .Select(s => s.Trim().TrimStart('#').ToUpperInvariant())
                      .Where(IsHex)
                      .Distinct()
                      .ToList();
        }

        private static string Normalize(string hex)
        {
            if (string.IsNullOrWhiteSpace(hex)) return string.Empty;
            return hex.Trim().TrimStart('#').ToUpperInvariant();
        }

        /// <summary>Normaliza cualquier formato aceptado (#RGB, #RRGGBB, RRGGBB) a RRGGBB.</summary>
        public static string ToHex6(Color c) =>
            string.Format("{0:X2}{1:X2}{2:X2}", c.R, c.G, c.B);

        private static bool IsHex(string s)
        {
            if (string.IsNullOrEmpty(s) || s.Length != 6) return false;
            foreach (char ch in s)
            {
                bool hex = (ch >= '0' && ch <= '9') || (ch >= 'a' && ch <= 'f') || (ch >= 'A' && ch <= 'F');
                if (!hex) return false;
            }
            return true;
        }
    }
}
