using System;
using System.Linq;
using System.Windows;

namespace archimedes
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
        }

        private void OnToggleTheme(object sender, RoutedEventArgs e)
        {
            var app = Application.Current;
            if (app == null) return;

            var merged = app.Resources.MergedDictionaries;

            int idx = -1;
            bool currentlyDark = false;

            for (int i = 0; i < merged.Count; i++)
            {
                var src = merged[i].Source?.ToString() ?? "";
                if (src.Contains("/themes/", StringComparison.OrdinalIgnoreCase) &&
                    (src.EndsWith("dark.xaml", StringComparison.OrdinalIgnoreCase) ||
                     src.EndsWith("light.xaml", StringComparison.OrdinalIgnoreCase)))
                {
                    idx = i;
                    currentlyDark = src.EndsWith("dark.xaml", StringComparison.OrdinalIgnoreCase);
                    break;
                }
            }

            var next = new ResourceDictionary
            {
                Source = new Uri(
                    currentlyDark
                        ? "pack://application:,,,/themes/Light.xaml"
                        : "pack://application:,,,/themes/Dark.xaml",
                    UriKind.Absolute)
            };

            if (idx >= 0) merged[idx] = next;
            else merged.Add(next);
        }

        private void OnBootTidal(object sender, RoutedEventArgs e)
        {
        }
    }
}
