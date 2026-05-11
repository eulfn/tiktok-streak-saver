using Microsoft.Maui.Controls.Shapes;
using Feener.Models;
using Feener.Services;
using Feener.Views;

namespace Feener.Pages;

[Microsoft.Maui.Controls.Internals.Preserve(AllMembers = true)]
public partial class HistoryPage : ContentPage
{
    private readonly SettingsService _settingsService;
    private readonly SuccessRateDrawable _chartDrawable;
    private bool _isExportingLogs = false;

    public HistoryPage()
    {
        InitializeComponent();
        _settingsService = new SettingsService();
        _chartDrawable = new SuccessRateDrawable();
        SuccessChartView.Drawable = _chartDrawable;
    }

    private Color GetThemeColor(string key, string fallbackHex = "#92979E")
    {
        if (Application.Current != null && Application.Current.Resources.TryGetValue(key, out var resource) && resource is Color color)
            return color;
        return Color.FromArgb(fallbackHex);
    }

    private bool _isBurstModeActive = false;

    protected override void OnAppearing()
    {
        base.OnAppearing();
        this.Opacity = 1;
        this.TranslationY = 0;

        _isBurstModeActive = _settingsService.IsBurstModeActive();
        if (_isBurstModeActive) SetBurstModeUI();
        else SetNormalModeUI();

        LoadStats();
        LoadHistory();
    }

    private void OnNormalModeTapped(object? sender, TappedEventArgs e)
    {
        _isBurstModeActive = false;
        SetNormalModeUI();
        LoadStats();
        LoadHistory();
    }

    private void OnBurstModeTapped(object? sender, TappedEventArgs e)
    {
        _isBurstModeActive = true;
        SetBurstModeUI();
        LoadStats();
        LoadHistory();
    }

    private void SetNormalModeUI()
    {
        NormalModeTabBorder.BackgroundColor = GetThemeColor("Primary", "#FE2C55");
        NormalModeTabLabel.TextColor = GetThemeColor("White", "#FFFFFF");
        BurstModeTabBorder.BackgroundColor = Colors.Transparent;
        BurstModeTabLabel.TextColor = GetThemeColor("Gray600", "#4B5563");
    }

    private void SetBurstModeUI()
    {
        BurstModeTabBorder.BackgroundColor = GetThemeColor("BurstAccent", "#8B5CF6");
        BurstModeTabLabel.TextColor = Colors.White;
        NormalModeTabBorder.BackgroundColor = Colors.Transparent;
        NormalModeTabLabel.TextColor = GetThemeColor("Gray600", "#4B5563");
    }

    private void OnRefreshing(object? sender, EventArgs e)
    {
        LoadStats();
        LoadHistory();
        MainRefreshView.IsRefreshing = false;
    }

    private void LoadStats()
    {
        var history = _settingsService.GetRunHistory().Where(r => r.IsBurstMode == _isBurstModeActive).ToList();

        int totalMessages;
        int successMessages;
        float rate;

        if (_isBurstModeActive)
        {
            // Burst: use BurstMessagesSent for successful runs vs daily limit as the target
            int burstTarget = _settingsService.GetBurstDailyLimit();
            totalMessages = history.Sum(r => r.BurstMessagesSent);
            successMessages = history.Where(r => r.Success).Sum(r => r.BurstMessagesSent);
            rate = totalMessages > 0 ? (float)successMessages / totalMessages : 0;
        }
        else
        {
            // Normal: use FriendResults for per-friend message tracking
            totalMessages = history.Sum(r => r.FriendResults?.Count ?? 0);
            successMessages = history.Sum(r => r.FriendResults?.Count(f => f.Success) ?? 0);
            rate = totalMessages > 0 ? (float)successMessages / totalMessages : 0;
        }

        bool isDark = Application.Current?.RequestedTheme == AppTheme.Dark;
        _chartDrawable.IsDarkTheme = isDark;
        _chartDrawable.SuccessRate = rate;
        _chartDrawable.RateText = totalMessages > 0 ? $"{(int)(rate * 100)}%" : "—";
        _chartDrawable.SubText = totalMessages > 0 ? "success" : "";
        SuccessChartView.Invalidate();

        if (history.Count > 0)
        {
            SuccessRateLabel.Text = totalMessages > 0
                ? $"{successMessages} of {totalMessages} messages successful"
                : $"{history.Count(r => r.Success)} of {history.Count} runs successful";
            TotalRunsLabel.Text = $"Last {Math.Min(history.Count, 50)} runs tracked";

            var lastRun = history.FirstOrDefault();
            if (lastRun != null && lastRun.Duration.HasValue)
            {
                var dur = lastRun.Duration.Value;
                if (dur.TotalSeconds < 1)
                {
                    AvgDurationLabel.Text = "Last run: < 1s";
                }
                else
                {
                    AvgDurationLabel.Text = dur.TotalMinutes >= 1
                        ? $"Last run: ~{(int)dur.TotalMinutes}m {dur.Seconds}s"
                        : $"Last run: ~{(int)dur.TotalSeconds}s";
                }
            }
            else
            {
                AvgDurationLabel.Text = "Last run: N/A";
            }
        }
        else
        {
            SuccessRateLabel.Text = "No data yet";
            TotalRunsLabel.Text = "";
            AvgDurationLabel.Text = "N/A";
        }
    }

    private void LoadHistory()
    {
        var allHistory = _settingsService.GetRunHistory().Where(r => r.IsBurstMode == _isBurstModeActive).ToList();
        var itemsToRemove = HistoryContainer.Children.Where(c => c != NoHistoryLabel).ToList();
        foreach (var item in itemsToRemove) HistoryContainer.Children.Remove(item);
        NoHistoryLabel.IsVisible = allHistory.Count == 0;
        foreach (var run in allHistory) HistoryContainer.Children.Add(CreateHistoryView(run));
    }

    private string ShortenErrorMessage(string? originalMsg)
    {
        if (string.IsNullOrEmpty(originalMsg)) return "";
        if (originalMsg.Contains("login required", StringComparison.OrdinalIgnoreCase)) return "Login required";
        if (originalMsg.Contains("navigate to", StringComparison.OrdinalIgnoreCase)) return "Navigation failed";
        if (originalMsg.Contains("No network", StringComparison.OrdinalIgnoreCase)) return "No network";
        if (originalMsg.Contains("stopped by user", StringComparison.OrdinalIgnoreCase)) return "Stopped by user";
        if (originalMsg.Contains("WebView", StringComparison.OrdinalIgnoreCase)) return "WebView error";
        if (originalMsg.Contains("target username", StringComparison.OrdinalIgnoreCase)) return "No target set";
        return originalMsg;
    }

    private View CreateHistoryView(StreakRunResult run)
    {
        var statusColor = run.Success ? Color.FromArgb("#22C55E") : GetThemeColor("Error", "#9C2121");
        var border = new Border
        {
            StrokeThickness = 0,
            StrokeShape = new RoundRectangle { CornerRadius = 16 },
            Padding = new Thickness(14, 12),
            Margin = new Thickness(0, 4)
        };
        border.SetAppThemeColor(Border.BackgroundColorProperty, GetThemeColor("Gray100", "#F3F4F6"), GetThemeColor("Gray900", "#111827"));

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitionCollection
            {
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = GridLength.Star },
            },
            ColumnSpacing = 12,
            VerticalOptions = LayoutOptions.Center
        };
        var statusDot = new Border
        {
            WidthRequest = 8, HeightRequest = 8, StrokeThickness = 0,
            StrokeShape = new RoundRectangle { CornerRadius = 4 },
            BackgroundColor = statusColor, VerticalOptions = LayoutOptions.Center, Margin = new Thickness(4, 0)
        };
        grid.Children.Add(statusDot);
        var infoStack = new VerticalStackLayout { Spacing = 3 };
        infoStack.Children.Add(new Label { Text = run.RunTime.ToString("MMM dd, HH:mm"), FontSize = 15, FontFamily = "InterMedium" });

        // Build the detail line based on run type
        if (run.IsBurstMode)
        {
            // Burst runs: use BurstMessagesSent for the detail line
            string detail;
            if (run.Success)
            {
                detail = $"{run.BurstMessagesSent} messages sent";
            }
            else if (!string.IsNullOrEmpty(run.ErrorMessage))
            {
                detail = ShortenErrorMessage(run.ErrorMessage);
            }
            else
            {
                detail = "Burst failed";
            }
            var detailLabel = new Label { Text = detail, FontSize = 13, LineBreakMode = LineBreakMode.TailTruncation };
            detailLabel.TextColor = run.Success ? GetThemeColor("Gray400") : statusColor;
            infoStack.Children.Add(detailLabel);
        }
        else
        {
            // Normal runs: use FriendResults
            var successCount = run.FriendResults.Count(r => r.Success);
            var totalCount = run.FriendResults.Count;
            if (totalCount > 0)
            {
                var skippedCount = totalCount - successCount;
                if (skippedCount > 0)
                {
                    var hStack = new HorizontalStackLayout { Spacing = 16 };
                    var l1 = new Label { Text = $"{successCount}/{totalCount} sent", FontSize = 13 };
                    l1.SetAppThemeColor(Label.TextColorProperty, GetThemeColor("Gray400"), GetThemeColor("Gray400"));
                    var l2 = new Label { Text = $"{skippedCount} skipped", FontSize = 13 };
                    l2.SetAppThemeColor(Label.TextColorProperty, GetThemeColor("Gray400"), GetThemeColor("Gray400"));
                    hStack.Children.Add(l1);
                    hStack.Children.Add(l2);
                    infoStack.Children.Add(hStack);
                }
                else
                {
                    var infoLabel = new Label { Text = $"{successCount}/{totalCount} messages sent", FontSize = 13 };
                    infoLabel.SetAppThemeColor(Label.TextColorProperty, GetThemeColor("Gray400"), GetThemeColor("Gray400"));
                    infoStack.Children.Add(infoLabel);
                }
            }
            else if (!string.IsNullOrEmpty(run.ErrorMessage))
            {
                var shortMsg = ShortenErrorMessage(run.ErrorMessage);
                infoStack.Children.Add(new Label { Text = shortMsg, FontSize = 12, TextColor = statusColor, LineBreakMode = LineBreakMode.TailTruncation });
            }
        }

        Grid.SetColumn(infoStack, 1);
        grid.Children.Add(infoStack);
        border.Content = grid;
        return border;
    }

    private async void OnClearHistoryClicked(object? sender, EventArgs e)
    {
        var history = _settingsService.GetRunHistory();
        if (history.Count == 0) return;
        bool confirm = await DisplayAlert("Clear History", "Are you sure you want to clear your run history?", "Clear", "Cancel");
        if (confirm) { _settingsService.ClearRunHistory(); LoadHistory(); LoadStats(); }
    }

    private async void OnExportLogsClicked(object? sender, EventArgs e)
    {
        if (_isExportingLogs) return;
        _isExportingLogs = true;
        try
        {
#if ANDROID
            var logs = Feener.Platforms.Android.Services.StreakService.GetLogs();
#else
            var logs = new List<string>();
#endif
            if (logs == null || logs.Count == 0) { await DisplayAlert("Export Logs", "No logs to export", "OK"); return; }
            var textContent = string.Join(Environment.NewLine, logs);
            var fileName = $"streak_logs_{DateTime.Now:yyyyMMdd_HHmm}.txt";
            var filePath = System.IO.Path.Combine(FileSystem.CacheDirectory, fileName);
            await System.IO.File.WriteAllTextAsync(filePath, textContent);
            await Share.Default.RequestAsync(new ShareFileRequest { Title = "Export System Logs", File = new ShareFile(filePath, "text/plain") });
        }
        catch (Exception ex) { await DisplayAlert("Export Failed", $"Could not export logs: {ex.Message}", "OK"); }
        finally { _isExportingLogs = false; }
    }
}
