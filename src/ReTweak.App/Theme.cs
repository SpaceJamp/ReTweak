using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using ReTweak.Core;
using Windows.UI;

namespace ReTweak.App;

/// <summary>Status colours. WinUI brushes are thread-bound, so only touch these on the UI thread.</summary>
internal static class Theme
{
    public static SolidColorBrush Ok { get; } = Make(0x2E, 0xCC, 0x71);
    public static SolidColorBrush Warn { get; } = Make(0xF5, 0xA6, 0x23);
    public static SolidColorBrush Critical { get; } = Make(0xE2, 0x26, 0x3C);
    public static SolidColorBrush Info { get; } = Make(0x4C, 0x9A, 0xFF);
    public static SolidColorBrush Muted { get; } = Make(0x8A, 0x91, 0xA0);

    public static SolidColorBrush For(AlertSeverity s) => s switch
    {
        AlertSeverity.Critical => Critical,
        AlertSeverity.Warning => Warn,
        AlertSeverity.Info => Info,
        _ => Ok,
    };

    public static InfoBarSeverity InfoBar(AlertSeverity s) => s switch
    {
        AlertSeverity.Critical => InfoBarSeverity.Error,
        AlertSeverity.Warning => InfoBarSeverity.Warning,
        _ => InfoBarSeverity.Informational,
    };

    private static SolidColorBrush Make(byte r, byte g, byte b) => new(Color.FromArgb(0xFF, r, g, b));
}
