using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Runtime;
using Android.Webkit;
using AndroidX.Core.App;
using Java.Interop;
using Microsoft.Maui.Controls.Internals;
using Feener.Services;
using WebView = Android.Webkit.WebView;

namespace Feener.Platforms.Android.Services;

[Service(Name = AppConstants.PackageName + ".Services.CollectFriendsService", ForegroundServiceType = ForegroundService.TypeDataSync)]
[Preserve(AllMembers = true)]
public class CollectFriendsService : Service
{
    private const string ChannelId = "collect_friends_channel";
    private const string ChannelName = "Friend Collection";
    private const int NotificationId = 2001;

    private WebView? _webView;
    private Handler? _mainHandler;
    private string _baseScript = string.Empty;
    private bool _automationStarted = false;
    private PowerManager.WakeLock? _wakeLock;

    // ── Run-level mutex ──
    private static volatile bool _isRunning = false;
    private static readonly object _runLock = new();
    public static bool IsRunning => _isRunning;

    // ── Collected results (read by FriendsPage) ──
    private static readonly List<string> _collectedUsernames = new();
    private static readonly object _resultsLock = new();
    private static string? _statusMessage;
    private static bool _isDone = false;
    private static string? _errorMessage;

    private static readonly List<string> _logs = new();

    // ── Public accessors for FriendsPage ──

    public static List<string> GetCollectedUsernames()
    {
        lock (_resultsLock)
        {
            return new List<string>(_collectedUsernames);
        }
    }

    public static string? GetStatusMessage() => _statusMessage;
    public static bool IsDone => _isDone;
    public static string? GetError() => _errorMessage;

    public static List<string> GetLogs() => _logs ?? new List<string>();

    public static void ClearResults()
    {
        lock (_resultsLock)
        {
            _collectedUsernames.Clear();
        }
        _statusMessage = null;
        _isDone = false;
        _errorMessage = null;
        _logs.Clear();
    }

    private static void AppLog(string message)
    {
        var entry = $"[{DateTime.Now:HH:mm:ss}] [COLLECT] {message}";
        _logs.Add(entry);
        System.Diagnostics.Debug.WriteLine(entry);
    }

    // ── Service lifecycle ──

    public override void OnCreate()
    {
        base.OnCreate();
        CreateNotificationChannel();
        _mainHandler = new Handler(Looper.MainLooper!);
        AcquireWakeLock();
        StartForegroundServiceImmediate();
    }

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        if (intent?.Action == "STOP_SERVICE")
        {
            AppLog("Stop requested by user");
            CompleteService("Collection stopped by user.");
            return StartCommandResult.NotSticky;
        }

        StartForegroundServiceImmediate();

        lock (_runLock)
        {
            if (_isRunning)
            {
                AppLog("OnStartCommand ignored — collection already running");
                return StartCommandResult.NotSticky;
            }
            _isRunning = true;
        }

        ClearResults();
        _statusMessage = "Loading TikTok messages...";
        _mainHandler?.Post(StartWebViewCollection);

        return StartCommandResult.NotSticky;
    }

    public override IBinder? OnBind(Intent? intent) => null;

    public override void OnDestroy()
    {
        lock (_runLock) { _isRunning = false; }
        ReleaseWakeLock();
        CleanupWebView();
        base.OnDestroy();
    }

    // ── Foreground service plumbing (mirrors StreakService) ──

    private void StartForegroundServiceImmediate()
    {
        try
        {
            var notification = CreateNotification("Collecting friends...");
            if (Build.VERSION.SdkInt >= BuildVersionCodes.Q)
                StartForeground(NotificationId, notification, ForegroundService.TypeDataSync);
            else
                StartForeground(NotificationId, notification);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"StartForeground error: {ex.Message}");
        }
    }

    private void AcquireWakeLock()
    {
        var powerManager = (PowerManager?)GetSystemService(PowerService);
        _wakeLock = powerManager?.NewWakeLock(WakeLockFlags.Partial, "Feener::CollectWakeLock");
        _wakeLock?.Acquire(30L * 60 * 1000); // 30 minutes max
    }

    private void ReleaseWakeLock()
    {
        if (_wakeLock?.IsHeld == true) _wakeLock.Release();
    }

    private void CreateNotificationChannel()
    {
        if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
        {
            var notificationManager = (NotificationManager?)GetSystemService(NotificationService);
            if (notificationManager?.GetNotificationChannel(ChannelId) != null) return;

            var channel = new NotificationChannel(ChannelId, ChannelName, NotificationImportance.Low)
            {
                Description = "Notification channel for friend collection"
            };
            channel.SetShowBadge(false);
            notificationManager?.CreateNotificationChannel(channel);
        }
    }

    private Notification CreateNotification(string message)
    {
        var intent = new Intent(this, typeof(MainActivity));
        intent.SetFlags(ActivityFlags.NewTask | ActivityFlags.ClearTop);
        var pendingIntent = PendingIntent.GetActivity(this, 0, intent,
            PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);

        return new NotificationCompat.Builder(this, ChannelId)
            .SetContentTitle("Feener")
            .SetContentText(message)
            .SetSmallIcon(Resource.Drawable.ic_notification)
            .SetContentIntent(pendingIntent)
            .SetOngoing(true)
            .SetForegroundServiceBehavior(NotificationCompat.ForegroundServiceImmediate)
            .SetCategory(NotificationCompat.CategoryService)
            .SetPriority(NotificationCompat.PriorityLow)
            .SetProgress(0, 0, true)
            .Build()!;
    }

    private void UpdateNotification(string message)
    {
        var intent = new Intent(this, typeof(MainActivity));
        intent.SetFlags(ActivityFlags.NewTask | ActivityFlags.ClearTop);
        var pendingIntent = PendingIntent.GetActivity(this, 0, intent,
            PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);

        var notification = new NotificationCompat.Builder(this, ChannelId)
            .SetContentTitle("Feener")
            .SetContentText(message)
            .SetSmallIcon(Resource.Drawable.ic_notification)
            .SetContentIntent(pendingIntent)
            .SetOngoing(true)
            .SetForegroundServiceBehavior(NotificationCompat.ForegroundServiceImmediate)
            .SetCategory(NotificationCompat.CategoryService)
            .SetPriority(NotificationCompat.PriorityLow)
            .SetProgress(0, 0, true)
            .Build()!;

        var notificationManager = (NotificationManager?)GetSystemService(NotificationService);
        notificationManager?.Notify(NotificationId, notification);
    }

    // ── WebView automation (same pattern as StreakService) ──

    private async void StartWebViewCollection()
    {
        try
        {
            AppLog("Starting background friend collection");

            // Pre-flight network check
            if (!NetworkConnectivity.HasWifiOrCellularInternet(this))
            {
                AppLog("No network connection");
                CompleteService("No network connection.");
                return;
            }

            // Load the collection JS
            using var stream = await FileSystem.OpenAppPackageFileAsync("tiktok_collect_friends.js");
            using var reader = new StreamReader(stream);
            _baseScript = await reader.ReadToEndAsync();
            _baseScript = string.Join("\n", _baseScript.Split('\n')
                .Where(line => !line.TrimStart().StartsWith("//")));
            _baseScript = System.Text.RegularExpressions.Regex.Replace(_baseScript, @"\s+", " ").Trim();

            // Create native Android WebView (no UI needed)
            _webView = new WebView(this);
            _webView.Settings.JavaScriptEnabled = true;
            _webView.Settings.DomStorageEnabled = true;
            _webView.Settings.DatabaseEnabled = true;
            _webView.Settings.CacheMode = CacheModes.Normal;

            var sessionService = new SessionService();
            var loginUa = sessionService.GetLoginUserAgent()
                ?? "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/91.0.4472.124 Safari/537.36";
            _webView.Settings.UserAgentString = loginUa;
            _webView.Settings.SetSupportZoom(true);
            _webView.Settings.BuiltInZoomControls = true;

            // Enable cookies (use the same session as StreakService)
            var cookieManager = CookieManager.Instance;
            cookieManager?.SetAcceptCookie(true);
            cookieManager?.SetAcceptThirdPartyCookies(_webView, true);

            // Set up WebView client
            _webView.SetWebViewClient(new CollectWebViewClient(this));

            // Add JavaScript interface (same bridge name as StreakService)
            _webView.AddJavascriptInterface(new CollectJsInterface(this), "StreakApp");

            // Load TikTok messages page
            _webView.LoadUrl("https://www.tiktok.com/messages?lang=en");

            // Safety timeout: if page doesn't load in 10s, retry once
            _mainHandler!.PostDelayed(() =>
            {
                if (!(_webView?.Url ?? "").Contains("tiktok.com/messages"))
                {
                    _webView?.LoadUrl("https://www.tiktok.com/messages?lang=en");
                    _mainHandler.PostDelayed(() =>
                    {
                        if (!(_webView?.Url ?? "").Contains("tiktok.com/messages"))
                        {
                            CompleteService("Could not navigate to TikTok messages.");
                        }
                    }, 5000);
                }
            }, 5000);
        }
        catch (Exception ex)
        {
            CompleteService($"Error starting collection: {ex.Message}");
        }
    }

    internal void OnPageLoaded(string url)
    {
        if (url.Contains("tiktok.com/messages"))
        {
            if (_automationStarted) return;
            _automationStarted = true;

            _statusMessage = "Scanning DM list...";
            UpdateNotification("Scanning DM list...");
            AppLog("Messages page ready, injecting collection script");

            // Wait 3s for SPA to render, then inject the JS
            _mainHandler?.PostDelayed(() =>
            {
                _webView?.EvaluateJavascript(_baseScript, null);
            }, 3000);
        }
        else if (url.Contains("login"))
        {
            AppLog("TikTok login required");
            CompleteService("TikTok login required. Please login via the app first.");
        }
    }

    // ── JS bridge callbacks ──

    internal void OnFriendFound(string username)
    {
        lock (_resultsLock)
        {
            var key = username.ToLowerInvariant();
            if (!_collectedUsernames.Any(u => u.Equals(username, StringComparison.OrdinalIgnoreCase)))
            {
                _collectedUsernames.Add(username);
            }
        }
        var count = 0;
        lock (_resultsLock) { count = _collectedUsernames.Count; }
        _statusMessage = $"Collecting... {count} found";
        UpdateNotification($"Collecting friends: {count} found");
    }

    internal void OnCollectComplete(int total)
    {
        int count;
        lock (_resultsLock) { count = _collectedUsernames.Count; }
        AppLog($"Collection complete: {count} unique friends found");
        _statusMessage = $"Done — {count} friend{(count == 1 ? "" : "s")} found";
        _isDone = true;

        // Show final notification
        ShowCompletionNotification($"Found {count} friend{(count == 1 ? "" : "s")} in your DM list");
        Cleanup();
    }

    internal void OnCollectError(string error)
    {
        int count;
        lock (_resultsLock) { count = _collectedUsernames.Count; }
        AppLog($"Collection error: {error} (collected {count} before error)");
        _errorMessage = error;
        _isDone = true;

        if (count > 0)
            _statusMessage = $"Partial — {count} friend{(count == 1 ? "" : "s")} found ({error})";
        else
            _statusMessage = $"Failed: {error}";

        ShowCompletionNotification(count > 0
            ? $"Found {count} friend{(count == 1 ? "" : "s")} (stopped: {error})"
            : $"Collection failed: {error}");
        Cleanup();
    }

    private void CompleteService(string message)
    {
        AppLog(message);
        _statusMessage = message;
        _isDone = true;
        ShowCompletionNotification(message);
        Cleanup();
    }

    private void ShowCompletionNotification(string message)
    {
        var notification = new NotificationCompat.Builder(this, ChannelId)
            .SetContentTitle("Feener")
            .SetContentText(message)
            .SetSmallIcon(Resource.Drawable.ic_notification)
            .SetAutoCancel(true)
            .SetPriority(NotificationCompat.PriorityDefault)
            .Build()!;

        var notificationManager = (NotificationManager?)GetSystemService(NotificationService);
        notificationManager?.Notify(NotificationId + 1, notification);
    }

    private void Cleanup()
    {
        lock (_runLock) { _isRunning = false; }
        CleanupWebView();
        StopForeground(StopForegroundFlags.Remove);
        StopSelf();
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

    // ── Inner classes (same pattern as StreakService) ──

    private class CollectWebViewClient : WebViewClient
    {
        private readonly CollectFriendsService _service;
        public CollectWebViewClient(CollectFriendsService service) => _service = service;

        public override void OnPageFinished(WebView? view, string? url)
        {
            base.OnPageFinished(view, url);
            if (!string.IsNullOrEmpty(url)) _service.OnPageLoaded(url);
        }

        public override bool ShouldOverrideUrlLoading(WebView? view, IWebResourceRequest? request)
        {
            if (request?.Url is not null)
            {
                if ((request.Url.EncodedSchemeSpecificPart ?? "").StartsWith("//aweme"))
                    return true;
            }
            return false;
        }
    }

    private class CollectJsInterface : Java.Lang.Object
    {
        private readonly CollectFriendsService _service;
        public CollectJsInterface(CollectFriendsService service) => _service = service;

        [JavascriptInterface]
        [Export("onFriendFound")]
        public void OnFriendFound(string username)
        {
            _service._mainHandler?.Post(() => _service.OnFriendFound(username));
        }

        [JavascriptInterface]
        [Export("onCollectComplete")]
        public void OnCollectComplete(int total)
        {
            _service._mainHandler?.Post(() => _service.OnCollectComplete(total));
        }

        [JavascriptInterface]
        [Export("onCollectError")]
        public void OnCollectError(string error)
        {
            _service._mainHandler?.Post(() => _service.OnCollectError(error));
        }

        [JavascriptInterface]
        [Export("log")]
        public void Log(string message)
        {
            var entry = $"[{DateTime.Now:HH:mm:ss}] {message}";
            CollectFriendsService._logs.Add(entry);
        }

        // ── Stubs for tiktok_automation.js compatibility ──
        // The collection JS uses the same "StreakApp" bridge name.
        // If tiktok_automation.js methods are called by accident, these prevent crashes.
        [JavascriptInterface]
        [Export("onMessageSent")]
        public void OnMessageSent(string username, bool success, string error) { }
    }
}
