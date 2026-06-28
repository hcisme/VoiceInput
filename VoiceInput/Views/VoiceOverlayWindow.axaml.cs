using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;

namespace VoiceInput.Views;

public partial class VoiceOverlayWindow : Window
{
    private int _animationToken;

    public VoiceOverlayWindow()
    {
        InitializeComponent();

        LayoutUpdated += (s, e) => UpdatePosition();
    }

    public async void ShowWithAnimation()
    {
        var border = this.FindControl<Border>("MainBorder");
        if (border == null) return;

        _animationToken += 1;

        border.Opacity = 0;
        border.Margin = new Thickness(0, 20, 0, 0);

        Show();

        await Task.Delay(10);

        border.Opacity = 1;
        border.Margin = new Thickness(0, 0, 0, 0);
    }

    public async Task HideWithAnimation()
    {
        var border = this.FindControl<Border>("MainBorder");
        if (border == null) return;

        var currentToken = ++_animationToken;

        border.Opacity = 0;
        border.Margin = new Thickness(0, 20, 0, 0);

        await Task.Delay(150);
        if (_animationToken == currentToken)
        {
            Hide();
        }
    }

    public void UpdateText(string text)
    {
        var textBlock = this.FindControl<TextBlock>("RecognizedTextBlock")!;
        var border = this.FindControl<Border>("MainBorder")!;

        if (string.IsNullOrWhiteSpace(text))
        {
            textBlock.IsVisible = false;
            textBlock.Text = "";
            border.Width = 50;
            border.Padding = new Thickness(0);
        }
        else
        {
            textBlock.IsVisible = true;
            textBlock.Text = text;
            border.ClearValue(WidthProperty);
            border.Padding = new Thickness(15, 0, 20, 0);
        }
    }

    public void UpdateVolume(float volume)
    {
        var micIcon = this.FindControl<Path>("MicIcon");
        if (micIcon?.RenderTransform is ScaleTransform scale)
        {
            var targetScale = 1.0 + (volume * 0.4);

            targetScale = Math.Clamp(targetScale, 1.0, 1.4);

            scale.ScaleX = targetScale;
            scale.ScaleY = targetScale;
        }
    }

    private void UpdatePosition()
    {
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen != null)
        {
            // WorkingArea 已经自动排除了任务栏的高度
            var workArea = screen.WorkingArea;

            // 获取窗口当前的实际宽高
            var windowWidth = (int)Bounds.Width;
            var windowHeight = (int)Bounds.Height;

            // 如果窗口还没渲染出来，宽高是 0，跳过计算
            if (windowWidth == 0 || windowHeight == 0) return;

            // X 轴：屏幕宽度的一半 减去 窗口宽度的一半
            var x = workArea.X + (workArea.Width - windowWidth) / 2;

            // Y 轴：工作区底部 减去 窗口高度，再往上抬 16 个像素
            var y = workArea.Bottom - windowHeight - 16;

            // 如果计算出的位置和当前位置不同，就移动它
            var newPosition = new PixelPoint(x, y);
            if (Position != newPosition)
            {
                Position = newPosition;
            }
        }
    }
}