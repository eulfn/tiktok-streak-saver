using Feener.Services;
using Microsoft.Maui.Controls.Shapes;

namespace Feener.Pages;

[Microsoft.Maui.Controls.Internals.Preserve(AllMembers = true)]
public partial class ProfilePage : ContentPage
{
    private readonly SessionService _sessionService;
    private readonly SettingsService _settingsService;
    public ProfilePage()
    {
        InitializeComponent();
        _sessionService = new SessionService();
        _settingsService = new SettingsService();
    }

    private Color GetThemeColor(string key, string fallbackHex = "#92979E")
    {
        if (Application.Current != null && Application.Current.Resources.TryGetValue(key, out var resource) && resource is Color color)
            return color;
        return Color.FromArgb(fallbackHex);
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        this.Opacity = 1;
        this.TranslationY = 0;

        LoadProfilePhoto();

        // Load display name
        DisplayNameEntry.Text = _sessionService.GetDisplayName();

        ScheduleSwitch.IsToggled = _settingsService.IsScheduled();
        ScheduleOptionsPanel.IsVisible = ScheduleSwitch.IsToggled;
        FixedTimeSwitch.IsToggled = _settingsService.GetUseFixedTime();
        TimePickerRow.IsVisible = FixedTimeSwitch.IsToggled;
        ScheduleTimePicker.Time = new TimeSpan(
            _settingsService.GetFixedTimeHour(),
            _settingsService.GetFixedTimeMinute(), 0);

        SkipUnreachableSwitch.IsToggled = _settingsService.GetSkipUnreachableUsers();
        RandomizeMessagesSwitch.IsToggled = _settingsService.GetRandomizeNormalMessages();

        // Load version
        VersionLabel.Text = $"v{AppInfo.Current.VersionString}";

        // Update UI based on current session status
        UpdateLoginButtonState(_sessionService.IsSessionValid());
    }

    // ─── Profile Photo ──────────────────────────────────────────────────────────

    private void LoadProfilePhoto()
    {
        var photoPath = _sessionService.GetProfileImagePath();
        if (!string.IsNullOrEmpty(photoPath) && System.IO.File.Exists(photoPath))
        {
            ProfilePhoto.Source = ImageSource.FromFile(photoPath);
            ProfilePhoto.IsVisible = true;
            ProfileEmoji.IsVisible = false;
            // Clip the image to the circle
            ProfilePhoto.Clip = new EllipseGeometry
            {
                Center = new Point(28, 28),
                RadiusX = 28,
                RadiusY = 28
            };
        }
        else
        {
            ProfilePhoto.IsVisible = false;
            ProfileEmoji.IsVisible = true;
        }
    }

    private async void OnProfilePhotoTapped(object? sender, TappedEventArgs e)
    {
        try
        {
            var result = await MediaPicker.Default.PickPhotoAsync(new MediaPickerOptions
            {
                Title = "Please pick a photo"
            });

            if (result != null)
            {
                var newFile = System.IO.Path.Combine(FileSystem.AppDataDirectory, result.FileName);
                // Delete the previous profile photo if it exists and differs
                var oldPath = _sessionService.GetProfileImagePath();
                if (!string.IsNullOrEmpty(oldPath) && oldPath != newFile && System.IO.File.Exists(oldPath))
                {
                    try { System.IO.File.Delete(oldPath); } catch { }
                }

                using (var stream = await result.OpenReadAsync())
                using (var newStream = System.IO.File.Create(newFile))
                    await stream.CopyToAsync(newStream);

                _sessionService.SetProfileImagePath(newFile);
                LoadProfilePhoto();
            }
        }
        catch (Exception ex)
        {
            await DisplayAlert("Photo", $"Could not pick photo: {ex.Message}", "OK");
        }
    }

    // ─── Display Name ───────────────────────────────────────────────────────────

    private void OnDisplayNameChanged(object? sender, EventArgs e)
    {
        _sessionService.SetDisplayName(DisplayNameEntry.Text ?? "User");
    }

    private void UpdateLoginButtonState(bool isSessionValid)
    {
        if (isSessionValid)
        {
            LoginButton.Text = "Session OK";
            LoginButton.BackgroundColor = GetThemeColor("Success", "#22946E");
            LoginButton.IsEnabled = false;
            SessionDot.BackgroundColor = GetThemeColor("Success", "#22946E");
            SessionStatusLabel.Text = "Session active";
            var lastCheck = _sessionService.GetLastCheckTime();
            SessionLastCheckLabel.Text = lastCheck.HasValue ? $"Verified {lastCheck.Value:MMM dd, HH:mm}" : "";
        }
        else
        {
            LoginButton.Text = "Login to TikTok";
            LoginButton.BackgroundColor = GetThemeColor("Primary", "#FE2C55");
            LoginButton.IsEnabled = true;
            SessionDot.BackgroundColor = GetThemeColor("Error", "#9C2121");
            SessionStatusLabel.Text = "Not logged in";
            SessionLastCheckLabel.Text = "Tap below to login";
        }

    }

    // ─── Actions ────────────────────────────────────────────────────────────────

    private async void OnLoginClicked(object? sender, EventArgs e)
    {
        await Navigation.PushAsync(new LoginPage());
    }

    private void OnScheduleToggled(object? sender, ToggledEventArgs e)
    {
        ScheduleOptionsPanel.IsVisible = e.Value;
#if ANDROID
        var context = Platform.CurrentActivity ?? Android.App.Application.Context;
        if (e.Value)
            Feener.Platforms.Android.StreakScheduler.ScheduleNextRun(context);
        else
            Feener.Platforms.Android.StreakScheduler.CancelSchedule(context);
#endif
    }

    private void OnFixedTimeToggled(object? sender, ToggledEventArgs e)
    {
        _settingsService.SetUseFixedTime(e.Value);
        TimePickerRow.IsVisible = e.Value;
#if ANDROID
        if (_settingsService.IsScheduled())
        {
            var context = Platform.CurrentActivity ?? Android.App.Application.Context;
            Feener.Platforms.Android.StreakScheduler.ScheduleNextRun(context);
        }
#endif
    }

    private void OnTimePickerChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(TimePicker.Time)) return;
        var time = ScheduleTimePicker.Time;
        _settingsService.SetFixedTimeHour(time.Hours);
        _settingsService.SetFixedTimeMinute(time.Minutes);
#if ANDROID
        if (_settingsService.IsScheduled())
        {
            var context = Platform.CurrentActivity ?? Android.App.Application.Context;
            Feener.Platforms.Android.StreakScheduler.ScheduleNextRun(context);
        }
#endif
    }

    private void OnSkipUnreachableToggled(object? sender, ToggledEventArgs e)
    {
        _settingsService.SetSkipUnreachableUsers(e.Value);
    }

    private void OnRandomizeMessagesToggled(object? sender, ToggledEventArgs e)
    {
        _settingsService.SetRandomizeNormalMessages(e.Value);
    }

    private async void OnAboutClicked(object? sender, EventArgs e)
    {
        string currentVersion = AppInfo.Current.VersionString;
        await Navigation.PushModalAsync(new AboutPopupPage(
            "About Feener", currentVersion, string.Empty, false));
    }

    private async void OnLogoutClicked(object? sender, EventArgs e)
    {
        bool confirm = await DisplayAlert("Logout", "This will clear your TikTok session. You'll need to login again before running automations.", "Logout", "Cancel");
        if (confirm)
        {
            _sessionService.ClearSession();
            UpdateLoginButtonState(false);
        }
    }
}
