using Microsoft.Maui.Controls.Shapes;
using Feener.Models;
using Feener.Services;
using Feener.Views;

namespace Feener.Pages;

[Microsoft.Maui.Controls.Internals.Preserve(AllMembers = true)]
public partial class DashboardPage : ContentPage
{
    private readonly SettingsService _settingsService;
    private readonly SessionService _sessionService;
    private readonly UpdateService _updateService;
    private bool _isCheckingForUpdates = false;
    private bool _isAppInForeground = false;
    private IDispatcherTimer? _statusTimer;
    private readonly BurstProgressDrawable _burstProgressDrawable;
    private readonly NormalProgressDrawable _normalProgressDrawable;

    public DashboardPage()
    {
        InitializeComponent();
        _settingsService = new SettingsService();
        _sessionService = new SessionService();
        _updateService = new UpdateService();
        
        _burstProgressDrawable = new BurstProgressDrawable();
        BurstProgressGraphicsView.Drawable = _burstProgressDrawable;

        _normalProgressDrawable = new NormalProgressDrawable();
        OverviewProgressGraphicsView.Drawable = _normalProgressDrawable;
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
        _isAppInForeground = true;
        this.Opacity = 1;
        this.TranslationY = 0;

        // Update greeting
        GreetingLabel.Text = $"Hi, {_sessionService.GetDisplayName()}";

        // Load profile photo
        LoadProfilePhoto();

        // Update session indicator
        UpdateSessionIndicator();

        LoadSettings();
        UpdateStatus();

        // Check global session
        CheckGlobalSessionStatus();

        await EvaluatePermissionsAsync();

        if (_statusTimer == null)
        {
            _statusTimer = Dispatcher.CreateTimer();
            _statusTimer.Interval = TimeSpan.FromSeconds(1);
            _statusTimer.Tick += OnStatusTimerTick;
        }
        _statusTimer.Start();
        OnStatusTimerTick(null, EventArgs.Empty);

        _ = CheckStartupPopupAsync();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _isAppInForeground = false;
        if (_statusTimer != null)
        {
            _statusTimer.Stop();
            _statusTimer.Tick -= OnStatusTimerTick;
            _statusTimer = null;
        }
    }

    private async void OnCheckProfileTapped(object? sender, EventArgs e)
    {
        await Shell.Current.GoToAsync("//ProfilePage");
    }

    // ─── Profile Photo ──────────────────────────────────────────────────────────

    private void LoadProfilePhoto()
    {
        var photoPath = _sessionService.GetProfileImagePath();
        if (!string.IsNullOrEmpty(photoPath) && System.IO.File.Exists(photoPath))
        {
            ProfileAvatarImage.Source = ImageSource.FromFile(photoPath);
            ProfileAvatarImage.IsVisible = true;
            ProfileAvatarEmoji.IsVisible = false;
            // Clip the image to the circle
            ProfileAvatarImage.Clip = new EllipseGeometry
            {
                Center = new Point(22, 22),
                RadiusX = 22,
                RadiusY = 22
            };
        }
        else
        {
            ProfileAvatarImage.IsVisible = false;
            ProfileAvatarEmoji.IsVisible = true;
        }
    }

    // ─── Session ────────────────────────────────────────────────────────────────

    private void UpdateSessionIndicator()
    {
        bool valid = _sessionService.IsSessionValid();
        MasterRunButton.IsEnabled = valid;
        MasterRunButton.Opacity = valid ? 1.0 : 0.5;
        if (!valid && !MasterRunButton.Text.Contains("Login Required"))
        {
            MasterRunButton.Text = "Login Required";
        }
    }



    private void CheckGlobalSessionStatus()
    {
        // Direct Cookie Check: No network, instant result.
        bool isValid = TikTokWebViewHelper.HasValidSessionCookie();
        _sessionService.SetSessionValid(isValid);
        UpdateSessionIndicator();
    }

    private void OnStatusTimerTick(object? sender, EventArgs e)
    {
        bool isRunning = false;
#if ANDROID
        isRunning = Feener.Platforms.Android.Services.StreakService.IsRunning;
#endif
        RunButtonsContainer.IsVisible = !isRunning;
        StopServiceButton.IsVisible = isRunning;
        
        // ─── The "Running" State Lock ───
        MessageEditor.IsEnabled = !isRunning;
        MessageEditor.Opacity = isRunning ? 0.6 : 1.0;
        
        BurstTargetUserEntry.IsEnabled = !isRunning;
        BurstTargetUserEntry.Opacity = isRunning ? 0.6 : 1.0;
        
        BurstDailyLimitEntry.IsEnabled = !isRunning;
        BurstDailyLimitEntry.Opacity = isRunning ? 0.6 : 1.0;
        
        foreach (var child in BurstMessagesStack.Children)
        {
            if (child is Border b && b.Content is Grid g)
            {
                if (g.Children.Count > 0 && g.Children[0] is Editor ed) { ed.IsEnabled = !isRunning; ed.Opacity = isRunning ? 0.6 : 1.0; }
                if (g.Children.Count > 1 && g.Children[1] is Button btn) btn.IsVisible = !isRunning;
            }
        }
        AddBurstMessageButton.IsVisible = !isRunning && BurstMessagesStack.Children.Count < 5;

        UpdateStatus();
        UpdateBurstPlanDisplay(isRunning);
    }

    // ─── Update / Startup Popup Logic ───

    private static string NormalizeVersion(string raw)
        => raw.StartsWith("v", StringComparison.OrdinalIgnoreCase) ? raw.Substring(1) : raw;

    private async Task CheckStartupPopupAsync()
    {
        if (_isCheckingForUpdates) return;
        _isCheckingForUpdates = true;
        try
        {
            if (!_isAppInForeground) return;
            if (Navigation.ModalStack.Any(p => p is AboutPopupPage)) return;

            string currentVersion = NormalizeVersion(AppInfo.Current.VersionString);

            bool updateJustInstalled = Preferences.Default.Get("UpdateJustInstalled", false);
            if (updateJustInstalled)
            {
                Preferences.Default.Remove("UpdateJustInstalled");
                Preferences.Default.Set("LastAppVersionSeen", currentVersion);
                _isCheckingForUpdates = false;
                await CheckUpdateOnlyAsync();
                return;
            }

            string lastAppSeen = NormalizeVersion(Preferences.Default.Get("LastAppVersionSeen", string.Empty));
            if (string.IsNullOrEmpty(lastAppSeen) || lastAppSeen != currentVersion)
            {
                await MainThread.InvokeOnMainThreadAsync(async () =>
                    await Navigation.PushModalAsync(new AboutPopupPage(
                        "Welcome to Feener", currentVersion, string.Empty, false)));
                return;
            }

            _isCheckingForUpdates = false;
            await CheckUpdateOnlyAsync();
        }
        catch { }
        finally { _isCheckingForUpdates = false; }
    }

    private async Task CheckUpdateOnlyAsync()
    {
        if (_isCheckingForUpdates) return;
        _isCheckingForUpdates = true;
        try
        {
            if (!_isAppInForeground) return;
            if (Navigation.ModalStack.Any(p => p is AboutPopupPage)) return;

            string currentVersion = NormalizeVersion(AppInfo.Current.VersionString);
            string lastRemoteSeen = NormalizeVersion(Preferences.Default.Get("LastRemoteVersionSeen", string.Empty));

            var updateCheck = await _updateService.CheckForUpdatesAsync();
            if (updateCheck == null || !updateCheck.HasUpdate) return;

            string remoteVersion = NormalizeVersion(updateCheck.LatestVersion);
            if (remoteVersion == lastRemoteSeen || remoteVersion == currentVersion) return;
            if (Navigation.ModalStack.Any(p => p is AboutPopupPage)) return;

            await MainThread.InvokeOnMainThreadAsync(async () =>
                await Navigation.PushModalAsync(new AboutPopupPage(
                    "Update Available!", remoteVersion, updateCheck.Changelog, true, updateCheck.ApkDownloadUrl)));
        }
        catch { }
        finally { _isCheckingForUpdates = false; }
    }

    // ─── Settings / Mode Switching ───

    private void LoadSettings()
    {
        MessageEditor.Text = _settingsService.GetMessageText();

        // Reflect randomize toggle state on the message editor
        var isRandomized = _settingsService.GetRandomizeNormalMessages();
        MessageEditor.IsEnabled = !isRandomized;
        MessageEditorBorder.Opacity = isRandomized ? 0.4 : 1.0;
        MessageEditorHint.Text = isRandomized
            ? "Randomized messages enabled — 50 built-in variants"
            : "Message sent to each friend during a streak run";

        var isBurstActive = _settingsService.IsBurstModeActive();
        if (isBurstActive) SetBurstModeUI();
        else SetNormalModeUI();
        LoadBurstMessages();
        BurstTargetUserEntry.Text = _settingsService.GetBurstTargetUsername();
        BurstDailyLimitEntry.Text = _settingsService.GetBurstDailyLimit().ToString();
        UpdateBurstPlanDisplay();
    }

    private void OnNormalModeTapped(object? sender, TappedEventArgs e)
    {
        _settingsService.SetBurstModeActive(false);
        SetNormalModeUI();
    }

    private void OnBurstModeTapped(object? sender, TappedEventArgs e)
    {
        _settingsService.SetBurstModeActive(true);
        SetBurstModeUI();
    }

    private void SetNormalModeUI()
    {
        if (BurstModeContainer.IsVisible)
        {
            BurstModeContainer.IsVisible = false;
            BurstModeContainer.Opacity = 0;
        }
        NormalModeTabBorder.BackgroundColor = GetThemeColor("Primary", "#FE2C55");
        NormalModeTabLabel.TextColor = GetThemeColor("White", "#FFFFFF");
        BurstModeTabBorder.BackgroundColor = Colors.Transparent;
        BurstModeTabLabel.TextColor = GetThemeColor("Gray600", "#4B5563");
        NormalModeContainer.IsVisible = true;
        NormalModeContainer.Opacity = 1;
        MasterRunButton.Text = "Run Normal";
        MasterRunButton.BackgroundColor = GetThemeColor("Primary", "#FE2C55");
    }

    private void SetBurstModeUI()
    {
        if (NormalModeContainer.IsVisible)
        {
            NormalModeContainer.IsVisible = false;
            NormalModeContainer.Opacity = 0;
        }
        BurstModeTabBorder.BackgroundColor = GetThemeColor("BurstAccent", "#8B5CF6");
        BurstModeTabLabel.TextColor = Colors.White;
        NormalModeTabBorder.BackgroundColor = Colors.Transparent;
        NormalModeTabLabel.TextColor = GetThemeColor("Gray600", "#4B5563");
        BurstModeContainer.IsVisible = true;
        BurstModeContainer.Opacity = 1;
        MasterRunButton.Text = "Run Burst";
        MasterRunButton.BackgroundColor = GetThemeColor("BurstAccent", "#8B5CF6");
    }

    // ─── Burst Plan / Messages ───

    private int _lockedDailyLimit = 0;

    private void UpdateBurstPlanDisplay(bool isRunning = false)
    {
        var dailyLimit = isRunning && _lockedDailyLimit > 0 ? _lockedDailyLimit : _settingsService.GetBurstDailyLimit();
        var dailySent = _settingsService.GetBurstDailySentCount();
        var remaining = Math.Max(0, dailyLimit - dailySent);
        
        int avgChunk = (SettingsService.BurstChunkSizeMin + SettingsService.BurstChunkSizeMax) / 2;
        int sessions = remaining > 0 ? (int)Math.Ceiling((double)remaining / avgChunk) : 0;
        
        int hibernationAvg = (SettingsService.BurstHibernationMinMs + SettingsService.BurstHibernationMaxMs) / 2;
        int totalSeconds = sessions > 1 ? (sessions - 1) * (hibernationAvg / 1000) : 0;
        
        var hours = totalSeconds / 3600;
        var partMins = (totalSeconds % 3600) / 60;
        var partSecs = totalSeconds % 60;
        string timeStr = hours > 0 ? $"~{hours}h {partMins}m" : $"~{partMins}m {partSecs}s";

        // Update progress label
        BurstDailyProgressLabel.Text = $"{dailySent}/{dailyLimit}";

        // Update linear progress bar
        float progress = dailyLimit > 0 ? (float)dailySent / dailyLimit : 0;
        _burstProgressDrawable.Progress = progress;
        _burstProgressDrawable.TotalSessions = sessions > 0 ? sessions : 1;
        _burstProgressDrawable.IsDarkTheme = Application.Current?.RequestedTheme == AppTheme.Dark;
        BurstProgressGraphicsView.Invalidate();

        // Update stat labels
        BurstRemainingLabel.Text = remaining > 0 ? remaining.ToString() : "0";
        BurstSessionsLabel.Text = remaining > 0 ? $"~{sessions}" : "0";
        BurstTimeEstimateLabel.Text = remaining > 0 ? timeStr : "0m 0s";

        // Plan summary using visual hierarchy
        if (remaining > 0)
        {
            BurstPlanStack.IsVisible = true;
            BurstPlanCapReachedLabel.IsVisible = false;
            BurstPlanRemainingValue.Text = remaining.ToString();
            BurstPlanSessionsValue.Text = $"~{sessions}";
        }
        else
        {
            BurstPlanStack.IsVisible = false;
            BurstPlanCapReachedLabel.IsVisible = true;
        }
    }

    private async void OnBurstLimitChanged(object? sender, EventArgs e)
    {
        if (int.TryParse(BurstDailyLimitEntry.Text, out int newLimit))
        {
            if (newLimit > SettingsService.BurstMaxDailyCeiling)
                await DisplayAlert("Limit Capped", $"The daily burst limit cannot exceed {SettingsService.BurstMaxDailyCeiling} messages for security and anti-spam reasons.", "OK");
            _settingsService.SetBurstDailyLimit(newLimit);
            BurstDailyLimitEntry.Text = _settingsService.GetBurstDailyLimit().ToString();
            UpdateBurstPlanDisplay();
        }
    }

    private void LoadBurstMessages()
    {
        BurstMessagesStack.Children.Clear();
        var msgs = _settingsService.GetBurstMessages();
        if (msgs.Count == 0) msgs.Add(SettingsService.DefaultMessage);
        foreach (var m in msgs) AddBurstMessageEditorUI(m);
        UpdateAddBurstMessageButtonVisibility();
    }

    private void AddBurstMessageEditorUI(string initialText)
    {
        var border = new Border
        {
            Stroke = GetThemeColor("BorderColorLight", "#E5E5E5"),
            StrokeThickness = 1,
            StrokeShape = new RoundRectangle { CornerRadius = 14 },
            Margin = new Thickness(0, 0, 0, 8)
        };
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitionCollection
            {
                new ColumnDefinition { Width = GridLength.Star },
                new ColumnDefinition { Width = GridLength.Auto }
            }
        };
        var editor = new Editor { Text = initialText, Placeholder = "Enter burst message...", HeightRequest = 60, Margin = new Thickness(12, 8) };
        editor.TextChanged += OnBurstSettingsChanged;
        var removeBtn = new Border
        {
            BackgroundColor = Colors.Transparent,
            StrokeThickness = 0,
            Padding = 12,
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Center
        };
        var deletePath = new Microsoft.Maui.Controls.Shapes.Path
        {
            Data = (Microsoft.Maui.Controls.Shapes.Geometry)(new Microsoft.Maui.Controls.Shapes.PathGeometryConverter().ConvertFromInvariantString("M15,3H9V4H3V6H21V4H15V3M5,7V20A2,2 0 0,0 7,22H17A2,2 0 0,0 19,20V7H5M7,20V9H17V20H7Z") ?? new Microsoft.Maui.Controls.Shapes.PathGeometry()),
            Fill = GetThemeColor("Gray500", "#6B7280"),
            Aspect = Stretch.Uniform,
            HeightRequest = 18,
            WidthRequest = 18,
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Center
        };
        removeBtn.Content = deletePath;

        var tapGesture = new TapGestureRecognizer();
        tapGesture.Tapped += (s, e) =>
        {
            if (BurstMessagesStack.Children.Count > 1) { BurstMessagesStack.Children.Remove(border); SaveBurstSettings(); UpdateAddBurstMessageButtonVisibility(); }
            else DisplayAlert("Limit Reached", "You must have at least one burst message.", "OK");
        };
        removeBtn.GestureRecognizers.Add(tapGesture);

#if WINDOWS || MACCATALYST
        var pointerGesture = new PointerGestureRecognizer();
        pointerGesture.PointerEntered += (s, e) => deletePath.Fill = GetThemeColor("DeleteColor", "#EF4444");
        pointerGesture.PointerExited += (s, e) => deletePath.Fill = GetThemeColor("Gray500", "#6B7280");
        removeBtn.GestureRecognizers.Add(pointerGesture);
#endif

        grid.Children.Add(editor); Grid.SetColumn(editor, 0);
        grid.Children.Add(removeBtn); Grid.SetColumn(removeBtn, 1);
        border.Content = grid;
        BurstMessagesStack.Children.Add(border);
    }

    private void OnAddBurstMessageClicked(object? sender, EventArgs e)
    {
        if (BurstMessagesStack.Children.Count < 5) { AddBurstMessageEditorUI(""); SaveBurstSettings(); UpdateAddBurstMessageButtonVisibility(); }
    }

    private void UpdateAddBurstMessageButtonVisibility() => AddBurstMessageButton.IsVisible = BurstMessagesStack.Children.Count < 5;

    private void OnBurstSettingsChanged(object? sender, TextChangedEventArgs e) => SaveBurstSettings();

    private void SaveBurstSettings()
    {
        _settingsService.SetBurstTargetUsername(BurstTargetUserEntry.Text?.Trim() ?? "");
        var messages = new List<string>();
        foreach (Border border in BurstMessagesStack.Children)
        {
            if (border.Content is Grid grid && grid.Children[0] is Editor editor)
            {
                var text = editor.Text?.Trim() ?? "";
                if (!string.IsNullOrEmpty(text)) messages.Add(text);
            }
        }
        if (messages.Count == 0) messages.Add(SettingsService.DefaultMessage);
        _settingsService.SetBurstMessages(messages);
    }

    // ─── Status ─────────────────────────────────────────────────────────────────

    private void UpdateStatus()
    {
        var isScheduled = _settingsService.IsScheduled();
        var lastRun = _settingsService.GetLastRunTime();
        if (lastRun.HasValue)
        {
            var timeSince = DateTime.Now - lastRun.Value;
            if (timeSince.TotalMinutes < 60) LastRunLabel.Text = $"{(int)timeSince.TotalMinutes} minutes ago";
            else if (timeSince.TotalHours < 24) LastRunLabel.Text = $"{(int)timeSince.TotalHours} hours ago";
            else LastRunLabel.Text = lastRun.Value.ToString("MMM dd, HH:mm");
        }
        else LastRunLabel.Text = "Never";

        if (isScheduled)
        {
            var nextRun = _settingsService.GetNextRunTime();
            var timeUntil = nextRun - DateTime.Now;
            if (timeUntil.TotalMinutes < 60) NextRunLabel.Text = $"In {(int)timeUntil.TotalMinutes} minutes";
            else if (timeUntil.TotalHours < 24) NextRunLabel.Text = $"In {(int)timeUntil.TotalHours} hours";
            else NextRunLabel.Text = nextRun.ToString("MMM dd, HH:mm");
        }
        else NextRunLabel.Text = "Not scheduled";

        var history = _settingsService.GetRunHistory();
        var latestResult = history.FirstOrDefault(r => !r.IsBurstMode);
        var currentEnabledFriends = _settingsService.GetEnabledFriends();
        
        bool ranToday = latestResult != null && latestResult.RunTime.Date == DateTime.Today;
        
        int sentToday = 0;
        int successPercent = 0;
        int remainingToday = currentEnabledFriends.Count;
        string progressText = $"0/{currentEnabledFriends.Count}";
        float progressFraction = 0f;

        if (ranToday && latestResult != null)
        {
            sentToday = latestResult.FriendResults.Count(r => r.Success);
            int totalAttempted = latestResult.FriendResults.Count;
            
            if (totalAttempted > 0)
            {
                successPercent = (int)((double)sentToday / totalAttempted * 100);
            }
            else
            {
                successPercent = 0;
            }

            remainingToday = Math.Max(0, currentEnabledFriends.Count - sentToday);
            progressText = $"{sentToday}/{currentEnabledFriends.Count}";
            progressFraction = currentEnabledFriends.Count > 0 ? (float)sentToday / currentEnabledFriends.Count : 0f;
        }

        OverviewSentLabel.Text = sentToday.ToString();
        OverviewSuccessLabel.Text = ranToday ? $"{successPercent}%" : "--";
        OverviewRemainingLabel.Text = remainingToday.ToString();

        OverviewProgressLabel.Text = progressText;
        _normalProgressDrawable.Progress = progressFraction;
        _normalProgressDrawable.IsDarkTheme = Application.Current?.RequestedTheme == AppTheme.Dark;
        OverviewProgressGraphicsView.Invalidate();
    }

    // ─── Actions ────────────────────────────────────────────────────────────────

    private void OnMessageChanged(object? sender, TextChangedEventArgs e)
    {
        if (!string.IsNullOrEmpty(e.NewTextValue)) _settingsService.SetMessageText(e.NewTextValue);
    }

    private async void OnMasterRunClicked(object? sender, EventArgs e)
    {

        bool isBurstMode = _settingsService.IsBurstModeActive();
        if (isBurstMode)
        {
            var target = _settingsService.GetBurstTargetUsername();
            if (string.IsNullOrWhiteSpace(target)) { await DisplayAlert("No Target", "Please enter a target username for Burst Mode.", "OK"); return; }
            var plan = _settingsService.CalculateBurstPlan();
            if (plan.remaining == 0) { await DisplayAlert("Daily Cap Reached", $"You've already sent {plan.dailyLimit} burst messages today. Come back tomorrow!", "OK"); return; }
            var hours = plan.estimatedTotalSeconds / 3600; var mins = (plan.estimatedTotalSeconds % 3600) / 60; var secs = plan.estimatedTotalSeconds % 60;
            var timeStr = hours > 0 ? $"{hours}h {mins}m" : $"{mins}m {secs}s";
            var confirm = await DisplayAlert("Burst Mode",
                $"Target: @{target}\nMessages: {plan.remaining} remaining today\nSessions: ~{plan.sessionsNeeded} (with hibernation breaks)\nEstimated time: ~{timeStr}\n\nMessages will be chunked into batches of {SettingsService.BurstChunkSizeMin}-{SettingsService.BurstChunkSizeMax} with smart hibernation breaks to preserve battery.",
                "Start Bursting", "Cancel");
            if (!confirm) return;
#if ANDROID
            bool permissionGranted = await RequestNotificationPermission();
            if (!permissionGranted) return;

            var context = Platform.CurrentActivity ?? Android.App.Application.Context;
            bool started = Feener.Platforms.Android.StreakScheduler.RunNow(context, isBurstMode: true);
            if (started) { _lockedDailyLimit = _settingsService.GetBurstDailyLimit(); await DisplayAlert("Burst Started", $"Sending {plan.remaining} messages in ~{plan.sessionsNeeded} sessions with hibernation breaks. Tap Stop to cancel anytime.", "OK"); UpdateStatus(); }
            else await DisplayAlert("Service Locked", "An automation process is already active.", "OK");
#else
            await DisplayAlert("Info", "This feature is only available on Android", "OK");
#endif
        }
        else
        {
            var friends = _settingsService.GetEnabledFriends();
            if (friends.Count == 0) { await DisplayAlert("No Friends", "Please add at least one friend before running.", "OK"); return; }
            var confirm = await DisplayAlert("Run Now", $"This will send your streak message to {friends.Count} friend{(friends.Count != 1 ? "s" : "")}. Continue?", "Run", "Cancel");
            if (!confirm) return;
#if ANDROID
            bool permissionGranted = await RequestNotificationPermission();
            if (!permissionGranted) return;

            var context = Platform.CurrentActivity ?? Android.App.Application.Context;
            bool started = Feener.Platforms.Android.StreakScheduler.RunNow(context, isBurstMode: false);
            if (started) { await DisplayAlert("Started", "Normal streak run started. Check the notification for progress.", "OK"); UpdateStatus(); }
            else await DisplayAlert("Already Running", "A process is already running. Please wait for it to finish.", "OK");
#else
            await DisplayAlert("Info", "This feature is only available on Android", "OK");
#endif
        }
    }

    private void OnStopServiceClicked(object? sender, EventArgs e)
    {
#if ANDROID
        var context = Platform.CurrentActivity ?? Android.App.Application.Context;
        Feener.Platforms.Android.StreakScheduler.StopService(context);
#endif
    }

    private async void OnRefreshing(object? sender, EventArgs e)
    {
        GreetingLabel.Text = $"Hi, {_sessionService.GetDisplayName()}";
        LoadProfilePhoto();
        UpdateSessionIndicator();
        LoadSettings();
        UpdateStatus();
        await EvaluatePermissionsAsync();
        await CheckUpdateOnlyAsync();
        MainRefreshView.IsRefreshing = false;
    }

    // ─── Permissions ────────────────────────────────────────────────────────────

    private async Task EvaluatePermissionsAsync()
    {
#if ANDROID
        var context = Platform.CurrentActivity ?? Android.App.Application.Context;
        bool exactAlarmGranted = Feener.Platforms.Android.StreakScheduler.CanScheduleExactAlarms(context);
        bool batteryOptGranted = Feener.Platforms.Android.StreakScheduler.IsIgnoringBatteryOptimizations(context);
        bool notificationGranted = true;
        if (Android.OS.Build.VERSION.SdkInt >= Android.OS.BuildVersionCodes.Tiramisu)
        {
            var status = await Permissions.CheckStatusAsync<Permissions.PostNotifications>();
            notificationGranted = (status == PermissionStatus.Granted);
        }
        BtnExactAlarm.IsVisible = !exactAlarmGranted;
        BtnBatteryOpt.IsVisible = !batteryOptGranted;
        BtnNotification.IsVisible = !notificationGranted;
        PermissionsPanel.IsVisible = !exactAlarmGranted || !batteryOptGranted || !notificationGranted;
#else
        PermissionsPanel.IsVisible = false;
#endif
    }

    private void OnRequestExactAlarmClicked(object? sender, EventArgs e)
    {
#if ANDROID
        var context = Platform.CurrentActivity ?? Android.App.Application.Context;
        Feener.Platforms.Android.StreakScheduler.RequestExactAlarmPermission(context);
#endif
    }

    private void OnRequestBatteryOptimizationClicked(object? sender, EventArgs e)
    {
#if ANDROID
        var context = Platform.CurrentActivity ?? Android.App.Application.Context;
        Feener.Platforms.Android.StreakScheduler.RequestBatteryOptimizationExemption(context);
#endif
    }

    private async void OnRequestNotificationClicked(object? sender, EventArgs e)
    {
        await RequestNotificationPermission();
        await EvaluatePermissionsAsync();
    }

    private async Task<bool> RequestNotificationPermission()
    {
#if ANDROID
        if (Android.OS.Build.VERSION.SdkInt >= Android.OS.BuildVersionCodes.Tiramisu)
        {
            var status = await Permissions.RequestAsync<Permissions.PostNotifications>();
            if (status != PermissionStatus.Granted)
            {
                await DisplayAlert("Permission Required", "Notification permission is required to show status while sending streaks.", "OK");
                return false;
            }
        }
#endif
        return true;
    }
}
