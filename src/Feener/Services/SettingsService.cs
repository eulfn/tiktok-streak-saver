using System.Text.Json;
using Feener.Models;

namespace Feener.Services;

/// <summary>
/// Service for managing app settings and persistent storage
/// </summary>
[Microsoft.Maui.Controls.Internals.Preserve(AllMembers = true)]
public class SettingsService
{
    private const string FriendsListKey = "friends_list";
    private const string MessageTextKey = "message_text";
    private const string LastRunKey = "last_run";
    private const string IsScheduledKey = "is_scheduled";
    private const string RunHistoryKey = "run_history";
    private const string IntervalHoursKey = "interval_hours";
    private const string SkipUnreachableUsersKey = "skip_unreachable_users";
    private const string BurstMessagesKey = "burst_messages_list";
    private const string IsBurstModeActiveKey = "is_burst_mode_active";
    private const string BurstTargetUsernameKey = "burst_target_username";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    /// <summary>
    /// Default message to send
    /// </summary>
    public const string DefaultMessage = "Streak";

    /// <summary>
    /// Default interval in hours
    /// </summary>
    public const int DefaultIntervalHours = 23;

    #region Friends List

    /// <summary>
    /// Get the list of configured friends
    /// </summary>
    public List<FriendConfig> GetFriendsList()
    {
        try
        {
            var json = Preferences.Get(FriendsListKey, string.Empty);
            if (string.IsNullOrEmpty(json))
                return new List<FriendConfig>();

            return JsonSerializer.Deserialize<List<FriendConfig>>(json, JsonOptions) ?? new List<FriendConfig>();
        }
        catch
        {
            return new List<FriendConfig>();
        }
    }

    /// <summary>
    /// Save the friends list
    /// </summary>
    public void SaveFriendsList(List<FriendConfig> friends)
    {
        var json = JsonSerializer.Serialize(friends, JsonOptions);
        Preferences.Set(FriendsListKey, json);
    }

    /// <summary>
    /// Add a new friend to the list
    /// </summary>
    public void AddFriend(FriendConfig friend)
    {
        var friends = GetFriendsList();
        friends.Add(friend);
        SaveFriendsList(friends);
    }

    /// <summary>
    /// Remove a friend from the list
    /// </summary>
    public void RemoveFriend(string friendId)
    {
        var friends = GetFriendsList();
        friends.RemoveAll(f => f.Id == friendId);
        SaveFriendsList(friends);
    }

    /// <summary>
    /// Update a friend's configuration
    /// </summary>
    public void UpdateFriend(FriendConfig friend)
    {
        var friends = GetFriendsList();
        var index = friends.FindIndex(f => f.Id == friend.Id);
        if (index >= 0)
        {
            friends[index] = friend;
            SaveFriendsList(friends);
        }
    }

    /// <summary>
    /// Get enabled friends only
    /// </summary>
    public List<FriendConfig> GetEnabledFriends()
    {
        return GetFriendsList().Where(f => f.IsEnabled).ToList();
    }

    #endregion

    #region Message Configuration

    /// <summary>
    /// Get the message text to send
    /// </summary>
    public string GetMessageText()
    {
        return Preferences.Get(MessageTextKey, DefaultMessage);
    }

    /// <summary>
    /// Set the message text to send
    /// </summary>
    public void SetMessageText(string message)
    {
        Preferences.Set(MessageTextKey, message);
    }

    /// <summary>
    /// Get the list of burst messages
    /// </summary>
    public List<string> GetBurstMessages()
    {
        try
        {
            var json = Preferences.Get(BurstMessagesKey, string.Empty);
            if (string.IsNullOrEmpty(json))
                return new List<string> { "Burst Message" };

            return JsonSerializer.Deserialize<List<string>>(json, JsonOptions) ?? new List<string> { "Burst Message" };
        }
        catch
        {
            return new List<string> { "Burst Message" };
        }
    }

    /// <summary>
    /// Set the list of burst messages
    /// </summary>
    public void SetBurstMessages(List<string> messages)
    {
        if (messages == null || messages.Count == 0)
        {
            messages = new List<string> { "Burst Message" };
        }
        var json = JsonSerializer.Serialize(messages, JsonOptions);
        Preferences.Set(BurstMessagesKey, json);
    }

    // ── Randomized Normal Messages ──

    private const string RandomizeNormalMessagesKey = "randomize_normal_messages";

    /// <summary>
    /// 50 built-in short streak messages (4 words max) for randomized normal-mode sends.
    /// </summary>
    public static readonly List<string> BuiltInStreakMessages = new()
    {
        "Streak",
        "streak",
        "streakk",
        "streaak",
        "streaakkk",
        "streaaak",
        "strk",
        "Strk",
        "s",
        "S",
        "streaks",
        "Streaks",
        "streakss",
        "streak lol",
        "yo streak",
        "yoo streak",
        "yoo streakk",
        "hey streak",
        "hii streak",
        "hi streak",
        "heyy streak",
        "streak hii",
        "streak hi",
        "streak yo",
        "streak yoo",
        "streakkk",
        "strek",
        "streek",
        "streeek",
        "streeeek",
        "yo",
        "yoo",
        "yooo",
        "hey",
        "hii",
        "heyy",
        "heyyy",
        "here",
        "heree",
        "strkeee",
        "streak rn",
        "quick streak",
        "streakk lol",
        "streak lmao",
        "lol streak",
        "streaaakk",
        "streakkk lol",
        "daily streak",
        "streak streak",
        "ayo streak"
    };

    /// <summary>
    /// Get whether randomized built-in messages are enabled for normal mode
    /// </summary>
    public bool GetRandomizeNormalMessages()
    {
        return Preferences.Get(RandomizeNormalMessagesKey, false);
    }

    /// <summary>
    /// Set whether randomized built-in messages are enabled for normal mode
    /// </summary>
    public void SetRandomizeNormalMessages(bool enabled)
    {
        Preferences.Set(RandomizeNormalMessagesKey, enabled);
    }
    
    /// <summary>
    /// Get if Burst Mode is currently active
    /// </summary>
    public bool IsBurstModeActive()
    {
        return Preferences.Get(IsBurstModeActiveKey, false);
    }

    /// <summary>
    /// Set if Burst Mode is currently active
    /// </summary>
    public void SetBurstModeActive(bool active)
    {
        Preferences.Set(IsBurstModeActiveKey, active);
    }

    /// <summary>
    /// Get the Burst Mode specific target username
    /// </summary>
    public string GetBurstTargetUsername()
    {
        return Preferences.Get(BurstTargetUsernameKey, string.Empty);
    }

    /// <summary>
    /// Set the Burst Mode specific target username
    /// </summary>
    public void SetBurstTargetUsername(string username)
    {
        Preferences.Set(BurstTargetUsernameKey, username);
    }

    #endregion

    #region Scheduling

    /// <summary>
    /// Get the interval in hours
    /// </summary>
    public int GetIntervalHours()
    {
        return Preferences.Get(IntervalHoursKey, DefaultIntervalHours);
    }

    /// <summary>
    /// Set the interval in hours
    /// </summary>
    public void SetIntervalHours(int hours)
    {
        Preferences.Set(IntervalHoursKey, hours);
    }

    /// <summary>
    /// Get the last run timestamp
    /// </summary>
    public DateTime? GetLastRunTime()
    {
        var ticks = Preferences.Get(LastRunKey, 0L);
        return ticks > 0 ? new DateTime(ticks) : null;
    }

    /// <summary>
    /// Set the last run timestamp
    /// </summary>
    public void SetLastRunTime(DateTime time)
    {
        Preferences.Set(LastRunKey, time.Ticks);
    }


    /// <summary>
    /// Get whether the scheduler is enabled
    /// </summary>
    public bool IsScheduled()
    {
        return Preferences.Get(IsScheduledKey, false);
    }

    /// <summary>
    /// Set whether the scheduler is enabled
    /// </summary>
    public void SetScheduled(bool scheduled)
    {
        Preferences.Set(IsScheduledKey, scheduled);
    }

    /// <summary>
    /// Get whether to skip unreachable users and continue the run
    /// </summary>
    public bool GetSkipUnreachableUsers()
    {
        return Preferences.Get(SkipUnreachableUsersKey, false);
    }

    /// <summary>
    /// Set whether to skip unreachable users and continue the run
    /// </summary>
    public void SetSkipUnreachableUsers(bool skip)
    {
        Preferences.Set(SkipUnreachableUsersKey, skip);
    }

    /// <summary>
    /// Calculate the next run time based on last run and interval
    /// </summary>
    public DateTime GetNextRunTime()
    {
        var lastRun = GetLastRunTime();
        var intervalHours = GetIntervalHours();

        if (lastRun.HasValue)
        {
            return lastRun.Value.AddHours(intervalHours);
        }

        // If never run, schedule for now
        return DateTime.Now;
    }

    #endregion

    #region Burst Quota

    private const string BurstDailySentCountKey = "burst_daily_sent_count";
    private const string BurstDailyDateKey = "burst_daily_date";
    private const string BurstDailyLimitKey = "burst_daily_limit";

    /// <summary>Absolute maximum burst messages per day (hard ceiling)</summary>
    public const int BurstMaxDailyCeiling = 720;

    /// <summary>Minimum messages per burst chunk</summary>
    public const int BurstChunkSizeMin = 30;

    /// <summary>Maximum messages per burst chunk</summary>
    public const int BurstChunkSizeMax = 40;

    /// <summary>Minimum hibernation between chunks in ms (5 min)</summary>
    public const int BurstHibernationMinMs = 5 * 60 * 1000;

    /// <summary>Maximum hibernation between chunks in ms (15 min)</summary>
    public const int BurstHibernationMaxMs = 15 * 60 * 1000;

    /// <summary>
    /// Get user-configured daily burst limit (1-720, defaults to 720)
    /// </summary>
    public int GetBurstDailyLimit()
    {
        var limit = Preferences.Get(BurstDailyLimitKey, BurstMaxDailyCeiling);
        return Math.Clamp(limit, 1, BurstMaxDailyCeiling);
    }

    /// <summary>
    /// Set user-configured daily burst limit (clamped to 1-720)
    /// </summary>
    public void SetBurstDailyLimit(int limit)
    {
        Preferences.Set(BurstDailyLimitKey, Math.Clamp(limit, 1, BurstMaxDailyCeiling));
    }

    /// <summary>
    /// Get burst messages sent today (resets on new day)
    /// </summary>
    public int GetBurstDailySentCount()
    {
        var savedDate = Preferences.Get(BurstDailyDateKey, string.Empty);
        var today = DateTime.Now.ToString("yyyy-MM-dd");
        if (savedDate != today)
        {
            Preferences.Set(BurstDailyDateKey, today);
            Preferences.Set(BurstDailySentCountKey, 0);
            return 0;
        }
        return Preferences.Get(BurstDailySentCountKey, 0);
    }

    /// <summary>
    /// Set burst messages sent today
    /// </summary>
    public void SetBurstDailySentCount(int count)
    {
        Preferences.Set(BurstDailyDateKey, DateTime.Now.ToString("yyyy-MM-dd"));
        Preferences.Set(BurstDailySentCountKey, count);
    }

    /// <summary>
    /// Increment burst daily sent count by 1
    /// </summary>
    public void IncrementBurstDailySentCount()
    {
        var current = GetBurstDailySentCount();
        SetBurstDailySentCount(current + 1);
    }

    /// <summary>
    /// Calculate burst session plan with precise time estimates
    /// </summary>
    public (int sessionsNeeded, int estimatedMinutes, int estimatedTotalSeconds, int remaining, int dailyLimit) CalculateBurstPlan()
    {
        var dailyLimit = GetBurstDailyLimit();
        var dailySent = GetBurstDailySentCount();
        var remaining = Math.Max(0, dailyLimit - dailySent);
        var avgChunkSize = (BurstChunkSizeMin + BurstChunkSizeMax) / 2;
        var sessionsNeeded = remaining > 0 ? (int)Math.Ceiling((double)remaining / avgChunkSize) : 0;
        var avgDelaySeconds = 6.5; // avg of 3-10s
        var totalSendSeconds = remaining * avgDelaySeconds;
        var totalHibernationSeconds = Math.Max(0, sessionsNeeded - 1) * ((BurstHibernationMinMs + BurstHibernationMaxMs) / 2.0 / 1000.0);
        var estimatedTotalSeconds = (int)(totalSendSeconds + totalHibernationSeconds);
        var estimatedMinutes = estimatedTotalSeconds / 60;
        return (sessionsNeeded, estimatedMinutes, estimatedTotalSeconds, remaining, dailyLimit);
    }

    #endregion

    #region Run History

    /// <summary>
    /// Get the run history (last 50 runs)
    /// </summary>
    public List<StreakRunResult> GetRunHistory()
    {
        try
        {
            var json = Preferences.Get(RunHistoryKey, string.Empty);
            if (string.IsNullOrEmpty(json))
                return new List<StreakRunResult>();

            return JsonSerializer.Deserialize<List<StreakRunResult>>(json, JsonOptions) ?? new List<StreakRunResult>();
        }
        catch
        {
            return new List<StreakRunResult>();
        }
    }

    /// <summary>
    /// Add a run result to history
    /// </summary>
    public void AddRunResult(StreakRunResult result)
    {
        var history = GetRunHistory();
        history.Insert(0, result);

        // Keep only last 50 runs
        if (history.Count > 50)
        {
            history = history.Take(50).ToList();
        }

        var json = JsonSerializer.Serialize(history, JsonOptions);
        Preferences.Set(RunHistoryKey, json);
    }

    #endregion

    #region Clear Data

    /// <summary>
    /// Clear the run history
    /// </summary>
    public void ClearRunHistory()
    {
        Preferences.Remove(RunHistoryKey);
    }

    /// <summary>
    /// Clear all settings
    /// </summary>
    public void ClearAll()
    {
        Preferences.Clear();
    }

    #endregion
}
