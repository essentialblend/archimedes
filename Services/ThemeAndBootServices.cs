using System;
using System.Collections.Generic;
using System.Text;

using System;
using System.Linq;
using System.Windows;

namespace archimedes
{
    // Contract: anything that can “boot” Tidal (placeholder today).
    public interface IBootTidalAction
    {
        void Boot();
    }

    // Implementation: toggles between Themes/Light.xaml and Themes/Dark.xaml
    public sealed class ResourceDictionaryThemeToggler : IThemeToggler
    {
        public void ToggleTheme()
        {
            // Grab the merged dictionaries from App.xaml (you merge one theme dictionary there).
            var merged = Application.Current.Resources.MergedDictionaries;

            // Find the first merged dictionary whose Source contains "Themes/".
            var themeDict = merged.FirstOrDefault(d =>
                d.Source?.OriginalString.Replace('\\', '/').Contains("Themes/") == true);

            // If we can’t find it, do nothing (safe no-op).
            if (themeDict?.Source == null) return;

            // Normalize path separators for reliable string checks.
            var current = themeDict.Source.OriginalString.Replace('\\', '/');

            // Switch to the other theme based on the current theme file name.
            var next = current.EndsWith("Themes/Light.xaml", StringComparison.OrdinalIgnoreCase)
                ? "Themes/Dark.xaml"
                : "Themes/Light.xaml";

            // Replace the Source; WPF will re-resolve DynamicResource bindings.
            themeDict.Source = new Uri(next, UriKind.Relative);
        }
    }

    // Implementation: placeholder boot action (no external process assumptions yet).
    public sealed class PlaceholderBootTidalAction : IBootTidalAction
    {
        public void Boot()
        {
            MessageBox.Show("Boot Tidal (placeholder).", "archimedes");
        }
    }
}
