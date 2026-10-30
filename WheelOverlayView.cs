using System;
using Android.Animation;
using Android.Content;
using Android.Graphics;
using Android.OS;
using Android.Views;

namespace Infiniroll;

public enum ScrollDirection
{
    Forward,   // スクロール下 (時計回り)
    Backward   // スクロール上 (反時計回り)
}

public class WheelOverlayView : View
{
    public const float SideButtonExtraDp = 34f;
    public const float BackButtonExtraLeftDp = SideButtonExtraDp;
    public const float SettingsButtonExtraRightDp = SideButtonExtraDp;

    private enum TouchZone
    {
        None,
        ResizeTab,
        BackButton,
        SettingsButton,
        CenterButton,
        DialTrack,
        ScrollUpButton,
        ScrollDownButton
    }

    private const long RepeatInitialDelayMs = 300;
    private const long RepeatIntervalMs = 400;

    private readonly AppSettings _settings;
    private readonly Vibrator? _vibrator;

    // Paints
    private readonly Paint _outerDialPaint;
    private readonly Paint _buttonPressedPaint;
    private readonly Paint _innerButtonPaint;
    private readonly Paint _tabPaint;
    private readonly Paint _backButtonPaint;
    private readonly Paint _backIconPaint;
    private readonly Paint _settingsIconPaint;
    private readonly Paint _strokePaint;
    private readonly Paint _markerPaint;
    private readonly Paint _tabHandlePaint;
    private readonly Android.Graphics.Path _backIconPath;
    private readonly Android.Graphics.Path _arrowPath;
    private readonly RectF _upArcRect;

    // Dimensions
    private float _density;
    private float _tabHeight;
    private float _tabWidth;
    private float _outerRadius;
    private float _innerRadius;
    private float _centerX;
    private float _centerY;
    private float _sideButtonRadius;
    private float _backBtnCenterX;
    private float _backBtnCenterY;
    private float _settingsBtnCenterX;
    private float _settingsBtnCenterY;

    // State
    private TouchZone _activeZone = TouchZone.None;
    private TouchZone _activeRepeatZone = TouchZone.None;
    private float _lastTouchRawX;
    private float _lastTouchRawY;
    private double _lastAngle;
    private double _accumulatedAngle;
    private bool _isPressedCenter;
    private bool _isPressedTab;
    private bool _isPressedBack;
    private bool _isPressedSettings;
    private bool _isPressedUp;
    private bool _isPressedDown;

    // Animation & Timers
    private readonly Handler _fadeHandler;
    private readonly Action _fadeOutRunnable;
    private readonly Handler _buttonRepeatHandler;
    private readonly Action _buttonRepeatAction;
    private bool _isFadedOut;

    // Callbacks
    public event Action<int, int>? MoveRequested;
    public event Action? ResizeStarted;
    public event Action<int>? ResizeRequested;
    public event Action? BackRequested;
    public event Action? SettingsRequested;
    public event Action? ScrollStarted;
    public event Action? WheelMotionDetected;
    public event Action<ScrollDirection, int>? ScrollRequested;
    public event Action? ScrollStopped;

    public WheelOverlayView(Context context, AppSettings settings) : base(context)
    {
        _settings = settings;
        _density = context.Resources?.DisplayMetrics?.Density ?? 1.0f;
        _fadeHandler = new Handler(Looper.MainLooper!);
        _fadeOutRunnable = StartFadeOut;
        _buttonRepeatHandler = new Handler(Looper.MainLooper!);
        _buttonRepeatAction = OnButtonRepeat;

        if (OperatingSystem.IsAndroidVersionAtLeast(31))
        {
            var vibratorManager = context.GetSystemService(Context.VibratorManagerService) as VibratorManager;
            _vibrator = vibratorManager?.DefaultVibrator;
        }
        else
        {
#pragma warning disable CA1422
            _vibrator = context.GetSystemService(Context.VibratorService) as Vibrator;
#pragma warning restore CA1422
        }

        // Initialize paints
        _outerDialPaint = new Paint(PaintFlags.AntiAlias)
        {
            Color = Color.Rgb(38, 38, 42),
            Alpha = 220
        };
        _outerDialPaint.SetStyle(Paint.Style.Fill);

        _buttonPressedPaint = new Paint(PaintFlags.AntiAlias)
        {
            Color = Color.Rgb(65, 70, 84),
            Alpha = 240
        };
        _buttonPressedPaint.SetStyle(Paint.Style.Fill);

        _innerButtonPaint = new Paint(PaintFlags.AntiAlias)
        {
            Color = Color.Rgb(58, 60, 68),
            Alpha = 240
        };
        _innerButtonPaint.SetStyle(Paint.Style.Fill);

        _tabPaint = new Paint(PaintFlags.AntiAlias)
        {
            Color = Color.Rgb(48, 50, 56),
            Alpha = 230
        };
        _tabPaint.SetStyle(Paint.Style.Fill);

        _backButtonPaint = new Paint(PaintFlags.AntiAlias)
        {
            Color = Color.Rgb(48, 50, 56),
            Alpha = 230
        };
        _backButtonPaint.SetStyle(Paint.Style.Fill);

        _backIconPaint = new Paint(PaintFlags.AntiAlias)
        {
            Color = Color.Rgb(210, 215, 228)
        };
        _backIconPaint.SetStyle(Paint.Style.Fill);

        _settingsIconPaint = new Paint(PaintFlags.AntiAlias)
        {
            Color = Color.Rgb(210, 215, 228),
            StrokeWidth = 2.2f * _density,
            StrokeCap = Paint.Cap.Round
        };
        _settingsIconPaint.SetStyle(Paint.Style.Stroke);

        _backIconPath = new Android.Graphics.Path();
        _arrowPath = new Android.Graphics.Path();
        _upArcRect = new RectF();

        _strokePaint = new Paint(PaintFlags.AntiAlias)
        {
            Color = Color.Rgb(80, 84, 96),
            StrokeWidth = 2f * _density
        };
        _strokePaint.SetStyle(Paint.Style.Stroke);

        _markerPaint = new Paint(PaintFlags.AntiAlias)
        {
            Color = Color.Rgb(140, 145, 160),
            StrokeWidth = 2f * _density,
            StrokeCap = Paint.Cap.Round
        };
        _markerPaint.SetStyle(Paint.Style.Stroke);

        _tabHandlePaint = new Paint(PaintFlags.AntiAlias)
        {
            Color = Color.Rgb(180, 185, 195),
            StrokeWidth = 2.5f * _density,
            StrokeCap = Paint.Cap.Round
        };
        _tabHandlePaint.SetStyle(Paint.Style.Stroke);

        SetWillNotDraw(false);

        // Schedule initial fade out if auto fade enabled
        ScheduleFadeOut();
    }

    public int GetExtraLeftPx()
    {
        return _settings.ShowBackButtonEnabled ? (int)MathF.Ceiling(SideButtonExtraDp * _density) : 0;
    }

    public int GetExtraRightPx()
    {
        return _settings.ShowSettingsButtonEnabled ? (int)MathF.Ceiling(SideButtonExtraDp * _density) : 0;
    }

    private void RecalculateGeometry(int totalWidth)
    {
        int extraLeftPx = GetExtraLeftPx();
        int extraRightPx = GetExtraRightPx();
        int wheelDiameter = Math.Max(1, totalWidth - extraLeftPx - extraRightPx);

        _tabHeight = 24f * _density;
        _tabWidth = Math.Max(wheelDiameter * 0.38f, 50f * _density);

        _outerRadius = (wheelDiameter - (4f * _density)) / 2f;
        _innerRadius = _outerRadius * 0.45f;
        _centerX = extraLeftPx + (wheelDiameter / 2f);
        _centerY = _tabHeight + (wheelDiameter / 2f);

        _sideButtonRadius = 19f * _density;
        float orbitRadius = _outerRadius + (5f * _density) + _sideButtonRadius;

        // 外側の円の外側 10:00 方向 (-150°) に戻るボタンを配置
        float rad10OClock = -150f * MathF.PI / 180f;
        _backBtnCenterX = _centerX + (orbitRadius * MathF.Cos(rad10OClock));
        _backBtnCenterY = _centerY + (orbitRadius * MathF.Sin(rad10OClock));

        // 外側の円の外側 2:00 方向 (-30°) にアプリ（設定）起動ボタンを配置
        float rad2OClock = -30f * MathF.PI / 180f;
        _settingsBtnCenterX = _centerX + (orbitRadius * MathF.Cos(rad2OClock));
        _settingsBtnCenterY = _centerY + (orbitRadius * MathF.Sin(rad2OClock));
    }

    protected override void OnMeasure(int widthMeasureSpec, int heightMeasureSpec)
    {
        int totalWidth = MeasureSpec.GetSize(widthMeasureSpec);
        int extraLeftPx = GetExtraLeftPx();
        int extraRightPx = GetExtraRightPx();
        int wheelDiameter = Math.Max(1, totalWidth - extraLeftPx - extraRightPx);

        _tabHeight = 24f * _density;
        _tabWidth = Math.Max(wheelDiameter * 0.38f, 50f * _density);

        // View height includes the wheel diameter + top resize tab
        int totalHeight = wheelDiameter + (int)_tabHeight;
        SetMeasuredDimension(totalWidth, totalHeight);
    }

    protected override void OnSizeChanged(int w, int h, int oldw, int oldh)
    {
        base.OnSizeChanged(w, h, oldw, oldh);
        RecalculateGeometry(w);
    }

    protected override void OnDraw(Canvas canvas)
    {
        base.OnDraw(canvas);

        // 0. Draw optional 10 o'clock Back button ("◀") outside the outer circle
        if (_settings.ShowBackButtonEnabled)
        {
            DrawBackButton(canvas);
        }

        // 0b. Draw optional 2 o'clock Settings button ("≡") outside the outer circle
        if (_settings.ShowSettingsButtonEnabled)
        {
            DrawSettingsButton(canvas);
        }

        // 1. Draw top resize tab (folder index tab style)
        float tabLeft = _centerX - (_tabWidth / 2f);
        float tabRight = _centerX + (_tabWidth / 2f);
        float tabTop = 2f * _density;
        float tabBottom = _tabHeight + (8f * _density); // overlaps slightly into dial

        var tabRect = new RectF(tabLeft, tabTop, tabRight, tabBottom);
        float cornerRadius = 6f * _density;
        _tabPaint.Color = _isPressedTab ? Color.Rgb(70, 75, 88) : Color.Rgb(48, 50, 56);
        canvas.DrawRoundRect(tabRect, cornerRadius, cornerRadius, _tabPaint);
        canvas.DrawRoundRect(tabRect, cornerRadius, cornerRadius, _strokePaint);

        // Draw grip line on the tab
        float gripWidth = 20f * _density;
        float gripY = (tabTop + _tabHeight) / 2f;
        canvas.DrawLine(_centerX - gripWidth / 2f, gripY, _centerX + gripWidth / 2f, gripY, _tabHandlePaint);
        canvas.DrawLine(_centerX - (gripWidth * 0.6f) / 2f, gripY - 3f * _density, _centerX + (gripWidth * 0.6f) / 2f, gripY - 3f * _density, _tabHandlePaint);
        canvas.DrawLine(_centerX - (gripWidth * 0.6f) / 2f, gripY + 3f * _density, _centerX + (gripWidth * 0.6f) / 2f, gripY + 3f * _density, _tabHandlePaint);

        // 2. Draw outer dial circle / buttons
        if (_settings.ScrollMode == ScrollModeType.LogicalButtons)
        {
            DrawLogicalButtons(canvas);
        }
        else
        {
            canvas.DrawCircle(_centerX, _centerY, _outerRadius, _outerDialPaint);
            canvas.DrawCircle(_centerX, _centerY, _outerRadius, _strokePaint);

            // 3. Draw subtle tick marks / scroll hints around the dial
            DrawDialMarkers(canvas);

            // 4. Draw center button
            _innerButtonPaint.Color = _isPressedCenter ? Color.Rgb(80, 85, 100) : Color.Rgb(58, 60, 68);
            canvas.DrawCircle(_centerX, _centerY, _innerRadius, _innerButtonPaint);
            canvas.DrawCircle(_centerX, _centerY, _innerRadius, _strokePaint);
        }
    }

    private void DrawLogicalButtons(Canvas canvas)
    {
        _upArcRect.Set(_centerX - _outerRadius, _centerY - _outerRadius, _centerX + _outerRadius, _centerY + _outerRadius);

        // 1. 上半分（▲）の円弧背景
        var upPaint = _isPressedUp ? _buttonPressedPaint : _outerDialPaint;
        canvas.DrawArc(_upArcRect, -180, 180, true, upPaint);

        // 2. 下半分（▼）の円弧背景
        var downPaint = _isPressedDown ? _buttonPressedPaint : _outerDialPaint;
        canvas.DrawArc(_upArcRect, 0, 180, true, downPaint);

        // 3. 外周リング枠線
        canvas.DrawCircle(_centerX, _centerY, _outerRadius, _strokePaint);

        // 4. 水平スリット線（中央円の左右に伸びる区切り線）
        canvas.DrawLine(_centerX - _outerRadius, _centerY, _centerX - _innerRadius, _centerY, _strokePaint);
        canvas.DrawLine(_centerX + _innerRadius, _centerY, _centerX + _outerRadius, _centerY, _strokePaint);

        // 5. 上「▲」アイコン
        float arrowHeight = 11f * _density;
        float arrowHalfWidth = 9f * _density;
        float upCenterY = _centerY - ((_outerRadius + _innerRadius) / 2f);

        _arrowPath.Reset();
        _arrowPath.MoveTo(_centerX, upCenterY - (arrowHeight * 0.55f));
        _arrowPath.LineTo(_centerX - arrowHalfWidth, upCenterY + (arrowHeight * 0.45f));
        _arrowPath.LineTo(_centerX + arrowHalfWidth, upCenterY + (arrowHeight * 0.45f));
        _arrowPath.Close();
        canvas.DrawPath(_arrowPath, _backIconPaint);

        // 6. 下「▼」アイコン
        float downCenterY = _centerY + ((_outerRadius + _innerRadius) / 2f);

        _arrowPath.Reset();
        _arrowPath.MoveTo(_centerX, downCenterY + (arrowHeight * 0.55f));
        _arrowPath.LineTo(_centerX - arrowHalfWidth, downCenterY - (arrowHeight * 0.45f));
        _arrowPath.LineTo(_centerX + arrowHalfWidth, downCenterY - (arrowHeight * 0.45f));
        _arrowPath.Close();
        canvas.DrawPath(_arrowPath, _backIconPaint);

        // 7. 中央移動ボタン
        _innerButtonPaint.Color = _isPressedCenter ? Color.Rgb(80, 85, 100) : Color.Rgb(58, 60, 68);
        canvas.DrawCircle(_centerX, _centerY, _innerRadius, _innerButtonPaint);
        canvas.DrawCircle(_centerX, _centerY, _innerRadius, _strokePaint);
    }

    private void DrawBackButton(Canvas canvas)
    {
        _backButtonPaint.Color = _isPressedBack ? Color.Rgb(80, 88, 108) : Color.Rgb(48, 50, 56);
        _backButtonPaint.Alpha = _isPressedBack ? 250 : 230;

        canvas.DrawCircle(_backBtnCenterX, _backBtnCenterY, _sideButtonRadius, _backButtonPaint);
        canvas.DrawCircle(_backBtnCenterX, _backBtnCenterY, _sideButtonRadius, _strokePaint);

        // 視覚的な重心が円の中心に来るよう光学補正した「◀」アイコンを描画
        float tipX = _backBtnCenterX - (_sideButtonRadius * 0.36f);
        float tipY = _backBtnCenterY;
        float rightX = _backBtnCenterX + (_sideButtonRadius * 0.24f);
        float topY = _backBtnCenterY - (_sideButtonRadius * 0.36f);
        float bottomY = _backBtnCenterY + (_sideButtonRadius * 0.36f);

        _backIconPath.Reset();
        _backIconPath.MoveTo(tipX, tipY);
        _backIconPath.LineTo(rightX, topY);
        _backIconPath.LineTo(rightX, bottomY);
        _backIconPath.Close();

        canvas.DrawPath(_backIconPath, _backIconPaint);
    }

    private void DrawSettingsButton(Canvas canvas)
    {
        _backButtonPaint.Color = _isPressedSettings ? Color.Rgb(80, 88, 108) : Color.Rgb(48, 50, 56);
        _backButtonPaint.Alpha = _isPressedSettings ? 250 : 230;

        canvas.DrawCircle(_settingsBtnCenterX, _settingsBtnCenterY, _sideButtonRadius, _backButtonPaint);
        canvas.DrawCircle(_settingsBtnCenterX, _settingsBtnCenterY, _sideButtonRadius, _strokePaint);

        // 「≡」（三本線）アイコンを描画
        float lineWidth = 15f * _density;
        float lineSpacing = 4.2f * _density;
        float startX = _settingsBtnCenterX - (lineWidth / 2f);
        float endX = _settingsBtnCenterX + (lineWidth / 2f);

        canvas.DrawLine(startX, _settingsBtnCenterY - lineSpacing, endX, _settingsBtnCenterY - lineSpacing, _settingsIconPaint);
        canvas.DrawLine(startX, _settingsBtnCenterY, endX, _settingsBtnCenterY, _settingsIconPaint);
        canvas.DrawLine(startX, _settingsBtnCenterY + lineSpacing, endX, _settingsBtnCenterY + lineSpacing, _settingsIconPaint);
    }

    private void DrawDialMarkers(Canvas canvas)
    {
        // Draw 12 subtle tick marks around the dial track
        float markerRadiusOuter = _outerRadius - (8f * _density);
        float markerRadiusInner = markerRadiusOuter - (6f * _density);

        for (int i = 0; i < 12; i++)
        {
            double rad = (i * 30.0) * Math.PI / 180.0;
            float cos = (float)Math.Cos(rad);
            float sin = (float)Math.Sin(rad);

            float startX = _centerX + markerRadiusInner * cos;
            float startY = _centerY + markerRadiusInner * sin;
            float endX = _centerX + markerRadiusOuter * cos;
            float endY = _centerY + markerRadiusOuter * sin;

            canvas.DrawLine(startX, startY, endX, endY, _markerPaint);
        }
    }

    public override bool OnTouchEvent(MotionEvent? e)
    {
        if (e == null) return false;

        float rawX = e.RawX;
        float rawY = e.RawY;
        float localX = e.GetX();
        float localY = e.GetY();

        switch (e.ActionMasked)
        {
            case MotionEventActions.Down:
                _lastTouchRawX = rawX;
                _lastTouchRawY = rawY;
                _accumulatedAngle = 0;

                _activeZone = DetectTouchZone(localX, localY);
                if (_activeZone == TouchZone.None)
                {
                    return false;
                }

                CancelFadeOut();
                FadeInImmediately();

                if (_activeZone == TouchZone.CenterButton)
                {
                    _isPressedCenter = true;
                    Invalidate();
                }
                else if (_activeZone == TouchZone.ResizeTab)
                {
                    _isPressedTab = true;
                    ResizeStarted?.Invoke();
                    Invalidate();
                }
                else if (_activeZone == TouchZone.BackButton)
                {
                    _isPressedBack = true;
                    Invalidate();
                }
                else if (_activeZone == TouchZone.SettingsButton)
                {
                    _isPressedSettings = true;
                    Invalidate();
                }
                else if (_activeZone == TouchZone.ScrollUpButton)
                {
                    _isPressedUp = true;
                    _activeRepeatZone = TouchZone.ScrollUpButton;
                    ScrollRequested?.Invoke(ScrollDirection.Backward, 1);
                    PlayHapticTick();
                    StartButtonRepeat();
                    Invalidate();
                }
                else if (_activeZone == TouchZone.ScrollDownButton)
                {
                    _isPressedDown = true;
                    _activeRepeatZone = TouchZone.ScrollDownButton;
                    ScrollRequested?.Invoke(ScrollDirection.Forward, 1);
                    PlayHapticTick();
                    StartButtonRepeat();
                    Invalidate();
                }
                else if (_activeZone == TouchZone.DialTrack)
                {
                    _lastAngle = CalculateAngle(localX, localY);
                    ScrollStarted?.Invoke();
                }
                return true;

            case MotionEventActions.Move:
                float deltaRawX = rawX - _lastTouchRawX;
                float deltaRawY = rawY - _lastTouchRawY;

                if (_activeZone == TouchZone.CenterButton)
                {
                    // Center drag moves position
                    MoveRequested?.Invoke((int)deltaRawX, (int)deltaRawY);
                    _lastTouchRawX = rawX;
                    _lastTouchRawY = rawY;
                }
                else if (_activeZone == TouchZone.ResizeTab)
                {
                    // Dragging tab vertically resizes the wheel from its center
                    // Pulling up increases radius by deltaY (so diameter changes by 2 * deltaY)
                    int deltaSize = -(int)Math.Round((deltaRawY * 2f) / _density);
                    if (Math.Abs(deltaSize) >= 1)
                    {
                        ResizeRequested?.Invoke(deltaSize);
                        _lastTouchRawY = rawY;
                    }
                }
                else if (_activeZone == TouchZone.BackButton)
                {
                    float bdx = localX - _backBtnCenterX;
                    float bdy = localY - _backBtnCenterY;
                    bool stillInside = MathF.Sqrt((bdx * bdx) + (bdy * bdy)) <= (_sideButtonRadius + (10f * _density));
                    if (_isPressedBack != stillInside)
                    {
                        _isPressedBack = stillInside;
                        Invalidate();
                    }
                }
                else if (_activeZone == TouchZone.SettingsButton)
                {
                    float sdx = localX - _settingsBtnCenterX;
                    float sdy = localY - _settingsBtnCenterY;
                    bool stillInside = MathF.Sqrt((sdx * sdx) + (sdy * sdy)) <= (_sideButtonRadius + (10f * _density));
                    if (_isPressedSettings != stillInside)
                    {
                        _isPressedSettings = stillInside;
                        Invalidate();
                    }
                }
                else if (_activeZone == TouchZone.ScrollUpButton || _activeZone == TouchZone.ScrollDownButton)
                {
                    TouchZone currentZone = DetectTouchZone(localX, localY);
                    if (currentZone != _activeZone)
                    {
                        CancelButtonRepeat();
                        _isPressedUp = false;
                        _isPressedDown = false;
                        _activeRepeatZone = TouchZone.None;
                        Invalidate();
                    }
                }
                else if (_activeZone == TouchZone.DialTrack)
                {
                    float dx = localX - _centerX;
                    float dy = localY - _centerY;
                    float dist = MathF.Sqrt(dx * dx + dy * dy);

                    // 中心点付近の特異点（わずかな移動で角度が急変する領域）では角度加算を行わない
                    if (dist < _innerRadius * 0.35f)
                    {
                        return true;
                    }

                    double currentAngle = CalculateAngle(localX, localY);
                    double deltaAngle = currentAngle - _lastAngle;

                    // Handle angle wraparound at -180 / 180 boundary
                    if (deltaAngle > 180) deltaAngle -= 360;
                    else if (deltaAngle < -180) deltaAngle += 360;

                    _lastAngle = currentAngle;

                    // 単一フレームでの異常な角度ジャンプ（指を離す瞬間のタッチ座標飛び等）を無視
                    if (Math.Abs(deltaAngle) > 60.0)
                    {
                        return true;
                    }

                    if (Math.Abs(deltaAngle) >= 0.15)
                    {
                        WheelMotionDetected?.Invoke();
                    }

                    _accumulatedAngle += deltaAngle;

                    float notchDegrees = Math.Max(_settings.NotchAngleDegrees, AppSettings.MinNotchAngle);

                    if (_accumulatedAngle >= notchDegrees)
                    {
                        int steps = (int)(_accumulatedAngle / notchDegrees);
                        _accumulatedAngle -= steps * notchDegrees;
                        // 加速を防ぐため常に1ステップずつ通知
                        ScrollRequested?.Invoke(ScrollDirection.Forward, 1);
                        PlayHapticTick();
                    }
                    else if (_accumulatedAngle <= -notchDegrees)
                    {
                        int steps = (int)(-_accumulatedAngle / notchDegrees);
                        _accumulatedAngle += steps * notchDegrees;
                        // 加速を防ぐため常に1ステップずつ通知
                        ScrollRequested?.Invoke(ScrollDirection.Backward, 1);
                        PlayHapticTick();
                    }
                }
                return true;

            case MotionEventActions.Up:
                CancelButtonRepeat();
                if (_activeZone == TouchZone.DialTrack)
                {
                    ScrollStopped?.Invoke();
                }
                else if (_activeZone == TouchZone.BackButton && _isPressedBack)
                {
                    PlayHapticTick();
                    BackRequested?.Invoke();
                }
                else if (_activeZone == TouchZone.SettingsButton && _isPressedSettings)
                {
                    PlayHapticTick();
                    SettingsRequested?.Invoke();
                }
                _isPressedCenter = false;
                _isPressedTab = false;
                _isPressedBack = false;
                _isPressedSettings = false;
                _isPressedUp = false;
                _isPressedDown = false;
                _activeZone = TouchZone.None;
                _activeRepeatZone = TouchZone.None;
                Invalidate();
                ScheduleFadeOut();
                return true;

            case MotionEventActions.Cancel:
                CancelButtonRepeat();
                if (_activeZone == TouchZone.DialTrack)
                {
                    ScrollStopped?.Invoke();
                }
                _isPressedCenter = false;
                _isPressedTab = false;
                _isPressedBack = false;
                _isPressedSettings = false;
                _isPressedUp = false;
                _isPressedDown = false;
                _activeZone = TouchZone.None;
                _activeRepeatZone = TouchZone.None;
                Invalidate();
                ScheduleFadeOut();
                return true;
        }

        return base.OnTouchEvent(e);
    }

    private TouchZone DetectTouchZone(float x, float y)
    {
        float dx = x - _centerX;
        float dy = y - _centerY;
        float dist = MathF.Sqrt((dx * dx) + (dy * dy));

        // 0a. Check 10 o'clock Back button zone (when enabled and outside outer dial rim)
        if (_settings.ShowBackButtonEnabled && dist > _outerRadius + (2f * _density))
        {
            float bdx = x - _backBtnCenterX;
            float bdy = y - _backBtnCenterY;
            float backDist = MathF.Sqrt((bdx * bdx) + (bdy * bdy));
            if (backDist <= _sideButtonRadius + (4f * _density))
            {
                return TouchZone.BackButton;
            }
        }

        // 0b. Check 2 o'clock Settings button zone (when enabled and outside outer dial rim)
        if (_settings.ShowSettingsButtonEnabled && dist > _outerRadius + (2f * _density))
        {
            float sdx = x - _settingsBtnCenterX;
            float sdy = y - _settingsBtnCenterY;
            float settingsDist = MathF.Sqrt((sdx * sdx) + (sdy * sdy));
            if (settingsDist <= _sideButtonRadius + (4f * _density))
            {
                return TouchZone.SettingsButton;
            }
        }

        // 1. Check tab zone
        float tabLeft = _centerX - (_tabWidth / 2f);
        float tabRight = _centerX + (_tabWidth / 2f);
        if (x >= tabLeft && x <= tabRight && y >= 0 && y <= _tabHeight + (10f * _density))
        {
            return TouchZone.ResizeTab;
        }

        // 2. Check wheel distance
        if (dist <= _innerRadius)
        {
            return TouchZone.CenterButton;
        }
        else if (dist <= _outerRadius + (10f * _density))
        {
            if (_settings.ScrollMode == ScrollModeType.LogicalButtons)
            {
                return dy < 0 ? TouchZone.ScrollUpButton : TouchZone.ScrollDownButton;
            }
            return TouchZone.DialTrack;
        }

        return TouchZone.None;
    }

    private double CalculateAngle(float x, float y)
    {
        float dx = x - _centerX;
        float dy = y - _centerY;
        // In degrees: -180 to +180
        return Math.Atan2(dy, dx) * 180.0 / Math.PI;
    }

    private void PlayHapticTick()
    {
        if (!_settings.HapticFeedbackEnabled) return;

        try
        {
            if (OperatingSystem.IsAndroidVersionAtLeast(29))
            {
                _vibrator?.Vibrate(VibrationEffect.CreatePredefined(VibrationEffect.EffectClick));
            }
            else
            {
#pragma warning disable CA1422
                _vibrator?.Vibrate(15);
#pragma warning restore CA1422
            }
        }
        catch
        {
            PerformHapticFeedback(FeedbackConstants.ClockTick);
        }
    }

    public void WakeUpTemporarily()
    {
        CancelFadeOut();
        FadeInImmediately();
        ScheduleFadeOut();
    }

    public void ScheduleFadeOut()
    {
        CancelFadeOut();
        if (!_settings.AutoFadeEnabled)
        {
            Alpha = 1.0f;
            _isFadedOut = false;
            return;
        }

        long delayMillis = (long)(_settings.FadeDelaySeconds * 1000f);
        if (delayMillis < 200) delayMillis = 200;
        _fadeHandler.PostDelayed(_fadeOutRunnable, delayMillis);
    }

    public void ApplySettings()
    {
        if (Width > 0)
        {
            RecalculateGeometry(Width);
        }

        if (!_settings.AutoFadeEnabled)
        {
            CancelFadeOut();
            Alpha = 1.0f;
            _isFadedOut = false;
        }
        else if (_isFadedOut)
        {
            Alpha = _settings.FadeAlpha;
        }
        else
        {
            ScheduleFadeOut();
        }
        Invalidate();
    }

    private void CancelFadeOut()
    {
        _fadeHandler.RemoveCallbacks(_fadeOutRunnable);
    }

    private void FadeInImmediately()
    {
        if (_isFadedOut || Alpha < 0.99f)
        {
            Animate()?.Alpha(1.0f)?.SetDuration(150)?.Start();
            _isFadedOut = false;
        }
    }

    private void StartFadeOut()
    {
        if (!_settings.AutoFadeEnabled) return;

        float targetAlpha = _settings.FadeAlpha;
        Animate()?.Alpha(targetAlpha)?.SetDuration(400)?.Start();
        _isFadedOut = true;
    }

    private void StartButtonRepeat()
    {
        CancelButtonRepeat();
        _buttonRepeatHandler.PostDelayed(_buttonRepeatAction, RepeatInitialDelayMs);
    }

    private void CancelButtonRepeat()
    {
        _buttonRepeatHandler.RemoveCallbacks(_buttonRepeatAction);
    }

    private void OnButtonRepeat()
    {
        if (_activeRepeatZone == TouchZone.ScrollUpButton && _isPressedUp)
        {
            ScrollRequested?.Invoke(ScrollDirection.Backward, 1);
            PlayHapticTick();
            _buttonRepeatHandler.PostDelayed(_buttonRepeatAction, RepeatIntervalMs);
        }
        else if (_activeRepeatZone == TouchZone.ScrollDownButton && _isPressedDown)
        {
            ScrollRequested?.Invoke(ScrollDirection.Forward, 1);
            PlayHapticTick();
            _buttonRepeatHandler.PostDelayed(_buttonRepeatAction, RepeatIntervalMs);
        }
        else
        {
            CancelButtonRepeat();
        }
    }

    protected override void OnDetachedFromWindow()
    {
        CancelButtonRepeat();
        CancelFadeOut();
        base.OnDetachedFromWindow();
    }
}
