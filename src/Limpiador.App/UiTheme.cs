using System.Windows;
using System.Windows.Media;

namespace Limpiador.App;

/// <summary>Respect Windows High Contrast, including changes made while the app is open.</summary>
internal static class UiTheme
{
    private static bool _initialized;
    private static readonly Dictionary<string, string> Palette = new()
    {
        ["Ink"] = "#202B3B", ["Muted"] = "#526174", ["Accent"] = "#185ACB", ["Line"] = "#DCE3ED",
        ["Surface"] = "#FFFFFF", ["Canvas"] = "#F5F7FB", ["Sidebar"] = "#EDF2F9", ["Subtle"] = "#F2F5FA",
        ["AccentInk"] = "#FFFFFF", ["Success"] = "#176943", ["Caution"] = "#8B3D00", ["Danger"] = "#A32430", ["SoftLine"] = "#E8EDF4"
    };
    internal static void Initialize()
    {
        if (_initialized) return;
        _initialized = true;
        Apply();
        SystemParameters.StaticPropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(SystemParameters.HighContrast))
                Application.Current?.Dispatcher.Invoke(Apply);
        };
    }
    private static void Apply()
    {
        if (Application.Current is not { } app) return;
        foreach (var (key, hex) in Palette)
        {
            var color = !SystemParameters.HighContrast ? (Color)ColorConverter.ConvertFromString(hex) : key switch
            {
                "Surface" or "Canvas" or "Sidebar" or "Subtle" => SystemColors.WindowColor,
                "Accent" => SystemColors.HighlightColor,
                "AccentInk" => SystemColors.HighlightTextColor,
                _ => SystemColors.WindowTextColor
            };
            var brush = new SolidColorBrush(color); brush.Freeze(); app.Resources[key] = brush;
        }
    }
}
