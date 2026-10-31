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
    private LyricText _transTb => _lyricVisualMode.TranslationText;
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
    private Control? _crossfadeMain;
    private Control? _crossfadeTrans;

    // 超长歌词跑马灯滚动状态
    private double _scrollOverflow;
    private double _currentScrollX;

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

        LayoutUpdated += (_, _) =>
        {
            SyncWindowHeight();
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
            if (_coverAnimating && _coverPosDirty)
            {
                _coverPosDirty = false;
                UpdateCoverTitlePosition();
            }
            if (_lineAnimKind == "Shine" && _shineLayer != null)
                PositionShineLayer();
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

        RootPanel.SizeChanged += (_, _) =>
        {
            UpdateCoverSize();
            UpdateCoverPosition();
            RecalculateScrollOverflow();
        };
        _vm.PropertyChanged += OnVmChanged;
        _vm.LineChanged += OnLineChanged;
        SizeChanged += OnSizeChanged;
    }

    /// <summary>hover 显示播放控制条（内容下方展开）；动画期间不显示（避免窗口高度变化干扰）。</summary>
    private void SetControlsVisible(bool visible)
    {
        if (visible && _coverAnimating)
            return;
        ControlBar.Height = visible ? 34 : 0;
        ControlBar.Opacity = visible ? 1 : 0;
        ControlBar.IsHitTestVisible = visible;
    }

    private double ContentHeight()
    {
        var h = _mainGrid.Bounds.Height;
        if (_transGrid.Bounds.Height > 0)
            h += _transGrid.Bounds.Height + _vm.LineSpacing;
        h = Math.Max(h, Cover.Bounds.Height);
        if (_coverLayoutExpanded)
            h = Math.Max(h, _coverAnimSize);
        if (_coverAnimating && CoverTitleBox.IsVisible && CoverTitleBox.Bounds.Height > 0)
            h = Math.Max(h, _coverAnimSize + 8 + CoverTitleBox.Bounds.Height);
        return h + ControlBar.Height + 12;
    }

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
        PlayPauseIcon.Text = _vm.IsPlaying ? "\uE769" : "\uE768";
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
            RecalculateScrollOverflow();
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
            if (!_coverAnimating)
                LyricsArea.MinWidth = _vm.MaxTextWidth;
            RecalculateScrollOverflow();
        }
        else if (e.PropertyName == nameof(OverlayViewModel.CoverEnabled))
        {
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
        else if (e.PropertyName is nameof(OverlayViewModel.WordHighlightLength)
                 or nameof(OverlayViewModel.WordHighlightFraction))
        {
            // 逐字进度变化时，平滑驱动超长歌词横向滚动
            UpdateMarqueeScroll();
        }
        else if (e.PropertyName == nameof(OverlayViewModel.CoverTitleEnabled))
        {
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

    // ---------- 跑马灯滚动逻辑 ----------

    /// <summary>
    /// 测量当前歌词文字是否超过了容器可视区宽度。超长时计算需要向左滚动的溢出距离。
    /// </summary>
    private void RecalculateScrollOverflow()
    {
        _mainGrid.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var textW = _mainGrid.DesiredSize.Width;
        var maxW = _vm.MaxTextWidth;

        _scrollOverflow = Math.Max(0, textW - maxW);
        if (_scrollOverflow <= 0)
        {
            ApplyScrollOffset(0);
        }
    }

    /// <summary>
    /// 根据当前行的逐字点亮进度（0.0 ~ 1.0）驱动超长歌词平滑向左滚动。
    /// 前 15% 保持静止看句首，中间 70% 匀速滚动，后 15% 停留看句尾。
    /// </summary>
    private void UpdateMarqueeScroll()
    {
        if (_scrollOverflow <= 0)
            return;

        var totalChars = _vm.MainText.Length;
        if (totalChars <= 0)
            return;

        // 计算当前句进度 (0.0 ~ 1.0)
        double p;
        if (_vm.WordHighlightLength < 0)
        {
            p = 0; // 非逐字模式保持首部
        }
        else
        {
            var rawProgress = _vm.WordHighlightLength + _vm.WordHighlightFraction;
            p = Math.Clamp(rawProgress / totalChars, 0.0, 1.0);
        }

        // 两端留白窗口映射
        double scrollRatio;
        if (p <= 0.15)
            scrollRatio = 0.0;
        else if (p >= 0.85)
            scrollRatio = 1.0;
        else
            scrollRatio = (p - 0.15) / 0.70;

        var targetOffsetX = -(_scrollOverflow * scrollRatio);
        ApplyScrollOffset(targetOffsetX);
    }

    private void ApplyScrollOffset(double offsetX)
    {
        _currentScrollX = offsetX;
        var trans = Translate(offsetX, 0);
        RootPanel.RenderTransform = trans;
        if (_lineAnimKind == "Shine" && _shineLayer != null)
            PositionShineLayer();
    }

    // ---------- 行间切换动效 ----------

    private DateTimeOffset _lineTransitionUntil;

    private void OnLineChanged(bool suppress)
    {
        // 换行时复位滚动偏移
        ApplyScrollOffset(0);
        RecalculateScrollOverflow();

        if (!_settingsService.Current.LineTransitionEnabled)
        {
            ClearExitOverlays();
            SyncLastLineText();
            return;
        }
        if (suppress || _coverAnimating || !IsVisible)
        {
            ClearExitOverlays();
            ResetLineTransitionState();
            SyncLastLineText();
            return;
        }

        if (_lastMainText == _vm.MainText && _lastTransText == _vm.TransText)
        {
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

    private void SyncLastLineText()
    {
        _lastMainText = _vm.MainText;
        _lastTransText = _vm.TransText;
    }

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
            old.HorizontalAlignment = HorizontalAlignment.Left;
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

    private Point ExitBasePoint(Control c, Control verticalAnchor)
    {
        var p = verticalAnchor.TranslatePoint(new Point(0, 0), LyricsArea) ?? new Point(0, 0);
        var cx = LyricsArea.Bounds.Width / 2;
        var w = c.DesiredSize.Width > 0 ? c.DesiredSize.Width : c.Bounds.Width;
        return new Point(cx - w / 2, p.Y);
    }

    private static TransformOperations ExitTransform(Point t, double scale)
    {
        var b = new TransformOperations.Builder(2);
        b.AppendTranslate(t.X, t.Y);
        b.AppendScale(scale, scale);
        return b.Build();
    }

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
        if (_crossfadeMain != null)
        {
            _mainGrid.Children.Remove(_crossfadeMain);
            _crossfadeMain = null;
        }
        if (_crossfadeTrans != null)
        {
            _transGrid.Children.Remove(_crossfadeTrans);
            _crossfadeTrans = null;
        }
    }

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

        if (s.LineTransitionType == "Slide" && s.LineTransitionSlideBlurEnabled)
            StartSlideBlur(durMs);
    }

    private void StartSlideBlur(int durMs)
    {
        var radius = Math.Clamp(_settingsService.Current.LineTransitionSlideBlurRadius, 0, 20);
        if (radius <= 0)
            return;
        _mainGrid.Effect = new BlurEffect { Radius = radius };
        _transGrid.Effect = new BlurEffect { Radius = radius };

        StartLineProgressAnimation("SlideBlur", durMs);
    }

    private void ClearSlideBlur()
    {
        _mainGrid.Effect = null;
        _transGrid.Effect = null;
    }

    private void PlayCrossfade(int durMs)
    {
        var s = _settingsService.Current;
        var dur = TimeSpan.FromMilliseconds(durMs);

        _mainGrid.Opacity = 1;
        _transGrid.Opacity = 1;

        if (_lastMainText.Length > 0)
        {
            var old = CreateExitLyric(_lastMainText);
            _mainGrid.Children.Add(old);
            _crossfadeMain = old;
            FadeOutAndRemove(_mainGrid, old, dur, () =>
            {
                if (ReferenceEquals(_crossfadeMain, old))
                    _crossfadeMain = null;
            });
        }
        if (s.ShowTranslation && _lastTransText.Length > 0)
        {
            var oldT = CreateExitTranslation(_lastTransText);
            _transGrid.Children.Add(oldT);
            _crossfadeTrans = oldT;
            FadeOutAndRemove(_transGrid, oldT, dur, () =>
            {
                if (ReferenceEquals(_crossfadeTrans, oldT))
                    _crossfadeTrans = null;
            });
        }
    }

    private TextAlignment LineAlignmentValue =>
        (TextAlignment)Enum.Parse(typeof(TextAlignment), _settingsService.Current.Alignment);

    private HorizontalAlignment LineHorizontalAlignment => _settingsService.Current.Alignment switch
    {
        "Left" => HorizontalAlignment.Left,
        "Right" => HorizontalAlignment.Right,
        _ => HorizontalAlignment.Center,
    };

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

    private LyricText CreateExitTranslation(string text)
    {
        return new LyricText
        {
            Text = text,
            FontFamily = _transTb.FontFamily,
            FontWeight = _transTb.FontWeight,
            FontSize = _transTb.FontSize,
            TextAlignment = LineAlignmentValue,
            HorizontalAlignment = LineHorizontalAlignment,
            Fill = _transTb.Fill,
            StrokeEnabled = _transTb.StrokeEnabled,
            StrokeBrush = _transTb.StrokeBrush,
            StrokeThickness = _transTb.StrokeThickness,
            Effect = _transTb.Effect,
            HighlightLength = -1,
            HighlightClip = LyricText.ClipSideMode.None,
            Opacity = 1,
        };
    }

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

    private void PositionShineLayer()
    {
        if (_shineLayer == null)
            return;
        var p = _mainGrid.TranslatePoint(new Point(0, 0), LyricsArea) ?? new Point(0, 0);
        _shineLayer.RenderTransform = Translate(p.X + _currentScrollX, p.Y);
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

    private static (double dx, double dy) LineSlideOffset(int dir, double dist)
    {
        var d = Math.Clamp(dist, 0, 60);
        return dir switch
        {
            1 => (0, -d),
            2 => (0, d),
            3 => (-d, 0),
            4 => (d, 0),
            _ => (0, 0),
        };
    }

    private static TransformOperations Scale(double sx, double sy)
    {
        var b = new TransformOperations.Builder(1);
        b.AppendScale(sx, sy);
        return b.Build();
    }

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

    private void OnLineTransitionSettingsChanged()
    {
        if (!_settingsService.Current.LineTransitionEnabled)
            ResetLineTransitionState();
    }

    // ---------- 封面 ----------

    private double _coverSize;

    private static TransformOperations Translate(double x, double y)
    {
        var b = new TransformOperations.Builder(1);
        b.AppendTranslate(x, y);
        return b.Build();
    }

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

    private static readonly TimeSpan CoverAnimTimeout = TimeSpan.FromSeconds(6);

    private void OnCoverChanged()
    {
        ApplyCoverLayout();
        UpdateCoverSize();
        var animate = _vm.CoverCutAnimation && _vm.ConsumeCutAnimationFlag();
        Log.Info($"cover: track={_vm.CoverTrackKey} image={(_vm.CoverImage != null)} animate={animate} enabled={_vm.CoverEnabled}");

        if (_vm.CoverImage == null)
        {
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

        _coverAnimating = false;
        _coverLayoutExpanded = false;
        ExitCoverTitle();
        ApplyCoverRestingState(immediate: true);
        SyncWindowHeight();
    }

    private void OnLyricsPhaseChanged()
    {
        if (_vm.Phase is LyricsPhase.Resolving or LyricsPhase.NoSession)
        {
            ClearExitOverlays();
            CancelLineAnimation();
            _lastMainText = "";
            _lastTransText = "";
            ApplyScrollOffset(0);
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
        ExitCoverTitle();
        _coverPlan = CoverAnimationPlan.Create(
            OuterGrid.Bounds.Width, _coverSize, _coverAnimSize, CoverSlotOffset(),
            _settingsService.Current.CoverAnimDirection,
            _settingsService.Current.CoverAnimSlideDistance);
        if (_vm.CoverEnabled)
        {
            ApplyCoverVisual(_coverPlan.PersistentEnd);
        }
        else
        {
            ApplyCoverVisual(_coverPlan.TransientEnd);
        }

        _coverStageTimer.Interval = TimeSpan.FromMilliseconds(
            Math.Clamp(_settingsService.Current.CoverAnimDurationMs, 100, 2000) + 50);
        _coverStageTimer.Start();
    }

    private void EnterCoverTitle()
    {
        if (!_vm.CoverTitleEnabled)
            return;
        CoverTitleBox.IsVisible = true;
        _coverTitleEnterPending = true;
    }

    private void ExitCoverTitle()
    {
        var (dx, dy) = CoverTitleSlideOffset();
        UpdateCoverTitlePosition(dx, dy);
        CoverTitleBox.Opacity = 0;
        _ = Task.Run(async () =>
        {
            await Task.Delay(450);
            Dispatcher.UIThread.Post(() =>
            {
                if (_coverAnimating)
                    return;
                CoverTitleBox.IsVisible = false;
            });
        });
    }

    private (double dx, double dy) CoverTitleSlideOffset()
    {
        var dir = _vm.CoverTitleAnimDirection;
        var d = Math.Clamp(_settingsService.Current.CoverTitleSlideDistance, 0, 60);
        return dir switch
        {
            1 => (0, -d),
            2 => (0, d),
            3 => (-d, 0),
            4 => (d, 0),
            _ => (0, 0),
        };
    }

    private void UpdateCoverTitlePosition(double dx = 0, double dy = 0)
    {
        if (!CoverTitleBox.IsVisible)
            return;
        const double gap = 8;
        var y = (_coverAnimating ? _coverAnimSize : Cover.Bounds.Height) + gap;
        CoverTitleBox.RenderTransform = Translate(dx, y + dy);
    }

    // 将原本设置 Viewbox 透明度适配为 Border 视口
    private void SetLyricsOpacity(double opacity) => LyricsViewport.Opacity = opacity;

    private double AnimCoverSize()
    {
        var h = _mainGrid.Bounds.Height;
        if (h <= 0)
            h = 40;
        return Math.Max(h * 3, _coverSize);
    }

    private Point CoverSlotOffset()
    {
        var p = CoverSlot.TranslatePoint(new Point(0, 0), OuterGrid) ?? default;
        return new Point(Math.Max(0, p.X), Math.Max(0, p.Y));
    }

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

    private void ApplyCoverVisual(CoverVisualState state)
    {
        var b = new TransformOperations.Builder(2);
        b.AppendScale(state.Scale, state.Scale);
        b.AppendTranslate(state.X, state.Y);
        Cover.RenderTransform = b.Build();
        Cover.Opacity = state.Opacity;
    }

    private void UpdateCoverSize()
    {
        if (_coverAnimating)
            return;
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
        Win32.HideFromAltTab(_hwnd);
        UpdatePlayPauseIcon();
        
        UpdateVisibility();
        ApplyAnchor();
        
        LogSizeDiagnostics();
        ScheduleSizeDiagnostics();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
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
        Log.Debug($"trc OnSizeChanged client={e.NewSize.Width:F1}x{e.NewSize.Height:F1} old={e.PreviousSize.Width:F1}x{e.PreviousSize.Height:F1} wc={e.WidthChanged} hc={e.HeightChanged} pos={Position.X},{Position.Y} first={_firstSizeApplied} grow={_settingsService.Current.HeightGrowMode}");
        if (!_suppressPositionUpdate && ClientSize.Width > 0 && ClientSize.Height > 0)
        {
            var grow = _settingsService.Current.HeightGrowMode;
            if (!_firstSizeApplied)
            {
                ScheduleInitialAnchorApply();
            }
            else
            {
                RepositionForSizeChange(e, grow);
            }
        }
        var scale = Screens.ScreenFromWindow(this)?.Scaling ?? 1.0;
        _lastGrowBottom = Position.Y + (int)Math.Round(ClientSize.Height * scale);
        LogSizeDiagnostics();
    }

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
        Log.Debug($"trc RepositionForSizeChange grow={grow} anchor=({_anchor.X},{_anchor.Y}) " +
                 $"client={ClientSize.Width:F1}x{ClientSize.Height:F1} s={s:F3} pos={Position.X},{Position.Y}");
    }

    private DateTimeOffset _lastSizeDiag = DateTimeOffset.MinValue;

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
        Log.Debug($"diag: client={ClientSize.Width:F1}x{ClientSize.Height:F1} " +
                 $"screenScale={screenScale:F3} renderScale={renderScale:F3} " +
                 $"expectByScreen={expectByScreen.Item1:F0}x{expectByScreen.Item2:F0} " +
                 $"expectByRender={expectByRender.Item1:F0}x{expectByRender.Item2:F0} " +
                 $"winPhys={win.W}x{win.H} err={win.Err} clientPhys={client.W}x{client.H} err={client.Err} " +
                 $"minW={MinWidth:F0} maxW={(double.IsPositiveInfinity(MaxWidth) ? -1 : MaxWidth):F0}");
    }

    private void ScheduleSizeDiagnostics()
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(800);
            Dispatcher.UIThread.Post(LogSizeDiagnostics);
        });
    }

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
        ScheduleInitialAnchorApply();
    }

    private static readonly double InitialHeightThreshold = 300;

    private void ScheduleInitialAnchorApply()
    {
        if (ClientSize.Height < 10 || ClientSize.Height > InitialHeightThreshold || ClientSize.Width > 2000)
            return;

        _initialAnchorDebouncer.Schedule(TimeSpan.FromMilliseconds(150), ApplyInitialAnchor);
    }

    private void ApplyInitialAnchor()
    {
        if (_firstSizeApplied)
            return;

        if (ClientSize.Height < 20 || !IsVisible)
        {
            _initialAnchorDebouncer.Schedule(TimeSpan.FromMilliseconds(100), ApplyInitialAnchor);
            return;
        }

        _firstSizeApplied = true;
        RepositionToAnchor();

        var s = Screens.ScreenFromWindow(this)?.Scaling ?? 1.0;
        _lastGrowBottom = Position.Y + (int)Math.Round(ClientSize.Height * s);
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
        Log.Debug($"trc RepositionToAnchor anchor=({_anchor.X},{_anchor.Y}) client={ClientSize.Width:F1}x{ClientSize.Height:F1} s={s:F3} pos={Position.X},{Position.Y}");
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
