using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;
using Windows.UI;

namespace PhotoOrganizer;

public sealed class MainWindow : Window
{
    private static readonly Color TitleBarBackground = Color.FromArgb(255, 11, 11, 11);
    private static readonly Color DimmedForeground = Color.FromArgb(255, 160, 160, 160);

    public MainWindow()
    {
        Title = "FirstBatch";
        AppWindow.SetIcon("Assets/AppIcon.ico");
        AppWindow.Resize(new SizeInt32(1180, 800));

        if (AppWindow.TitleBar is not null)
        {
            // The caption buttons sit on a system-drawn title bar with no surface behind them to
            // blend with, so every background must be opaque. A transparent colour loses its alpha
            // and paints the button strip solid white.
            AppWindow.TitleBar.BackgroundColor = TitleBarBackground;
            AppWindow.TitleBar.ForegroundColor = Colors.White;
            AppWindow.TitleBar.ButtonBackgroundColor = TitleBarBackground;
            AppWindow.TitleBar.ButtonForegroundColor = Colors.White;
            AppWindow.TitleBar.ButtonHoverBackgroundColor = Color.FromArgb(255, 34, 34, 34);
            AppWindow.TitleBar.ButtonHoverForegroundColor = Colors.White;
            AppWindow.TitleBar.ButtonPressedBackgroundColor = Color.FromArgb(255, 55, 55, 55);
            AppWindow.TitleBar.ButtonPressedForegroundColor = Colors.White;
            AppWindow.TitleBar.InactiveBackgroundColor = TitleBarBackground;
            AppWindow.TitleBar.InactiveForegroundColor = DimmedForeground;
            AppWindow.TitleBar.ButtonInactiveBackgroundColor = TitleBarBackground;
            AppWindow.TitleBar.ButtonInactiveForegroundColor = DimmedForeground;
        }

        var rootFrame = new Frame
        {
            Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(TitleBarBackground),
        };
        Content = rootFrame;
        rootFrame.Navigate(typeof(MainPage));
    }
}
