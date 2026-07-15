using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Serilog;

namespace VoiceInput.Views;

public partial class TrayMenuWindow : Window
{
    private Border _mainBorder = null!;
    private int _animationToken;

    public TrayMenuWindow()
    {
        InitializeComponent();

        _mainBorder = this.FindControl<Border>("MainBorder")
                      ?? throw new InvalidOperationException("找不到控件: MainBorder");

        Deactivated += (s, e) => { _ = HideWithAnimation(); };
    }

    public async void ShowWithAnimation(int x, int y)
    {
        _animationToken += 1;
        var currentToken = _animationToken;

        _mainBorder.Opacity = 0;
        _mainBorder.Margin = new Thickness(0, 10, 0, 0);

        Position = new PixelPoint(x, y);
        Topmost = false;
        Topmost = true;
        Show();
        Activate();

        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
        if (_animationToken != currentToken) return;

        _mainBorder.Opacity = 1;
        _mainBorder.Margin = new Thickness(0);
    }

    private async Task HideWithAnimation()
    {
        _animationToken += 1;
        var currentToken = _animationToken;

        _mainBorder.Opacity = 0;
        _mainBorder.Margin = new Thickness(0, 10, 0, 0);

        await Task.Delay(150);

        if (_animationToken == currentToken) Hide();
    }

    private async void Exit_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            await HideWithAnimation();
            (Application.Current as App)?.ExitApplication(null, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "退出时发生异常");
        }
    }
}