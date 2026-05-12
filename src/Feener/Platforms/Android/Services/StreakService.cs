using System.Diagnostics;
using Android.App;
using Android.Content;
using Android.OS;
using Android.Webkit;
using Android.Runtime;
using AndroidX.Core.App;
using System.Text.Json;
using Android.Content.PM;
using Java.Interop;
using Microsoft.Maui.Controls.Internals;
using RandomUserAgent;
using Feener.Models;
using Feener.Services;
using System.Threading.Tasks;
using AsyncAwaitBestPractices;
using WebView = Android.Webkit.WebView;

namespace Feener.Platforms.Android.Services;

[Service(Name = AppConstants.PackageName + ".Services.StreakService", ForegroundServiceType = global::Android.Content.PM.ForegroundService.TypeDataSync)]
[Microsoft.Maui.Controls.Internals.Preserve(AllMembers = true)]
public class StreakService : Service
{
    private const string ChannelId = "streak_service_channel";
    private const string ChannelName = "Streak Service";
    private const int NotificationId = 1001;

    private WebView? _webView;
    private Handler? _mainHandler;
    private SettingsService? _settingsService;
    private List<FriendConfig>? _friendsToProcess;
    private int _currentFriendIndex;
    private StreakRunResult? _runResult;
    private PowerManager.WakeLock? _wakeLock;
    private string _baseScript = string.Empty;
    private readonly List<string> _disabledUsernames = new();
    private readonly Random _rng = new();
    private const string UserNotFoundError = "User not found in chat list";

    // ── Burst Mode state (Smart Daily Quota) ──
    private bool _isBurstMode = false;
    private List<string> _burstMessages = new();
    private int _lastBurstMessageIndex = -1;
    private int _burstTotalSent = 0;
    private int _burstChunkSent = 0;
    private int _burstCurrentChunkSize = 35;
    private int _burstSessionCount = 0;
    private int _burstRemaining = 0;
    private bool _isHibernating = false;
    private long _hibernationEndTimeMs = 0;

    // ── Randomized Normal Messages state ──
    private List<string>? _shuffledNormalMessages;
    private int _normalMessageIndex = 0;

    // ── Service lifecycle flags ──
    private bool _isCancelRequested = false;
    private bool _automationStarted = false;

    // ── Retry-only filter (Feature 3) ──
    private List<string>? _retryUsernames = null;


    // ── Run-level mutex: prevents concurrent automation sessions ──
    private static volatile bool _isRunning = false;
    private static readonly object _runLock = new();

    /// <summary>
    /// True while an automation session is active. Checked by StreakScheduler.RunNow
    /// and OnStartCommand to prevent overlapping runs.
    /// </summary>
    public static bool IsRunning => _isRunning;

    private int _cooldownSkippedCount = 0;
    private int _failureAttemptsForCurrentFriend = 0;
    private const int MaxSendAttemptsPerFriend = 4;

    private static List<string> _logs = new();

    public static List<string> GetLogs()
    {
        return _logs ?? new List<string>();
    }

    private static void AppLog(string phase, string username, string message)
    {
        var entry = $"[{DateTime.Now:HH:mm:ss}] [{phase}] [{username}] {message}";
        _logs.Add(entry);
        System.Diagnostics.Debug.WriteLine(entry);
    }

    public override void OnCreate()
    {
        base.OnCreate();

        // Create notification channel FIRST before anything else
        CreateNotificationChannel();

        _mainHandler = new Handler(Looper.MainLooper!);
        _settingsService = new SettingsService();
        AcquireWakeLock();

        // Start foreground IMMEDIATELY in OnCreate to avoid ANR
        StartForegroundServiceImmediate();
    }

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        if (intent?.Action == "STOP_SERVICE")
        {
            _isCancelRequested = true;
            AppLog("SYSTEM", "-", "Service stop requested by user");
            CompleteService(false, "Run stopped by user.");
            return StartCommandResult.NotSticky;
        }

        // Handle Burst Mode flag
        _isBurstMode = intent?.GetBooleanExtra("IsBurstMode", false) ?? false;

        // Handle Retry-only filter (Feature 3)
        var retryJson = intent?.GetStringExtra("RetryUsernames");
        if (!string.IsNullOrEmpty(retryJson))
        {
            try { _retryUsernames = System.Text.Json.JsonSerializer.Deserialize<List<string>>(retryJson); }
            catch { _retryUsernames = null; }
        }

        // Ensure we're in foreground mode (in case OnCreate didn't complete it)
        StartForegroundServiceImmediate();

        // ── Run-level mutex: reject if another automation session is already active ──
        lock (_runLock)
        {
            if (_isRunning)
            {
                AppLog("SYSTEM", "-", "OnStartCommand ignored — automation already running");
                return StartCommandResult.NotSticky;
            }
            _isRunning = true;
        }

        // Start the WebView automation on main thread
        _mainHandler?.Post(StartWebViewAutomation);

        return StartCommandResult.NotSticky;
    }

    private void StartForegroundServiceImmediate()
    {
        try
        {
            var notification = CreateNotification("Preparing to send streaks...");

            if (Build.VERSION.SdkInt >= BuildVersionCodes.Q)
            {
                // Android 10+ requires specifying the foreground service type
                StartForeground(NotificationId, notification, ForegroundService.TypeDataSync);
            }
            else
            {
                StartForeground(NotificationId, notification);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"StartForeground error: {ex.Message}");
        }
    }

    public override IBinder? OnBind(Intent? intent) => null;

    public override void OnDestroy()
    {
        // Safety net: clear the run-level mutex if the service is destroyed
        // without CompleteService (e.g., system kill while WebView is loading)
        lock (_runLock)
        {
            _isRunning = false;
        }
        ReleaseWakeLock();
        CleanupWebView();
        base.OnDestroy();
    }

    private void AcquireWakeLock()
    {
        var powerManager = (PowerManager?)GetSystemService(PowerService);
        _wakeLock = powerManager?.NewWakeLock(WakeLockFlags.Partial, "Feener::StreakWakeLock");
        _wakeLock?.Acquire(6L * 60 * 60 * 1000); // 6 hours max (auto-released on service destroy)
    }

    private void ReleaseWakeLock()
    {
        if (_wakeLock?.IsHeld == true)
        {
            _wakeLock.Release();
        }
    }

    private void CreateNotificationChannel()
    {
        if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
        {
            var notificationManager = (NotificationManager?)GetSystemService(NotificationService);
            if (notificationManager == null) return;

            // Check if channel already exists
            var existingChannel = notificationManager.GetNotificationChannel(ChannelId);
            if (existingChannel != null) return;

            var channel = new NotificationChannel(ChannelId, ChannelName, NotificationImportance.Low)
            {
                Description = "Notification channel for streak service"
            };
            channel.SetShowBadge(false);

            notificationManager?.CreateNotificationChannel(channel);
        }
    }

    private Notification CreateNotification(string message)
    {
        var intent = new Intent(this, typeof(MainActivity));
        intent.SetFlags(ActivityFlags.NewTask | ActivityFlags.ClearTop);
        var pendingIntent = PendingIntent.GetActivity(this, 0, intent, PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);

        var builder = new NotificationCompat.Builder(this, ChannelId)
            .SetContentTitle("Feener")
            .SetContentText(message)
            .SetStyle(new NotificationCompat.BigTextStyle().BigText(message))
            .SetSmallIcon(Resource.Drawable.ic_notification)
            .SetContentIntent(pendingIntent)
            .SetOngoing(true)
            .SetForegroundServiceBehavior(NotificationCompat.ForegroundServiceImmediate)
            .SetCategory(NotificationCompat.CategoryService)
            .SetPriority(NotificationCompat.PriorityLow)
            .SetProgress(0, 0, true);

        return builder.Build()!;
    }

    private void UpdateNotification(string message, int progress = -1, int max = 0)
    {
        var intent = new Intent(this, typeof(MainActivity));
        intent.SetFlags(ActivityFlags.NewTask | ActivityFlags.ClearTop);
        var pendingIntent = PendingIntent.GetActivity(this, 0, intent, PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);

        var builder = new NotificationCompat.Builder(this, ChannelId)
            .SetContentTitle("Feener")
            .SetContentText(message)
            .SetStyle(new NotificationCompat.BigTextStyle().BigText(message))
            .SetSmallIcon(Resource.Drawable.ic_notification)
            .SetContentIntent(pendingIntent)
            .SetOngoing(true)
            .SetForegroundServiceBehavior(NotificationCompat.ForegroundServiceImmediate)
            .SetCategory(NotificationCompat.CategoryService)
            .SetPriority(NotificationCompat.PriorityLow);

        if (progress >= 0 && max > 0)
            builder!.SetProgress(max, progress, false);
        else
            builder!.SetProgress(0, 0, true);

        var notificationManager = (NotificationManager?)GetSystemService(NotificationService);
        notificationManager?.Notify(NotificationId, builder.Build()!);
    }

    private async void StartWebViewAutomation()
    {
        try
        {
            var allEnabled = _settingsService?.GetEnabledFriends() ?? new List<FriendConfig>();
            _currentFriendIndex = 0;
            _runResult = new StreakRunResult { IsBurstMode = _isBurstMode };
            _cooldownSkippedCount = 0;
            _logs.Clear();

            // ── Normal or Burst Initialization ──
            _friendsToProcess = new List<FriendConfig>();
            
            if (_isBurstMode)
            {
                var target = _settingsService?.GetBurstTargetUsername() ?? "";
                if (string.IsNullOrWhiteSpace(target))
                {
                    AppLog("SYSTEM", "-", "Burst Mode started without target username");
                    CompleteService(false, "No target username set for Burst Mode.");
                    return;
                }
                
                // Check daily cap before starting
                var dailyLimit = _settingsService?.GetBurstDailyLimit() ?? 0;
                _burstRemaining = Math.Max(0, dailyLimit - (_settingsService?.GetBurstDailySentCount() ?? 0));
                if (_burstRemaining == 0)
                {
                    AppLog("SYSTEM", "-", $"Daily burst cap already reached ({dailyLimit}).");
                    CompleteService(true, $"Daily burst cap already reached ({dailyLimit} messages sent today).");
                    return;
                }
                
                _friendsToProcess.Add(new FriendConfig { Username = target, IsEnabled = true });
                _burstMessages = _settingsService?.GetBurstMessages() ?? new List<string> { SettingsService.DefaultMessage };
                if (_burstMessages.Count == 0) _burstMessages.Add(SettingsService.DefaultMessage);
                _burstTotalSent = 0;
                _burstChunkSent = 0;
                _burstSessionCount = 0;
                _burstCurrentChunkSize = _rng.Next(SettingsService.BurstChunkSizeMin, SettingsService.BurstChunkSizeMax + 1);
                
                var avgChunk = (SettingsService.BurstChunkSizeMin + SettingsService.BurstChunkSizeMax) / 2;
                var estSessions = (int)Math.Ceiling((double)_burstRemaining / avgChunk);
                AppLog("SYSTEM", "-", $"Starting SMART BURST targeting @{target}: {_burstRemaining} msgs remaining, ~{estSessions} sessions, {_burstMessages.Count} message variants.");
            }
            else
            {
                var today = DateTime.Now.Date;
                foreach (var friend in allEnabled)
                {
                    // Feature 3: If retry filter is active, only include matching usernames
                    if (_retryUsernames != null && _retryUsernames.Count > 0)
                    {
                        var matchKey = friend.IsGroup ? friend.DisplayName : friend.Username;
                        if (!_retryUsernames.Any(r => r.Equals(matchKey, StringComparison.OrdinalIgnoreCase)))
                            continue;
                    }

                    if (friend.LastMessageSent.HasValue && friend.LastMessageSent.Value.Date == today)
                    {
                        // When retrying, don't skip based on cooldown — we explicitly want to resend
                        if (_retryUsernames != null && _retryUsernames.Count > 0)
                        {
                            _friendsToProcess.Add(friend);
                        }
                        else
                        {
                            _cooldownSkippedCount++;
                            AppLog("SKIP", $"@{friend.Username}", $"Already messaged today at {friend.LastMessageSent.Value:HH:mm}");
                        }
                    }
                    else
                    {
                        _friendsToProcess.Add(friend);
                    }
                }

                // Initialize randomized message pool if enabled
                if (_settingsService.GetRandomizeNormalMessages())
                {
                    _shuffledNormalMessages = new List<string>(SettingsService.BuiltInStreakMessages);
                    ShuffleList(_shuffledNormalMessages);
                    _normalMessageIndex = 0;
                    AppLog("SYSTEM", "-", $"Randomized messages enabled: {_shuffledNormalMessages.Count} variants loaded");
                }

                // Sort by chat priority setting
                var priority = _settingsService.GetChatPriority();
                if (priority == SettingsService.PriorityFriendsFirst)
                    _friendsToProcess = _friendsToProcess.OrderBy(f => f.IsGroup).ToList();
                else if (priority == SettingsService.PriorityGroupsFirst)
                    _friendsToProcess = _friendsToProcess.OrderByDescending(f => f.IsGroup).ToList();

                var modeLabel = _retryUsernames != null ? "retry" : "normal";
                AppLog("SYSTEM", "-", $"Starting {modeLabel} automation: {_friendsToProcess.Count} to process, {_cooldownSkippedCount} skipped (already sent today){(priority != SettingsService.PriorityMixed ? $", priority: {priority} first" : "")}");

                if (_friendsToProcess.Count == 0)
                {
                    var msg = _cooldownSkippedCount > 0
                        ? $"All {_cooldownSkippedCount} friends already messaged today"
                        : "No friends configured";
                    CompleteService(_cooldownSkippedCount > 0, msg);
                    return;
                }
            }

            // Pre-flight network check
            if (!NetworkConnectivity.HasWifiOrCellularInternet(this))
            {
                AppLog("SYSTEM", "-", "No Wi-Fi or mobile data — skipping run");
                CompleteService(false, "No network connection — skipped.");
                return;
            }

            UpdateNotification("Preparing automation...");

            //read tiktok_automation.js from assets
            using var resourceStream = await FileSystem.OpenAppPackageFileAsync("tiktok_automation.js");
            using var reader = new StreamReader(resourceStream);
            this._baseScript = await reader.ReadToEndAsync();
            // Minify: strip comment lines, collapse whitespace
            this._baseScript = string.Join("\n", this._baseScript.Split('\n').Where(line => !line.TrimStart().StartsWith("//")));
            this._baseScript = System.Text.RegularExpressions.Regex.Replace(this._baseScript, @"\s+", " ").Trim();

            // Create WebView
            _webView = new WebView(this);
            _webView.Settings.JavaScriptEnabled = true;
            _webView.Settings.DomStorageEnabled = true;
            _webView.Settings.DatabaseEnabled = true;
            _webView.Settings.CacheMode = CacheModes.Normal;

            // Use the same UA that was used during login to maintain session consistency.
            // Falls back to Chrome 91 desktop UA which avoids TikTok bot detection.
            var sessionService = new SessionService();
            var loginUa = sessionService.GetLoginUserAgent()
                ?? "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";
            _webView.Settings.UserAgentString = loginUa;
            _webView.Settings.SetSupportZoom(true);
            _webView.Settings.BuiltInZoomControls = true;

            // Give the headless WebView a real viewport so TikTok's virtualized
            // chat list renders its children. Without dimensions, the WebView is
            // 0x0 and lazy-rendered elements (dm-new-conversation-item) never appear.
            _webView.Settings.UseWideViewPort = true;
            _webView.Settings.LoadWithOverviewMode = true;
            _webView.Layout(0, 0, 1920, 1080);

            // Enable cookies
            var cookieManager = CookieManager.Instance;
            cookieManager?.SetAcceptCookie(true);
            cookieManager?.SetAcceptThirdPartyCookies(_webView, true);

            // Set up WebView client
            _webView.SetWebViewClient(new StreakWebViewClient(this));

            // Add JavaScript interface
            _webView.AddJavascriptInterface(new StreakJsInterface(this), "StreakApp");

            // Load TikTok messages page
            _webView.LoadUrl("https://www.tiktok.com/messages?lang=en");


            _mainHandler!.PostDelayed(() =>
            {
                if (!(_webView?.Url ?? "").Contains("tiktok.com/messages"))
                {
                    _webView?.LoadUrl("https://www.tiktok.com/messages?lang=en");
                    _mainHandler.PostDelayed(() =>
                    {
                        if (!(_webView?.Url ?? "").Contains("tiktok.com/messages"))
                        {
                            CompleteService(false, "Could not navigate to tiktok.com/messages");
                        }
                    }, 5000);
                }
            }, 5000);
        }
        catch (Exception ex)
        {
            CompleteService(false, $"Error starting WebView: {ex.Message}");
        }
    }

    private void CleanupWebView()
    {
        _mainHandler?.Post(() =>
        {
            _webView?.StopLoading();
            _webView?.Destroy();
            _webView = null;
        });
    }

    internal void OnPageLoaded(string url)
    {
        // Check if we're on the messages page
        if (url.Contains("tiktok.com/messages"))
        {
            // Guard: only start the automation chain once (first page load)
            if (_automationStarted) return;
            _automationStarted = true;

            UpdateNotification("Connecting to TikTok...");
            AppLog("NAVIGATION", "-", "Messages page ready");
            // Wait a bit for the page to fully render, then start automation
            _mainHandler?.PostDelayed(ProcessNextFriend, 3000);
        }
        else if (url.Contains("login"))
        {
            AppLog("NAVIGATION", "-", "TikTok login required");
            // User needs to login
            CompleteService(false, "TikTok login required. Please login via the app first.");
        }
    }

    private void ProcessNextFriend()
    {
        if (_isCancelRequested) return;

        // When "Skip Unreachable Users" is OFF, abort the entire run on any per-user failure
        bool skipUnreachable = _settingsService?.GetSkipUnreachableUsers() ?? false;
        if (!skipUnreachable && _runResult is not null && _runResult.Failed)
        {
            CompleteService(false, $"Run stopped: {_runResult.ErrorMessage ?? _runResult.FriendsErrorMessage}");
            return;
        }

        if (_friendsToProcess == null || _currentFriendIndex >= _friendsToProcess.Count)
        {
            // All friends processed — mark success only if every friend succeeded
            var allSucceeded = _runResult?.FriendResults.All(r => r.Success) ?? false;
            var completionMessage = allSucceeded
                ? "All messages sent successfully"
                : $"{_runResult?.FriendResults.Count(r => r.Success) ?? 0} of {_runResult?.FriendResults.Count ?? 0} sent";
            CompleteService(allSucceeded, completionMessage);
            return;
        }

        var friend = _friendsToProcess[_currentFriendIndex];

        if (!_isBurstMode)
        {
            var logTarget = friend.IsGroup ? $"Group: {friend.DisplayName}" : $"@{friend.Username}";
            AppLog("PROCESS", logTarget, $"Starting normal messaging");
        }

        SendCurrentFriendMessage();
    }

    private void SendCurrentFriendMessage()
    {
        if (_isCancelRequested) return;

        var friend = _friendsToProcess![_currentFriendIndex];
        string message = "";

        if (_isBurstMode)
        {
            // Pick a message, avoiding the exact same as last time if we have >1 option
            int nextIdx = 0;
            if (_burstMessages.Count > 1)
            {
                do { nextIdx = _rng.Next(_burstMessages.Count); } while (nextIdx == _lastBurstMessageIndex);
            }
            
            _lastBurstMessageIndex = nextIdx;
            message = _burstMessages[nextIdx];
            
            UpdateNotification($"Bursting @{friend.Username} (Total: {_burstTotalSent})...");
            AppLog("BURST", $"@{friend.Username}", $"Injecting random message: {message}");
        }
        else
        {
            if (_shuffledNormalMessages != null && _shuffledNormalMessages.Count > 0)
            {
                message = _shuffledNormalMessages[_normalMessageIndex % _shuffledNormalMessages.Count];
                _normalMessageIndex++;
                // Reshuffle when pool is exhausted to avoid repeating the same sequence
                if (_normalMessageIndex >= _shuffledNormalMessages.Count)
                {
                    ShuffleList(_shuffledNormalMessages);
                    _normalMessageIndex = 0;
                }
            }
            else
            {
                message = _settingsService?.GetMessageText() ?? SettingsService.DefaultMessage;
            }
            UpdateNotification($"{_currentFriendIndex + 1}/{_friendsToProcess.Count} \u2014 Processing: {(friend.IsGroup ? friend.DisplayName : "@" + friend.Username)}", _currentFriendIndex, _friendsToProcess.Count);
        }

        // Inject JavaScript to find and message the friend/group
        var target = friend.IsGroup ? friend.DisplayName : friend.Username;
        
        if (string.IsNullOrWhiteSpace(target))
        {
            AppLog("FAIL", "-", friend.IsGroup ? "Group name is empty" : "Username is empty");
            _currentFriendIndex++;
            _mainHandler?.PostDelayed(ProcessNextFriend, 1000);
            return;
        }

        var js = GetFriendMessageScript(target, message, friend.IsGroup);
        _webView?.EvaluateJavascript(js, null);
    }

    private string GetFriendMessageScript(string target, string message, bool isGroup)
    {
        // Escape special characters for JavaScript
        target = target ?? string.Empty;
        message = message ?? string.Empty;
        var escapedTarget = target.Replace("\\", "\\\\").Replace("'", "\\'").Replace("\"", "\\\"");
        var escapedMessage = message.Replace("\\", "\\\\").Replace("'", "\\'").Replace("\"", "\\\"").Replace("\n", "\\n");


        var automationScript = this._baseScript.Replace("[UserName]", escapedTarget);
        automationScript = automationScript.Replace("[Message]", escapedMessage);
        automationScript = automationScript.Replace("[IsGroup]", isGroup ? "true" : "false");
        return automationScript;
    }

    /// <summary>
    /// Fisher-Yates shuffle for in-place randomization of a list.
    /// </summary>
    private void ShuffleList<T>(List<T> list)
    {
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = _rng.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }

    internal void OnMessageResult(string username, bool success, string error)
    {
        if (_isCancelRequested) return;
        if (_friendsToProcess == null || _settingsService == null) return;

        var friend = _friendsToProcess.FirstOrDefault(f =>
            f.IsGroup
                ? f.DisplayName.Equals(username, StringComparison.OrdinalIgnoreCase)
                : f.Username.Equals(username, StringComparison.OrdinalIgnoreCase));

        if (friend != null)
        {
            if (!success)
            {
                _failureAttemptsForCurrentFriend++;

                // Retry before giving up (skip retries in burst — same target each time)
                if (!_isBurstMode && _failureAttemptsForCurrentFriend < MaxSendAttemptsPerFriend && error != UserNotFoundError)
                {
                    AppLog("RETRY", $"@{username}", $"Attempt {_failureAttemptsForCurrentFriend}/{MaxSendAttemptsPerFriend}: {error}");
                    _mainHandler?.PostDelayed(SendCurrentFriendMessage, 3000);
                    return;
                }

                // Max retries exceeded — record failure and move on
                friend.FailureCount++;
                friend.ConsecutiveFailures++;
                AppLog("FAIL", $"@{username}", error);

                // Auto-disable users not found in chat list when skip is enabled
                bool skipUnreachable = _settingsService.GetSkipUnreachableUsers();
                if (skipUnreachable && error == UserNotFoundError)
                {
                    friend.IsEnabled = false;
                    _disabledUsernames.Add($"@{username}");
                    AppLog("DISABLED", $"@{username}", "Auto-disabled \u2014 not found in chat list");
                }
                _settingsService.UpdateFriend(friend);

                _runResult?.FriendResults.Add(new FriendMessageResult
                {
                    FriendId = friend.Id,
                    Username = username,
                    Success = false,
                    ErrorMessage = error
                });

                _failureAttemptsForCurrentFriend = 0;
                if (_isBurstMode)
                {
                    // Burst targets a single user — don't advance the index.
                    // Retry after delay since the failure may be transient.
                    AppLog("BURST", $"@{username}", "Failure recorded, retrying after delay...");
                    _mainHandler?.PostDelayed(SendCurrentFriendMessage, 5000);
                }
                else
                {
                    _currentFriendIndex++;
                    UpdateNotification($"{_currentFriendIndex}/{_friendsToProcess.Count} : Failed: @{username}", _currentFriendIndex, _friendsToProcess.Count);
                    _mainHandler?.PostDelayed(ProcessNextFriend, 3000);
                }
                return;
            }

            // SUCCESS
            if (_isBurstMode)
            {
                _burstTotalSent++;
                _burstChunkSent++;
                _burstRemaining = Math.Max(0, _burstRemaining - 1);
                _settingsService.IncrementBurstDailySentCount();
                
                // Check daily cap
                var dailyLimit = _settingsService.GetBurstDailyLimit();
                var totalToday = _settingsService.GetBurstDailySentCount();
                if (totalToday >= dailyLimit)
                {
                    AppLog("BURST", $"@{username}", $"Daily cap reached! {totalToday}/{dailyLimit} sent today.");
                    CompleteService(true, $"Burst complete \u2014 {totalToday}/{dailyLimit} messages sent today.");
                    return;
                }
                
                // Check chunk boundary \u2014 enter hibernation
                if (_burstChunkSent >= _burstCurrentChunkSize)
                {
                    _burstSessionCount++;
                    var hibernationMs = _rng.Next(SettingsService.BurstHibernationMinMs, SettingsService.BurstHibernationMaxMs + 1);
                    var hibernationMin = hibernationMs / 60000.0;
                    AppLog("HIBERNATE", $"@{username}", $"Session {_burstSessionCount} complete ({_burstChunkSent} msgs). Hibernating {hibernationMin:F1} min...");
                    
                    StartHibernationCountdown(hibernationMs);
                    _mainHandler?.PostDelayed(StartNextBurstChunk, hibernationMs);
                    return;
                }
                
                // Normal inter-message delay
                int delayMs = _rng.Next(3000, 10001);
                AppLog("BURST", $"@{username}", $"Message {_burstTotalSent}/{_burstRemaining} sent (chunk {_burstChunkSent}/{_burstCurrentChunkSize}). Waiting {delayMs / 1000.0:F1}s...");
                
                UpdateNotification($"Bursting @{username} \u2014 {_burstTotalSent}/{_burstRemaining} (Session {_burstSessionCount + 1})");
                
                if (!_isCancelRequested)
                {
                    _mainHandler?.PostDelayed(SendCurrentFriendMessage, delayMs);
                }
                return;
            }

            // All simple burst chunks done — record success
            friend.SuccessCount++;
            friend.ConsecutiveFailures = 0;
            friend.LastMessageSent = DateTime.Now;
            AppLog("SUCCESS", $"@{username}", "Message sequence complete");

            _settingsService.UpdateFriend(friend);

            _runResult?.FriendResults.Add(new FriendMessageResult
            {
                FriendId = friend.Id,
                Username = username,
                Success = true,
                ErrorMessage = null
            });
        }
        else
        {
            // Username reported by JS doesn't match any friend in the list.
            // This can happen if TikTok returns a different username format.
            // Retry with the current friend rather than silently skipping.
            AppLog("WARN", $"@{username}", "Username from JS callback did not match any friend in the list. Retrying current friend...");
            _failureAttemptsForCurrentFriend++;
            if (_failureAttemptsForCurrentFriend < MaxSendAttemptsPerFriend)
            {
                _mainHandler?.PostDelayed(SendCurrentFriendMessage, 3000);
            }
            else
            {
                AppLog("FAIL", $"@{username}", "Max retries exceeded for unmatched username");
                _failureAttemptsForCurrentFriend = 0;
                _currentFriendIndex++;
                _mainHandler?.PostDelayed(ProcessNextFriend, 3000);
            }
            return;
        }

        // ── Normal flow: advance to next friend ──
        AdvanceToNextFriend(username);
    }

    /// <summary>
    /// Advance the friend index and schedule the next friend processing.
    /// Extracted to share between normal and burst completion paths.
    /// </summary>
    private void AdvanceToNextFriend(string username)
    {
        _currentFriendIndex++;
        _failureAttemptsForCurrentFriend = 0;
        var completedCount = _currentFriendIndex;
        var totalCount = _friendsToProcess?.Count ?? 0;
        var resultText = $"{completedCount}/{totalCount} : Sent to @{username}";
        UpdateNotification(resultText, completedCount, totalCount);
        _mainHandler?.PostDelayed(ProcessNextFriend, 3000);
    }

    /// <summary>
    /// Wake from hibernation and start the next burst chunk.
    /// </summary>
    private void StartNextBurstChunk()
    {
        if (_isCancelRequested) return;
        StopHibernationCountdown();
        
        _burstCurrentChunkSize = _rng.Next(SettingsService.BurstChunkSizeMin, SettingsService.BurstChunkSizeMax + 1);
        _burstChunkSent = 0;
        
        var target = _friendsToProcess?[0]?.Username ?? "unknown";
        AppLog("BURST", $"@{target}", $"Waking from hibernation. Starting session {_burstSessionCount + 1}, chunk size: {_burstCurrentChunkSize}");
        UpdateNotification($"Bursting @{target} \u2014 Resuming session {_burstSessionCount + 1}...");
        
        SendCurrentFriendMessage();
    }

    private void StartHibernationCountdown(int totalMs)
    {
        _isHibernating = true;
        _hibernationEndTimeMs = Java.Lang.JavaSystem.CurrentTimeMillis() + totalMs;
        TickHibernationCountdown();
    }

    private void StopHibernationCountdown()
    {
        _isHibernating = false;
    }

    private void TickHibernationCountdown()
    {
        if (!_isHibernating || _isCancelRequested) return;
        var remainingMs = _hibernationEndTimeMs - Java.Lang.JavaSystem.CurrentTimeMillis();
        if (remainingMs <= 0) return;
        var remainingMin = remainingMs / 60000;
        var remainingSec = (remainingMs % 60000) / 1000;
        UpdateNotification($"Hibernating \u2014 Session {_burstSessionCount} done \u2022 {_burstTotalSent} sent \u2022 Next in {remainingMin}m {remainingSec}s");
        _mainHandler?.PostDelayed(TickHibernationCountdown, 30_000);
    }

    private void CompleteService(bool success, string message)
    {
        try
        {
            // Update run result
            if (_runResult != null && _settingsService != null)
            {
                _runResult.Success = success;
                _runResult.ErrorMessage = success ? null : message;
                _runResult.Duration = DateTime.Now - _runResult.RunTime;
                
                if (_isBurstMode)
                    _runResult.BurstMessagesSent = _burstTotalSent;

                _settingsService.AddRunResult(_runResult);

                if (!_isBurstMode)
                {
                    _settingsService.SetLastRunTime(DateTime.Now);
                }
            }

            // Show completion notification
            string finalText;
            if (_isBurstMode)
            {
                // Burst mode tracks progress in _burstTotalSent, not FriendResults
                var dailyTotal = _settingsService?.GetBurstDailySentCount() ?? _burstTotalSent;
                var dailyLimit = _settingsService?.GetBurstDailyLimit() ?? 0;
                if (success)
                    finalText = $"Burst done — {_burstTotalSent} sent this session ({dailyTotal}/{dailyLimit} today)";
                else
                    finalText = $"Burst stopped — {_burstTotalSent} sent this session: {message}";
            }
            else
            {
                var successCount = _runResult?.FriendResults.Count(r => r.Success) ?? 0;
                var totalSent = _runResult?.FriendResults.Count ?? 0;
                var failedCount = totalSent - successCount;

                var cooldownNote = _cooldownSkippedCount > 0
                    ? $", {_cooldownSkippedCount} already sent"
                    : string.Empty;

                // Feature 2: Build failed username list for notification
                var failedNames = _runResult?.FriendResults
                    .Where(r => !r.Success)
                    .Select(r => $"@{r.Username}")
                    .ToList() ?? new List<string>();
                var failedSummary = "";
                if (failedNames.Count > 0 && failedNames.Count <= 5)
                    failedSummary = $" \u2014 Failed: {string.Join(", ", failedNames)}";
                else if (failedNames.Count > 5)
                    failedSummary = $" \u2014 Failed: {string.Join(", ", failedNames.Take(5))}, +{failedNames.Count - 5} more";

                if (success)
                {
                    finalText = $"Done : {successCount}/{totalSent} sent successfully{cooldownNote}";
                }
                else if (totalSent > 0 && successCount > 0)
                {
                    if (_disabledUsernames.Count > 0)
                        finalText = $"Done : {successCount}/{totalSent} sent, {_disabledUsernames.Count} disabled ({string.Join(", ", _disabledUsernames)}){cooldownNote}";
                    else
                        finalText = $"Done : {successCount}/{totalSent} sent, {failedCount} failed{failedSummary}{cooldownNote}";
                }
                else
                {
                    if (_disabledUsernames.Count > 0)
                        finalText = $"Done : 0/{totalSent} sent, {_disabledUsernames.Count} disabled ({string.Join(", ", _disabledUsernames)}){cooldownNote}";
                    else if (totalSent > 0)
                        finalText = $"Done : 0/{totalSent} sent, {failedCount} failed{failedSummary}{cooldownNote}";
                    else
                        finalText = $"Stopped : {message}";
                }
            }

            var finalNotification = new NotificationCompat.Builder(this, ChannelId)
                .SetContentTitle("Feener")
                .SetContentText(finalText)
                .SetSmallIcon(Resource.Drawable.ic_notification)
                .SetAutoCancel(true)
                .SetPriority(NotificationCompat.PriorityDefault)
                .Build()!;

            var notificationManager = (NotificationManager?)GetSystemService(NotificationService);
            notificationManager?.Notify(NotificationId + 1, finalNotification);

            // Only re-arm the scheduler if scheduling is enabled
            if (_settingsService?.IsScheduled() == true)
                StreakScheduler.ScheduleNextRun(this);
            AppLog("SYSTEM", "-", $"Run complete: {(success ? "Success" : message)}");
        }
        finally
        {
            // ── Clear the run-level mutex on ALL exit paths ──
            lock (_runLock)
            {
                _isRunning = false;
            }

            CleanupWebView();
            StopForeground(StopForegroundFlags.Remove);
            StopSelf();
        }
    }

    /// <summary>
    /// WebView client for handling page events
    /// </summary>
    private class StreakWebViewClient : WebViewClient
    {
        private readonly StreakService _service;

        public StreakWebViewClient(StreakService service)
        {
            _service = service;
        }

        public override void OnPageFinished(WebView? view, string? url)
        {
            base.OnPageFinished(view, url);
            if (!string.IsNullOrEmpty(url))
            {
                _service.OnPageLoaded(url);
            }
        }

        public override bool ShouldOverrideUrlLoading(WebView? view, IWebResourceRequest? request)
        {
            if (request?.Url is not null)
            {
                if ((request.Url.EncodedSchemeSpecificPart ?? "").StartsWith("//aweme"))
                {
                    return true;
                }
            }
            // Allow navigation within TikTok
            return false;
        }
    }

    /// <summary>
    /// JavaScript interface for communication from WebView
    /// </summary>
    private class StreakJsInterface : Java.Lang.Object
    {
        private readonly StreakService _service;

        public StreakJsInterface(StreakService service)
        {
            _service = service;
        }

        [JavascriptInterface]
        [Export("onMessageSent")]
        public void OnMessageSent(string username, bool success, string error)
        {
            _service._mainHandler?.Post(() => _service.OnMessageResult(username, success, error));
        }

        [JavascriptInterface]
        [Export("log")]
        public void Log(string message)
        {
            var entry = $"[{DateTime.Now:HH:mm:ss}] {message}";
            StreakService._logs.Add(entry);
        }
    }
}
