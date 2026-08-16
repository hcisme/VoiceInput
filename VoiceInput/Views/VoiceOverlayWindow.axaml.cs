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
    private readonly Border _mainBorder;
    private readonly TextBlock _recognizedTextBlock;
    private readonly Path _micIcon;

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

    public void ShowWithAnimation()
    {
        _mainBorder.Opacity = 0;
        _mainBorder.Margin = new Thickness(0, 20, 0, 0);

        Focusable = false;
        ShowActivated = false;
        Topmost = false;
        Topmost = true;
        Show();
        UpdatePosition();
        Dispatcher.UIThread.Post(UpdatePosition, DispatcherPriority.Render);

        _mainBorder.Opacity = 1;
        _mainBorder.Margin = new Thickness(0);
    }

    public async Task HideWithAnimation()
    {
        _animationToken += 1;
        var currentToken = _animationToken;

        _mainBorder.Opacity = 0;
        _mainBorder.Margin = new Thickness(0, 20, 0, 0);

        await Task.Delay(100);
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

            // 首次显示时可能还没有完成布局，使用接近实际内容的默认尺寸，
            // 避免 Avalonia/Wayland 把窗口临时放到屏幕中间。
            if (windowWidth <= 0) windowWidth = 50;
            if (windowHeight <= 0) windowHeight = 70;

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
