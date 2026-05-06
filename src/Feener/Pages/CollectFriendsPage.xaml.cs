using Feener.Models;
using Feener.Services;
using Microsoft.Maui.Controls.Shapes;

namespace Feener.Pages;

[Microsoft.Maui.Controls.Internals.Preserve(AllMembers = true)]
public partial class CollectFriendsPage : ContentPage
{
    private readonly SessionService _sessionService;
    private readonly SettingsService _settingsService;
    private IDispatcherTimer? _pollTimer;
    private bool _jsInjected = false;
    private bool _collectionDone = false;
    private string? _collectJsSource;

    // Collected friends parsed from JS state
    private List<CollectedFriend> _collectedFriends = new();
    private int _newFriendsCount = 0;

    private enum PagePhase { Loading, Collecting, Results }
    private PagePhase _phase = PagePhase.Loading;

    public CollectFriendsPage()
    {
        InitializeComponent();
        _sessionService = new SessionService();
        _settingsService = new SettingsService();
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();

        // Pre-load the JS source from raw assets
        _collectJsSource = await LoadRawAsset("tiktok_collect_friends.js");
        if (string.IsNullOrEmpty(_collectJsSource))
        {
            await DisplayAlert("Error", "Could not load collection script.", "OK");
            await Navigation.PopAsync();
            return;
        }

        // Check for a valid session before loading TikTok
        bool hasSession = TikTokWebViewHelper.HasValidSessionCookie();
        if (!hasSession)
        {
            await DisplayAlert("Not Logged In",
                "Please log in to TikTok first from the Profile page before collecting friends.", "OK");
            await Navigation.PopAsync();
            return;
        }

        // Configure WebView with the same UA used during login
        var loginUa = _sessionService.GetLoginUserAgent();
#if ANDROID
        TikTokWebViewHelper.ConfigureWebView(TikTokWebView, loginUa);
#endif

        TikTokWebView.Source = TikTokWebViewHelper.MessagesUrl;
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        StopPolling();
    }

    // ── Asset Loading ───────────────────────────────────────────────────────

    private static async Task<string?> LoadRawAsset(string fileName)
    {
        try
        {
            using var stream = await FileSystem.OpenAppPackageFileAsync(fileName);
            using var reader = new StreamReader(stream);
            return await reader.ReadToEndAsync();
        }
        catch
        {
            return null;
        }
    }

    // ── WebView Events ──────────────────────────────────────────────────────

    private void OnWebViewNavigated(object? sender, WebNavigatedEventArgs e)
    {
        // Don't auto-inject based on URL — we don't know the actual page state.
        // TikTok is an SPA; the Navigated event fires before the DOM is ready
        // and URL checks are unreliable.
        //
        // Instead: hide the loading overlay and enable the "Start" button.
        // The JS itself handles all page detection (login redirect, DOM readiness).
        if (_jsInjected) return;

        LoadingOverlay.IsVisible = false;
        ActionButton.Text = "Start Collection";
        ActionButton.IsEnabled = true;
    }

    // ── Collection Logic ────────────────────────────────────────────────────

    private async Task StartCollection()
    {
        if (_jsInjected || string.IsNullOrEmpty(_collectJsSource)) return;
        _jsInjected = true;
        _phase = PagePhase.Collecting;

        // Show the collecting overlay (semi-transparent so WebView is still visible)
        CollectingOverlay.IsVisible = true;
        CollectingStatusLabel.Text = "Collecting friends...";
        CollectingCountLabel.Text = "Waiting for TikTok to load";
        ActionButton.Text = "Collecting...";
        ActionButton.IsEnabled = false;

        // Inject the collection JS — it handles its own page detection and timing
        await TikTokWebView.EvaluateJavaScriptAsync(_collectJsSource);

        // Start polling for results
        StartPolling();
    }

    // ── Polling ─────────────────────────────────────────────────────────────

    private void StartPolling()
    {
        if (_pollTimer != null) return;
        _pollTimer = Dispatcher.CreateTimer();
        _pollTimer.Interval = TimeSpan.FromMilliseconds(1200);
        _pollTimer.Tick += OnPollTick;
        _pollTimer.Start();
    }

    private void StopPolling()
    {
        if (_pollTimer != null)
        {
            _pollTimer.Stop();
            _pollTimer.Tick -= OnPollTick;
            _pollTimer = null;
        }
    }

    private async void OnPollTick(object? sender, EventArgs e)
    {
        if (_collectionDone) { StopPolling(); return; }

        try
        {
            var json = await TikTokWebView.EvaluateJavaScriptAsync(
                "JSON.stringify(window.__feenerState)");

            if (string.IsNullOrEmpty(json)) return;

            // EvaluateJavaScriptAsync wraps the result in quotes and escapes inner quotes
            json = UnescapeJsString(json);

            var state = System.Text.Json.JsonSerializer.Deserialize<CollectionState>(json,
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (state == null) return;

            // Update UI with progress
            MainThread.BeginInvokeOnMainThread(() =>
            {
                CollectingCountLabel.Text = $"{state.Count} friend{(state.Count == 1 ? "" : "s")} found";

                if (state.Status == "scrolling")
                    CollectingStatusLabel.Text = "Scrolling for more...";
                else if (state.Status == "collecting")
                    CollectingStatusLabel.Text = "Collecting friends...";
                else if (state.Status == "initializing")
                    CollectingStatusLabel.Text = "Waiting for page to load...";
            });

            // Check terminal states
            if (state.Status == "done" || state.Status == "error")
            {
                _collectionDone = true;
                StopPolling();

                MainThread.BeginInvokeOnMainThread(() =>
                {
                    if (state.Status == "error")
                    {
                        // If we collected some friends before the error, still show results
                        if (state.Count > 0)
                        {
                            ShowResults(state.Friends ?? new List<CollectedFriend>(),
                                        errorMessage: state.Error);
                        }
                        else
                        {
                            CollectingStatusLabel.Text = "Collection failed";
                            CollectingCountLabel.Text = state.Error ?? "Unknown error";
                            ActionButton.Text = "Back";
                            ActionButton.IsEnabled = true;
                        }
                    }
                    else
                    {
                        ShowResults(state.Friends ?? new List<CollectedFriend>());
                    }
                });
            }
        }
        catch
        {
            // Polling errors are non-fatal — just skip this tick
        }
    }

    /// <summary>
    /// EvaluateJavaScriptAsync returns strings with escaped quotes.
    /// This strips the outer quotes and unescapes inner content.
    /// </summary>
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

    // ── Results Display ─────────────────────────────────────────────────────

    private void ShowResults(List<CollectedFriend> friends, string? errorMessage = null)
    {
        _phase = PagePhase.Results;
        _collectedFriends = friends;

        // Determine which are new vs already in the list
        var existingFriends = _settingsService.GetFriendsList();
        var existingUsernames = new HashSet<string>(
            existingFriends.Select(f => f.Username.ToLowerInvariant()));

        int total = friends.Count;
        int existing = 0;

        ResultsListContainer.Children.Clear();

        foreach (var friend in friends)
        {
            bool alreadyExists = existingUsernames.Contains(friend.Username.ToLowerInvariant());
            if (alreadyExists) existing++;

            ResultsListContainer.Children.Add(CreateResultItem(friend, alreadyExists));
        }

        _newFriendsCount = total - existing;

        // Update summary
        ResultsTotalLabel.Text = total.ToString();
        ResultsNewLabel.Text = _newFriendsCount.ToString();
        ResultsExistingLabel.Text = existing.ToString();

        if (errorMessage != null)
        {
            ResultsTitleLabel.Text = "Partial Results";
        }

        if (total == 0)
        {
            NoResultsLabel.IsVisible = true;
            NoResultsLabel.Text = "No friends found in your DM list.";
        }
        else if (_newFriendsCount == 0)
        {
            NoResultsLabel.IsVisible = true;
            NoResultsLabel.Text = "All found friends are already in your list.";
        }

        // Switch UI
        CollectingOverlay.IsVisible = false;
        ResultsOverlay.IsVisible = true;

        ActionButton.Text = _newFriendsCount > 0
            ? $"Add {_newFriendsCount} Friend{(_newFriendsCount == 1 ? "" : "s")}"
            : "Done";
        ActionButton.IsEnabled = true;
    }

    private Color GetThemeColor(string key, string fallbackHex = "#92979E")
    {
        if (Application.Current != null && Application.Current.Resources.TryGetValue(key, out var resource) && resource is Color color)
            return color;
        return Color.FromArgb(fallbackHex);
    }

    private View CreateResultItem(CollectedFriend friend, bool alreadyExists)
    {
        var border = new Border
        {
            StrokeShape = new RoundRectangle { CornerRadius = 12 },
            Stroke = Colors.Transparent,
            Padding = new Thickness(12, 10),
            Opacity = alreadyExists ? 0.5 : 1.0
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

        var usernameLabel = new Label
        {
            Text = $"@{friend.Username}",
            FontSize = 14,
            FontFamily = "InterSemiBold"
        };

        grid.Children.Add(usernameLabel);

        if (alreadyExists)
        {
            var badge = new Label
            {
                Text = "Already added",
                FontSize = 11,
                FontFamily = "InterMedium",
                TextColor = GetThemeColor("Gray400", "#8B8F96"),
                VerticalOptions = LayoutOptions.Center
            };
            Grid.SetColumn(badge, 1);
            grid.Children.Add(badge);
        }
        else
        {
            var dot = new Border
            {
                WidthRequest = 8,
                HeightRequest = 8,
                StrokeThickness = 0,
                StrokeShape = new RoundRectangle { CornerRadius = 4 },
                BackgroundColor = GetThemeColor("Success", "#22946E"),
                VerticalOptions = LayoutOptions.Center
            };
            Grid.SetColumn(dot, 1);
            grid.Children.Add(dot);
        }

        border.Content = grid;
        return border;
    }

    // ── Actions ─────────────────────────────────────────────────────────────

    private async void OnBackClicked(object? sender, EventArgs e)
    {
        await Navigation.PopAsync();
    }

    private async void OnActionClicked(object? sender, EventArgs e)
    {
        // Phase: Loading → start collection
        if (_phase == PagePhase.Loading && !_jsInjected)
        {
            await StartCollection();
            return;
        }

        // Phase: Results with new friends → import
        if (_phase == PagePhase.Results && _newFriendsCount > 0)
        {
            ImportNewFriends();
            await DisplayAlert("Import Complete",
                $"{_newFriendsCount} friend{(_newFriendsCount == 1 ? "" : "s")} added to your streak list.", "OK");
            await Navigation.PopAsync();
            return;
        }

        // Default: go back
        await Navigation.PopAsync();
    }

    private void ImportNewFriends()
    {
        var existingFriends = _settingsService.GetFriendsList();
        var existingUsernames = new HashSet<string>(
            existingFriends.Select(f => f.Username.ToLowerInvariant()));

        foreach (var friend in _collectedFriends)
        {
            if (existingUsernames.Contains(friend.Username.ToLowerInvariant()))
                continue;

            var config = new FriendConfig
            {
                Username = friend.Username,
                DisplayName = string.Empty,
                IsEnabled = true
            };
            existingFriends.Add(config);
            existingUsernames.Add(friend.Username.ToLowerInvariant());
        }

        _settingsService.SaveFriendsList(existingFriends);
    }

    // ── DTOs ────────────────────────────────────────────────────────────────

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
