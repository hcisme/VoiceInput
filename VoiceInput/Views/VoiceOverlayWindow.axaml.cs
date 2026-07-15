using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Avalonia.Threading;

namespace VoiceInput.Views;

public partial class VoiceOverlayWindow : Window
{
    private Border _mainBorder = null!;
    private TextBlock _recognizedTextBlock = null!;
    private Path _micIcon = null!;

    private int _animationToken;

    public VoiceOverlayWindow()
    {
        InitializeComponent();

        _mainBorder = this.FindControl<Border>("MainBorder")
                      ?? throw new InvalidOperationException("找不到控件: MainBorder");
        _recognizedTextBlock = this.FindControl<TextBlock>("RecognizedTextBlock")
                               ?? throw new InvalidOperationException("找不到控件: RecognizedTextBlock");
        _micIcon = this.FindControl<Path>("MicIcon")
                   ?? throw new InvalidOperationException("找不到控件: MicIcon");

        SizeChanged += (s, e) => UpdatePosition();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        UpdatePosition();
    }

    public async Task ShowWithAnimation()
    {
        _animationToken += 1;
        var currentToken = _animationToken;

        _mainBorder.Opacity = 0;
        _mainBorder.Margin = new Thickness(0, 20, 0, 0);

        Topmost = false;
        Topmost = true;
        WindowState = WindowState.Normal;
        Show();
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
        if (_animationToken != currentToken) return;

        _mainBorder.Opacity = 1;
        _mainBorder.Margin = new Thickness(0);
    }

    public async Task HideWithAnimation()
    {
        _animationToken += 1;
        var currentToken = _animationToken;

        _mainBorder.Opacity = 0;
        _mainBorder.Margin = new Thickness(0, 20, 0, 0);

        await Task.Delay(150);
        if (_animationToken == currentToken)
        {
            Hide();
        }
    }

    public void UpdateText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            _recognizedTextBlock.IsVisible = false;
            _recognizedTextBlock.Text = "";
            _mainBorder.Width = 50;
            _mainBorder.Padding = new Thickness(0);
        }
        else
        {
            _recognizedTextBlock.IsVisible = true;
            _recognizedTextBlock.Text = text;
            _mainBorder.ClearValue(WidthProperty);
            _mainBorder.Padding = new Thickness(15, 0, 20, 0);
        }
    }

    public void UpdateVolume(float volume)
    {
        if (_micIcon.RenderTransform is ScaleTransform scale)
        {
            var targetScale = Math.Clamp(1.0 + (volume * 0.4), 1.0, 1.4);
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