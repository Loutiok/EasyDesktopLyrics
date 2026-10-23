using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.Platform;
using Avalonia.Threading;
using EasyDesktopLyrics.Interop;
using EasyDesktopLyrics.Infrastructure;
using EasyDesktopLyrics.Models;
using EasyDesktopLyrics.Rendering.Animation;
using EasyDesktopLyrics.Services;
using EasyDesktopLyrics.ViewModels;
using EasyDesktopLyrics.Views.Lyrics;

namespace EasyDesktopLyrics.Views;

public sealed partial class LyricsOverlayWindow : Window
{
    private readonly OverlayViewModel _vm;
    private readonly SettingsService _settingsService;
    private readonly BackgroundFxLayer _bgFx;
    private readonly LyricVisualModeRegistry _lyricVisualModes = LyricVisualModeRegistry.CreateDefault();
    private readonly DispatcherTimer _topmostTimer;
    private readonly DispatcherTimer _hideControlsTimer;
    private readonly DispatcherTimer _coverStageTimer;
    private readonly DispatcherTimer _coverTimeoutTimer;
    private readonly AnimationProgressDriver _lineAnimation = new(TimeSpan.FromMilliseconds(16));
    private readonly UiDebouncer _anchorDebouncer = new();
    private readonly UiDebouncer _initialAnchorDebouncer = new();

    private IntPtr _hwnd;
    private bool _locked;
    private bool _allowClose;
    private PixelPoint _anchor;
    private bool _suppressPositionUpdate;
    private bool _dragStarted;
    private bool _coverAnimating;
    private bool _coverLayoutExpanded;
    private double _coverAnimSize;
    private DateTimeOffset _coverAnimStart;
    private bool _coverTitleEnterPending;
    private bool _coverPosDirty;
    private int _coverMotionToken;
    private CoverAnimationPlan _coverPlan;
    private bool _firstSizeApplied;
    private int _lastGrowBottom;
    private bool _lastCoverEnabled;
    private double _lastCoverSizePct;
    private bool _lastCoverTitleEnabled;
    private IImage? _lastCoverImage;

    // 当前视觉模式提供稳定的歌词层；窗口只消费这些锚点，不再负责构建描边/辉光树。
    private ILyricVisualMode _lyricVisualMode = null!;
    private Grid _mainGrid => _lyricVisualMode.MainHost;
    private LyricText _inactiveLyric => _lyricVisualMode.InactiveLyric;
    private LyricText _mainLyric => _lyricVisualMode.ActiveLyric;
    private Grid _transGrid => _lyricVisualMode.TranslationHost;
    private TextBlock _transTb => _lyricVisualMode.TranslationText;
    private IReadOnlyList<LyricText> _mainGlowLyrics => _lyricVisualMode.ActiveGlowLyrics;
    private IReadOnlyList<LyricText> _inactiveGlowLyrics => _lyricVisualMode.InactiveGlowLyrics;

    // 行间切换动效状态
    private string _lineAnimKind = "";
    private bool _revealHidingInactive;
    private LyricText? _shineLayer;
    private string _lastMainText = "";
    private string _lastTransText = "";
    private Control? _exitMainOverlay;
    private Control? _exitTransOverlay;

    public event Action<double, double>? AnchorChanged;

    /// <summary>
    /// 锁定/解锁窗口：锁定时整个窗口鼠标穿透（WS_EX_TRANSPARENT）且禁止拖动，
    /// 解锁仅能通过托盘菜单或设置页（穿透后窗口本身无法接收点击）。
    /// </summary>
    public void SetLocked(bool locked)
    {
        _locked = locked;
        if (_hwnd == IntPtr.Zero)
            return;
        Win32.SetClickThrough(_hwnd, locked);
        Log.Info($"overlay locked={locked}");
    }

    /// <summary>允许真正关闭窗口（仅程序退出时由 Cleanup 调用，此后窗口不可再显示）。</summary>
    public void AllowClose() => _allowClose = true;

    public LyricsOverlayWindow(OverlayViewModel vm, SettingsService settingsService)
    {
        InitializeComponent();
        DataContext = _vm = vm;
        _settingsService = settingsService;

        // 背景动效层：插入到 FxRoot 最底层（歌词/封面之下），屏幕取色提供者 + 可见性联动
        _bgFx = new BackgroundFxLayer(_settingsService);
        FxRoot.Children.Insert(0, _bgFx);
        _bgFx.SetScreenRect(() => _hwnd == IntPtr.Zero
            ? (0, 0, 0, 0)
            : Win32.GetWindowScreenRect(_hwnd) ?? (0, 0, 0, 0));
        _bgFx.SetPlaying(_vm.IsPlaying);
        _bgFx.SetWindowVisible(false);

        BuildTextLayers();

        // 初始封面占位尺寸：未运行 UpdateCoverSize 前封面会按原图尺寸（如 512px）布局，
        // 把窗口撑到巨大瞬时尺寸 → 按锚点定位时顶部被推到屏幕外、再被系统钳到顶边（重启后位置偏移的根因）。
        CoverImage.Width = 0;
        CoverImage.Height = 0;
        // 初始阶段定位前先移出屏幕，避免默认位置 + 瞬时巨大尺寸的闪烁
        Position = new PixelPoint(-20000, -20000);

        _topmostTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _topmostTimer.Tick += (_, _) => Win32.AssertTopmost(_hwnd);
        _topmostTimer.Start();

        _hideControlsTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _hideControlsTimer.Tick += (_, _) =>
        {
            _hideControlsTimer.Stop();
            SetControlsVisible(false);
        };

        _coverStageTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _coverStageTimer.Tick += (_, _) => OnCoverStage();

        _coverTimeoutTimer = new DispatcherTimer { Interval = CoverAnimTimeout };
        _coverTimeoutTimer.Tick += (_, _) => OnCoverStage();

        PointerEntered += (_, _) => { _hideControlsTimer.Stop(); SetControlsVisible(true); };
        PointerExited += (_, _) => _hideControlsTimer.Start();

        // 窗口高度显式管理：Avalonia 的 SizeToContent 高度跟随在高分屏环境不可靠
        // （窗口高度不随内容增长 → 封面/胶囊被窗口边缘截断），宽度仍由 SizeToContent 自动。
        LayoutUpdated += (_, _) =>
        {
            SyncWindowHeight();
            // 歌名进入：布局完成后（Bounds 就绪）瞬移起始位置（临时禁过渡）→ 淡入
            if (_coverAnimating && _coverTitleEnterPending)
            {
                _coverTitleEnterPending = false;
                var saved = CoverTitleBox.Transitions;
                CoverTitleBox.Transitions = null;
                var (dx, dy) = CoverTitleSlideOffset();
                UpdateCoverTitlePosition(dx, dy);
                CoverTitleBox.Transitions = saved;
                CoverTitleBox.Opacity = 1;
                _coverPosDirty = true;
            }
            // 校正到最终位置：从起始位置沿方向轴平滑滑入（x 恒定，无斜向）
            if (_coverAnimating && _coverPosDirty)
            {
                _coverPosDirty = false;
                UpdateCoverTitlePosition();
            }
            // 扫光层跟随主行位置（行宽/布局变化后保持对齐）
            if (_lineAnimKind == "Shine" && _shineLayer != null)
                PositionShineLayer();
            // 背景动效：歌词行区域（频谱“行中央”定位基准）
            if (LyricsArea.Bounds.Height > 0)
            {
                var origin = LyricsArea.TranslatePoint(new Point(0, 0), _bgFx);
                if (origin.HasValue)
                    _bgFx.SetLyricsRect(new Rect(origin.Value, LyricsArea.Bounds.Size));
            }
        };

        _lastCoverEnabled = _vm.CoverEnabled;
        _lastCoverSizePct = _vm.CoverSizePct;
        _lastCoverTitleEnabled = _vm.CoverTitleEnabled;
        _lastCoverImage = _vm.CoverImage;
        LyricsArea.MinWidth = _vm.MaxTextWidth;

        ApplyCoverAnimationStyle();
        _settingsService.Changed += ApplyCoverAnimationStyle;
        _settingsService.Changed += OnLineTransitionSettingsChanged;

        // 视觉模式切换后主行控件会替换，因此统一观察稳定的 RootPanel 宿主。
        RootPanel.SizeChanged += (_, _) =>
        {
            UpdateCoverSize();
            UpdateCoverPosition();
        };
        _vm.PropertyChanged += OnVmChanged;
        _vm.LineChanged += OnLineChanged;
        SizeChanged += OnSizeChanged;
    }

    /// <summary>hover 显示播放控制条（内容下方展开）；动画期间不显示（避免窗口高度变化干扰）。</summary>
    private void SetControlsVisible(bool visible)
    {
        if (visible && _coverAnimating)
            return; // 切歌动画期间保持隐藏
        ControlBar.Height = visible ? 34 : 0;
        ControlBar.Opacity = visible ? 1 : 0;
        ControlBar.IsHitTestVisible = visible;
    }

    /// <summary>
    /// 窗口高度 = 内容布局期望高度（主行 + 翻译行 + spacing + 封面覆盖层 + 歌名区 + 控制条 + padding）。
    /// 各组件 Bounds 均与窗口高度无关（宽度由 SizeToContent 决定，缩放由此确定）→ 收敛无循环。
    /// </summary>
    private double ContentHeight()
    {
        var h = _mainGrid.Bounds.Height;
        if (_transGrid.Bounds.Height > 0)
            h += _transGrid.Bounds.Height + _vm.LineSpacing;
        h = Math.Max(h, Cover.Bounds.Height);
        if (_coverLayoutExpanded)
            h = Math.Max(h, _coverAnimSize);
        // 动画期间歌名显示在封面下方：窗口容纳封面目标高度（行高×3）+ 间距 + 歌名区
        if (_coverAnimating && CoverTitleBox.IsVisible && CoverTitleBox.Bounds.Height > 0)
            h = Math.Max(h, _coverAnimSize + 8 + CoverTitleBox.Bounds.Height);
        return h + ControlBar.Height + 12; // 12 = Border padding 6×2
    }

    /// <summary>显式同步窗口高度（差异 > 0.5 才设置，避免抖动/循环）。</summary>
    private void SyncWindowHeight()
    {
        if (!IsVisible || Height <= 0)
            return;
        var want = ContentHeight();
        if (want > 0 && Math.Abs(want - Height) > 0.5)
            Height = want;
    }

    private void UpdatePlayPauseIcon()
    {
        PlayPauseIcon.Text = _vm.IsPlaying ? "\uE769" : "\uE768"; // Pause / Play
    }

    private void BuildTextLayers()
    {
        try
        {
            _lyricVisualMode = _lyricVisualModes.Create(_vm.LyricVisualMode, _vm, RootPanel);
        }
        catch (Exception ex)
        {
            Log.Error($"lyric visual mode '{_vm.LyricVisualMode}', fallback to classic", ex);
            _lyricVisualMode = _lyricVisualModes.Create(LyricVisualModeIds.Classic, _vm, RootPanel);
        }
        ApplyAlignment();
    }

    private void SwitchLyricVisualMode()
    {
        if (string.Equals(_lyricVisualMode.Id, _vm.LyricVisualMode, StringComparison.OrdinalIgnoreCase))
            return;

        CancelLineAnimation();
        BuildTextLayers();
        SyncLastLineText();
        Dispatcher.UIThread.Post(() =>
        {
            UpdateCoverSize();
            UpdateCoverPosition();
            SyncWindowHeight();
        });
    }

    private void OnVmChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(OverlayViewModel.WindowVisible))
            UpdateVisibility();
        else if (e.PropertyName == nameof(OverlayViewModel.IsPlaying))
        {
            UpdatePlayPauseIcon();
            _bgFx.SetPlaying(_vm.IsPlaying);
        }
        else if (e.PropertyName == nameof(OverlayViewModel.Phase))
            OnLyricsPhaseChanged();
        else if (e.PropertyName == nameof(OverlayViewModel.CoverTrackKey))
            OnCoverChanged();
        else if (e.PropertyName == nameof(OverlayViewModel.CoverImage))
        {
            if (ReferenceEquals(_lastCoverImage, _vm.CoverImage))
                return;
            _lastCoverImage = _vm.CoverImage;
            // 封面内容刷新（同歌重取等），不重放切歌动画
            CoverImage.Source = _vm.CoverImage;
            _bgFx.SetCoverImage(_vm.CoverImage);
            if (!_coverAnimating)
            {
                UpdateCoverSize();
                ApplyCoverRestingState(immediate: true);
            }
        }
        else if (e.PropertyName == nameof(OverlayViewModel.MaxTextWidth))
        {
            // 歌词区域宽度上限变化：常驻时同步（动画期间由动画状态管理）
            if (!_coverAnimating)
                LyricsArea.MinWidth = _vm.MaxTextWidth;
        }
        else if (e.PropertyName == nameof(OverlayViewModel.CoverEnabled))
        {
            // 播放状态广播频繁且值可能未变：仅值真正变化时才处理
            if (_vm.CoverEnabled == _lastCoverEnabled)
                return;
            _lastCoverEnabled = _vm.CoverEnabled;
            ApplyCoverLayout();
            UpdateCoverSize();
            ApplyCoverRestingState(immediate: true);
        }
        else if (e.PropertyName == nameof(OverlayViewModel.CoverSizePct))
        {
            if (Math.Abs(_vm.CoverSizePct - _lastCoverSizePct) < 0.5)
                return;
            _lastCoverSizePct = _vm.CoverSizePct;
            UpdateCoverSize();
        }
        else if (e.PropertyName is nameof(OverlayViewModel.StrokeEnabled)
                 or nameof(OverlayViewModel.StrokeThickness))
            _lyricVisualMode.RefreshStroke();
        else if (e.PropertyName is nameof(OverlayViewModel.GlowEffect)
                 or nameof(OverlayViewModel.InactiveGlowEffect))
            _lyricVisualMode.RefreshGlow();
        else if (e.PropertyName == nameof(OverlayViewModel.LyricVisualMode))
            SwitchLyricVisualMode();
        else if (e.PropertyName == nameof(OverlayViewModel.TextAlignment))
            ApplyAlignment();
        else if (e.PropertyName == nameof(OverlayViewModel.CoverTitleEnabled))
        {
            // StateChanged 广播频繁且值可能未变：仅值真正变化时才处理，避免动画中反复重启滑入过渡
            if (_vm.CoverTitleEnabled == _lastCoverTitleEnabled)
                return;
            _lastCoverTitleEnabled = _vm.CoverTitleEnabled;
            if (_coverAnimating)
            {
                if (_vm.CoverTitleEnabled)
                    EnterCoverTitle();
                else
                    ExitCoverTitle();
            }
        }
    }

    // ---------- 行间切换动效 ----------

    /// <summary>上一次行间动效的结束时刻；行变化过密时回退为瞬切，避免反复从 0 淡入导致持续偏暗。</summary>
    private DateTimeOffset _lineTransitionUntil;

    /// <summary>
    /// 行切换回调（由 Orchestrator 触发，UI 线程）：开关关闭/抑制（切歌、seek、手动校正）/封面动画期间直接忽略，
    /// 保持当前版本的即时切换；否则按类型播放进入动效。任何异常回退到即时切换（Opacity=1、无变换）。
    /// </summary>
    private void OnLineChanged(bool suppress)
    {
        if (!_settingsService.Current.LineTransitionEnabled)
        {
            ClearExitOverlays();
            SyncLastLineText();
            return;
        }
        if (suppress || _coverAnimating || !IsVisible)
        {
            ClearExitOverlays();   // 切歌/seek/封面动画期间：清理残留覆盖层，瞬切
            SyncLastLineText();
            return;
        }

        var durMs = Math.Clamp(_settingsService.Current.LineTransitionDurationMs, 50, 1500);
        if (DateTimeOffset.UtcNow < _lineTransitionUntil)
        {
            CancelLineAnimation();
            ResetLineTransitionState();
            SyncLastLineText();
            return;
        }
        _lineTransitionUntil = DateTimeOffset.UtcNow.AddMilliseconds(durMs);

        try
        {
            if (_settingsService.Current.LineTransitionType != "Crossfade")
                PlayExitOverlay(durMs);
            switch (_settingsService.Current.LineTransitionType)
            {
                case "Crossfade": PlayCrossfade(durMs); break;
                case "Reveal": PlayReveal(durMs); break;
                case "Shine": PlayShine(durMs); break;
                default: PlayLineTransition(); break;
            }
        }
        catch (Exception ex)
        {
            Log.Error("line transition", ex);
            CancelLineAnimation();
            ResetLineTransitionState();
        }
        SyncLastLineText();
    }

    /// <summary>记录当前行文本，供交叉淡化的旧行快照使用。</summary>
    private void SyncLastLineText()
    {
        _lastMainText = _vm.MainText;
        _lastTransText = _vm.TransText;
    }

    /// <summary>
    /// 旧行退场：把上一行文本作为覆盖层叠加在 LyricsArea 上，按动效类型播放退场动画，
    /// 与新行的进入动效同时进行，避免旧行瞬灭。覆盖层不放在主行网格内，因此不受
    /// 进入动效对网格透明度的影响。
    /// </summary>
    private void PlayExitOverlay(int durMs)
    {
        var s = _settingsService.Current;
        var type = s.LineTransitionType;
        var durMsClamped = Math.Clamp(durMs, 50, 1500);
        var fadeDur = TimeSpan.FromMilliseconds(Math.Min(200, durMsClamped));
        var moveDur = TimeSpan.FromMilliseconds(Math.Min(250, durMsClamped));

        if (_lastMainText.Length > 0)
        {
            var old = CreateExitLyric(_lastMainText);
            old.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            old.HorizontalAlignment = HorizontalAlignment.Left; // 取文字宽度，由 RenderTransform 定位
            LyricsArea.Children.Add(old);
            _exitMainOverlay = old;
            AnimateExitOverlay(old, _mainGrid, type, s, fadeDur, moveDur);
        }
        if (s.ShowTranslation && _lastTransText.Length > 0)
        {
            var oldT = CreateExitTranslation(_lastTransText);
            oldT.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            oldT.HorizontalAlignment = HorizontalAlignment.Left;
            LyricsArea.Children.Add(oldT);
            _exitTransOverlay = oldT;
            AnimateExitOverlay(oldT, _transGrid, type, s, fadeDur, moveDur);
        }
    }

    /// <summary>
    /// 退场覆盖层动画：淡出类只做透明度；缩放弹入可放大淡出（设置开关）；位移淡入沿
    /// 与入场相反的一侧退出。覆盖层居中原位（水平 = 歌词区中心 - 文字宽度/2，垂直跟随锚点行）。
    /// </summary>
    private void AnimateExitOverlay(Control c, Control verticalAnchor, string type, AppSettings s, TimeSpan fadeDur, TimeSpan moveDur)
    {
        var basePoint = ExitBasePoint(c, verticalAnchor);
        c.RenderTransform = Translate(basePoint.X, basePoint.Y);
        c.RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative);

        var dur = fadeDur;
        TransformOperations? start = null, end = null;
        if (type == "Scale" && s.LineTransitionScaleExitGrow)
        {
            dur = moveDur;
            start = ExitTransform(basePoint, 1.0);
            end = ExitTransform(basePoint, 1.15);
        }
        else if (type == "Slide")
        {
            dur = moveDur;
            var (dx, dy) = LineSlideOffset(s.LineTransitionDirection, s.LineTransitionDistance);
            start = Translate(basePoint.X, basePoint.Y);
            end = Translate(basePoint.X - dx, basePoint.Y - dy);
        }

        var transitions = new Transitions
        {
            new DoubleTransition { Property = Visual.OpacityProperty, Duration = dur },
        };
        if (start != null && end != null)
            transitions.Add(new TransformOperationsTransition
            {
                Property = Visual.RenderTransformProperty,
                Duration = dur,
                Easing = EasingFor(s.LineTransitionEasing),
            });

        c.Transitions = null;
        c.Opacity = 1;
        if (start != null)
            c.RenderTransform = start;
        c.Transitions = transitions;
        c.Opacity = 0;
        if (end != null)
            c.RenderTransform = end;

        RemoveExitAfter(c, dur);
    }

    /// <summary>退场覆盖层居中原位（水平 = 歌词区中心 - 文字宽度/2，垂直跟随锚点行）。</summary>
    private Point ExitBasePoint(Control c, Control verticalAnchor)
    {
        var p = verticalAnchor.TranslatePoint(new Point(0, 0), LyricsArea) ?? new Point(0, 0);
        var cx = LyricsArea.Bounds.Width / 2;
        var w = c.DesiredSize.Width > 0 ? c.DesiredSize.Width : c.Bounds.Width;
        return new Point(cx - w / 2, p.Y);
    }

    /// <summary>位移 + 缩放的组合变换（先缩放后平移，配合中心轴 RenderTransformOrigin）。</summary>
    private static TransformOperations ExitTransform(Point t, double scale)
    {
        var b = new TransformOperations.Builder(2);
        b.AppendTranslate(t.X, t.Y);
        b.AppendScale(scale, scale);
        return b.Build();
    }

    /// <summary>退场动画结束后移除覆盖层并清理引用。</summary>
    private void RemoveExitAfter(Control c, TimeSpan dur)
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(dur + TimeSpan.FromMilliseconds(60));
            Dispatcher.UIThread.Post(() =>
            {
                LyricsArea.Children.Remove(c);
                if (ReferenceEquals(_exitMainOverlay, c))
                    _exitMainOverlay = null;
                if (ReferenceEquals(_exitTransOverlay, c))
                    _exitTransOverlay = null;
            });
        });
    }

    /// <summary>立即移除未完成的退场覆盖层（动效中断/开关关闭时清理残留）。</summary>
    private void ClearExitOverlays()
    {
        if (_exitMainOverlay != null)
        {
            LyricsArea.Children.Remove(_exitMainOverlay);
            _exitMainOverlay = null;
        }
        if (_exitTransOverlay != null)
        {
            LyricsArea.Children.Remove(_exitTransOverlay);
            _exitTransOverlay = null;
        }
    }

    /// <summary>淡入/位移淡入/缩放弹入：主行与翻译行整体从起始状态过渡到最终状态。</summary>
    private void PlayLineTransition()
    {
        var s = _settingsService.Current;
        var durMs = Math.Clamp(s.LineTransitionDurationMs, 50, 1500);
        var dur = TimeSpan.FromMilliseconds(durMs);
        var easing = EasingFor(s.LineTransitionEasing);

        var (dx, dy) = LineSlideOffset(s.LineTransitionDirection, s.LineTransitionDistance);
        var start = s.LineTransitionType switch
        {
            "Slide" => Translate(dx, dy),
            "Scale" => Scale(s.LineTransitionScale, s.LineTransitionScale),
            _ => TransformOperations.Identity,
        };

        AnimateLineHost(_mainGrid, start, dur, easing);
        AnimateLineHost(_transGrid, start, dur, easing);

        // 位移淡入入场模糊：进入过程中文字由模糊逐渐清晰
        if (s.LineTransitionType == "Slide" && s.LineTransitionSlideBlurEnabled)
            StartSlideBlur(durMs);
    }

    /// <summary>位移淡入入场模糊：给主行/翻译行网格挂 BlurEffect，由统一进度驱动器衰减到 0。</summary>
    private void StartSlideBlur(int durMs)
    {
        var radius = Math.Clamp(_settingsService.Current.LineTransitionSlideBlurRadius, 0, 20);
        if (radius <= 0)
            return;
        _mainGrid.Effect = new BlurEffect { Radius = radius };
        _transGrid.Effect = new BlurEffect { Radius = radius };

        StartLineProgressAnimation("SlideBlur", durMs);
    }

    /// <summary>取消入场模糊：移除网格上的模糊效果。</summary>
    private void ClearSlideBlur()
    {
        _mainGrid.Effect = null;
        _transGrid.Effect = null;
    }

    /// <summary>
    /// 交叉淡化：新行已就位（整行保持可见），旧行快照叠加在最上层淡出，自然露出新行。
    /// 避免动画整行网格透明度——否则旧行快照作为子元素会随之变暗，视觉上"藏在"新行后面。
    /// </summary>
    private void PlayCrossfade(int durMs)
    {
        var s = _settingsService.Current;
        var dur = TimeSpan.FromMilliseconds(durMs);
        var easing = EasingFor(s.LineTransitionEasing);

        _mainGrid.Opacity = 1;
        _transGrid.Opacity = 1;

        if (_lastMainText.Length > 0)
        {
            var old = CreateExitLyric(_lastMainText);
            _mainGrid.Children.Add(old);
            FadeOutAndRemove(_mainGrid, old, dur);
        }
        if (s.ShowTranslation && _lastTransText.Length > 0)
        {
            var oldT = CreateExitTranslation(_lastTransText);
            _transGrid.Children.Add(oldT);
            FadeOutAndRemove(_transGrid, oldT, dur);
        }
    }

    /// <summary>当前歌词对齐方式（按设置解析）。</summary>
    private TextAlignment LineAlignmentValue =>
        (TextAlignment)Enum.Parse(typeof(TextAlignment), _settingsService.Current.Alignment);

    /// <summary>
    /// 当前歌词水平定位方式（按设置解析）。LyricText 把文字绘制在自身 (0,0)，必须让行控件
    /// 保持文字宽度并居中定位，否则网格被旧行快照撑宽时行控件被 Stretch 拉宽、文字滞留左端。
    /// </summary>
    private HorizontalAlignment LineHorizontalAlignment => _settingsService.Current.Alignment switch
    {
        "Left" => HorizontalAlignment.Left,
        "Right" => HorizontalAlignment.Right,
        _ => HorizontalAlignment.Center,
    };

    /// <summary>旧行快照（整行主色，无逐字遮罩），样式复制自当前已唱层，并按当前对齐设定渲染。</summary>
    private LyricText CreateExitLyric(string text)
    {
        return new LyricText
        {
            Text = text,
            FontFamily = _mainLyric.FontFamily,
            FontWeight = _mainLyric.FontWeight,
            FontSize = _mainLyric.FontSize,
            TextAlignment = LineAlignmentValue,
            HorizontalAlignment = LineHorizontalAlignment,
            Fill = _mainLyric.Fill,
            StrokeEnabled = _mainLyric.StrokeEnabled,
            StrokeBrush = _mainLyric.StrokeBrush,
            StrokeThickness = _mainLyric.StrokeThickness,
            Effect = _mainLyric.Effect,
            HighlightLength = -1,
            HighlightClip = LyricText.ClipSideMode.None,
            Opacity = 1,
        };
    }

    /// <summary>旧翻译行快照，样式复制自当前翻译行，并按当前对齐设定渲染。</summary>
    private TextBlock CreateExitTranslation(string text)
    {
        return new TextBlock
        {
            Text = text,
            FontFamily = _transTb.FontFamily,
            FontSize = _transTb.FontSize,
            TextAlignment = LineAlignmentValue,
            Foreground = _transTb.Foreground,
            Effect = _transTb.Effect,
            Opacity = 1,
        };
    }

    /// <summary>淡出后从父网格移除（任务延迟 → UI 线程移除）；afterRemoved 用于清理引用。</summary>
    private static void FadeOutAndRemove(Grid parent, Control c, TimeSpan dur, Action? afterRemoved = null)
    {
        c.Transitions = new Transitions
        {
            new DoubleTransition { Property = Visual.OpacityProperty, Duration = dur },
        };
        c.Opacity = 0;
        _ = Task.Run(async () =>
        {
            await Task.Delay(dur + TimeSpan.FromMilliseconds(60));
            Dispatcher.UIThread.Post(() =>
            {
                parent.Children.Remove(c);
                afterRemoved?.Invoke();
            });
        });
    }

    /// <summary>
    /// 逐字显现：隐藏未唱层/未唱辉光，仅已唱层由 EnterProgress 驱动从左到右逐字符点亮；
    /// 翻译行以淡入同步进入。结束后恢复未唱层，交还卡拉 OK 逐字进度。
    /// </summary>
    private void PlayReveal(int durMs)
    {
        _revealHidingInactive = true;
        _inactiveLyric.IsVisible = false;
        foreach (var glow in _inactiveGlowLyrics)
            glow.IsVisible = false;

        AnimateLineHost(_transGrid, TransformOperations.Identity,
            TimeSpan.FromMilliseconds(durMs), EasingFor(_settingsService.Current.LineTransitionEasing));
        StartLineProgressAnimation("Reveal", durMs);
    }

    /// <summary>
    /// 扫光：主行整体压暗，白色亮带叠加在主行上方从左到右扫描（白底白字时仍清晰可见）。
    /// 亮带层放在 LyricsArea（视图盒的同级覆盖层）上，不受主行网格透明度影响。
    /// </summary>
    private void PlayShine(int durMs)
    {
        EnsureShineLayer();

        _mainGrid.Transitions = null;
        _mainGrid.Opacity = 0.35;
        PositionShineLayer();
        AnimateLineHost(_transGrid, TransformOperations.Identity,
            TimeSpan.FromMilliseconds(durMs), EasingFor(_settingsService.Current.LineTransitionEasing));
        StartLineProgressAnimation("Shine", durMs);
    }

    private void EnsureShineLayer()
    {
        if (_shineLayer != null)
            return;
        var sl = new LyricText();
        sl.Bind(LyricText.TextProperty, new Avalonia.Data.Binding("MainText"));
        sl.Bind(LyricText.FontFamilyProperty, new Avalonia.Data.Binding("FontFamilyValue"));
        sl.Bind(LyricText.FontWeightProperty, new Avalonia.Data.Binding("WeightValue"));
        sl.Bind(LyricText.FontSizeProperty, new Avalonia.Data.Binding("MainFontSize"));
        sl.Bind(LyricText.TextAlignmentProperty, new Avalonia.Data.Binding("TextAlignment"));
        sl.Fill = Brushes.White;
        sl.Opacity = 1;
        sl.ShineProgress = -1;
        LyricsArea.Children.Add(sl);
        _shineLayer = sl;
    }

    /// <summary>把扫光层定位到主行（MainGrid）在 LyricsArea 坐标系中的位置。</summary>
    private void PositionShineLayer()
    {
        if (_shineLayer == null)
            return;
        var p = _mainGrid.TranslatePoint(new Point(0, 0), LyricsArea) ?? new Point(0, 0);
        _shineLayer.RenderTransform = Translate(p.X, p.Y);
    }

    private void ApplyEnterProgress(double p)
    {
        _mainLyric.EnterProgress = p;
        foreach (var glow in _mainGlowLyrics)
            glow.EnterProgress = p;
    }

    private void ApplyShineProgress(double p)
    {
        if (_shineLayer != null)
            _shineLayer.ShineProgress = p;
    }

    private void StartLineProgressAnimation(string kind, int durationMs)
    {
        _lineAnimKind = kind;
        var duration = TimeSpan.FromMilliseconds(Math.Clamp(durationMs, 50, 1500));
        _lineAnimation.Start(duration, ApplyLineAnimationProgress, FinishLineAnimation);
    }

    private void ApplyLineAnimationProgress(double p)
    {
        switch (_lineAnimKind)
        {
            case "Reveal": ApplyEnterProgress(p); break;
            case "Shine": ApplyShineProgress(p); break;
            case "SlideBlur":
                {
                    var r = Math.Clamp(_settingsService.Current.LineTransitionSlideBlurRadius, 0, 20);
                    var blur = r * (1 - p);
                    if (_mainGrid.Effect is BlurEffect mb) mb.Radius = blur;
                    if (_transGrid.Effect is BlurEffect tb) tb.Radius = blur;
                    break;
                }
        }
    }

    private void FinishLineAnimation()
    {
        if (_lineAnimKind == "Shine")
        {
            PlayShineOutro();
        }
        else if (_lineAnimKind == "Reveal")
        {
            _mainLyric.EnterProgress = -1;
            foreach (var glow in _mainGlowLyrics)
                glow.EnterProgress = -1;
            if (_revealHidingInactive)
            {
                _inactiveLyric.IsVisible = true;
                foreach (var glow in _inactiveGlowLyrics)
                    glow.IsVisible = true;
                _revealHidingInactive = false;
            }
        }
        else if (_lineAnimKind == "SlideBlur")
        {
            ClearSlideBlur();
        }
        _lineAnimKind = "";
    }

    /// <summary>
    /// 扫光退出：主行从压暗平滑恢复到正常亮度，亮带同步淡出后移除，避免"扫完直接跳回"的突兀感。
    /// </summary>
    private void PlayShineOutro()
    {
        var durMs = Math.Clamp(_settingsService.Current.LineTransitionDurationMs, 50, 1500);
        var outro = TimeSpan.FromMilliseconds(Math.Min(200, durMs));

        _mainGrid.Transitions = new Transitions
        {
            new DoubleTransition { Property = Visual.OpacityProperty, Duration = outro, Easing = new QuadraticEaseOut() },
        };
        _mainGrid.Opacity = 1;

        var sl = _shineLayer;
        if (sl != null)
        {
            _shineLayer = null;
            sl.Transitions = new Transitions
            {
                new DoubleTransition { Property = Visual.OpacityProperty, Duration = outro },
            };
            sl.Opacity = 0;
            _ = Task.Run(async () =>
            {
                await Task.Delay(outro + TimeSpan.FromMilliseconds(60));
                Dispatcher.UIThread.Post(() => LyricsArea.Children.Remove(sl));
            });
        }
    }

    /// <summary>中断行间动效（新行到来/开关关闭/异常回退）：停表、清理逐字/扫光临时层并恢复隐藏层。</summary>
    private void CancelLineAnimation()
    {
        _lineAnimation.Cancel();
        _lineAnimKind = "";

        _mainLyric.EnterProgress = -1;
        foreach (var glow in _mainGlowLyrics)
            glow.EnterProgress = -1;
        if (_shineLayer != null)
        {
            LyricsArea.Children.Remove(_shineLayer);
            _shineLayer = null;
        }
        _mainGrid.Opacity = 1;
        ClearSlideBlur();
        if (_revealHidingInactive)
        {
            _inactiveLyric.IsVisible = true;
            foreach (var glow in _inactiveGlowLyrics)
                glow.IsVisible = true;
            _revealHidingInactive = false;
        }
        ClearExitOverlays();
    }

    /// <summary>
    /// 行载体进入动画：先禁用过渡瞬移起始状态，再挂过渡并将属性设为目标值 → 播放过渡。
    /// （与封面动画同套路；RenderTransform 不参与布局，不干扰 SizeToContent。）
    /// </summary>
    private static void AnimateLineHost(Control host, TransformOperations start, TimeSpan dur, Easing easing)
    {
        var transitions = new Transitions
        {
            new DoubleTransition { Property = Visual.OpacityProperty, Duration = dur, Easing = easing },
            new TransformOperationsTransition { Property = Visual.RenderTransformProperty, Duration = dur, Easing = easing },
        };
        host.Transitions = null;
        host.Opacity = 0;
        host.RenderTransform = start;
        host.Transitions = transitions;
        host.Opacity = 1;
        host.RenderTransform = TransformOperations.Identity;
    }

    /// <summary>行间滑入方向偏移：1=上，2=下，3=左，4=右，0=无位移；位移强度可配置。</summary>
    private static (double dx, double dy) LineSlideOffset(int dir, double dist)
    {
        var d = Math.Clamp(dist, 0, 60);
        return dir switch
        {
            1 => (0, -d), // 起点在上方（向下滑入）
            2 => (0, d),  // 起点在下方（向上滑入）
            3 => (-d, 0), // 起点在左侧（向右滑入）
            4 => (d, 0),  // 起点在右侧（向左滑入）
            _ => (0, 0),
        };
    }

    private static TransformOperations Scale(double sx, double sy)
    {
        var b = new TransformOperations.Builder(1);
        b.AppendScale(sx, sy);
        return b.Build();
    }

    /// <summary>回退到即时切换的安全状态：取消动效、恢复完全可见与无变换。</summary>
    private void ResetLineTransitionState()
    {
        CancelLineAnimation();
        _mainGrid.Transitions = null;
        _transGrid.Transitions = null;
        _mainGrid.Opacity = 1;
        _transGrid.Opacity = 1;
        _mainGrid.RenderTransform = TransformOperations.Identity;
        _transGrid.RenderTransform = TransformOperations.Identity;
    }

    /// <summary>设置变化：关闭行间动效时清理可能残留的动画状态。</summary>
    private void OnLineTransitionSettingsChanged()
    {
        if (!_settingsService.Current.LineTransitionEnabled)
            ResetLineTransitionState();
    }

    // ---------- 封面 ----------

    private double _coverSize;

    /// <summary>构建 translate 变换（Parse 不接受小数，用 Builder）。</summary>
    private static TransformOperations Translate(double x, double y)
    {
        var b = new TransformOperations.Builder(1);
        b.AppendTranslate(x, y);
        return b.Build();
    }

    /// <summary>按设置重建封面动画过渡（缓动曲线 + 时长），作用于移动/缩放/淡入淡出。</summary>
    private void ApplyCoverAnimationStyle()
    {
        var easing = EasingFor(_settingsService.Current.CoverAnimEasing);
        var dur = TimeSpan.FromMilliseconds(Math.Clamp(_settingsService.Current.CoverAnimDurationMs, 100, 2000));

        Cover.Transitions = new Transitions
        {
            new DoubleTransition { Property = Visual.OpacityProperty, Duration = dur, Easing = easing },
            new TransformOperationsTransition { Property = Visual.RenderTransformProperty, Duration = dur, Easing = easing },
        };
    }

    private static Easing EasingFor(string name) => name switch
    {
        "Linear" => new LinearEasing(),
        "Cubic" => new CubicEaseOut(),
        "Sine" => new SineEaseOut(),
        "Exponential" => new ExponentialEaseOut(),
        "Back" => new BackEaseOut(),
        _ => new QuadraticEaseOut(),
    };

    /// <summary>歌词加载完成的等待超时（防封面卡死）。</summary>
    private static readonly TimeSpan CoverAnimTimeout = TimeSpan.FromSeconds(6);

    /// <summary>
    /// 切歌（任意来源：播放器内切歌 / 悬浮窗上一首下一首）动画状态机（方案二：窗口内叠加）：
    /// 窗口宽度全程不变；高度仅在动画期间容纳放大后的封面及歌名区；
    /// 封面作为覆盖层放大到行高尺寸显示在歌词行中央（歌词透明被掩盖），
    /// 歌词 Ready/NoLyric 后（或超时）执行分支：
    /// 常驻=封面缩小移动到常驻位 / 不常驻=按方向滑出淡出。尺寸与位置均由 Transitions 平滑过渡，
    /// 缓动曲线与时长可配置（ApplyCoverAnimationStyle）。
    /// </summary>
    private void OnCoverChanged()
    {
        ApplyCoverLayout();
        UpdateCoverSize();
        var animate = _vm.CoverCutAnimation && _vm.ConsumeCutAnimationFlag();
        Log.Info($"cover: track={_vm.CoverTrackKey} image={(_vm.CoverImage != null)} animate={animate} enabled={_vm.CoverEnabled}");

        if (_vm.CoverImage == null)
        {
            // 无封面：直接显示歌词
            _coverAnimating = false;
            _coverLayoutExpanded = false;
            _coverMotionToken++;
            Cover.Opacity = 0;
            ExitCoverTitle();
            SetLyricsOpacity(1);
            SyncWindowHeight();
            return;
        }

        CoverImage.Source = _vm.CoverImage;

        if (animate)
        {
            _coverAnimating = true;
            _coverStageTimer.Stop();
            _coverTimeoutTimer.Stop();
            SetLyricsOpacity(0);
            _coverAnimSize = AnimCoverSize();
            _coverLayoutExpanded = true;
            SyncWindowHeight();
            _coverAnimStart = DateTimeOffset.UtcNow;
            _coverTimeoutTimer.Interval = TimeSpan.FromMilliseconds(_vm.CoverAnimMaxMs);
            _coverTimeoutTimer.Start();

            // 固定布局尺寸 + 中心缩放：不再动画 Width/Height，避免左上角扩张造成斜向淡入。
            CoverImage.Width = _coverSize;
            CoverImage.Height = _coverSize;
            _coverPlan = CoverAnimationPlan.Create(
                OuterGrid.Bounds.Width, _coverSize, _coverAnimSize, CoverSlotOffset(),
                _settingsService.Current.CoverAnimDirection,
                _settingsService.Current.CoverAnimSlideDistance);
            var start = _vm.CoverEnabled
                ? _coverPlan.PersistentStart
                : _coverPlan.TransientStart;
            BeginCoverMotion(start, _coverPlan.Center);

            EnterCoverTitle();
            _coverPosDirty = true;
            Log.Info($"cover: animation started, winW={ClientSize.Width:F0} size={_coverAnimSize:F0} titleEnabled={_vm.CoverTitleEnabled}");
            return;
        }

        // 非动画切歌：统一应用静止状态
        _coverAnimating = false;
        _coverLayoutExpanded = false;
        ExitCoverTitle();
        ApplyCoverRestingState(immediate: true);
        SyncWindowHeight();
    }

    private void OnLyricsPhaseChanged()
    {
        // 切歌/重新解析：立即清理可能残留的退出覆盖层（旧曲行切换的动画快照）。
        // 否则旧曲的那一行会作为 LyricsArea 的覆盖层短暂显示在新歌歌词上方，
        // 直到原动画的延迟移除任务（约 200ms）才消失。
        if (_vm.Phase is LyricsPhase.Resolving or LyricsPhase.NoSession)
        {
            ClearExitOverlays();
            CancelLineAnimation();
        }

        if (!_coverAnimating)
            return;
        if (_vm.Phase is LyricsPhase.Ready or LyricsPhase.NoLyric)
        {
            var elapsed = (DateTimeOffset.UtcNow - _coverAnimStart).TotalMilliseconds;
            var wait = Math.Max(400, _vm.CoverAnimMinMs - elapsed);
            _coverStageTimer.Interval = TimeSpan.FromMilliseconds(wait);
            _coverStageTimer.Start();
        }
    }

    private void OnCoverStage()
    {
        _coverStageTimer.Stop();
        _coverTimeoutTimer.Stop();
        if (!_coverAnimating)
        {
            if (_coverLayoutExpanded)
            {
                _coverLayoutExpanded = false;
                SyncWindowHeight();
            }
            return;
        }

        _coverAnimating = false;
        _coverPosDirty = false;
        SetLyricsOpacity(1);
        ExitCoverTitle(); // 歌名淡出（250ms）后退出布局
        _coverPlan = CoverAnimationPlan.Create(
            OuterGrid.Bounds.Width, _coverSize, _coverAnimSize, CoverSlotOffset(),
            _settingsService.Current.CoverAnimDirection,
            _settingsService.Current.CoverAnimSlideDistance);
        if (_vm.CoverEnabled)
        {
            // 常驻：围绕中心缩小并移动到常驻位，布局尺寸保持不变。
            ApplyCoverVisual(_coverPlan.PersistentEnd);
        }
        else
        {
            // 不常驻：沿配置方向滑出并淡出。
            ApplyCoverVisual(_coverPlan.TransientEnd);
        }

        // 保持动画画布高度直到退出变换结束，避免窗口先收缩而裁掉正在缩放/滑出的封面。
        _coverStageTimer.Interval = TimeSpan.FromMilliseconds(
            Math.Clamp(_settingsService.Current.CoverAnimDurationMs, 100, 2000) + 50);
        _coverStageTimer.Start();
    }

    /// <summary>
    /// 歌名进入布局（仅动画期间调用）：先不可见地进入布局（保持 Opacity=0），
    /// 等布局完成后（Bounds 就绪）在同一轮内完成：瞬移起始位置（无过渡）→ 淡入 → 滑向最终位置。
    /// 避免"旧位置先可见再被拖动"的跳变。开关关闭时不进入。
    /// </summary>
    private void EnterCoverTitle()
    {
        if (!_vm.CoverTitleEnabled)
            return;
        CoverTitleBox.IsVisible = true; // Opacity 保持 0：布局完成前不可见
        _coverTitleEnterPending = true;
    }

    /// <summary>歌名淡出（400ms）并沿淡入反方向滑出；快速切歌时跳过，避免误关新歌名。</summary>
    private void ExitCoverTitle()
    {
        var (dx, dy) = CoverTitleSlideOffset();
        UpdateCoverTitlePosition(dx, dy); // 滑向偏移位置（与淡入同向偏移，视觉对称）
        CoverTitleBox.Opacity = 0;
        _ = Task.Run(async () =>
        {
            await Task.Delay(450); // 等待淡出过渡完成
            Dispatcher.UIThread.Post(() =>
            {
                if (_coverAnimating)
                    return; // 新动画已开始：跳过退出
                CoverTitleBox.IsVisible = false;
            });
        });
    }

    /// <summary>淡入淡出方向偏移：1=上，2=下，3=左，4=右，0=无位移；强度可配置。</summary>
    private (double dx, double dy) CoverTitleSlideOffset()
    {
        var dir = _vm.CoverTitleAnimDirection;
        var d = Math.Clamp(_settingsService.Current.CoverTitleSlideDistance, 0, 60);
        return dir switch
        {
            1 => (0, -d), // 起点在上方（向下滑入）
            2 => (0, d),  // 起点在下方（向上滑入）
            3 => (-d, 0), // 起点在左侧（向右滑入）
            4 => (d, 0),  // 起点在右侧（向左滑入）
            _ => (0, 0),
        };
    }

    /// <summary>
    /// 歌名定位：水平居中于窗口（与封面同轴）；垂直 = 封面目标高度（动画期间 = 行高×3）下方，
    /// 不依赖封面过渡中的实时尺寸 → 歌名始终位于最终封面下方，不悬浮覆盖在封面上。
    /// 淡入淡出时叠加方向偏移（dx, dy）。
    /// </summary>
    private void UpdateCoverTitlePosition(double dx = 0, double dy = 0)
    {
        if (!CoverTitleBox.IsVisible)
            return;
        const double gap = 8;
        var y = (_coverAnimating ? _coverAnimSize : Cover.Bounds.Height) + gap;
        CoverTitleBox.RenderTransform = Translate(dx, y + dy);
    }

    private void SetLyricsOpacity(double opacity) => LyricsViewbox.Opacity = opacity;

    /// <summary>动画封面尺寸 = 行高 × 300%（掩盖歌词加载的大封面；窗口高度由 ContentHeight 自动容纳）。</summary>
    private double AnimCoverSize()
    {
        var h = _mainGrid.Bounds.Height;
        if (h <= 0)
            h = 40;
        return Math.Max(h * 3, _coverSize);
    }

    /// <summary>常驻位置（CoverSlot 左上角）在外层 Grid 内的坐标（与封面元素同一坐标系）。</summary>
    private Point CoverSlotOffset()
    {
        var p = CoverSlot.TranslatePoint(new Point(0, 0), OuterGrid) ?? default;
        return new Point(Math.Max(0, p.X), Math.Max(0, p.Y));
    }

    /// <summary>封面位置跟随布局（歌词宽度变化等）；动画期间不移动。</summary>
    private void UpdateCoverPosition()
    {
        if (_coverAnimating)
            return;
        if (_vm.CoverEnabled && Cover.Opacity > 0)
        {
            var target = CoverSlotOffset();
            ApplyCoverVisual(CoverAnimationPlan.Resting(
                OuterGrid.Bounds.Width, _coverSize, target, visible: true));
        }
    }

    /// <summary>
    /// 应用非切歌动画期间的封面静止状态。设置页开关、封面数据刷新和普通曲目绑定都走这里，
    /// 避免只扩大占位列却没有把现有封面设为可见。immediate=true 时禁用过渡，防止设置切换
    /// 从上一次动画残留位置滑入。
    /// </summary>
    private void ApplyCoverRestingState(bool immediate)
    {
        if (_coverAnimating)
            return;

        var coverTransitions = Cover.Transitions;
        if (immediate)
        {
            _coverMotionToken++;
            Cover.Transitions = null;
        }

        try
        {
            SetLyricsOpacity(1);
            if (_vm.CoverEnabled && _vm.CoverImage != null)
            {
                CoverImage.Source = _vm.CoverImage;
                CoverImage.Width = _coverSize;
                CoverImage.Height = _coverSize;
                var target = CoverSlotOffset();
                ApplyCoverVisual(CoverAnimationPlan.Resting(
                    OuterGrid.Bounds.Width, _coverSize, target, visible: true));
            }
            else
            {
                ApplyCoverVisual(CoverAnimationPlan.Resting(
                    OuterGrid.Bounds.Width, _coverSize, CoverSlotOffset(), visible: false));
            }
        }
        finally
        {
            if (immediate)
                Cover.Transitions = coverTransitions;
        }
    }

    /// <summary>先提交无过渡起点，下一调度轮次再进入目标状态，确保方向起点真实参与渲染。</summary>
    private void BeginCoverMotion(CoverVisualState start, CoverVisualState target)
    {
        var token = ++_coverMotionToken;
        var transitions = Cover.Transitions;
        Cover.Transitions = null;
        ApplyCoverVisual(start);
        Cover.Transitions = transitions;
        Dispatcher.UIThread.Post(() =>
        {
            if (token == _coverMotionToken && _coverAnimating)
                ApplyCoverVisual(target);
        }, DispatcherPriority.Background);
    }

    /// <summary>应用统一的 Scale + Translate + Opacity 状态；位移放在缩放之后，避免位移量被倍率放大。</summary>
    private void ApplyCoverVisual(CoverVisualState state)
    {
        var b = new TransformOperations.Builder(2);
        b.AppendScale(state.Scale, state.Scale);
        b.AppendTranslate(state.X, state.Y);
        Cover.RenderTransform = b.Build();
        Cover.Opacity = state.Opacity;
    }

    /// <summary>
    /// 封面尺寸 = 主歌词行实际高度 × 占比（正方形）。
    /// 基准行高只受字号等设置控制（行文本长度变化不影响行高），设置变化时由 SizeChanged 驱动。
    /// </summary>
    private void UpdateCoverSize()
    {
        if (_coverAnimating)
            return; // 动画期间冻结尺寸，避免布局波动打断动画/引起抖动
        var baseH = _mainGrid.Bounds.Height;
        if (baseH <= 0)
            baseH = 40;
        _coverSize = Math.Max(0, baseH * _vm.CoverSizePct / 100.0);
        var w = _vm.CoverEnabled && _vm.CoverImage != null ? _coverSize : 0;
        CoverSlot.Width = w;
        CoverSlot.Height = w;
        RightPad.Width = w;
        RightPad.Height = w;
        CoverImage.Width = _coverSize;
        CoverImage.Height = _coverSize;
    }

    /// <summary>封面位置固定左侧（占位列 0，歌词列 1，对称占位列 2 → 歌词保持居中）。</summary>
    private void ApplyCoverLayout()
    {
        Grid.SetColumn(CoverSlot, 0);
        Grid.SetColumn(LyricsArea, 1);
        Grid.SetColumn(RightPad, 2);
        UpdateCoverPosition();
    }

    private void ApplyAlignment()
    {
        _lyricVisualMode.ApplyAlignment(_vm.TextAlignment, LineHorizontalAlignment);
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        _hwnd = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        Log.Info($"overlay opened, hwnd={_hwnd:X8}");
        if (_hwnd != IntPtr.Zero)
            Win32.AssertTopmost(_hwnd);
        SetLocked(_settingsService.Current.Locked);
        Win32.HideFromAltTab(_hwnd);   // ← 新增：从 Alt+Tab 列表隐藏
        UpdatePlayPauseIcon();
        ApplyAnchor();
        UpdateVisibility();
        LogSizeDiagnostics();
        ScheduleSizeDiagnostics();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // 桌面歌词窗口不允许用户关闭：关闭 = 隐藏并同步设置（托盘勾选），
        // 避免窗口销毁后托盘再次 Show() 崩溃；程序退出时由 Cleanup 先置 AllowClose。
        if (!_allowClose)
        {
            e.Cancel = true;
            if (_settingsService.Current.LyricsVisible)
                _settingsService.Update(s => s.LyricsVisible = false);
            Hide();
            return;
        }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _topmostTimer.Stop();
        _hideControlsTimer.Stop();
        _coverStageTimer.Stop();
        _coverTimeoutTimer.Stop();
        _lineAnimation.Dispose();
        _settingsService.Changed -= ApplyCoverAnimationStyle;
        _settingsService.Changed -= OnLineTransitionSettingsChanged;
        _vm.LineChanged -= OnLineChanged;
        base.OnClosed(e);
    }

    private void OnSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        Log.Info($"trc OnSizeChanged client={e.NewSize.Width:F1}x{e.NewSize.Height:F1} old={e.PreviousSize.Width:F1}x{e.PreviousSize.Height:F1} wc={e.WidthChanged} hc={e.HeightChanged} pos={Position.X},{Position.Y} first={_firstSizeApplied} grow={_settingsService.Current.HeightGrowMode}");
        if (!_suppressPositionUpdate && ClientSize.Width > 0 && ClientSize.Height > 0)
        {
            var grow = _settingsService.Current.HeightGrowMode;
            if (!_firstSizeApplied)
            {
                // 初始阶段：尺寸剧烈变化（封面/布局瞬时巨大尺寸），不立即定位
                // （会把顶部推到屏幕外再被钳到顶边），待尺寸稳定合理后再以锚点定位一次。
                ScheduleInitialAnchorApply();
            }
            else
            {
                RepositionForSizeChange(e, grow);
            }
        }
        // 记录当前窗口底边（物理像素），供"向上扩展"模式使用
        var scale = Screens.ScreenFromWindow(this)?.Scaling ?? 1.0;
        _lastGrowBottom = Position.Y + (int)Math.Round(ClientSize.Height * scale);
        LogSizeDiagnostics();
    }

    /// <summary>
    /// 自动布局变化只移动窗口，不改写用户锚点。宽度始终围绕水平锚点变化；高度遵循扩展方向，
    /// 即使宽高在同一布局事件中同时变化，也不能退回到上下居中的旧逻辑。
    /// </summary>
    private void RepositionForSizeChange(SizeChangedEventArgs e, int grow)
    {
        var s = Screens.ScreenFromWindow(this)?.Scaling ?? 1.0;
        var x = e.WidthChanged
            ? (int)Math.Round(_anchor.X - ClientSize.Width * s / 2)
            : Position.X;
        var y = Position.Y;
        if (e.HeightChanged)
        {
            y = grow switch
            {
                2 => (int)Math.Round(_lastGrowBottom - ClientSize.Height * s),
                0 => (int)Math.Round(_anchor.Y - ClientSize.Height * s / 2),
                _ => Position.Y,
            };
        }

        if (x == Position.X && y == Position.Y)
            return;

        _suppressPositionUpdate = true;
        Position = new PixelPoint(x, y);
        _suppressPositionUpdate = false;
        Log.Info($"trc RepositionForSizeChange grow={grow} anchor=({_anchor.X},{_anchor.Y}) " +
                 $"client={ClientSize.Width:F1}x{ClientSize.Height:F1} s={s:F3} pos={Position.X},{Position.Y}");
    }

    private DateTimeOffset _lastSizeDiag = DateTimeOffset.MinValue;

    /// <summary>
    /// 尺寸诊断（节流 1s）：对比 DIP 期望物理尺寸与窗口实际物理尺寸，
    /// 验证 2K 高分屏下 SizeToContent 物理换算是否正确（客户区 = ClientSize × 缩放？）。
    /// RenderScaling = 窗口实际渲染缩放，若与屏幕缩放不一致即为 DPI 换算错误的直接证据。
    /// </summary>
    private void LogSizeDiagnostics()
    {
        if (DateTimeOffset.UtcNow - _lastSizeDiag < TimeSpan.FromSeconds(1))
            return;
        _lastSizeDiag = DateTimeOffset.UtcNow;
        if (ClientSize.Width <= 0)
            return;
        var screenScale = Screens.ScreenFromWindow(this)?.Scaling ?? 1.0;
        var renderScale = RenderScaling;
        var expectByScreen = (ClientSize.Width * screenScale, ClientSize.Height * screenScale);
        var expectByRender = (ClientSize.Width * renderScale, ClientSize.Height * renderScale);
        var win = Win32.GetWindowSize(_hwnd);
        var client = Win32.GetClientSize(_hwnd);
        Log.Info($"diag: client={ClientSize.Width:F1}x{ClientSize.Height:F1} " +
                 $"screenScale={screenScale:F3} renderScale={renderScale:F3} " +
                 $"expectByScreen={expectByScreen.Item1:F0}x{expectByScreen.Item2:F0} " +
                 $"expectByRender={expectByRender.Item1:F0}x{expectByRender.Item2:F0} " +
                 $"winPhys={win.W}x{win.H} err={win.Err} clientPhys={client.W}x{client.H} err={client.Err} " +
                 $"minW={MinWidth:F0} maxW={(double.IsPositiveInfinity(MaxWidth) ? -1 : MaxWidth):F0}");
    }

    /// <summary>窗口首次布局完成后再采集一次尺寸诊断（OnOpened 时 SizeToContent 可能未完成）。</summary>
    private void ScheduleSizeDiagnostics()
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(800);
            Dispatcher.UIThread.Post(LogSizeDiagnostics);
        });
    }

    /// <summary>按下即拖动（控制条区域除外，避免与按钮点击冲突）；锁定状态下禁止拖动。</summary>
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (_locked)
            return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;
        if (IsInsideControlBar(e.Source as Avalonia.Visual))
            return;
        _dragStarted = true;
        BeginMoveDrag(e);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_dragStarted)
            WriteAnchorFromPosition();
        _dragStarted = false;
    }

    private bool IsInsideControlBar(Avalonia.Visual? source)
    {
        while (source != null)
        {
            if (ReferenceEquals(source, ControlBar))
                return true;
            source = source.Parent as Avalonia.Visual;
        }
        return false;
    }

    public void UpdateVisibility()
    {
        if (_vm.WindowVisible) Show(); else Hide();
        _bgFx.SetPlaying(_vm.IsPlaying);
        _bgFx.SetWindowVisible(IsVisible);
    }

    private void ApplyAnchor()
    {
        var s = _settingsService.Current;
        if (s.AnchorX.HasValue && s.AnchorY.HasValue)
            _anchor = new PixelPoint((int)s.AnchorX.Value, (int)s.AnchorY.Value);
        else
        {
            var area = Screens.ScreenFromWindow(this)?.WorkingArea
                       ?? Screens.Primary?.WorkingArea
                       ?? new PixelRect(100, 100, 1720, 880);
            _anchor = new PixelPoint(area.X + area.Width / 2, (int)(area.Y + area.Height * 0.85));
        }
        ClampAnchorToScreens();
        // 不立即定位：初始布局阶段尺寸巨大/振荡，立即定位会把窗口推到屏幕外。
        // 待尺寸稳定且合理后再以锚点定位一次。
        ScheduleInitialAnchorApply();
    }

    /// <summary>初始阶段尺寸"合理"的高度上限（正常内容高度远小于此；>此说明处于瞬时巨大尺寸）。</summary>
    private static readonly double InitialHeightThreshold = 300;

    /// <summary>
    /// 延迟到初始尺寸稳定（连续若干毫秒不再变化）后再以锚点定位。
    /// 尺寸仍处于瞬时巨大值时先不安排（等下一次变化再判断），避免用巨大尺寸定位。
    /// </summary>
    private void ScheduleInitialAnchorApply()
    {
        if (ClientSize.Height > InitialHeightThreshold || ClientSize.Width > 2000)
            return;
        _initialAnchorDebouncer.Schedule(TimeSpan.FromMilliseconds(200), ApplyInitialAnchor);
    }

    /// <summary>初始定位：以当前（稳定后的）尺寸按锚点居中放置窗口，并同步锚点。</summary>
    private void ApplyInitialAnchor()
    {
        if (_firstSizeApplied)
            return;
        _firstSizeApplied = true;
        RepositionToAnchor();
        WriteAnchorFromPosition();
    }

    private void RepositionToAnchor()
    {
        if (!IsInitialized) return;
        var s = Screens.ScreenFromWindow(this)?.Scaling ?? 1.0;
        var pw = ClientSize.Width * s;
        var ph = ClientSize.Height * s;
        _suppressPositionUpdate = true;
        Position = new PixelPoint((int)(_anchor.X - pw / 2), (int)(_anchor.Y - ph / 2));
        _suppressPositionUpdate = false;
        _lastGrowBottom = Position.Y + (int)Math.Round(ClientSize.Height * s);
        Log.Info($"trc RepositionToAnchor anchor=({_anchor.X},{_anchor.Y}) client={ClientSize.Width:F1}x{ClientSize.Height:F1} s={s:F3} pos={Position.X},{Position.Y}");
    }

    private void WriteAnchorFromPosition()
    {
        if (ClientSize.Width <= 0 || ClientSize.Height <= 0)
            return;
        var s = Screens.ScreenFromWindow(this)?.Scaling ?? 1.0;
        _anchor = new PixelPoint(
            (int)(Position.X + ClientSize.Width * s / 2),
            (int)(Position.Y + ClientSize.Height * s / 2));
        _lastGrowBottom = Position.Y + (int)Math.Round(ClientSize.Height * s);
        var ax = _anchor.X; var ay = _anchor.Y;
        _anchorDebouncer.Schedule(TimeSpan.FromMilliseconds(500), () => AnchorChanged?.Invoke(ax, ay));
    }

    /// <summary>退出前立即持久化当前锚点（绕过防抖），保证下次启动位置与退出时一致。</summary>
    public void PersistAnchor()
    {
        if (ClientSize.Width <= 0 || ClientSize.Height <= 0)
            return;
        var s = Screens.ScreenFromWindow(this)?.Scaling ?? 1.0;
        _anchor = new PixelPoint(
            (int)(Position.X + ClientSize.Width * s / 2),
            (int)(Position.Y + ClientSize.Height * s / 2));
        AnchorChanged?.Invoke(_anchor.X, _anchor.Y);
    }

    private void ClampAnchorToScreens()
    {
        int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
        foreach (var scr in Screens.All) { var b = scr.Bounds; if (b.X < minX) minX = b.X; if (b.Y < minY) minY = b.Y; if (b.Right > maxX) maxX = b.Right; if (b.Bottom > maxY) maxY = b.Bottom; }
        if (minX >= maxX) return;
        _anchor = new PixelPoint(Math.Clamp(_anchor.X, minX + 80, maxX - 80), Math.Clamp(_anchor.Y, minY + 80, maxY - 80));
    }

    public void SetAnchor(double x, double y)
    {
        _anchor = new PixelPoint((int)Math.Round(x), (int)Math.Round(y));
        RepositionToAnchor();
        _anchorDebouncer.Cancel();
    }

    public void SnapToPreset(PositionPreset preset)
    {
        var area = Screens.ScreenFromWindow(this)?.WorkingArea
                   ?? Screens.Primary?.WorkingArea
                   ?? new PixelRect(100, 100, 1720, 880);
        int cx = area.X + area.Width / 2;
        int cy = area.Y + area.Height / 2;
        var m = 80;
        int x = preset switch
        {
            PositionPreset.TopLeft or PositionPreset.MiddleLeft or PositionPreset.BottomLeft => area.X + m,
            PositionPreset.TopCenter or PositionPreset.Center or PositionPreset.BottomCenter => cx,
            _ => area.X + area.Width - m,
        };
        int y = preset switch
        {
            PositionPreset.TopLeft or PositionPreset.TopCenter or PositionPreset.TopRight => area.Y + m,
            PositionPreset.MiddleLeft or PositionPreset.Center or PositionPreset.MiddleRight => cy,
            _ => area.Y + area.Height - m,
        };
        _anchor = new PixelPoint(x, y);
        RepositionToAnchor();
        _anchorDebouncer.Cancel();
        AnchorChanged?.Invoke(x, y);
    }
}
