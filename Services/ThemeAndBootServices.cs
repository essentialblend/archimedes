using System;
using System.Windows;

namespace archimedes
{
    public interface IBootTidalAction
    {
        void Boot();
    }

    public sealed class ResourceDictionaryThemeToggler : IThemeToggler
    {
        public void ToggleTheme()
        {
            var app = Application.Current;
            if (app is null) return;

            var merged = app.Resources.MergedDictionaries;

            int idx = -1;
            string current = "";

            for (int i = 0; i < merged.Count; i++)
            {
                var src = merged[i].Source?.OriginalString ?? "";
                src = src.Replace('\\', '/');

                if (src.EndsWith("Themes/Light.xaml", StringComparison.OrdinalIgnoreCase) ||
                    src.EndsWith("Themes/Dark.xaml", StringComparison.OrdinalIgnoreCase))
                {
                    idx = i;
                    current = src;
                    break;
                }
            }

            if (idx < 0) return;

            var next = current.EndsWith("Themes/Light.xaml", StringComparison.OrdinalIgnoreCase)
                ? "Themes/Dark.xaml"
                : "Themes/Light.xaml";

            merged[idx] = new ResourceDictionary
            {
                Source = new Uri(next, UriKind.Relative)
            };
        }
    }

    public sealed class PlaceholderBootTidalAction : IBootTidalAction
    {
        public void Boot()
        {
            MessageBox.Show("Boot Tidal (placeholder).", "archimedes");
        }
    }
}
