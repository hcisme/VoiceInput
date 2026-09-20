using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace VoiceInput.Views;

public partial class VoiceOverlayWindow : Window
{
    private readonly Border _mainBorder;
    private readonly TextBlock _recognizedTextBlock;

    private int _animationToken;

    public VoiceOverlayWindow()
    {
        InitializeComponent();

        _mainBorder = this.FindControl<Border>("MainBorder")
                      ?? throw new InvalidOperationException("找不到控件: MainBorder");
        _recognizedTextBlock = this.FindControl<TextBlock>("RecognizedTextBlock")
                               ?? throw new InvalidOperationException("找不到控件: RecognizedTextBlock");

        SizeChanged += (s, e) => UpdatePosition();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        UpdatePosition();
    }

    public void ShowWithAnimation()
    {
        // 与 HideWithAnimation 对称：每次显示也递增 token，这样如果上一次
        // 隐藏动画的 100ms 延迟还没结束、新的显示已经发生，延迟回调会看到
        // token 已变化，从而不会把刚显示出来的窗口误 Hide 掉。
        _animationToken += 1;

        _mainBorder.Opacity = 0;
        _mainBorder.Margin = new Thickness(0, 20, 0, 0);

        Focusable = false;
        ShowActivated = false;
        Topmost = false;
        Topmost = true;

        UpdatePosition();

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

    private void UpdatePosition()
    {
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen != null)
        {
            // WorkingArea 已经自动排除了任务栏的高度
            var workArea = screen.WorkingArea;

            // 获取窗口当前的实际宽高。Bounds 是 DIP（设备无关像素），
            // 而 workArea / PixelPoint 是物理像素，需要按当前屏幕缩放系数换算，
            // 否则在 125%/150%/200% 等缩放下窗口位置会偏移。
            // 首次显示时窗口还没有完成布局，Bounds 宽高为 0。此时窗口处于
            // “空文字”状态：MainBorder 50×50 + 外层 Border 上下左右各 20 内边距，
            // 实际内容尺寸为 90×90（DIP），用它提前定位。
            var widthDips = Bounds.Width > 0 ? Bounds.Width : 90;
            var heightDips = Bounds.Height > 0 ? Bounds.Height : 90;
            var windowWidth = (int)Math.Round(widthDips * screen.Scaling);
            var windowHeight = (int)Math.Round(heightDips * screen.Scaling);

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
