using System;
using System.Collections.Generic;
using Android.AccessibilityServices;
using Android.Animation;
using Android.App;
using Android.Content;
using Android.Content.Res;
using Android.Graphics;
using Android.Hardware.Display;
using Android.OS;
using Android.Util;
using Android.Views;
using Android.Views.Accessibility;
using Android.Views.Animations;

namespace Infiniroll;

[Service(
    Label = "@string/accessibility_service_name",
    Permission = "android.permission.BIND_ACCESSIBILITY_SERVICE",
    Exported = true)]
[IntentFilter(new[] { "android.accessibilityservice.AccessibilityService" })]
[MetaData("android.accessibilityservice", Resource = "@xml/accessibility_service_config")]
public class WheelOverlayService : AccessibilityService
{
    public static bool IsRunning { get; private set; }
    public static event System.Action? ServiceStateChanged;

    private IWindowManager? _windowManager;
    private DisplayManager? _displayManager;
    private OverlayDisplayListener? _displayListener;
    private Handler? _mainHandler;
    private Java.Lang.IRunnable? _delayedGeometryCheckRunnable;
    private Java.Lang.IRunnable? _postGeometrySettleRunnable;
    private WheelOverlayView? _overlayView;
    private WindowManagerLayoutParams? _layoutParams;
    private AppSettings? _settings;

    private float _density = 1.0f;
    private int _screenWidth;
    private int _screenHeight;
    private int _lastCheckedScreenWidth;
    private int _lastCheckedScreenHeight;
    private SurfaceOrientation _lastCheckedRotation;

    private ValueAnimator? _pullInAnimator;
    private int _pullInTargetX;
    private int _pullInTargetY;

    public override void OnCreate()
    {
        base.OnCreate();
        Log.Info("Infiniroll", "WheelOverlayService.OnCreate called");
        _settings = new AppSettings(this);
        _mainHandler = new Handler(Looper.MainLooper!);
        _delayedGeometryCheckRunnable = new Java.Lang.Runnable(CheckDisplayGeometryChanged);
        _postGeometrySettleRunnable = new Java.Lang.Runnable(() => EnsureWheelInsideScreenBounds(animate: true));
        
        var rawWm = GetSystemService(WindowService);
        Log.Info("Infiniroll", $"rawWm type: {rawWm?.GetType().FullName}");

        if (rawWm is IWindowManager wm)
        {
            _windowManager = wm;
        }
        else if (rawWm != null)
        {
            try
            {
                _windowManager = Android.Runtime.Extensions.JavaCast<IWindowManager>(rawWm);
                Log.Info("Infiniroll", $"JavaCast success: {_windowManager != null}");
            }
            catch (Exception ex)
            {
                Log.Error("Infiniroll", $"JavaCast failed: {ex.Message}");
            }
        }

        var rawDm = GetSystemService(DisplayService);
        if (rawDm is DisplayManager dm)
        {
            _displayManager = dm;
        }
        else if (rawDm != null)
        {
            try
            {
                _displayManager = Android.Runtime.Extensions.JavaCast<DisplayManager>(rawDm);
            }
            catch (Exception ex)
            {
                Log.Warn("Infiniroll", $"DisplayManager JavaCast failed: {ex.Message}");
            }
        }

        if (_displayManager != null)
        {
            _displayListener = new OverlayDisplayListener(this);
            _displayManager.RegisterDisplayListener(_displayListener, _mainHandler);
        }

        UpdateDisplayMetrics();
        _lastCheckedScreenWidth = _screenWidth;
        _lastCheckedScreenHeight = _screenHeight;
        _lastCheckedRotation = GetCurrentRotation();

        AppSettings.PositionResetRequested += OnPositionResetRequested;
        AppSettings.SettingsChanged += OnSettingsChanged;
    }

    public override void OnConfigurationChanged(Configuration? newConfig)
    {
        base.OnConfigurationChanged(newConfig);
        OnDisplayOrConfigChanged();
    }

    protected override void OnServiceConnected()
    {
        base.OnServiceConnected();
        Log.Info("Infiniroll", "WheelOverlayService.OnServiceConnected called");
        IsRunning = true;
        ServiceStateChanged?.Invoke();

        InitOverlayView();
    }

    public override void OnAccessibilityEvent(AccessibilityEvent? e)
    {
        // 論理スクロールの操作はオーバーレイのタッチイベントで自律的に行われるため、
        // システムイベントの受信用としてはパッシブに待機
    }

    public override void OnInterrupt()
    {
    }

    public override bool OnUnbind(Intent? intent)
    {
        RemoveOverlayView();
        IsRunning = false;
        ServiceStateChanged?.Invoke();
        return base.OnUnbind(intent);
    }

    public override void OnDestroy()
    {
        if (_mainHandler != null)
        {
            if (_delayedGeometryCheckRunnable != null)
            {
                _mainHandler.RemoveCallbacks(_delayedGeometryCheckRunnable);
            }
            if (_postGeometrySettleRunnable != null)
            {
                _mainHandler.RemoveCallbacks(_postGeometrySettleRunnable);
            }
        }

        if (_displayManager != null && _displayListener != null)
        {
            try
            {
                _displayManager.UnregisterDisplayListener(_displayListener);
            }
            catch
            {
                // Ignore unregister errors
            }
            _displayListener = null;
        }

        RemoveOverlayView();
        AppSettings.PositionResetRequested -= OnPositionResetRequested;
        AppSettings.SettingsChanged -= OnSettingsChanged;

        IsRunning = false;
        ServiceStateChanged?.Invoke();
        base.OnDestroy();
    }

    private SurfaceOrientation GetCurrentRotation()
    {
        try
        {
            var display = _displayManager?.GetDisplay(Display.DefaultDisplay);
            if (display != null)
            {
                return display.Rotation;
            }
        }
        catch
        {
            // Ignore display query errors
        }
        return SurfaceOrientation.Rotation0;
    }

    private void UpdateDisplayMetrics()
    {
        var resMetrics = Resources?.DisplayMetrics;
        if (resMetrics != null && resMetrics.Density > 0)
        {
            _density = resMetrics.Density;
        }

        if (OperatingSystem.IsAndroidVersionAtLeast(30) && _windowManager != null)
        {
            try
            {
                var maxMetrics = _windowManager.MaximumWindowMetrics;
                var bounds = maxMetrics?.Bounds;
                if (bounds != null && bounds.Width() > 0 && bounds.Height() > 0)
                {
                    _screenWidth = bounds.Width();
                    _screenHeight = bounds.Height();
                    return;
                }
            }
            catch (Exception ex)
            {
                Log.Warn("Infiniroll", $"MaximumWindowMetrics fallback: {ex.Message}");
            }
        }

        var display = _displayManager?.GetDisplay(Display.DefaultDisplay);
        if (display != null)
        {
            try
            {
                using var realMetrics = new DisplayMetrics();
#pragma warning disable CA1422
                display.GetRealMetrics(realMetrics);
#pragma warning restore CA1422
                if (realMetrics.WidthPixels > 0 && realMetrics.HeightPixels > 0)
                {
                    _screenWidth = realMetrics.WidthPixels;
                    _screenHeight = realMetrics.HeightPixels;
                    if (realMetrics.Density > 0)
                    {
                        _density = realMetrics.Density;
                    }
                    return;
                }
            }
            catch (Exception ex)
            {
                Log.Warn("Infiniroll", $"GetRealMetrics fallback: {ex.Message}");
            }
        }

        if (resMetrics != null)
        {
            _screenWidth = resMetrics.WidthPixels;
            _screenHeight = resMetrics.HeightPixels;
        }
    }

    private (int Left, int Top, int Right, int Bottom) GetSafeScreenInsets()
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(30))
        {
            try
            {
                WindowInsets? windowInsets = _overlayView?.RootWindowInsets
                    ?? _windowManager?.CurrentWindowMetrics?.WindowInsets;
                if (windowInsets != null)
                {
                    int mask = WindowInsets.Type.SystemBars() | WindowInsets.Type.DisplayCutout();
                    var insets = windowInsets.GetInsetsIgnoringVisibility(mask);
                    if (insets != null)
                    {
                        return (insets.Left, insets.Top, insets.Right, insets.Bottom);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn("Infiniroll", $"GetSafeScreenInsets fallback: {ex.Message}");
            }
        }

        return (0, (int)(24f * _density), 0, (int)(16f * _density));
    }

    private void OnDisplayOrConfigChanged()
    {
        CheckDisplayGeometryChanged();
        if (_mainHandler != null && _delayedGeometryCheckRunnable != null)
        {
            _mainHandler.RemoveCallbacks(_delayedGeometryCheckRunnable);
            _mainHandler.PostDelayed(_delayedGeometryCheckRunnable, 150);
        }
    }

    private void CheckDisplayGeometryChanged()
    {
        if (_overlayView == null || _layoutParams == null) return;

        UpdateDisplayMetrics();
        SurfaceOrientation currentRotation = GetCurrentRotation();

        if (_screenWidth == _lastCheckedScreenWidth &&
            _screenHeight == _lastCheckedScreenHeight &&
            currentRotation == _lastCheckedRotation)
        {
            return;
        }

        _lastCheckedScreenWidth = _screenWidth;
        _lastCheckedScreenHeight = _screenHeight;
        _lastCheckedRotation = currentRotation;

        EnsureWheelInsideScreenBounds(animate: true);

        // ローテーション直後にシステムバーやノッチの WindowInsets が遅れて確定するケースに備えて再確認
        if (_mainHandler != null && _postGeometrySettleRunnable != null)
        {
            _mainHandler.RemoveCallbacks(_postGeometrySettleRunnable);
            _mainHandler.PostDelayed(_postGeometrySettleRunnable, 200);
        }
    }

    private static int ResolvePullInTarget(
        int currentPos,
        int itemSpan,
        int screenSpan,
        int safeMin,
        int safeMax)
    {
        if (itemSpan <= 0 || screenSpan <= 0) return currentPos;

        int visibleStart = Math.Max(0, currentPos);
        int visibleEnd = Math.Min(screenSpan, currentPos + itemSpan);
        int visibleSpan = Math.Max(0, visibleEnd - visibleStart);
        int cutOffSpan = itemSpan - visibleSpan;

        // 半分未満の見切れ（手動で少し端に寄せて見切れさせている運用）はそのまま維持する
        if ((cutOffSpan * 2) < itemSpan)
        {
            return currentPos;
        }

        // 半分以上見切れている（または完全に枠外へ出ている）場合は該当軸を画面枠内へ引き入れる
        int clampedMin = safeMin;
        int clampedMax = safeMax;
        if (clampedMax < clampedMin)
        {
            clampedMin = 0;
            clampedMax = Math.Max(0, screenSpan - itemSpan);
        }

        float itemCenter = currentPos + (itemSpan / 2f);
        float screenCenter = screenSpan / 2f;
        return itemCenter <= screenCenter ? clampedMin : clampedMax;
    }

    private void CancelPullInAnimation()
    {
        if (_pullInAnimator != null)
        {
            var animator = _pullInAnimator;
            _pullInAnimator = null;
            try
            {
                if (animator.IsRunning)
                {
                    animator.Cancel();
                }
            }
            catch
            {
                // Ignore animator cancel errors
            }
        }
    }

    private void EnsureWheelInsideScreenBounds(bool animate)
    {
        if (_layoutParams == null || _windowManager == null || _overlayView == null || _settings == null) return;

        UpdateDisplayMetrics();
        if (_screenWidth <= 0 || _screenHeight <= 0) return;

        int sizePx = (int)(_settings.WheelSizeDp * _density);
        int extraLeftPx = GetExpectedExtraLeftPx();
        int totalWidthPx = sizePx + extraLeftPx;
        int tabHeightPx = (int)(24f * _density);
        int totalHeightPx = sizePx + tabHeightPx;

        bool sizeChanged = _layoutParams.Width != totalWidthPx ||
                           _layoutParams.Height != totalHeightPx ||
                           _appliedExtraLeftPx != extraLeftPx;
        if (sizeChanged)
        {
            _appliedExtraLeftPx = extraLeftPx;
            _layoutParams.Width = totalWidthPx;
            _layoutParams.Height = totalHeightPx;
        }

        var (insetLeft, insetTop, insetRight, insetBottom) = GetSafeScreenInsets();
        int edgeMarginPx = (int)(8f * _density);

        int minSafeX = insetLeft + edgeMarginPx;
        int maxSafeX = _screenWidth - insetRight - totalWidthPx - edgeMarginPx;
        int minSafeY = insetTop + edgeMarginPx;
        int maxSafeY = _screenHeight - insetBottom - totalHeightPx - edgeMarginPx;

        bool animRunning = _pullInAnimator != null && _pullInAnimator.IsRunning;
        int baseX = animRunning ? _pullInTargetX : _layoutParams.X;
        int baseY = animRunning ? _pullInTargetY : _layoutParams.Y;

        int targetX = ResolvePullInTarget(baseX, totalWidthPx, _screenWidth, minSafeX, maxSafeX);
        int targetY = ResolvePullInTarget(baseY, totalHeightPx, _screenHeight, minSafeY, maxSafeY);

        if (animRunning && targetX == _pullInTargetX && targetY == _pullInTargetY && !sizeChanged)
        {
            return;
        }

        int currentX = _layoutParams.X;
        int currentY = _layoutParams.Y;

        if (targetX == currentX && targetY == currentY)
        {
            if (sizeChanged)
            {
                _windowManager.UpdateViewLayout(_overlayView, _layoutParams);
                _overlayView.RequestLayout();
                _overlayView.Invalidate();
            }
            return;
        }

        CancelPullInAnimation();

        _settings.WheelPosX = targetX;
        _settings.WheelPosY = targetY;

        if (!animate)
        {
            _layoutParams.X = targetX;
            _layoutParams.Y = targetY;
            _windowManager.UpdateViewLayout(_overlayView, _layoutParams);
            if (sizeChanged)
            {
                _overlayView.RequestLayout();
                _overlayView.Invalidate();
            }
            return;
        }

        // 引き入れ動作が見やすいように一時的にフェードインさせる
        _overlayView.WakeUpTemporarily();

        if (sizeChanged)
        {
            _overlayView.RequestLayout();
            _overlayView.Invalidate();
        }

        // 画面のはるか遠く（完全な枠外）にある場合は、その方向の画面端すぐ外側からスッと枠内へ引き入れる
        int peekPx = (int)(16f * _density);
        int animStartX = currentX;
        if (currentX > targetX)
        {
            animStartX = Math.Min(currentX, Math.Max(targetX, _screenWidth - peekPx));
        }
        else if (currentX < targetX)
        {
            animStartX = Math.Max(currentX, Math.Min(targetX, -totalWidthPx + peekPx));
        }

        int animStartY = currentY;
        if (currentY > targetY)
        {
            animStartY = Math.Min(currentY, Math.Max(targetY, _screenHeight - peekPx));
        }
        else if (currentY < targetY)
        {
            animStartY = Math.Max(currentY, Math.Min(targetY, -totalHeightPx + peekPx));
        }

        if (animStartX != currentX || animStartY != currentY)
        {
            _layoutParams.X = animStartX;
            _layoutParams.Y = animStartY;
            _windowManager.UpdateViewLayout(_overlayView, _layoutParams);
        }
        else if (sizeChanged)
        {
            _windowManager.UpdateViewLayout(_overlayView, _layoutParams);
        }

        _pullInTargetX = targetX;
        _pullInTargetY = targetY;

        var animator = ValueAnimator.OfFloat(0f, 1f);
        if (animator == null)
        {
            _layoutParams.X = targetX;
            _layoutParams.Y = targetY;
            _windowManager.UpdateViewLayout(_overlayView, _layoutParams);
            return;
        }

        animator.SetDuration(300);
        animator.SetInterpolator(new DecelerateInterpolator(1.6f));
        animator.Update += (_, e) =>
        {
            if (_layoutParams == null || _windowManager == null || _overlayView == null) return;
            float t = (e.Animation?.AnimatedValue as Java.Lang.Number)?.FloatValue() ?? 1f;
            int nextX = (int)MathF.Round(animStartX + ((targetX - animStartX) * t));
            int nextY = (int)MathF.Round(animStartY + ((targetY - animStartY) * t));

            if (_layoutParams.X != nextX || _layoutParams.Y != nextY)
            {
                _layoutParams.X = nextX;
                _layoutParams.Y = nextY;
                try
                {
                    _windowManager.UpdateViewLayout(_overlayView, _layoutParams);
                }
                catch (Exception ex)
                {
                    Log.Warn("Infiniroll", $"Pull-in UpdateViewLayout failed: {ex.Message}");
                }
            }
        };
        animator.AnimationEnd += (_, _) =>
        {
            if (ReferenceEquals(_pullInAnimator, animator))
            {
                _pullInAnimator = null;
            }
        };

        _pullInAnimator = animator;
        animator.Start();
    }

    private sealed class OverlayDisplayListener : Java.Lang.Object, DisplayManager.IDisplayListener
    {
        private readonly WheelOverlayService _service;

        public OverlayDisplayListener(WheelOverlayService service)
        {
            _service = service;
        }

        public void OnDisplayAdded(int displayId)
        {
        }

        public void OnDisplayRemoved(int displayId)
        {
        }

        public void OnDisplayChanged(int displayId)
        {
            if (displayId == Display.DefaultDisplay)
            {
                _service.OnDisplayOrConfigChanged();
            }
        }
    }

    private float _resizeAnchorCenterX;
    private float _resizeAnchorCenterY;
    private int _appliedExtraLeftPx;

    private int GetExpectedExtraLeftPx()
    {
        return (_settings?.ShowBackButtonEnabled == true)
            ? (int)MathF.Ceiling(WheelOverlayView.BackButtonExtraLeftDp * _density)
            : 0;
    }

    private void InitOverlayView()
    {
        Log.Info("Infiniroll", $"InitOverlayView called. _windowManager={_windowManager != null}, _overlayView={_overlayView != null}");
        if (_windowManager == null || _settings == null || _overlayView != null) return;

        UpdateDisplayMetrics();
        _lastCheckedScreenWidth = _screenWidth;
        _lastCheckedScreenHeight = _screenHeight;
        _lastCheckedRotation = GetCurrentRotation();

        _overlayView = new WheelOverlayView(this, _settings);
        _overlayView.MoveRequested += OnWheelMoveRequested;
        _overlayView.ResizeStarted += OnWheelResizeStarted;
        _overlayView.ResizeRequested += OnWheelResizeRequested;
        _overlayView.BackRequested += OnWheelBackRequested;
        _overlayView.ScrollStarted += OnWheelScrollStarted;
        _overlayView.WheelMotionDetected += OnWheelMotionDetected;
        _overlayView.ScrollRequested += OnWheelScrollRequested;
        _overlayView.ScrollStopped += OnWheelScrollStopped;

        int sizePx = (int)(_settings.WheelSizeDp * _density);
        _appliedExtraLeftPx = GetExpectedExtraLeftPx();
        int totalWidthPx = sizePx + _appliedExtraLeftPx;
        int tabHeightPx = (int)(24f * _density);
        int totalHeightPx = sizePx + tabHeightPx;

        // Position
        int posX = _settings.WheelPosX;
        int posY = _settings.WheelPosY;

        if (!_settings.HasSavedWheelPosition)
        {
            // Default position: bottom-right area
            posX = Math.Max(0, _screenWidth - totalWidthPx - (int)(20 * _density));
            posY = Math.Max(0, _screenHeight - totalHeightPx - (int)(80 * _density));
            _settings.WheelPosX = posX;
            _settings.WheelPosY = posY;
        }

        Log.Info("Infiniroll", $"Adding overlay view: size={totalWidthPx}x{totalHeightPx}, pos=({posX},{posY})");

        _layoutParams = new WindowManagerLayoutParams(
            totalWidthPx,
            totalHeightPx,
            WindowManagerTypes.AccessibilityOverlay,
            WindowManagerFlags.NotFocusable |
            WindowManagerFlags.LayoutInScreen |
            WindowManagerFlags.LayoutNoLimits |
            WindowManagerFlags.HardwareAccelerated,
            Format.Translucent)
        {
            Gravity = GravityFlags.Top | GravityFlags.Left,
            X = posX,
            Y = posY
        };

        try
        {
            _windowManager.AddView(_overlayView, _layoutParams);
            EnsureWheelInsideScreenBounds(animate: false);
            Log.Info("Infiniroll", "Overlay view successfully added to WindowManager!");
        }
        catch (Exception ex)
        {
            Log.Error("Infiniroll", $"Failed to add overlay view: {ex.Message}");
        }
    }

    private void RemoveOverlayView()
    {
        CancelPullInAnimation();
        if (_mainHandler != null)
        {
            if (_delayedGeometryCheckRunnable != null)
            {
                _mainHandler.RemoveCallbacks(_delayedGeometryCheckRunnable);
            }
            if (_postGeometrySettleRunnable != null)
            {
                _mainHandler.RemoveCallbacks(_postGeometrySettleRunnable);
            }
        }

        if (_overlayView != null && _windowManager != null)
        {
            try
            {
                _overlayView.MoveRequested -= OnWheelMoveRequested;
                _overlayView.ResizeStarted -= OnWheelResizeStarted;
                _overlayView.ResizeRequested -= OnWheelResizeRequested;
                _overlayView.BackRequested -= OnWheelBackRequested;
                _overlayView.ScrollStarted -= OnWheelScrollStarted;
                _overlayView.WheelMotionDetected -= OnWheelMotionDetected;
                _overlayView.ScrollRequested -= OnWheelScrollRequested;
                _overlayView.ScrollStopped -= OnWheelScrollStopped;
                _windowManager.RemoveView(_overlayView);
            }
            catch (Exception ex)
            {
                Log.Error("Infiniroll", $"Failed to remove overlay view: {ex.Message}");
            }
            finally
            {
                _overlayView = null;
                _layoutParams = null;
            }
        }
    }

    private void OnWheelMoveRequested(int deltaX, int deltaY)
    {
        if (_layoutParams == null || _windowManager == null || _overlayView == null || _settings == null) return;

        CancelPullInAnimation();
        UpdateDisplayMetrics();

        int newX = _layoutParams.X + deltaX;
        int newY = _layoutParams.Y + deltaY;

        // Keep horizontal bounds within screen edges
        int minX = -(int)(_layoutParams.Width * 0.7f);
        int maxX = _screenWidth - (int)(_layoutParams.Width * 0.3f);
        newX = Math.Clamp(newX, minX, maxX);

        // Vertical bounds: can hide down leaving only the top tab (drawer behavior)
        int tabHeightPx = (int)(24f * _density);
        int minY = 0;
        int maxY = _screenHeight - tabHeightPx - (int)(10f * _density);
        newY = Math.Clamp(newY, minY, maxY);

        _layoutParams.X = newX;
        _layoutParams.Y = newY;

        _windowManager.UpdateViewLayout(_overlayView, _layoutParams);

        _settings.WheelPosX = newX;
        _settings.WheelPosY = newY;
    }

    private void OnWheelBackRequested()
    {
        try
        {
            PerformGlobalAction(GlobalAction.Back);
        }
        catch (Exception ex)
        {
            Log.Warn("Infiniroll", $"GlobalAction.Back failed: {ex.Message}");
        }
    }

    private void OnWheelResizeStarted()
    {
        if (_layoutParams == null) return;
        CancelPullInAnimation();
        UpdateDisplayMetrics();
        int tabHeightPx = (int)(24f * _density);
        int currentWheelSizePx = Math.Max(1, _layoutParams.Width - _appliedExtraLeftPx);
        // ホイールの中心座標（スクリーン絶対座標）をアンカーとして記録
        _resizeAnchorCenterX = _layoutParams.X + _appliedExtraLeftPx + (currentWheelSizePx / 2f);
        _resizeAnchorCenterY = _layoutParams.Y + tabHeightPx + (currentWheelSizePx / 2f);
    }

    private void OnWheelResizeRequested(int deltaSizeDp)
    {
        if (_settings == null || _layoutParams == null || _windowManager == null || _overlayView == null) return;

        int newSizeDp = Math.Clamp(_settings.WheelSizeDp + deltaSizeDp, AppSettings.MinWheelSizeDp, AppSettings.MaxWheelSizeDp);
        if (newSizeDp == _settings.WheelSizeDp) return;

        UpdateDisplayMetrics();
        int tabHeightPx = (int)(24f * _density);
        int newSizePx = (int)(newSizeDp * _density);
        int expectedExtraLeftPx = GetExpectedExtraLeftPx();
        int newTotalWidthPx = newSizePx + expectedExtraLeftPx;

        // 中心アンカーが未初期化の場合（設定画面からの変更など）は現在の中心を基準にする
        if (_resizeAnchorCenterX == 0 && _resizeAnchorCenterY == 0)
        {
            int currentWheelSizePx = Math.Max(1, _layoutParams.Width - _appliedExtraLeftPx);
            _resizeAnchorCenterX = _layoutParams.X + _appliedExtraLeftPx + (currentWheelSizePx / 2f);
            _resizeAnchorCenterY = _layoutParams.Y + tabHeightPx + (currentWheelSizePx / 2f);
        }

        // ホイールの中心点が一切ズレないように新しい左上起点(X, Y)を逆算
        int newX = (int)Math.Round(_resizeAnchorCenterX - expectedExtraLeftPx - (newSizePx / 2f));
        int newY = (int)Math.Round(_resizeAnchorCenterY - tabHeightPx - (newSizePx / 2f));

        // 画面境界内に収める
        int minX = -(int)(newTotalWidthPx * 0.7f);
        int maxX = _screenWidth - (int)(newTotalWidthPx * 0.3f);
        newX = Math.Clamp(newX, minX, maxX);

        int minY = 0;
        int maxY = _screenHeight - tabHeightPx - (int)(10f * _density);
        newY = Math.Clamp(newY, minY, maxY);

        _settings.WheelPosX = newX;
        _settings.WheelPosY = newY;
        _settings.WheelSizeDp = newSizeDp;
        ApplyCurrentSizeAndPosition();
    }

    private void ApplyCurrentSizeAndPosition()
    {
        if (_settings == null || _layoutParams == null || _windowManager == null || _overlayView == null) return;

        int sizePx = (int)(_settings.WheelSizeDp * _density);
        int extraLeftPx = GetExpectedExtraLeftPx();
        int totalWidthPx = sizePx + extraLeftPx;
        int tabHeightPx = (int)(24f * _density);
        int totalHeightPx = sizePx + tabHeightPx;

        _appliedExtraLeftPx = extraLeftPx;
        _layoutParams.Width = totalWidthPx;
        _layoutParams.Height = totalHeightPx;
        _layoutParams.X = _settings.WheelPosX;
        _layoutParams.Y = _settings.WheelPosY;

        _windowManager.UpdateViewLayout(_overlayView, _layoutParams);
        _overlayView.RequestLayout();
        _overlayView.Invalidate();
    }

    private void OnPositionResetRequested()
    {
        if (_settings == null || _layoutParams == null || _windowManager == null || _overlayView == null) return;

        CancelPullInAnimation();
        UpdateDisplayMetrics();

        int sizePx = (int)(AppSettings.DefaultWheelSizeDp * _density);
        int extraLeftPx = GetExpectedExtraLeftPx();
        int totalWidthPx = sizePx + extraLeftPx;
        int tabHeightPx = (int)(24f * _density);
        int totalHeightPx = sizePx + tabHeightPx;

        int posX = Math.Max(0, _screenWidth - totalWidthPx - (int)(20 * _density));
        int posY = Math.Max(0, _screenHeight - totalHeightPx - (int)(80 * _density));

        _settings.WheelPosX = posX;
        _settings.WheelPosY = posY;

        _appliedExtraLeftPx = extraLeftPx;
        _layoutParams.Width = totalWidthPx;
        _layoutParams.Height = totalHeightPx;
        _layoutParams.X = posX;
        _layoutParams.Y = posY;

        _windowManager.UpdateViewLayout(_overlayView, _layoutParams);
        _overlayView.RequestLayout();
        _overlayView.Invalidate();
    }

    // Android VelocityTracker.cpp の HORIZON (100ms) を完全に超過させて
    // ImpulseVelocityTracker / LSQ2 の慣性速度を確実に 0.0 px/s に落とすための定数
    private const long SmoothMoveDurationMs = 32;
    private const long BrakeChunkDurationMs = 65;
    private const long RequiredStationaryBrakeMs = 195; // 65ms × 3 = 195ms (> 100ms HORIZON)
    private const long StalePendingTimeoutMs = 35;

    private long _lastLogicalScrollTime;
    private bool _isUserTouchingWheel;
    private long _lastWheelMotionUptimeMs;
    private bool _isSmoothGestureInFlight;
    private bool _isCurrentStrokeMove;
    private GestureDescription.StrokeDescription? _activeContinueStroke;
    private float _virtualFingerX;
    private float _virtualFingerY;
    private float _currentRunwayMinY;
    private float _currentRunwayMaxY;
    private long _stationaryHoldMs;
    private bool _resettingRunway;
    private int _pendingSmoothSteps;
    private ScrollDirection _pendingSmoothDirection;
    private bool _pendingQueuedDuringMove;

    private void OnSettingsChanged()
    {
        if (_settings != null && _layoutParams != null)
        {
            UpdateDisplayMetrics();
            int expectedWheelSizePx = (int)(_settings.WheelSizeDp * _density);
            int expectedExtraLeftPx = GetExpectedExtraLeftPx();
            int expectedTotalWidthPx = expectedWheelSizePx + expectedExtraLeftPx;

            if (_layoutParams.Width != expectedTotalWidthPx || _appliedExtraLeftPx != expectedExtraLeftPx)
            {
                CancelPullInAnimation();
                int tabHeightPx = (int)(24f * _density);
                int oldWheelSizePx = Math.Max(1, _layoutParams.Width - _appliedExtraLeftPx);
                float currentCenterX = _layoutParams.X + _appliedExtraLeftPx + (oldWheelSizePx / 2f);
                float currentCenterY = _layoutParams.Y + tabHeightPx + (oldWheelSizePx / 2f);

                int newX = (int)Math.Round(currentCenterX - expectedExtraLeftPx - (expectedWheelSizePx / 2f));
                int newY = (int)Math.Round(currentCenterY - tabHeightPx - (expectedWheelSizePx / 2f));

                int minX = -(int)(expectedTotalWidthPx * 0.7f);
                int maxX = _screenWidth - (int)(expectedTotalWidthPx * 0.3f);
                newX = Math.Clamp(newX, minX, maxX);

                int minY = 0;
                int maxY = _screenHeight - tabHeightPx - (int)(10f * _density);
                newY = Math.Clamp(newY, minY, maxY);

                _settings.WheelPosX = newX;
                _settings.WheelPosY = newY;
                ApplyCurrentSizeAndPosition();
            }
        }
        _overlayView?.ApplySettings();
    }

    private void OnWheelScrollStarted()
    {
        _isUserTouchingWheel = true;
        _lastWheelMotionUptimeMs = SystemClock.UptimeMillis();
        _pendingSmoothSteps = 0;
        _pendingQueuedDuringMove = false;
    }

    private void OnWheelMotionDetected()
    {
        _lastWheelMotionUptimeMs = SystemClock.UptimeMillis();
    }

    private void OnWheelScrollStopped()
    {
        // ホイールから指を離した瞬間に未消化の予約を完全破棄。
        // 実行中のストロークは完了直後に静止ブレーキ(195ms)へ移行し、慣性速度 0 で指を離す。
        _isUserTouchingWheel = false;
        _pendingSmoothSteps = 0;
        _pendingQueuedDuringMove = false;
        _resettingRunway = false;
    }

    private void ResetVirtualFingerState()
    {
        _isSmoothGestureInFlight = false;
        _isCurrentStrokeMove = false;
        _activeContinueStroke = null;
        _stationaryHoldMs = 0;
        _resettingRunway = false;
    }

    private void OnWheelScrollRequested(ScrollDirection direction, int steps)
    {
        if (_settings == null) return;

        // 加速を一切行わず常に1ノッチ=1ステップ等倍で処理
        if (_settings.ScrollMode == ScrollModeType.Smooth)
        {
            PerformSmoothScroll(direction);
        }
        else
        {
            PerformLogicalScroll(direction, 1);
        }
    }

    private void PerformSmoothScroll(ScrollDirection direction)
    {
        if (_settings == null) return;

        _lastWheelMotionUptimeMs = SystemClock.UptimeMillis();

        if (_isSmoothGestureInFlight)
        {
            // 最大1ステップのみ保持（複数ステップ蓄積による加速・余韻スクロールを防止）
            _pendingSmoothDirection = direction;
            _pendingSmoothSteps = 1;
            _pendingQueuedDuringMove = _isCurrentStrokeMove;
            return;
        }

        DispatchSmoothMoveStep(direction);
    }

    private void ComputeSafeRunway(
        ScrollDirection direction,
        out float targetX,
        out float minY,
        out float maxY,
        out float startY)
    {
        UpdateDisplayMetrics();

        float defaultMargin = 100f * _density;
        minY = MathF.Round(defaultMargin);
        maxY = MathF.Round(Math.Max(minY + (100f * _density), _screenHeight - defaultMargin));
        targetX = MathF.Round(_screenWidth * 0.28f);

        if (_layoutParams != null)
        {
            int wheelLeft = _layoutParams.X;
            int wheelRight = _layoutParams.X + _layoutParams.Width;
            int wheelTop = _layoutParams.Y;
            int wheelBottom = _layoutParams.Y + _layoutParams.Height;
            int wheelCenterX = (wheelLeft + wheelRight) / 2;
            int wheelCenterY = (wheelTop + wheelBottom) / 2;

            // ホイールと反対側の左右エリアを選択
            targetX = MathF.Round(wheelCenterX < (_screenWidth / 2)
                ? _screenWidth * 0.72f
                : _screenWidth * 0.28f);

            float safetyPad = 16f * _density;
            bool overlapsHorizontally = targetX >= (wheelLeft - safetyPad) && targetX <= (wheelRight + safetyPad);

            if (overlapsHorizontally)
            {
                // ホイールが大きく targetX と水平方向に重なる場合は、上下の空いている領域を滑走路にする
                if (wheelCenterY >= (_screenHeight / 2))
                {
                    maxY = MathF.Round(Math.Max(minY + (60f * _density), wheelTop - safetyPad));
                }
                else
                {
                    minY = MathF.Round(Math.Min(maxY - (60f * _density), wheelBottom + safetyPad));
                }
            }
        }

        float runwaySpan = Math.Max(40f * _density, maxY - minY);
        // 下スクロール(Forward)は指を上(Y減少方向)へ動かすため下寄り(80%)から開始、
        // 上スクロール(Backward)は指を下(Y増加方向)へ動かすため上寄り(20%)から開始
        startY = direction == ScrollDirection.Forward
            ? MathF.Round(minY + (runwaySpan * 0.80f))
            : MathF.Round(minY + (runwaySpan * 0.20f));
        startY = MathF.Round(Math.Clamp(startY, minY, maxY));
    }

    private void DispatchSmoothMoveStep(ScrollDirection direction)
    {
        if (_settings == null) return;

        try
        {
            if (OperatingSystem.IsAndroidVersionAtLeast(26))
            {
                // ケースA: まだ仮想の指が画面に置かれていない場合 -> 新規タッチ開始 (willContinue: true)
                if (_activeContinueStroke == null)
                {
                    ComputeSafeRunway(direction, out float targetX, out float minY, out float maxY, out float startY);

                    float touchSlopPx = 16f * _density;
                    float stepPx = _settings.SmoothDistanceDp * _density;
                    float totalPx = MathF.Round(stepPx + touchSlopPx);

                    float endY = direction == ScrollDirection.Forward ? startY - totalPx : startY + totalPx;
                    endY = MathF.Round(Math.Clamp(endY, minY, maxY));

                    if (MathF.Abs(endY - startY) < 2f)
                    {
                        return;
                    }

                    using var movePath = new Android.Graphics.Path();
                    movePath.MoveTo(targetX, startY);
                    movePath.LineTo(targetX, endY);

                    var moveStroke = new GestureDescription.StrokeDescription(movePath, 0, SmoothMoveDurationMs, true);
                    var gestureBuilder = new GestureDescription.Builder();
                    gestureBuilder.AddStroke(moveStroke);

                    var gesture = gestureBuilder.Build();
                    if (gesture != null)
                    {
                        _isSmoothGestureInFlight = true;
                        _isCurrentStrokeMove = true;
                        _virtualFingerX = targetX;
                        _virtualFingerY = endY;
                        _currentRunwayMinY = minY;
                        _currentRunwayMaxY = maxY;
                        _stationaryHoldMs = 0;
                        _resettingRunway = false;

                        bool dispatched = DispatchGesture(
                            gesture,
                            new ContinuousGestureCallback(this, moveStroke, willContinue: true, wasMoveStroke: true, holdDurationMs: 0),
                            null);
                        if (!dispatched)
                        {
                            ResetVirtualFingerState();
                        }
                    }
                    return;
                }

                // ケースB: すでに仮想の指が画面に置かれている場合 -> TouchSlop不要で現在位置からそのままスライド継続
                float continueStepPx = MathF.Round(_settings.SmoothDistanceDp * _density);
                float nextRawY = direction == ScrollDirection.Forward
                    ? _virtualFingerY - continueStepPx
                    : _virtualFingerY + continueStepPx;

                // 滑走路の端に到達した場合は、慣性が出ないよう静止ブレーキをかけてから安全に指を置き直す
                if (nextRawY < _currentRunwayMinY || nextRawY > _currentRunwayMaxY)
                {
                    _pendingSmoothDirection = direction;
                    _pendingSmoothSteps = 1;
                    _pendingQueuedDuringMove = false;
                    _resettingRunway = true;
                    DispatchStationaryBrakeStep();
                    return;
                }

                float nextY = MathF.Round(nextRawY);
                using var continuePath = new Android.Graphics.Path();
                continuePath.MoveTo(_virtualFingerX, _virtualFingerY);
                continuePath.LineTo(_virtualFingerX, nextY);

                var continuedStroke = _activeContinueStroke.ContinueStroke(continuePath, 0, SmoothMoveDurationMs, true);
                if (continuedStroke == null)
                {
                    ResetVirtualFingerState();
                    return;
                }

                var continueBuilder = new GestureDescription.Builder();
                continueBuilder.AddStroke(continuedStroke);

                var continueGesture = continueBuilder.Build();
                if (continueGesture != null)
                {
                    _isSmoothGestureInFlight = true;
                    _isCurrentStrokeMove = true;
                    _virtualFingerY = nextY;
                    _stationaryHoldMs = 0;

                    bool dispatched = DispatchGesture(
                        continueGesture,
                        new ContinuousGestureCallback(this, continuedStroke, willContinue: true, wasMoveStroke: true, holdDurationMs: 0),
                        null);
                    if (!dispatched)
                    {
                        ResetVirtualFingerState();
                    }
                }
                else
                {
                    ResetVirtualFingerState();
                }
            }
            else
            {
                // API 24-25 フォールバック: Fling最小速度(50dp/s)未満の低速ドラッグで慣性を防止
                ComputeSafeRunway(direction, out float targetX, out float minY, out float maxY, out float startY);
                float totalDp = _settings.SmoothDistanceDp + 16f;
                float totalPx = MathF.Round(totalDp * _density);
                float endY = direction == ScrollDirection.Forward ? startY - totalPx : startY + totalPx;
                endY = MathF.Round(Math.Clamp(endY, minY, maxY));

                using var path = new Android.Graphics.Path();
                path.MoveTo(targetX, startY);
                path.LineTo(targetX, endY);

                long duration = Math.Max(350L, (long)(totalDp / 35f * 1000f));
                var stroke = new GestureDescription.StrokeDescription(path, 0, duration);
                var gestureBuilder = new GestureDescription.Builder();
                gestureBuilder.AddStroke(stroke);

                var gesture = gestureBuilder.Build();
                if (gesture != null)
                {
                    _isSmoothGestureInFlight = true;
                    _isCurrentStrokeMove = true;
                    bool dispatched = DispatchGesture(gesture, new LegacySmoothGestureCallback(this), null);
                    if (!dispatched)
                    {
                        _isSmoothGestureInFlight = false;
                        _isCurrentStrokeMove = false;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            ResetVirtualFingerState();
            Log.Warn("Infiniroll", $"Smooth scroll failed: {ex.Message}");
        }
    }

    private void DispatchStationaryBrakeStep()
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(26) || _activeContinueStroke == null)
        {
            ResetVirtualFingerState();
            return;
        }

        try
        {
            long remainingMs = Math.Max(BrakeChunkDurationMs, RequiredStationaryBrakeMs - _stationaryHoldMs);
            long duration;
            bool willContinue;

            if (!_isUserTouchingWheel || _resettingRunway)
            {
                // 指を離した直後または滑走路リセット時は、残りの静止時間を一括実行して確実に速度0でリリースする
                duration = remainingMs;
                willContinue = false;
            }
            else
            {
                // ホイールに指を置いたまま止めている間は 65ms 刻みで静止ホールドし、
                // すぐ再回転したときの即応性と 195ms 静止後の完全ゼロ速度リリースを両立する
                duration = BrakeChunkDurationMs;
                willContinue = (_stationaryHoldMs + duration) < RequiredStationaryBrakeMs;
            }

            using var holdPath = new Android.Graphics.Path();
            holdPath.MoveTo(_virtualFingerX, _virtualFingerY);

            var holdStroke = _activeContinueStroke.ContinueStroke(holdPath, 0, duration, willContinue);
            if (holdStroke == null)
            {
                ResetVirtualFingerState();
                return;
            }

            var gestureBuilder = new GestureDescription.Builder();
            gestureBuilder.AddStroke(holdStroke);

            var gesture = gestureBuilder.Build();
            if (gesture != null)
            {
                _isSmoothGestureInFlight = true;
                _isCurrentStrokeMove = false;
                bool dispatched = DispatchGesture(
                    gesture,
                    new ContinuousGestureCallback(this, holdStroke, willContinue, wasMoveStroke: false, holdDurationMs: duration),
                    null);
                if (!dispatched)
                {
                    ResetVirtualFingerState();
                }
            }
            else
            {
                ResetVirtualFingerState();
            }
        }
        catch (Exception ex)
        {
            ResetVirtualFingerState();
            Log.Warn("Infiniroll", $"Stationary brake failed: {ex.Message}");
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("android26.0")]
    private void OnContinuousStrokeCompleted(
        GestureDescription.StrokeDescription stroke,
        bool willContinue,
        bool wasMoveStroke,
        long holdDurationMs)
    {
        _isSmoothGestureInFlight = false;
        _isCurrentStrokeMove = false;

        if (willContinue)
        {
            _activeContinueStroke = stroke;
            _stationaryHoldMs = wasMoveStroke ? 0 : (_stationaryHoldMs + holdDurationMs);
        }
        else
        {
            _activeContinueStroke = null;
            _stationaryHoldMs = 0;
            _resettingRunway = false;
        }

        if (_resettingRunway && _activeContinueStroke != null)
        {
            DispatchStationaryBrakeStep();
            return;
        }

        if (_pendingSmoothSteps > 0)
        {
            if (!_isUserTouchingWheel)
            {
                // 指がすでにホイールから離れている場合は残余ステップを実行しない
                _pendingSmoothSteps = 0;
                _pendingQueuedDuringMove = false;
            }
            else if (_pendingQueuedDuringMove)
            {
                // 高速回転中に予約されたステップでも、直後に指がピタッと止まっていたら破棄して即座に停止させる
                long idleMs = SystemClock.UptimeMillis() - _lastWheelMotionUptimeMs;
                if (idleMs > StalePendingTimeoutMs)
                {
                    _pendingSmoothSteps = 0;
                    _pendingQueuedDuringMove = false;
                }
            }
        }

        if (_pendingSmoothSteps > 0)
        {
            ScrollDirection nextDir = _pendingSmoothDirection;
            _pendingSmoothSteps = 0;
            _pendingQueuedDuringMove = false;
            DispatchSmoothMoveStep(nextDir);
        }
        else if (_activeContinueStroke != null)
        {
            // 次の回転入力がない場合は即座にその場で仮想指を静止固定し、慣性スクロール(Fling)を完全に封じる
            DispatchStationaryBrakeStep();
        }
    }

    private void OnContinuousStrokeCancelled()
    {
        // OS側で ACTION_CANCEL が発行されたため慣性は発生しない。仮想指の状態をクリアする
        ResetVirtualFingerState();

        if (!_isUserTouchingWheel)
        {
            _pendingSmoothSteps = 0;
            _pendingQueuedDuringMove = false;
            return;
        }

        if (_pendingSmoothSteps > 0)
        {
            long idleMs = SystemClock.UptimeMillis() - _lastWheelMotionUptimeMs;
            if (!_pendingQueuedDuringMove || idleMs <= StalePendingTimeoutMs)
            {
                ScrollDirection nextDir = _pendingSmoothDirection;
                _pendingSmoothSteps = 0;
                _pendingQueuedDuringMove = false;
                DispatchSmoothMoveStep(nextDir);
            }
            else
            {
                _pendingSmoothSteps = 0;
                _pendingQueuedDuringMove = false;
            }
        }
    }

    private void OnLegacySmoothGestureFinished()
    {
        _isSmoothGestureInFlight = false;
        _isCurrentStrokeMove = false;
        if (_isUserTouchingWheel && _pendingSmoothSteps > 0)
        {
            ScrollDirection nextDir = _pendingSmoothDirection;
            _pendingSmoothSteps = 0;
            _pendingQueuedDuringMove = false;
            DispatchSmoothMoveStep(nextDir);
        }
        else
        {
            _pendingSmoothSteps = 0;
            _pendingQueuedDuringMove = false;
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("android26.0")]
    private class ContinuousGestureCallback : GestureResultCallback
    {
        private readonly WheelOverlayService _service;
        private readonly GestureDescription.StrokeDescription _stroke;
        private readonly bool _willContinue;
        private readonly bool _wasMoveStroke;
        private readonly long _holdDurationMs;

        public ContinuousGestureCallback(
            WheelOverlayService service,
            GestureDescription.StrokeDescription stroke,
            bool willContinue,
            bool wasMoveStroke,
            long holdDurationMs)
        {
            _service = service;
            _stroke = stroke;
            _willContinue = willContinue;
            _wasMoveStroke = wasMoveStroke;
            _holdDurationMs = holdDurationMs;
        }

        public override void OnCompleted(GestureDescription? gestureDescription)
        {
            base.OnCompleted(gestureDescription);
            _service.OnContinuousStrokeCompleted(_stroke, _willContinue, _wasMoveStroke, _holdDurationMs);
        }

        public override void OnCancelled(GestureDescription? gestureDescription)
        {
            base.OnCancelled(gestureDescription);
            _service.OnContinuousStrokeCancelled();
        }
    }

    private class LegacySmoothGestureCallback : GestureResultCallback
    {
        private readonly WheelOverlayService _service;

        public LegacySmoothGestureCallback(WheelOverlayService service)
        {
            _service = service;
        }

        public override void OnCompleted(GestureDescription? gestureDescription)
        {
            base.OnCompleted(gestureDescription);
            _service.OnLegacySmoothGestureFinished();
        }

        public override void OnCancelled(GestureDescription? gestureDescription)
        {
            base.OnCancelled(gestureDescription);
            _service.OnLegacySmoothGestureFinished();
        }
    }

    private void PerformLogicalScroll(ScrollDirection direction, int steps)
    {
        // 論理スクロール処理 (ACTION_SCROLL_FORWARD / BACKWARD)
        long now = SystemClock.UptimeMillis();
        if (now - _lastLogicalScrollTime < 70)
        {
            // 連打による急激なワープを抑制
            return;
        }
        _lastLogicalScrollTime = now;

        try
        {
            var rootNode = RootInActiveWindow;
            if (rootNode == null) return;

            var targetNode = FindScrollableNode(rootNode);
            if (targetNode != null)
            {
                var action = direction == ScrollDirection.Forward
                    ? Android.Views.Accessibility.Action.ScrollForward
                    : Android.Views.Accessibility.Action.ScrollBackward;

                targetNode.PerformAction(action);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Infiniroll", $"Logical scroll failed: {ex.Message}");
        }
    }

    private AccessibilityNodeInfo? FindScrollableNode(AccessibilityNodeInfo root)
    {
        // 幅優先探索 (BFS) で最もふさわしいスクロール可能ノードを検索
        var queue = new Queue<AccessibilityNodeInfo>();
        queue.Enqueue(root);

        AccessibilityNodeInfo? bestCandidate = null;
        int maxArea = 0;

        var screenBounds = new Rect();

        while (queue.Count > 0)
        {
            var node = queue.Dequeue();

            if (node.Scrollable)
            {
                node.GetBoundsInScreen(screenBounds);
                int area = screenBounds.Width() * screenBounds.Height();

                // 画面上で最も大きく表示されているスクロール領域を最優先候補とする
                if (area > maxArea)
                {
                    maxArea = area;
                    bestCandidate = node;
                }
            }

            for (int i = 0; i < node.ChildCount; i++)
            {
                var child = node.GetChild(i);
                if (child != null)
                {
                    queue.Enqueue(child);
                }
            }
        }

        return bestCandidate;
    }
}
