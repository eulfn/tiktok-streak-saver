// TikTok Friend Collection Script
// Runs inside a native Android WebView via CollectFriendsService.
//
// Strategy (validated against live DOM):
// - Chat list items (dm-new-conversation-item) contain NO links.
//   Usernames are only available by clicking each item and reading
//   the header's a[href*="/@"] link.
// - Display names ARE visible in the list item text before clicking.
// - Optimal click delay: 1.2s (measured 780-1050ms header load time).
// - Group chats have no profile link — timeout and skip after 2s.
//
// Bridge: StreakApp.onFriendFound(username, displayName)
//         StreakApp.onCollectComplete(total)
//         StreakApp.onCollectError(error)
//         StreakApp.log(msg)

(function () {
    'use strict';

    var collected = {};
    var chatItems = [];
    var chatIndex = 0;
    var maxScrollAttempts = 15;
    var scrollAttempts = 0;

    var log = function (msg) {
        if (typeof StreakApp == 'undefined') {
            console.log('[Feener Collect] ' + msg);
            return;
        }
        StreakApp.log('[COLLECT] ' + msg);
    };

    // ── Selectors (from tiktok_automation.js, proven working) ───────────────

    var findChatItems = function () {
        var items = document.querySelectorAll("[data-e2e*='dm-new-conversation-item']");
        if (items.length > 0) {
            log('Found ' + items.length + ' items via dm-new-conversation-item');
            return items;
        }
        var fallbacks = [
            "[data-e2e*='chat-list-item']",
            "[data-e2e*='chat-item']"
        ];
        for (var i = 0; i < fallbacks.length; i++) {
            try {
                items = document.querySelectorAll(fallbacks[i]);
                if (items.length > 0) {
                    log('Found ' + items.length + ' items via fallback: ' + fallbacks[i]);
                    return items;
                }
            } catch (e) { }
        }
        return document.querySelectorAll("[data-e2e*='dm-new-conversation-item']");
    };

    var findChatListContainer = function () {
        if (chatItems.length > 0) {
            var parent = chatItems[0].parentElement;
            while (parent && parent !== document.body) {
                if (parent.scrollHeight > parent.clientHeight + 10) {
                    return parent;
                }
                parent = parent.parentElement;
            }
        }
        var candidates = document.querySelectorAll('[class*="ChatList"], [class*="chatList"], [class*="conversation-list"]');
        for (var i = 0; i < candidates.length; i++) {
            if (candidates[i].scrollHeight > candidates[i].clientHeight + 10) {
                return candidates[i];
            }
        }
        return null;
    };

    // ── Extract display name from the chat item (before clicking) ───────────

    var extractDisplayName = function (item) {
        // The display name is the first prominent text in the item.
        // It's NOT inside any link (items have zero links).
        var textNodes = item.querySelectorAll('p, span');
        for (var i = 0; i < textNodes.length; i++) {
            var t = (textNodes[i].textContent || '').trim();
            // Skip empty, timestamps (e.g., "08:42"), message previews
            if (t.length > 0 && t.length < 50 &&
                !t.match(/^\d{1,2}:\d{2}/) &&
                !t.match(/^(yesterday|today|just now|\d+[smhd]\s*ago)/i) &&
                !t.match(/^(shared|you shared|streak|sent|video|photo)/i)) {
                return t;
            }
        }
        // Fallback: first line of all text
        var all = (item.textContent || '').trim();
        var first = all.split(/[\n\r]/)[0].trim();
        return first.substring(0, 40);
    };

    // ── Extract username from the chat header (after clicking) ──────────────

    var findCurrentChatUsername = function () {
        // After clicking a chat item, the header area contains a[href*="/@username"]
        // Verified selectors from live DOM inspection:
        var chatHeader = document.querySelector('[class*="ChatHeader"]') ||
                         document.querySelector('[class*="chatHeader"]') ||
                         document.querySelector('[class*="DivChatHeader"]');

        if (chatHeader) {
            var headerLink = chatHeader.querySelector('a[href*="/@"]');
            if (headerLink) {
                var href = headerLink.getAttribute('href') || '';
                var match = href.match(/\/@([^\/\?]+)/);
                return match ? match[1] : '';
            }
        }

        // Fallback: find /@username links in dm-new-chatbox or unparented areas
        var links = document.querySelectorAll('a[href*="/@"]');
        for (var i = 0; i < links.length; i++) {
            var link = links[i];
            var parent = link.closest('[data-e2e]');
            var parentAttr = parent ? parent.getAttribute('data-e2e') : '';

            // Accept links from chat header areas only, skip inbox items
            if (!parentAttr || parentAttr === 'chat-header' ||
                parentAttr === 'dm-new-chatbox') {
                var href = link.getAttribute('href') || '';
                var match = href.match(/\/@([^\/\?]+)/);
                if (match && match[1]) {
                    return match[1];
                }
            }
        }

        return '';
    };

    // ── Collection logic ────────────────────────────────────────────────────

    var addFriend = function (username, displayName) {
        if (!username) return false;
        var key = username.toLowerCase().trim();
        if (key.length < 1 || collected[key]) return false;
        collected[key] = { username: username.trim(), displayName: displayName || '' };
        log('Collected: @' + username + ' (' + (displayName || 'no name') + ') [' + Object.keys(collected).length + ' total]');
        if (typeof StreakApp !== 'undefined') {
            StreakApp.onFriendFound(username.trim(), displayName || '');
        }
        return true;
    };

    // ── Click-per-item collection (the only working approach) ───────────────

    var collectNextChat = function () {
        if (chatIndex >= chatItems.length) {
            log('Processed all ' + chatItems.length + ' visible items. Trying scroll...');
            scrollAndCollectMore();
            return;
        }

        var chatItem = chatItems[chatIndex];

        // 1. Extract display name BEFORE clicking (it's in the item text)
        var displayName = extractDisplayName(chatItem);

        log('Clicking chat item ' + (chatIndex + 1) + '/' + chatItems.length + ' (' + displayName + ')');

        // 2. Remember current header username to detect change
        var prevUsername = findCurrentChatUsername();

        // 3. Click the chat item
        chatItem.click();

        // 4. Poll for header username to appear (faster than fixed delay)
        var pollCount = 0;
        var maxPolls = 20; // 20 * 100ms = 2s timeout for group chats
        var pollInterval = 100;

        var poll = function () {
            pollCount++;
            var username = findCurrentChatUsername();

            if (username && username !== prevUsername) {
                // Header loaded with new username
                addFriend(username, displayName);
                chatIndex++;
                // Short delay between items to avoid rate limiting
                setTimeout(collectNextChat, 300);
            } else if (pollCount < maxPolls) {
                setTimeout(poll, pollInterval);
            } else {
                // Timeout — likely a group chat or special item
                log('Timeout reading header for item ' + (chatIndex + 1) + ' (' + displayName + ') — skipping (likely group chat)');
                chatIndex++;
                setTimeout(collectNextChat, 300);
            }
        };

        // Start polling after 200ms (minimum header load time)
        setTimeout(poll, 200);
    };

    var scrollAndCollectMore = function () {
        scrollAttempts++;
        if (scrollAttempts > maxScrollAttempts) {
            log('Max scroll attempts reached (' + maxScrollAttempts + ')');
            reportDone();
            return;
        }

        var container = findChatListContainer();
        if (!container) {
            log('No scrollable chat container found');
            reportDone();
            return;
        }

        var prevCount = chatItems.length;
        var prevScrollTop = container.scrollTop;
        container.scrollTop = container.scrollHeight;

        log('Scrolling chat list (attempt ' + scrollAttempts + '/' + maxScrollAttempts + ')...');

        setTimeout(function () {
            chatItems = findChatItems();
            log('After scroll: ' + chatItems.length + ' items (was ' + prevCount + ')');

            if (chatItems.length > prevCount) {
                collectNextChat();
            } else if (container.scrollTop > prevScrollTop) {
                setTimeout(function () {
                    chatItems = findChatItems();
                    if (chatItems.length > prevCount) {
                        collectNextChat();
                    } else {
                        scrollAndCollectMore();
                    }
                }, 1500);
            } else {
                log('Scroll position unchanged — end of list');
                reportDone();
            }
        }, 1500);
    };

    var reportDone = function () {
        var total = Object.keys(collected).length;
        log('Collection complete: ' + total + ' unique friends');
        if (typeof StreakApp !== 'undefined') {
            StreakApp.onCollectComplete(total);
        }
    };

    var reportError = function (msg) {
        log('ERROR: ' + msg);
        if (typeof StreakApp !== 'undefined') {
            StreakApp.onCollectError(msg);
        }
    };

    var dumpPageDiagnostics = function () {
        log('=== PAGE DIAGNOSTICS ===');
        log('URL: ' + window.location.href);
        log('Title: ' + document.title);
        var allE2e = document.querySelectorAll('[data-e2e]');
        var uniqueVals = {};
        for (var i = 0; i < allE2e.length; i++) {
            var val = allE2e[i].getAttribute('data-e2e');
            if (val) uniqueVals[val] = true;
        }
        log('data-e2e attributes: ' + Object.keys(uniqueVals).join(', '));
        log('=== END DIAGNOSTICS ===');
    };

    // ── Entry point ─────────────────────────────────────────────────────────

    var init = function () {
        try {
            log('Starting friend collection...');
            log('URL: ' + window.location.href);

            if (window.location.href.toLowerCase().indexOf('/login') !== -1) {
                reportError('Not logged in. Please log in to TikTok first.');
                return;
            }

            // Wait for SPA to render
            setTimeout(function () {
                chatItems = findChatItems();
                log('Initial: ' + chatItems.length + ' chat items');

                if (chatItems.length === 0) {
                    dumpPageDiagnostics();
                    log('Retrying in 5 seconds...');
                    setTimeout(function () {
                        chatItems = findChatItems();
                        if (chatItems.length === 0) {
                            dumpPageDiagnostics();
                            reportError('No chat items found. Make sure you have DM conversations.');
                            return;
                        }
                        collectNextChat();
                    }, 5000);
                    return;
                }

                collectNextChat();
            }, 3000);

        } catch (e) {
            reportError('Unexpected error: ' + e.message);
        }
    };

    init();
})();
