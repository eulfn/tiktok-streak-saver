using Microsoft.Maui.Controls.Shapes;
using Feener.Models;
using Feener.Services;

namespace Feener.Pages;

[Microsoft.Maui.Controls.Internals.Preserve(AllMembers = true)]
public partial class FriendsPage : ContentPage
{
    private readonly SettingsService _settingsService;

    // ── Streaks mode state ──────────────────────────────────────────────────
    private bool _lastIsRunning = false;
    private IDispatcherTimer? _statusTimer;

    // ── Collect mode state ──────────────────────────────────────────────────
    private bool _lastCollectRunning = false;
    private bool _lastIsDone = false;
    private bool _isExportingCollectLogs = false;
    private List<(string Username, string DisplayName)> _collectedFriends = new();
    private string _lastCollectedSignature = string.Empty;

    public FriendsPage()
    {
        InitializeComponent();
        _settingsService = new SettingsService();
    }

    private Color GetThemeColor(string key, string fallbackHex = "#92979E")
    {
        if (Application.Current != null && Application.Current.Resources.TryGetValue(key, out var resource) && resource is Color color)
            return color;
        return Color.FromArgb(fallbackHex);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Lifecycle
    // ═══════════════════════════════════════════════════════════════════════

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        this.Opacity = 0;
        this.TranslationY = 12;
        await Task.WhenAll(
            this.FadeTo(1, 280, Easing.SinInOut),
            this.TranslateTo(0, 0, 280, Easing.SinInOut));
        LoadFriendsList();

        if (_statusTimer == null)
        {
            _statusTimer = Dispatcher.CreateTimer();
            _statusTimer.Interval = TimeSpan.FromSeconds(1);
            _statusTimer.Tick += OnStatusTimerTick;
        }
        _statusTimer.Start();
        OnStatusTimerTick(null, EventArgs.Empty);
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        if (_statusTimer != null)
        {
            _statusTimer.Stop();
            _statusTimer.Tick -= OnStatusTimerTick;
            _statusTimer = null;
        }
    }

    private void OnStatusTimerTick(object? sender, EventArgs e)
    {
        // ── Streaks mode: track StreakService state ──
        bool isRunning = false;
#if ANDROID
        isRunning = Feener.Platforms.Android.Services.StreakService.IsRunning;
#endif
        if (_lastIsRunning != isRunning)
        {
            _lastIsRunning = isRunning;
            LoadFriendsList();

            SearchAndBulkRow.IsEnabled = !isRunning;
            SearchAndBulkRow.Opacity = isRunning ? 0.6 : 1.0;

            ActionButtonsGrid.IsEnabled = !isRunning;
            ActionButtonsGrid.Opacity = isRunning ? 0.6 : 1.0;

            if (AddFriendPanel.IsVisible && isRunning)
            {
                AddFriendPanel.IsVisible = false;
            }
        }

        // ── Collect mode: track CollectFriendsService state ──
#if ANDROID
        bool collectRunning = Feener.Platforms.Android.Services.CollectFriendsService.IsRunning;
        bool isDone = Feener.Platforms.Android.Services.CollectFriendsService.IsDone;
        if (collectRunning != _lastCollectRunning)
        {
            _lastCollectRunning = collectRunning;
            UpdateCollectUI();
        }

        // While collecting, update the collected staging list in real-time.
        // Friends are not added to the streak list here — the user drives that
        // explicitly via the Add button on each collected item.
        if (collectRunning)
        {
            var current = Feener.Platforms.Android.Services.CollectFriendsService.GetCollectedFriends();
            var currentSignature = BuildCollectedSignature(current);
            if (!string.Equals(currentSignature, _lastCollectedSignature, StringComparison.Ordinal))
            {
                _collectedFriends = current;
                _lastCollectedSignature = currentSignature;
                RebuildCollectedList();
            }
            var status = Feener.Platforms.Android.Services.CollectFriendsService.GetStatusMessage();
            if (status != null) CollectStatusLabel.Text = status;
        }

        // When done, run exactly once per completed run (guard prevents re-firing every timer tick).
        // Only update the collected staging list and status label — no automatic streak list mutations.
        if (!collectRunning && isDone && !_lastIsDone)
        {
            var collected = Feener.Platforms.Android.Services.CollectFriendsService.GetCollectedFriends();
            _collectedFriends = collected;
            _lastCollectedSignature = BuildCollectedSignature(collected);

            var status = Feener.Platforms.Android.Services.CollectFriendsService.GetStatusMessage();
            if (status != null) CollectStatusLabel.Text = status;

            RebuildCollectedList();
            _lastIsDone = true;
        }

        if (!isDone)
            _lastIsDone = false;
#endif
    }

    private void OnRefreshing(object? sender, EventArgs e)
    {
        LoadFriendsList();
        if (CollectModeContainer.IsVisible)
            RebuildCollectedList();
        MainRefreshView.IsRefreshing = false;
    }

    private static string BuildCollectedSignature(List<(string Username, string DisplayName)> items)
    {
        if (items.Count == 0) return string.Empty;

        return string.Join("|", items
            .OrderBy(x => x.Username, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(x => $"{x.Username.Trim().ToLowerInvariant()}::{(x.DisplayName ?? string.Empty).Trim()}"));
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Mode Switcher
    // ═══════════════════════════════════════════════════════════════════════

    private void OnStreaksModeTapped(object? sender, TappedEventArgs e)
    {
        StreaksModeContainer.IsVisible = true;
        CollectModeContainer.IsVisible = false;

        StreaksTabBorder.BackgroundColor = GetThemeColor("Primary", "#FE2C55");
        StreaksTabLabel.TextColor = Application.Current?.RequestedTheme == AppTheme.Dark
            ? GetThemeColor("PrimaryDarkText", "#FFFFFF")
            : Colors.White;

        CollectTabBorder.BackgroundColor = Colors.Transparent;
        CollectTabLabel.TextColor = GetThemeColor("Gray400", "#8B8F96");
    }

    private void OnCollectModeTapped(object? sender, TappedEventArgs e)
    {
        StreaksModeContainer.IsVisible = false;
        CollectModeContainer.IsVisible = true;

        CollectTabBorder.BackgroundColor = GetThemeColor("Primary", "#FE2C55");
        CollectTabLabel.TextColor = Application.Current?.RequestedTheme == AppTheme.Dark
            ? GetThemeColor("PrimaryDarkText", "#FFFFFF")
            : Colors.White;

        StreaksTabBorder.BackgroundColor = Colors.Transparent;
        StreaksTabLabel.TextColor = GetThemeColor("Gray400", "#8B8F96");

        // If we already have results from a previous run, show them
        UpdateCollectUI();
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Streaks Mode — Friends List (existing logic, unchanged)
    // ═══════════════════════════════════════════════════════════════════════

    private void LoadFriendsList()
    {
        var allFriends = _settingsService.GetFriendsList();
        SearchAndBulkRow.IsVisible = allFriends.Count > 0;

        var searchText = SearchFriendEntry.Text?.Trim() ?? string.Empty;
        var displayFriends = allFriends;
        if (!string.IsNullOrEmpty(searchText))
        {
            displayFriends = allFriends.Where(f =>
                (f.Username != null && f.Username.Contains(searchText, StringComparison.OrdinalIgnoreCase)) ||
                (f.DisplayName != null && f.DisplayName.Contains(searchText, StringComparison.OrdinalIgnoreCase))
            ).ToList();
        }

        var itemsToRemove = FriendsListContainer.Children.Where(c => c != NoFriendsLabel).ToList();
        foreach (var item in itemsToRemove) FriendsListContainer.Children.Remove(item);

        if (allFriends.Count == 0)
        {
            NoFriendsLabel.Text = "No friends added. Tap 'Add' to begin.";
            NoFriendsLabel.IsVisible = true;
        }
        else if (displayFriends.Count == 0 && !string.IsNullOrEmpty(searchText))
        {
            NoFriendsLabel.Text = $"No friends found matching '{searchText}'";
            NoFriendsLabel.IsVisible = true;
        }
        else
        {
            NoFriendsLabel.IsVisible = false;
        }

        foreach (var friend in displayFriends) FriendsListContainer.Children.Add(CreateFriendView(friend));

        UpdateStatsCard(allFriends);
    }

    private void UpdateStatsCard(List<FriendConfig> allFriends)
    {
        FriendsStatsCard.IsVisible = allFriends.Count > 0;
        TotalFriendsLabel.Text = allFriends.Count.ToString();
        EnabledFriendsLabel.Text = allFriends.Count(f => f.IsEnabled).ToString();
        var today = DateTime.Now.Date;
        SentTodayLabel.Text = allFriends.Count(f => f.LastMessageSent.HasValue && f.LastMessageSent.Value.Date == today).ToString();
    }

    private View CreateFriendView(FriendConfig friend)
    {
        var border = new Border
        {
            StrokeShape = new RoundRectangle { CornerRadius = 16 },
            Stroke = Colors.Transparent,
            Padding = new Thickness(14, 12),
            Opacity = 0, TranslationY = 10
        };
        border.SetAppThemeColor(Border.BackgroundColorProperty,
            GetThemeColor("Gray100", "#F3F4F6"),
            GetThemeColor("Gray900", "#111827"));
        _ = border.FadeTo(1, 300, Easing.CubicOut);
        _ = border.TranslateTo(0, 0, 300, Easing.CubicOut);

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitionCollection
            {
                new ColumnDefinition { Width = GridLength.Star },
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = GridLength.Auto }
            },
            ColumnSpacing = 8
        };

        var infoStack = new VerticalStackLayout { Spacing = 3 };
        var displayName = string.IsNullOrEmpty(friend.DisplayName) ? friend.Username : friend.DisplayName;
        infoStack.Children.Add(new Label { Text = displayName, FontSize = 15, FontFamily = "InterSemiBold" });
        infoStack.Children.Add(new Label { Text = $"@{friend.Username}", FontSize = 13, TextColor = GetThemeColor("Gray400", "#8B8F96") });
        if (friend.LastMessageSent.HasValue)
            infoStack.Children.Add(new Label { Text = $"Last sent: {friend.LastMessageSent.Value:MMM dd}", FontSize = 12, TextColor = GetThemeColor("Gray400", "#8B8F96") });
        grid.Children.Add(infoStack);

        var editButton = new Button { Text = "Edit", BackgroundColor = Colors.Transparent, FontSize = 12, Padding = new Thickness(8), HeightRequest = 44, VerticalOptions = LayoutOptions.Center, IsEnabled = !_lastIsRunning, Opacity = _lastIsRunning ? 0.6 : 1.0 };
        editButton.SetAppThemeColor(Button.TextColorProperty, GetThemeColor("Gray400"), GetThemeColor("Gray400"));
        editButton.Clicked += async (s, e) =>
        {
            var newName = await DisplayPromptAsync("Edit Friend", "Enter new display name:", initialValue: friend.DisplayName ?? friend.Username);
            if (newName != null) { friend.DisplayName = newName; _settingsService.UpdateFriend(friend); LoadFriendsList(); }
        };
        Grid.SetColumn(editButton, 1); grid.Children.Add(editButton);

        var deleteButton = new Button { Text = "Delete", BackgroundColor = Colors.Transparent, FontSize = 12, Padding = new Thickness(8), HeightRequest = 44, VerticalOptions = LayoutOptions.Center, IsEnabled = !_lastIsRunning, Opacity = _lastIsRunning ? 0.6 : 1.0 };
        deleteButton.TextColor = GetThemeColor("DeleteColor", "#EE1D52");
        deleteButton.Clicked += async (s, e) =>
        {
            var confirm = await DisplayAlert("Remove Friend", $"Remove {displayName} from the list?", "Remove", "Cancel");
            if (confirm) { _settingsService.RemoveFriend(friend.Id); LoadFriendsList(); }
        };
        Grid.SetColumn(deleteButton, 2); grid.Children.Add(deleteButton);

        var toggleSwitch = new Switch { IsToggled = friend.IsEnabled, VerticalOptions = LayoutOptions.Center, IsEnabled = !_lastIsRunning, Opacity = _lastIsRunning ? 0.6 : 1.0 };
        toggleSwitch.SetAppThemeColor(Switch.ThumbColorProperty, GetThemeColor("White"), GetThemeColor("White"));
        toggleSwitch.SetAppThemeColor(Switch.OnColorProperty, GetThemeColor("Primary", "#FE2C55"), GetThemeColor("Primary", "#FE2C55"));
        toggleSwitch.Toggled += (s, e) => { friend.IsEnabled = e.Value; _settingsService.UpdateFriend(friend); };
        Grid.SetColumn(toggleSwitch, 3); grid.Children.Add(toggleSwitch);

        border.Content = grid;
        return border;
    }

    private void OnSearchFriendTextChanged(object? sender, TextChangedEventArgs e) => LoadFriendsList();

    private void OnAddFriendClicked(object? sender, EventArgs e)
    {
        AddFriendPanel.IsVisible = true;
        NewFriendUsernameEntry.Text = string.Empty;
        NewFriendDisplayNameEntry.Text = string.Empty;
        NewFriendUsernameEntry.Focus();
    }

    private void OnCancelAddFriend(object? sender, EventArgs e) => AddFriendPanel.IsVisible = false;

    private async void OnSaveFriend(object? sender, EventArgs e)
    {
        var username = NewFriendUsernameEntry.Text?.Trim().TrimStart('@');
        var displayName = NewFriendDisplayNameEntry.Text?.Trim();
        if (string.IsNullOrEmpty(username)) { await DisplayAlert("Error", "Please enter a username", "OK"); return; }
        var existingFriends = _settingsService.GetFriendsList();
        if (existingFriends.Any(f => f.Username.Equals(username, StringComparison.OrdinalIgnoreCase)))
        { await DisplayAlert("Error", "This friend is already in your list", "OK"); return; }
        var friend = new FriendConfig { Username = username, DisplayName = displayName ?? string.Empty, IsEnabled = true };
        _settingsService.AddFriend(friend);
        AddFriendPanel.IsVisible = false;
        LoadFriendsList();
    }

    private void OnEnableAllClicked(object? sender, EventArgs e)
    {
        var friends = _settingsService.GetFriendsList();
        if (friends.Count == 0) return;
        foreach (var f in friends) f.IsEnabled = true;
        _settingsService.SaveFriendsList(friends);
        LoadFriendsList();
    }

    private void OnDisableAllClicked(object? sender, EventArgs e)
    {
        var friends = _settingsService.GetFriendsList();
        if (friends.Count == 0) return;
        foreach (var f in friends) f.IsEnabled = false;
        _settingsService.SaveFriendsList(friends);
        LoadFriendsList();
    }

    private async void OnDeleteAllFriendsClicked(object? sender, EventArgs e)
    {
        var friends = _settingsService.GetFriendsList();
        if (friends.Count == 0) return;
        bool confirm = await DisplayAlert("Clear All Friends", "Are you sure you want to remove all friends? This cannot be undone.", "Clear All", "Cancel");
        if (confirm)
        {
            _settingsService.SaveFriendsList(new List<FriendConfig>());
            LoadFriendsList();
        }
    }

    private async void OnImportFriendsClicked(object? sender, EventArgs e)
    {
        try
        {
            var result = await FilePicker.Default.PickAsync(new PickOptions
            {
                PickerTitle = "Select friend list JSON file",
                FileTypes = new FilePickerFileType(new Dictionary<DevicePlatform, IEnumerable<string>>
                {
                    { DevicePlatform.Android, new[] { "application/json", "*/*" } },
                    { DevicePlatform.iOS, new[] { "public.json" } },
                    { DevicePlatform.WinUI, new[] { ".json" } },
                    { DevicePlatform.macOS, new[] { "json" } },
                })
            });
            if (result == null) return;
            string json;
            using (var stream = await result.OpenReadAsync())
            using (var reader = new System.IO.StreamReader(stream))
                json = await reader.ReadToEndAsync();
            if (string.IsNullOrWhiteSpace(json)) { await DisplayAlert("Import Failed", "The selected file is empty.", "OK"); return; }

            List<FriendConfig>? imported;
            try
            {
                var options = new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                imported = System.Text.Json.JsonSerializer.Deserialize<List<FriendConfig>>(json, options);
            }
            catch { await DisplayAlert("Import Failed", "The file is not a valid friend list.", "OK"); return; }
            if (imported == null || imported.Count == 0) { await DisplayAlert("Import", "The file contains no friend entries.", "OK"); return; }

            var existing = _settingsService.GetFriendsList();
            int added = 0, updated = 0, skipped = 0;
            var seenInBatch = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in imported)
            {
                if (string.IsNullOrWhiteSpace(entry.Username) || entry.Username.Trim().TrimStart('@').Length < 2) { skipped++; continue; }
                entry.Username = entry.Username.Trim().TrimStart('@');
                entry.DisplayName = entry.DisplayName?.Trim() ?? string.Empty;
                if (!seenInBatch.Add(entry.Username)) { skipped++; continue; }
                var match = existing.FirstOrDefault(f => f.Username.Equals(entry.Username, StringComparison.OrdinalIgnoreCase));
                if (match != null) { entry.Id = match.Id; existing[existing.IndexOf(match)] = entry; updated++; }
                else { if (string.IsNullOrEmpty(entry.Id)) entry.Id = Guid.NewGuid().ToString(); existing.Add(entry); added++; }
            }
            _settingsService.SaveFriendsList(existing);
            LoadFriendsList();
            await DisplayAlert("Import Complete", $"Import complete.\n\nAdded: {added}\nUpdated: {updated}\nSkipped: {skipped}", "OK");
        }
        catch (Exception ex) { await DisplayAlert("Import Failed", $"Unexpected error: {ex.Message}", "OK"); }
    }

    private async void OnExportFriendsClicked(object? sender, EventArgs e)
    {
        try
        {
            var friends = _settingsService.GetFriendsList();
            if (friends.Count == 0) { await DisplayAlert("Export", "Your friend list is empty.", "OK"); return; }
            var options = new System.Text.Json.JsonSerializerOptions { WriteIndented = true };
            var json = System.Text.Json.JsonSerializer.Serialize(friends, options);
            var fileName = $"streak_friends_{DateTime.Now:yyyyMMdd_HHmm}.json";
            var filePath = System.IO.Path.Combine(FileSystem.CacheDirectory, fileName);
            await System.IO.File.WriteAllTextAsync(filePath, json);
            await Share.Default.RequestAsync(new ShareFileRequest { Title = "Export Friend List", File = new ShareFile(filePath, "application/json") });
        }
        catch (Exception ex) { await DisplayAlert("Export Failed", $"Could not export: {ex.Message}", "OK"); }
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Collect Mode — Background Service
    // ═══════════════════════════════════════════════════════════════════════

    private async void OnCollectClicked(object? sender, EventArgs e)
    {
#if ANDROID
        if (Feener.Platforms.Android.Services.CollectFriendsService.IsRunning)
            return;

        bool hasSession = TikTokWebViewHelper.HasValidSessionCookie();
        if (!hasSession)
        {
            await DisplayAlert("Not Logged In",
                "Please log in to TikTok first from the Profile page.", "OK");
            return;
        }

        // Start the background service (same pattern as StreakScheduler.RunNow)
        var context = Platform.CurrentActivity ?? Platform.AppContext;
        var serviceIntent = new Android.Content.Intent(context,
            typeof(Feener.Platforms.Android.Services.CollectFriendsService));

        if (Android.OS.Build.VERSION.SdkInt >= Android.OS.BuildVersionCodes.O)
            context.StartForegroundService(serviceIntent);
        else
            context.StartService(serviceIntent);

        CollectButton.IsEnabled = false;
        CollectButton.Text = "Collecting...";
        CollectSpinner.IsVisible = true;
        CollectSpinner.IsRunning = true;
        CollectStatusLabel.Text = "Loading TikTok messages...";
#endif
    }

    private void UpdateCollectUI()
    {
#if ANDROID
        bool running = Feener.Platforms.Android.Services.CollectFriendsService.IsRunning;
        bool done = Feener.Platforms.Android.Services.CollectFriendsService.IsDone;

        if (running)
        {
            CollectButton.IsEnabled = false;
            CollectButton.Text = "Collecting...";
            CollectSpinner.IsVisible = true;
            CollectSpinner.IsRunning = true;
        }
        else
        {
            CollectButton.IsEnabled = true;
            CollectButton.Text = done ? "Collect Again" : "Collect";
            CollectSpinner.IsVisible = false;
            CollectSpinner.IsRunning = false;
        }

        if (done)
        {
            _collectedFriends = Feener.Platforms.Android.Services.CollectFriendsService.GetCollectedFriends();
            var status = Feener.Platforms.Android.Services.CollectFriendsService.GetStatusMessage();
            if (status != null) CollectStatusLabel.Text = status;
            RebuildCollectedList();
        }
#endif
    }

    // ── Collected friends list display ───────────────────────────────────────

    private void RebuildCollectedList()
    {
        CollectedListContainer.Children.Clear();

        if (_collectedFriends.Count == 0)
        {
            CollectedResultsCard.IsVisible = false;
            return;
        }

        CollectedResultsCard.IsVisible = true;
        CollectedEmptyLabel.IsVisible = false;

        var sorted = _collectedFriends
            .OrderBy(f => f.Username, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var existingFriends = _settingsService.GetFriendsList();
        var existingSet = new HashSet<string>(
            existingFriends.Select(f => f.Username.ToLowerInvariant()));

        foreach (var friend in sorted)
        {
            bool inList = existingSet.Contains(friend.Username.ToLowerInvariant());
            CollectedListContainer.Children.Add(CreateCollectedItem(friend.Username, friend.DisplayName, inList));
        }

        CollectedTotalLabel.Text = $"Total: {sorted.Count}";
    }

    private View CreateCollectedItem(string username, string displayName, bool inFriendList)
    {
        var border = new Border
        {
            StrokeShape = new RoundRectangle { CornerRadius = 12 },
            Stroke = Colors.Transparent,
            Padding = new Thickness(12, 10)
        };
        border.SetAppThemeColor(Border.BackgroundColorProperty,
            GetThemeColor("Gray100", "#F3F4F6"),
            GetThemeColor("Gray900", "#111827"));

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitionCollection
            {
                new ColumnDefinition { Width = GridLength.Star },
                new ColumnDefinition { Width = GridLength.Auto }
            },
            ColumnSpacing = 8
        };

        // Display name + username stacked
        var infoStack = new VerticalStackLayout { Spacing = 2, VerticalOptions = LayoutOptions.Center };
        if (!string.IsNullOrEmpty(displayName) && !displayName.Equals(username, StringComparison.OrdinalIgnoreCase))
        {
            infoStack.Children.Add(new Label
            {
                Text = displayName,
                FontSize = 14,
                FontFamily = "InterSemiBold"
            });
            infoStack.Children.Add(new Label
            {
                Text = $"@{username}",
                FontSize = 12,
                TextColor = GetThemeColor("Gray400", "#8B8F96")
            });
        }
        else
        {
            infoStack.Children.Add(new Label
            {
                Text = $"@{username}",
                FontSize = 14,
                FontFamily = "InterSemiBold"
            });
        }
        grid.Children.Add(infoStack);

        var actionButton = new Button
        {
            FontSize = 12,
            FontFamily = "InterMedium",
            Padding = new Thickness(12, 0),
            HeightRequest = 36,
            CornerRadius = 10,
            VerticalOptions = LayoutOptions.Center
        };

        if (inFriendList)
        {
            actionButton.Text = "Remove";
            actionButton.BackgroundColor = Colors.Transparent;
            actionButton.TextColor = GetThemeColor("DeleteColor", "#EE1D52");
            actionButton.Clicked += (s, e) => OnRemoveCollected(username);
        }
        else
        {
            actionButton.Text = "Add";
            actionButton.BackgroundColor = GetThemeColor("Primary", "#FE2C55");
            actionButton.TextColor = Colors.White;
            actionButton.Clicked += (s, e) => OnAddCollected(username, displayName);
        }

        Grid.SetColumn(actionButton, 1);
        grid.Children.Add(actionButton);

        border.Content = grid;
        return border;
    }

    private void OnAddCollected(string username, string displayName)
    {
        var existing = _settingsService.GetFriendsList();
        if (existing.Any(f => f.Username.Equals(username, StringComparison.OrdinalIgnoreCase)))
        {
            RebuildCollectedList();
            return;
        }

        existing.Add(new FriendConfig
        {
            Username = username,
            DisplayName = displayName ?? string.Empty,
            IsEnabled = true
        });
        _settingsService.SaveFriendsList(existing);
        LoadFriendsList();
        RebuildCollectedList();
    }

    private void OnRemoveCollected(string username)
    {
        var existing = _settingsService.GetFriendsList();
        var match = existing.FirstOrDefault(f => f.Username.Equals(username, StringComparison.OrdinalIgnoreCase));
        if (match != null)
        {
            _settingsService.RemoveFriend(match.Id);
            LoadFriendsList();
        }
        RebuildCollectedList();
    }

    private async void OnExportCollectLogsClicked(object? sender, EventArgs e)
    {
        if (_isExportingCollectLogs) return;
        _isExportingCollectLogs = true;
        try
        {
#if ANDROID
            var logs = Feener.Platforms.Android.Services.CollectFriendsService.GetLogs();
#else
            var logs = new List<string>();
#endif
            if (logs == null || logs.Count == 0)
            {
                await DisplayAlert("Export Logs", "No collect logs to export.", "OK");
                return;
            }

            var textContent = string.Join(Environment.NewLine, logs);
            var fileName = $"collect_logs_{DateTime.Now:yyyyMMdd_HHmm}.txt";
            var filePath = System.IO.Path.Combine(FileSystem.CacheDirectory, fileName);
            await System.IO.File.WriteAllTextAsync(filePath, textContent);
            await Share.Default.RequestAsync(new ShareFileRequest
            {
                Title = "Export Collect Logs",
                File = new ShareFile(filePath, "text/plain")
            });
        }
        catch (Exception ex)
        {
            await DisplayAlert("Export Failed", $"Could not export logs: {ex.Message}", "OK");
        }
        finally
        {
            _isExportingCollectLogs = false;
        }
    }
}
