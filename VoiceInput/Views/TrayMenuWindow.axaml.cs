using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace VoiceInput.Views;

public partial class TrayMenuWindow : Window
{
    public TrayMenuWindow()
    {
        InitializeComponent();

        Deactivated += async (s, e) => await HideWithAnimation();
    }

    public async void ShowWithAnimation(int x, int y)
    {
        var border = this.FindControl<Border>("MainBorder");
        if (border == null) return;

        border.Opacity = 0;
        border.Margin = new Thickness(0, 10, 0, 0);

        Position = new PixelPoint(x, y);
        Show();
        Activate();

        await Task.Delay(10);

        border.Opacity = 1;
        border.Margin = new Thickness(0, 0, 0, 0);
    }

    private async Task HideWithAnimation()
    {
        var border = this.FindControl<Border>("MainBorder");
        if (border != null)
        {
            border.Opacity = 0;
            border.Margin = new Thickness(0, 10, 0, 0);

            await Task.Delay(150);
        }

        Hide();
    }

    private async void Exit_Click(object? sender, RoutedEventArgs e)
    {
        await HideWithAnimation();
        (Application.Current as App)?.ExitApplication(null, EventArgs.Empty);
    }
}