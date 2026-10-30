using System;
using Android.Content;
using Android.Preferences;

namespace Infiniroll;

public enum ScrollModeType
{
    Smooth = 0,         // 超スムーズ（微小ジェスチャー・ピクセル追従）
    Logical = 1,        // 論理スクロール（ホイール回転・完全誤タップ防止）
    LogicalButtons = 2  // ボタン方式（▲/▼ボタン長押し・論理スクロール）
}

public class AppSettings
{
    private const string PrefName = "infiniroll_preferences";
    private const string KeyScrollMode = "pref_scroll_mode";
    private const string KeySmoothDistance = "pref_smooth_distance";
    private const string KeyAutoFade = "pref_auto_fade";
    private const string KeyFadeDelay = "pref_fade_delay";
    private const string KeyFadeAlpha = "pref_fade_alpha";
    private const string KeyNotchAngle = "pref_notch_angle";
    private const string KeyHaptic = "pref_haptic";
    private const string KeyShowBackButton = "pref_show_back_button";
    private const string KeyShowSettingsButton = "pref_show_settings_button";
    private const string KeyWheelSize = "pref_wheel_size";
    private const string KeyWheelPosX = "pref_wheel_pos_x";
    private const string KeyWheelPosY = "pref_wheel_pos_y";

    public const int DefaultWheelSizeDp = 200;
    public const int MinWheelSizeDp = 120;
    public const int MaxWheelSizeDp = 350;
    public const float DefaultNotchAngle = 10f;
    public const float DefaultFadeDelay = 1.0f;
    public const float DefaultFadeAlpha = 0.25f;
    public const int DefaultSmoothDistanceDp = 20;
    public const int MinSmoothDistanceDp = 5;
    public const int MaxSmoothDistanceDp = 150;
    public const float MinNotchAngle = 3f;
    public const float MaxNotchAngle = 45f;

    private readonly ISharedPreferences _prefs;

    public static event System.Action? SettingsChanged;
    public static event System.Action? PositionResetRequested;

    public AppSettings(Context context)
    {
        _prefs = context.GetSharedPreferences(PrefName, FileCreationMode.Private)!;
    }

    public ScrollModeType ScrollMode
    {
        get => (ScrollModeType)_prefs.GetInt(KeyScrollMode, (int)ScrollModeType.Smooth);
        set
        {
            using var editor = _prefs.Edit();
            editor?.PutInt(KeyScrollMode, (int)value)?.Apply();
            NotifyChanged();
        }
    }

    public int SmoothDistanceDp
    {
        get => _prefs.GetInt(KeySmoothDistance, DefaultSmoothDistanceDp);
        set
        {
            int clamped = Math.Clamp(value, MinSmoothDistanceDp, MaxSmoothDistanceDp);
            using var editor = _prefs.Edit();
            editor?.PutInt(KeySmoothDistance, clamped)?.Apply();
            NotifyChanged();
        }
    }

    public bool AutoFadeEnabled
    {
        get => _prefs.GetBoolean(KeyAutoFade, true);
        set
        {
            using var editor = _prefs.Edit();
            editor?.PutBoolean(KeyAutoFade, value)?.Apply();
            NotifyChanged();
        }
    }

    public float FadeDelaySeconds
    {
        get => _prefs.GetFloat(KeyFadeDelay, DefaultFadeDelay);
        set
        {
            using var editor = _prefs.Edit();
            editor?.PutFloat(KeyFadeDelay, value)?.Apply();
            NotifyChanged();
        }
    }

    public float FadeAlpha
    {
        get => _prefs.GetFloat(KeyFadeAlpha, DefaultFadeAlpha);
        set
        {
            float clamped = Math.Clamp(value, 0.05f, 0.85f);
            using var editor = _prefs.Edit();
            editor?.PutFloat(KeyFadeAlpha, clamped)?.Apply();
            NotifyChanged();
        }
    }

    public float NotchAngleDegrees
    {
        get => _prefs.GetFloat(KeyNotchAngle, DefaultNotchAngle);
        set
        {
            float clamped = Math.Clamp(value, MinNotchAngle, MaxNotchAngle);
            using var editor = _prefs.Edit();
            editor?.PutFloat(KeyNotchAngle, clamped)?.Apply();
            NotifyChanged();
        }
    }

    public bool HapticFeedbackEnabled
    {
        get => _prefs.GetBoolean(KeyHaptic, true);
        set
        {
            using var editor = _prefs.Edit();
            editor?.PutBoolean(KeyHaptic, value)?.Apply();
            NotifyChanged();
        }
    }

    public bool ShowBackButtonEnabled
    {
        get => _prefs.GetBoolean(KeyShowBackButton, false);
        set
        {
            using var editor = _prefs.Edit();
            editor?.PutBoolean(KeyShowBackButton, value)?.Apply();
            NotifyChanged();
        }
    }

    public bool ShowSettingsButtonEnabled
    {
        get => _prefs.GetBoolean(KeyShowSettingsButton, false);
        set
        {
            using var editor = _prefs.Edit();
            editor?.PutBoolean(KeyShowSettingsButton, value)?.Apply();
            NotifyChanged();
        }
    }

    public int WheelSizeDp
    {
        get => _prefs.GetInt(KeyWheelSize, DefaultWheelSizeDp);
        set
        {
            int clamped = Math.Clamp(value, MinWheelSizeDp, MaxWheelSizeDp);
            using var editor = _prefs.Edit();
            editor?.PutInt(KeyWheelSize, clamped)?.Apply();
            NotifyChanged();
        }
    }

    public bool HasSavedWheelPosition => _prefs.Contains(KeyWheelPosX) && _prefs.Contains(KeyWheelPosY);

    public int WheelPosX
    {
        get => _prefs.GetInt(KeyWheelPosX, -1);
        set
        {
            using var editor = _prefs.Edit();
            editor?.PutInt(KeyWheelPosX, value)?.Apply();
        }
    }

    public int WheelPosY
    {
        get => _prefs.GetInt(KeyWheelPosY, -1);
        set
        {
            using var editor = _prefs.Edit();
            editor?.PutInt(KeyWheelPosY, value)?.Apply();
        }
    }

    public void ResetPositionAndSize()
    {
        using (var editor = _prefs.Edit())
        {
            editor?.Remove(KeyWheelPosX)?
                   .Remove(KeyWheelPosY)?
                   .PutInt(KeyWheelSize, DefaultWheelSizeDp)?
                   .Apply();
        }
        
        PositionResetRequested?.Invoke();
        NotifyChanged();
    }

    public void ResetAllSettingsToDefault()
    {
        using (var editor = _prefs.Edit())
        {
            editor?.Clear()?.Apply();
        }

        PositionResetRequested?.Invoke();
        NotifyChanged();
    }

    private static void NotifyChanged()
    {
        SettingsChanged?.Invoke();
    }
}
