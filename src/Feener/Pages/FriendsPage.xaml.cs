using Microsoft.Maui.Controls.Shapes;
using Feener.Models;
using Feener.Services;

namespace Feener.Pages;

[Microsoft.Maui.Controls.Internals.Preserve(AllMembers = true)]
public partial class FriendsPage : ContentPage
{
    private readonly SettingsService _settingsService;
    private readonly SessionService _sessionService;

    // ── Streaks mode state ──────────────────────────────────────────────────
    private bool _lastIsRunning = false;
    private IDispatcherTimer? _statusTimer;

    // ── Collect mode state ──────────────────────────────────────────────────
    private bool _isCollecting = false;
    private bool _collectJsInjected = false;
    private string? _collectJsSource;
    private IDispatcherTimer? _collectPollTimer;
    private List<CollectedFriend> _collectedFriends = new();

    public FriendsPage()
    {
        InitializeComponent();
        _settingsService = new SettingsService();
        _sessionService = new SessionService();
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
        StopCollectPolling();
    }

    private void OnStatusTimerTick(object? sender, EventArgs e)
    {
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
    }

    private void OnRefreshing(object? sender, EventArgs e)
    {
        LoadFriendsList();
        if (CollectModeContainer.IsVisible)
            RebuildCollectedList();
        MainRefreshView.IsRefreshing = false;
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

        // Update stats card
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
    //  Collect Mode — Background WebView Collection
    // ═══════════════════════════════════════════════════════════════════════

    private async void OnCollectClicked(object? sender, EventArgs e)
    {
        if (_isCollecting) return;

        // Session check
        bool hasSession = TikTokWebViewHelper.HasValidSessionCookie();
        if (!hasSession)
        {
            await DisplayAlert("Not Logged In",
                "Please log in to TikTok first from the Profile page.", "OK");
            return;
        }

        // Load JS if needed
        if (string.IsNullOrEmpty(_collectJsSource))
        {
            try
            {
                using var stream = await FileSystem.OpenAppPackageFileAsync("tiktok_collect_friends.js");
                using var reader = new StreamReader(stream);
                _collectJsSource = await reader.ReadToEndAsync();
            }
            catch
            {
                await DisplayAlert("Error", "Could not load collection script.", "OK");
                return;
            }
        }

        _isCollecting = true;
        _collectJsInjected = false;
        _collectedFriends.Clear();

        // Update UI
        CollectButton.IsEnabled = false;
        CollectButton.Text = "Collecting...";
        CollectStatusLabel.Text = "Loading TikTok messages...";
        CollectSpinner.IsVisible = true;
        CollectSpinner.IsRunning = true;
        CollectedResultsCard.IsVisible = false;

        // Configure and load the hidden WebView
        var loginUa = _sessionService.GetLoginUserAgent();
#if ANDROID
        TikTokWebViewHelper.ConfigureWebView(CollectWebView, loginUa);
#endif
        CollectWebView.Navigated += OnCollectWebViewNavigated;
        CollectWebView.Source = TikTokWebViewHelper.MessagesUrl;
    }

    private async void OnCollectWebViewNavigated(object? sender, WebNavigatedEventArgs e)
    {
        if (_collectJsInjected) return;
        _collectJsInjected = true;

        // Detach so we don't fire again
        CollectWebView.Navigated -= OnCollectWebViewNavigated;

        CollectStatusLabel.Text = "Scanning your DM list...";

        // Inject the collection JS — it handles its own page detection and timing
        if (!string.IsNullOrEmpty(_collectJsSource))
        {
            await CollectWebView.EvaluateJavaScriptAsync(_collectJsSource);
        }

        // Start polling for results
        StartCollectPolling();
    }

    private void StartCollectPolling()
    {
        if (_collectPollTimer != null) return;
        _collectPollTimer = Dispatcher.CreateTimer();
        _collectPollTimer.Interval = TimeSpan.FromMilliseconds(1200);
        _collectPollTimer.Tick += OnCollectPollTick;
        _collectPollTimer.Start();
    }

    private void StopCollectPolling()
    {
        if (_collectPollTimer != null)
        {
            _collectPollTimer.Stop();
            _collectPollTimer.Tick -= OnCollectPollTick;
            _collectPollTimer = null;
        }
    }

    private async void OnCollectPollTick(object? sender, EventArgs e)
    {
        if (!_isCollecting) { StopCollectPolling(); return; }

        try
        {
            var json = await CollectWebView.EvaluateJavaScriptAsync(
                "JSON.stringify(window.__feenerState)");

            if (string.IsNullOrEmpty(json)) return;

            json = UnescapeJsString(json);

            var state = System.Text.Json.JsonSerializer.Deserialize<CollectionState>(json,
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (state == null) return;

            MainThread.BeginInvokeOnMainThread(() =>
            {
                if (state.Status == "collecting")
                    CollectStatusLabel.Text = $"Collecting... {state.Count} found";
                else if (state.Status == "scrolling")
                    CollectStatusLabel.Text = $"Scrolling for more... {state.Count} found";
                else if (state.Status == "initializing")
                    CollectStatusLabel.Text = "Waiting for page to load...";
            });

            if (state.Status == "done" || state.Status == "error")
            {
                StopCollectPolling();
                _isCollecting = false;

                MainThread.BeginInvokeOnMainThread(() =>
                {
                    _collectedFriends = state.Friends?
                        .OrderBy(f => f.Username, StringComparer.OrdinalIgnoreCase)
                        .ToList() ?? new();

                    CollectSpinner.IsRunning = false;
                    CollectSpinner.IsVisible = false;
                    CollectButton.IsEnabled = true;
                    CollectButton.Text = "Collect Again";

                    if (state.Status == "error" && state.Count == 0)
                    {
                        CollectStatusLabel.Text = state.Error ?? "Collection failed";
                    }
                    else
                    {
                        CollectStatusLabel.Text = $"Found {_collectedFriends.Count} friend{(_collectedFriends.Count == 1 ? "" : "s")}";
                    }

                    RebuildCollectedList();
                });
            }
        }
        catch { /* polling errors are non-fatal */ }
    }

    private static string UnescapeJsString(string raw)
    {
        if (raw.Length >= 2 && raw[0] == '"' && raw[^1] == '"')
            raw = raw[1..^1];
        raw = raw.Replace("\\\"", "\"")
                 .Replace("\\\\", "\\")
                 .Replace("\\/", "/")
                 .Replace("\\n", "\n")
                 .Replace("\\r", "\r")
                 .Replace("\\t", "\t");
        return raw;
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

        var existingFriends = _settingsService.GetFriendsList();
        var existingSet = new HashSet<string>(
            existingFriends.Select(f => f.Username.ToLowerInvariant()));

        foreach (var friend in _collectedFriends)
        {
            bool inList = existingSet.Contains(friend.Username.ToLowerInvariant());
            CollectedListContainer.Children.Add(CreateCollectedItem(friend, inList));
        }

        CollectedTotalLabel.Text = $"Total: {_collectedFriends.Count}";
    }

    private View CreateCollectedItem(CollectedFriend friend, bool inFriendList)
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

        var label = new Label
        {
            Text = $"@{friend.Username}",
            FontSize = 14,
            FontFamily = "InterSemiBold",
            VerticalOptions = LayoutOptions.Center
        };
        grid.Children.Add(label);

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
            actionButton.Clicked += (s, e) => OnRemoveCollected(friend.Username);
        }
        else
        {
            actionButton.Text = "Add";
            actionButton.BackgroundColor = GetThemeColor("Primary", "#FE2C55");
            actionButton.TextColor = Colors.White;
            actionButton.Clicked += (s, e) => OnAddCollected(friend.Username);
        }

        Grid.SetColumn(actionButton, 1);
        grid.Children.Add(actionButton);

        border.Content = grid;
        return border;
    }

    private void OnAddCollected(string username)
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
            DisplayName = string.Empty,
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

    // ── DTOs for JS state ───────────────────────────────────────────────────

    private class CollectionState
    {
        public string Status { get; set; } = string.Empty;
        public int Count { get; set; }
        public List<CollectedFriend>? Friends { get; set; }
        public string? Error { get; set; }
    }

    private class CollectedFriend
    {
        public string Username { get; set; } = string.Empty;
        public string? DisplayName { get; set; }
    }
}
