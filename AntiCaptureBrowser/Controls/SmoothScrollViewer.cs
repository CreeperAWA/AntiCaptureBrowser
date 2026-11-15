using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace AntiCaptureBrowser.Controls;

/// <summary>
/// 平滑滚动 + 浮层滚动条的 ScrollViewer：
/// - 鼠标滚轮通过逐帧指数平滑逼近目标位置（帧率无关、无动画重启抖动）；
/// - 垂直滚动条默认隐藏，滚动时淡入，停止滚动约 0.8 秒后自动淡出（仿 Chrome）；
/// - 拖动滚动条、键盘翻页保持即时定位。
/// </summary>
public class SmoothScrollViewer : ScrollViewer
{
    /// <summary>平滑系数：值越大收敛越快。</summary>
    private const double SmoothingRate = 14.0;

    /// <summary>停止滚动后滚动条延迟淡出的时间。</summary>
    private static readonly TimeSpan FadeOutDelay = TimeSpan.FromMilliseconds(800);

    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private readonly DispatcherTimer _fadeOutTimer;
    private double _targetOffset;
    private bool _hooked;
    private ScrollBar? _verticalBar;

    public SmoothScrollViewer()
    {
        _fadeOutTimer = new DispatcherTimer { Interval = FadeOutDelay };
        _fadeOutTimer.Tick += (_, _) =>
        {
            _fadeOutTimer.Stop();
            if (_verticalBar is not { IsMouseOver: true })
            {
                FadeOutBar();
            }
        };
        Unloaded += (_, _) => DetachRenderingHook();
        Loaded += (_, _) => EnsureVerticalBar();
    }

    // ==================== 滚轮平滑滚动 ====================

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        if (ScrollableHeight <= 0)
        {
            base.OnMouseWheel(e);
            return;
        }

        e.Handled = true;
        if (!_hooked)
        {
            _targetOffset = VerticalOffset;
            _stopwatch.Restart(); // 关键：否则首帧 dt 是自启动以来的累计时间，会直接跳过平滑
            CompositionTarget.Rendering += OnRenderFrame;
            _hooked = true;
        }
        _targetOffset = Math.Clamp(_targetOffset - e.Delta, 0, ScrollableHeight);
        ShowBar();
        RestartFadeOutTimer();
    }

    private void OnRenderFrame(object? sender, EventArgs e)
    {
        // 限制单帧 dt 上限，防止任何异常耗时（如窗口切换回来）导致瞬移
        double dt = Math.Clamp(_stopwatch.Elapsed.TotalSeconds, 0.001, 0.05);
        _stopwatch.Restart();

        _targetOffset = Math.Clamp(_targetOffset, 0, Math.Max(0, ScrollableHeight));
        double diff = _targetOffset - VerticalOffset;

        if (Math.Abs(diff) < 0.5)
        {
            ScrollToVerticalOffset(_targetOffset);
            DetachRenderingHook();
            return;
        }

        // 帧率无关的指数平滑：平滑系数换算到当前帧时长
        double smoothing = 1 - Math.Exp(-dt * SmoothingRate);
        ScrollToVerticalOffset(VerticalOffset + diff * smoothing);
        // 让拇指跟随（若 ScrollViewer 内部未接线则由这里同步）
        if (_verticalBar is not null && !_verticalBar.IsMouseCaptureWithin)
        {
            _verticalBar.Value = VerticalOffset;
        }
    }

    /// <summary>非滚轮引起的滚动（拖动滚动条、键盘等）时同步目标位置，避免互相打架。</summary>
    protected override void OnScrollChanged(ScrollChangedEventArgs e)
    {
        base.OnScrollChanged(e);
        if (ScrollableHeight <= 0)
        {
            _fadeOutTimer.Stop();
            if (_verticalBar is not null) _verticalBar.Opacity = 0;
            return;
        }
        if (!_hooked)
        {
            _targetOffset = VerticalOffset;
        }
    }

    private void DetachRenderingHook()
    {
        if (_hooked)
        {
            CompositionTarget.Rendering -= OnRenderFrame;
            _hooked = false;
        }
    }

    // ==================== 浮层滚动条显示/隐藏 ====================

    private void EnsureVerticalBar()
    {
        if (_verticalBar is not null) return;
        _verticalBar = FindDescendantScrollBar(this);
        if (_verticalBar is null) return;

        // 初始隐藏（内容可滚动时）
        _verticalBar.Opacity = ScrollableHeight > 0 ? 0 : 1;
        _verticalBar.MouseEnter += (_, _) => { _fadeOutTimer.Stop(); ShowBar(); };
        _verticalBar.MouseLeave += (_, _) => RestartFadeOutTimer();
        // 兜底：无论 ScrollViewer 内部是否接线，都保证拖动拇指能滚动内容
        _verticalBar.ValueChanged += OnBarValueChanged;
    }

    private void OnBarValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_verticalBar is not { IsMouseCaptureWithin: true }) return; // 忽略程序同步引起的变化
        if (_hooked)
        {
            _targetOffset = e.NewValue; // 动画进行中用户拖动拇指 → 改写平滑目标
        }
        else
        {
            ScrollToVerticalOffset(e.NewValue); // 静止状态下拖动 → 直接跟随
        }
    }

    private void ShowBar()
    {
        if (_verticalBar is null || ScrollableHeight <= 0) return;
        _verticalBar.BeginAnimation(OpacityProperty,
            new DoubleAnimation(1, TimeSpan.FromMilliseconds(120)) { From = _verticalBar.Opacity });
    }

    private void FadeOutBar()
    {
        if (_verticalBar is null) return;
        _verticalBar.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, TimeSpan.FromMilliseconds(300)) { From = _verticalBar.Opacity });
    }

    private void RestartFadeOutTimer()
    {
        _fadeOutTimer.Stop();
        _fadeOutTimer.Start();
    }

    /// <summary>在可视树中查找 ScrollViewer 模板的垂直滚动条（PART_VerticalScrollBar）。</summary>
    private static ScrollBar? FindDescendantScrollBar(DependencyObject root)
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollBar { Orientation: Orientation.Vertical } bar && bar.Name == "PART_VerticalScrollBar")
            {
                return bar;
            }
            var found = FindDescendantScrollBar(child);
            if (found is not null) return found;
        }
        return null;
    }
}
