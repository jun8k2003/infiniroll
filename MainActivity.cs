using System;
using Android.AccessibilityServices;
using Android.App;
using Android.Content;
using Android.OS;
using Android.Provider;
using Android.Views.Accessibility;
using Android.Widget;

namespace Infiniroll;

[Activity(
    Label = "@string/app_name",
    MainLauncher = true,
    Theme = "@style/AppTheme")]
public class MainActivity : Activity
{
    private AppSettings? _settings;

    private TextView? _textStatus;
    private Button? _btnOpenAccessibility;

    // Scroll Mode
    private RadioButton? _radioScrollSmooth;
    private RadioButton? _radioScrollLogical;
    private RadioButton? _radioScrollButtons;
    private TextView? _textSmoothDistanceLabel;
    private SeekBar? _seekSmoothDistance;

    // General Preferences
    private Switch? _switchAutoFade;
    private TextView? _textFadeAlphaLabel;
    private SeekBar? _seekFadeAlpha;
    private TextView? _textFadeDelayLabel;
    private SeekBar? _seekFadeDelay;
    private TextView? _textNotchAngleLabel;
    private SeekBar? _seekNotchAngle;
    private TextView? _textWheelSizeLabel;
    private SeekBar? _seekWheelSize;
    private Switch? _switchHaptic;
    private Switch? _switchShowBackButton;
    private Switch? _switchShowSettingsButton;
    private Button? _btnResetPosition;
    private Button? _btnResetDefaults;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        SetContentView(Resource.Layout.activity_main);

        _settings = new AppSettings(this);

        BindViews();
        SetupListeners();
        LoadSettingsToUi();
        PopulateTestLabels();
    }

    protected override void OnResume()
    {
        base.OnResume();
        UpdateServiceStatus();
        WheelOverlayService.ServiceStateChanged += OnServiceStateChanged;
    }

    protected override void OnPause()
    {
        base.OnPause();
        WheelOverlayService.ServiceStateChanged -= OnServiceStateChanged;
    }

    private void BindViews()
    {
        _textStatus = FindViewById<TextView>(Resource.Id.text_service_status);
        _btnOpenAccessibility = FindViewById<Button>(Resource.Id.btn_open_accessibility);

        _radioScrollSmooth = FindViewById<RadioButton>(Resource.Id.radio_scroll_smooth);
        _radioScrollLogical = FindViewById<RadioButton>(Resource.Id.radio_scroll_logical);
        _radioScrollButtons = FindViewById<RadioButton>(Resource.Id.radio_scroll_buttons);
        _textSmoothDistanceLabel = FindViewById<TextView>(Resource.Id.text_smooth_distance_label);
        _seekSmoothDistance = FindViewById<SeekBar>(Resource.Id.seekbar_smooth_distance);

        _switchAutoFade = FindViewById<Switch>(Resource.Id.switch_auto_fade);
        _textFadeAlphaLabel = FindViewById<TextView>(Resource.Id.text_fade_alpha_label);
        _seekFadeAlpha = FindViewById<SeekBar>(Resource.Id.seekbar_fade_alpha);
        _textFadeDelayLabel = FindViewById<TextView>(Resource.Id.text_fade_delay_label);
        _seekFadeDelay = FindViewById<SeekBar>(Resource.Id.seekbar_fade_delay);
        _textNotchAngleLabel = FindViewById<TextView>(Resource.Id.text_notch_angle_label);
        _seekNotchAngle = FindViewById<SeekBar>(Resource.Id.seekbar_notch_angle);
        _textWheelSizeLabel = FindViewById<TextView>(Resource.Id.text_wheel_size_label);
        _seekWheelSize = FindViewById<SeekBar>(Resource.Id.seekbar_wheel_size);
        _switchHaptic = FindViewById<Switch>(Resource.Id.switch_haptic);
        _switchShowBackButton = FindViewById<Switch>(Resource.Id.switch_show_back_button);
        _switchShowSettingsButton = FindViewById<Switch>(Resource.Id.switch_show_settings_button);
        _btnResetPosition = FindViewById<Button>(Resource.Id.btn_reset_position);
        _btnResetDefaults = FindViewById<Button>(Resource.Id.btn_reset_defaults);
    }

    private void SetupListeners()
    {
        if (_btnOpenAccessibility != null)
        {
            _btnOpenAccessibility.Click += (s, e) =>
            {
                var intent = new Intent(Settings.ActionAccessibilitySettings);
                intent.AddFlags(ActivityFlags.NewTask);
                StartActivity(intent);
            };
        }

        if (_radioScrollSmooth != null && _settings != null)
        {
            _radioScrollSmooth.CheckedChange += (s, e) =>
            {
                if (e.IsChecked)
                {
                    _settings.ScrollMode = ScrollModeType.Smooth;
                }
            };
        }

        if (_radioScrollLogical != null && _settings != null)
        {
            _radioScrollLogical.CheckedChange += (s, e) =>
            {
                if (e.IsChecked)
                {
                    _settings.ScrollMode = ScrollModeType.Logical;
                }
            };
        }

        if (_radioScrollButtons != null && _settings != null)
        {
            _radioScrollButtons.CheckedChange += (s, e) =>
            {
                if (e.IsChecked)
                {
                    _settings.ScrollMode = ScrollModeType.LogicalButtons;
                }
            };
        }

        if (_seekSmoothDistance != null && _settings != null)
        {
            _seekSmoothDistance.ProgressChanged += (s, e) =>
            {
                if (!e.FromUser) return;
                int distanceDp = AppSettings.MinSmoothDistanceDp + e.Progress;
                _settings.SmoothDistanceDp = distanceDp;
                UpdateSmoothDistanceLabel(distanceDp);
            };
        }

        if (_switchAutoFade != null && _settings != null)
        {
            _switchAutoFade.CheckedChange += (s, e) =>
            {
                _settings.AutoFadeEnabled = e.IsChecked;
            };
        }

        if (_seekFadeAlpha != null && _settings != null)
        {
            _seekFadeAlpha.ProgressChanged += (s, e) =>
            {
                if (!e.FromUser) return;
                float alpha = (10 + e.Progress) / 100f;
                _settings.FadeAlpha = alpha;
                UpdateFadeAlphaLabel(alpha);
            };
        }

        if (_seekFadeDelay != null && _settings != null)
        {
            _seekFadeDelay.ProgressChanged += (s, e) =>
            {
                if (!e.FromUser) return;
                float seconds = 0.5f + (e.Progress * 0.1f);
                _settings.FadeDelaySeconds = seconds;
                UpdateFadeDelayLabel(seconds);
            };
        }

        if (_seekNotchAngle != null && _settings != null)
        {
            _seekNotchAngle.ProgressChanged += (s, e) =>
            {
                if (!e.FromUser) return;
                float angle = AppSettings.MinNotchAngle + e.Progress;
                _settings.NotchAngleDegrees = angle;
                UpdateNotchAngleLabel(angle);
            };
        }

        if (_seekWheelSize != null && _settings != null)
        {
            _seekWheelSize.ProgressChanged += (s, e) =>
            {
                if (!e.FromUser) return;
                int sizeDp = AppSettings.MinWheelSizeDp + e.Progress;
                _settings.WheelSizeDp = sizeDp;
                UpdateWheelSizeLabel(sizeDp);
            };
        }

        if (_switchHaptic != null && _settings != null)
        {
            _switchHaptic.CheckedChange += (s, e) =>
            {
                _settings.HapticFeedbackEnabled = e.IsChecked;
            };
        }

        if (_switchShowBackButton != null && _settings != null)
        {
            _switchShowBackButton.CheckedChange += (s, e) =>
            {
                _settings.ShowBackButtonEnabled = e.IsChecked;
            };
        }

        if (_switchShowSettingsButton != null && _settings != null)
        {
            _switchShowSettingsButton.CheckedChange += (s, e) =>
            {
                _settings.ShowSettingsButtonEnabled = e.IsChecked;
            };
        }

        if (_btnResetPosition != null && _settings != null)
        {
            _btnResetPosition.Click += (s, e) =>
            {
                _settings.ResetPositionAndSize();
                LoadSettingsToUi();
                Toast.MakeText(this, Resource.String.reset_success_msg, ToastLength.Short)?.Show();
            };
        }

        if (_btnResetDefaults != null && _settings != null)
        {
            _btnResetDefaults.Click += (s, e) =>
            {
                var builder = new AlertDialog.Builder(this);
                builder.SetTitle(Resource.String.reset_defaults_confirm_title);
                builder.SetMessage(Resource.String.reset_defaults_confirm_msg);
                builder.SetPositiveButton(Resource.String.dialog_reset, (dialog, which) =>
                {
                    if (_settings != null)
                    {
                        _settings.ResetAllSettingsToDefault();
                        LoadSettingsToUi();
                        Toast.MakeText(this, Resource.String.reset_defaults_success_msg, ToastLength.Short)?.Show();
                    }
                });
                builder.SetNegativeButton(Resource.String.dialog_cancel, (IDialogInterfaceOnClickListener?)null);
                builder.Create()?.Show();
            };
        }
    }

    private void LoadSettingsToUi()
    {
        if (_settings == null) return;

        if (_radioScrollSmooth != null && _radioScrollLogical != null && _radioScrollButtons != null)
        {
            switch (_settings.ScrollMode)
            {
                case ScrollModeType.Smooth:
                    _radioScrollSmooth.Checked = true;
                    break;
                case ScrollModeType.Logical:
                    _radioScrollLogical.Checked = true;
                    break;
                case ScrollModeType.LogicalButtons:
                    _radioScrollButtons.Checked = true;
                    break;
            }
        }

        if (_seekSmoothDistance != null)
        {
            int progress = _settings.SmoothDistanceDp - AppSettings.MinSmoothDistanceDp;
            _seekSmoothDistance.Progress = Math.Clamp(progress, 0, _seekSmoothDistance.Max);
            UpdateSmoothDistanceLabel(_settings.SmoothDistanceDp);
        }

        if (_switchAutoFade != null)
        {
            _switchAutoFade.Checked = _settings.AutoFadeEnabled;
        }

        if (_seekFadeAlpha != null)
        {
            int progress = (int)Math.Round((_settings.FadeAlpha * 100f) - 10);
            _seekFadeAlpha.Progress = Math.Clamp(progress, 0, _seekFadeAlpha.Max);
            UpdateFadeAlphaLabel(_settings.FadeAlpha);
        }

        if (_seekFadeDelay != null)
        {
            int progress = (int)Math.Round((_settings.FadeDelaySeconds - 0.5f) / 0.1f);
            _seekFadeDelay.Progress = Math.Clamp(progress, 0, _seekFadeDelay.Max);
            UpdateFadeDelayLabel(_settings.FadeDelaySeconds);
        }

        if (_seekNotchAngle != null)
        {
            int progress = (int)Math.Round(_settings.NotchAngleDegrees - AppSettings.MinNotchAngle);
            _seekNotchAngle.Progress = Math.Clamp(progress, 0, _seekNotchAngle.Max);
            UpdateNotchAngleLabel(_settings.NotchAngleDegrees);
        }

        if (_seekWheelSize != null)
        {
            int progress = _settings.WheelSizeDp - AppSettings.MinWheelSizeDp;
            _seekWheelSize.Progress = Math.Clamp(progress, 0, _seekWheelSize.Max);
            UpdateWheelSizeLabel(_settings.WheelSizeDp);
        }

        if (_switchHaptic != null)
        {
            _switchHaptic.Checked = _settings.HapticFeedbackEnabled;
        }

        if (_switchShowBackButton != null)
        {
            _switchShowBackButton.Checked = _settings.ShowBackButtonEnabled;
        }

        if (_switchShowSettingsButton != null)
        {
            _switchShowSettingsButton.Checked = _settings.ShowSettingsButtonEnabled;
        }
    }

    private void UpdateSmoothDistanceLabel(int distanceDp)
    {
        if (_textSmoothDistanceLabel != null)
        {
            _textSmoothDistanceLabel.Text = $"1ノッチあたりの移動量: {distanceDp} dp";
        }
    }

    private void UpdateFadeAlphaLabel(float alpha)
    {
        if (_textFadeAlphaLabel != null)
        {
            _textFadeAlphaLabel.Text = $"無操作時の不透明度: {(int)(alpha * 100f)}%";
        }
    }

    private void UpdateFadeDelayLabel(float seconds)
    {
        if (_textFadeDelayLabel != null)
        {
            _textFadeDelayLabel.Text = $"フェード待機時間: {seconds:F1} 秒";
        }
    }

    private void UpdateNotchAngleLabel(float angle)
    {
        if (_textNotchAngleLabel != null)
        {
            _textNotchAngleLabel.Text = $"回転感度（1ノッチあたりの角度）: {angle:F0}°";
        }
    }

    private void UpdateWheelSizeLabel(int sizeDp)
    {
        if (_textWheelSizeLabel != null)
        {
            _textWheelSizeLabel.Text = $"ホイールサイズ: {sizeDp} dp";
        }
    }

    private void OnServiceStateChanged()
    {
        RunOnUiThread(UpdateServiceStatus);
    }

    private void UpdateServiceStatus()
    {
        bool isServiceEnabled = IsAccessibilityServiceEnabled();

        if (_textStatus != null)
        {
            if (isServiceEnabled || WheelOverlayService.IsRunning)
            {
                _textStatus.Text = GetString(Resource.String.service_status_running);
                _textStatus.SetTextColor(Android.Graphics.Color.Rgb(76, 175, 80)); // Green
            }
            else
            {
                _textStatus.Text = GetString(Resource.String.service_status_stopped);
                _textStatus.SetTextColor(Android.Graphics.Color.Rgb(244, 67, 54)); // Red
            }
        }
    }

    private bool IsAccessibilityServiceEnabled()
    {
        var accessibilityManager = GetSystemService(AccessibilityService) as AccessibilityManager;
        if (accessibilityManager == null) return false;

        var runningServices = accessibilityManager.GetEnabledAccessibilityServiceList(FeedbackFlags.Generic);
        if (runningServices != null)
        {
            string packageName = PackageName ?? string.Empty;
            foreach (var service in runningServices)
            {
                if (service.ResolveInfo?.ServiceInfo?.PackageName?.Equals(packageName, StringComparison.OrdinalIgnoreCase) == true)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private void PopulateTestLabels()
    {
        var container = FindViewById<LinearLayout>(Resource.Id.container_test_labels);
        if (container == null) return;

        float density = Resources?.DisplayMetrics?.Density ?? 1.0f;
        int paddingVertical = (int)(12 * density);
        int paddingHorizontal = (int)(16 * density);
        int marginVertical = (int)(3 * density);
        float cornerRadius = 6f * density;

        for (int i = 1; i <= 256; i++)
        {
            // 0度〜360度で色相(Hue)を滑らかに変化させ、全体を通した美しいグラデーションを生成
            float hue = ((i - 1) / 255f) * 360f;
            var hsv = new float[] { hue, 0.78f, 0.65f };
            var color = Android.Graphics.Color.HSVToColor(hsv);

            var drawable = new Android.Graphics.Drawables.GradientDrawable();
            drawable.SetColor(color);
            drawable.SetCornerRadius(cornerRadius);

            var textView = new TextView(this)
            {
                Text = i == 1 
                    ? $"[Ⅰ - 001 / 256]  開始位置 (0.4%)" 
                    : i == 256
                    ? $"[256 / 256]  最下部ゴール！ (100.0%)"
                    : $"[#{i:D3} / 256]  動作確認ラベル  ({(i / 256f * 100):F1}%)",
                TextSize = 14,
            };
            textView.SetTypeface(null, Android.Graphics.TypefaceStyle.Bold);
            textView.SetTextColor(Android.Graphics.Color.Rgb(255, 255, 255));
            textView.SetShadowLayer(3f, 1f, 1f, Android.Graphics.Color.Argb(160, 0, 0, 0));
            textView.Background = drawable;
            textView.SetPadding(paddingHorizontal, paddingVertical, paddingHorizontal, paddingVertical);

            var layoutParams = new LinearLayout.LayoutParams(
                Android.Views.ViewGroup.LayoutParams.MatchParent,
                Android.Views.ViewGroup.LayoutParams.WrapContent)
            {
                TopMargin = marginVertical,
                BottomMargin = marginVertical
            };
            textView.LayoutParameters = layoutParams;

            container.AddView(textView);
        }
    }
}
