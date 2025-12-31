using System;
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
            var merged = Application.Current?.Resources?.MergedDictionaries;
            if (merged == null) return;

            int idx = -1;
            string current = "";

            for (int i = 0; i < merged.Count; i++)
            {
                var src = merged[i].Source?.OriginalString?.Replace('\\', '/') ?? "";
                if (src.Contains("Themes/", StringComparison.OrdinalIgnoreCase) &&
                    (src.EndsWith("Light.xaml", StringComparison.OrdinalIgnoreCase) ||
                     src.EndsWith("Dark.xaml", StringComparison.OrdinalIgnoreCase)))
                {
                    idx = i;
                    current = src;
                    break;
                }
            }

            if (idx < 0) return;

            bool currentlyDark = current.EndsWith("Dark.xaml", StringComparison.OrdinalIgnoreCase);
            var next = new Uri(currentlyDark ? "Themes/Light.xaml" : "Themes/Dark.xaml", UriKind.Relative);

            merged[idx].Source = next;
        }

        private void OnBootTidal(object sender, RoutedEventArgs e)
        {
        }
    }
}
