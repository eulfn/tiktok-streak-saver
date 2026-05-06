// TikTok Friend Collection Script
// Mirrors the proven approach from tiktok_automation.js:
//   click each chat item → wait → read username from chat header
// Communicates state via window.__feenerState, polled by C#.

(function () {
    'use strict';

    window.__feenerState = {
        status: 'initializing',
        count: 0,
        friends: [],
        error: null
    };

    var collected = {};     // lowercase username → { username, displayName }
    var chatItems = [];
    var chatIndex = 0;
    var maxScrollAttempts = 15;
    var scrollAttempts = 0;

    // ── Logging (console only, no StreakApp bridge here) ─────────────────────

    var log = function (msg) {
        console.log('[Feener Collect] ' + msg);
    };

    // ── State reporting ─────────────────────────────────────────────────────

    var updateState = function (status) {
        var list = [];
        var keys = Object.keys(collected);
        for (var i = 0; i < keys.length; i++) {
            list.push(collected[keys[i]]);
        }
        window.__feenerState = {
            status: status,
            count: list.length,
            friends: list,
            error: null
        };
    };

    var reportError = function (msg) {
        log('ERROR: ' + msg);
        var list = [];
        var keys = Object.keys(collected);
        for (var i = 0; i < keys.length; i++) {
            list.push(collected[keys[i]]);
        }
        window.__feenerState = {
            status: 'error',
            count: list.length,
            friends: list,
            error: msg
        };
    };

    var reportDone = function () {
        updateState('done');
        log('Collection complete. Found ' + window.__feenerState.count + ' unique friends.');
    };

    // ── Copied from tiktok_automation.js (proven, battle-tested) ────────────

    var findChatItems = function () {
        // Primary selector (v1.8.0 original)
        var items = document.querySelectorAll("[data-e2e*='chat-list-item']");
        if (items.length > 0) {
            log('Found ' + items.length + ' items via primary: chat-list-item');
            return items;
        }

        // Fallback selectors — TikTok periodically renames data-e2e values
        var fallbacks = [
            "[data-e2e*='dm-new-conversation-item']",
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

        // Nothing found
        return document.querySelectorAll("[data-e2e*='chat-list-item']");
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

    var findCurrentChatUsername = function () {
        // Find the username from the chat header (the opened conversation)
        var chatHeader = document.querySelector('[class*="ChatHeader"]') ||
                         document.querySelector('[class*="chatHeader"]') ||
                         document.querySelector('[class*="DivChatHeader"]');

        if (chatHeader) {
            var headerLink = chatHeader.querySelector('a[href*="/@"]');
            if (headerLink) {
                var href = headerLink.getAttribute('href') || '';
                var match = href.match(/\/@([^\/]+)/);
                return match ? match[1] : '';
            }
        }

        // Fallback: look for links with no data-e2e parent (usually header area)
        var links = document.querySelectorAll('[class*="StyledLink"]');
        for (var i = 0; i < links.length; i++) {
            var link = links[i];
            var parent = link.closest('[data-e2e]');
            var parentAttr = parent ? parent.getAttribute('data-e2e') : '';

            // Skip inbox items, only look at header/none area
            if (!parentAttr || parentAttr === 'chat-header') {
                var href = link.getAttribute('href') || '';
                var match = href.match(/\/@([^\/]+)/);
                if (match && match[1]) {
                    return match[1];
                }
            }
        }

        return '';
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
        var keys = Object.keys(uniqueVals);
        log('Total data-e2e elements: ' + allE2e.length + ', Unique attributes: ' + keys.length);

        var chunk = [];
        for (var j = 0; j < keys.length; j++) {
            chunk.push(keys[j]);
            if (chunk.length >= 15 || j === keys.length - 1) {
                log('data-e2e snippet: ' + chunk.join(', '));
                chunk = [];
            }
        }

        log('=== END DIAGNOSTICS ===');
    };

    // ── Collection logic ────────────────────────────────────────────────────

    var addFriend = function (username) {
        if (!username) return false;
        var key = username.toLowerCase().trim();
        if (key.length < 1 || collected[key]) return false;
        collected[key] = { username: username.trim(), displayName: '' };
        log('Collected: @' + username + ' (' + Object.keys(collected).length + ' total)');
        return true;
    };

    // Click each chat item, wait, read the username from the header.
    // This mirrors checkNextChat() from tiktok_automation.js exactly.
    var collectNextChat = function () {
        if (chatIndex >= chatItems.length) {
            // All visible items processed — try scrolling for more
            log('Processed all ' + chatItems.length + ' visible items. Trying scroll...');
            scrollAndCollectMore();
            return;
        }

        var chatItem = chatItems[chatIndex];
        log('Clicking chat item ' + (chatIndex + 1) + '/' + chatItems.length);
        chatItem.click();
        updateState('collecting');

        // Same 1500ms wait as tiktok_automation.js
        setTimeout(function () {
            var username = findCurrentChatUsername();
            if (username) {
                addFriend(username);
            } else {
                log('Could not read username from chat header for item ' + (chatIndex + 1));
            }

            updateState('collecting');
            chatIndex++;
            collectNextChat();
        }, 1500);
    };

    // Scroll the chat list to load more items, then continue collecting.
    // Mirrors scrollAndRetry() from tiktok_automation.js.
    var scrollAndCollectMore = function () {
        scrollAttempts++;
        if (scrollAttempts > maxScrollAttempts) {
            log('Max scroll attempts reached (' + maxScrollAttempts + ')');
            reportDone();
            return;
        }

        var container = findChatListContainer();
        if (!container) {
            log('No scrollable chat container found — done');
            reportDone();
            return;
        }

        var prevCount = chatItems.length;
        var prevScrollTop = container.scrollTop;
        container.scrollTop = container.scrollHeight;
        updateState('scrolling');

        log('Scrolling chat list (attempt ' + scrollAttempts + '/' + maxScrollAttempts + ')...');

        setTimeout(function () {
            chatItems = findChatItems();
            log('After scroll: ' + chatItems.length + ' items (was ' + prevCount + ')');

            if (chatItems.length > prevCount) {
                // New items loaded — continue from where we left off
                // chatIndex already points to the first unprocessed item
                collectNextChat();
            } else if (container.scrollTop > prevScrollTop) {
                // Scroll moved but no new items yet — wait a bit more
                setTimeout(function () {
                    chatItems = findChatItems();
                    if (chatItems.length > prevCount) {
                        collectNextChat();
                    } else {
                        scrollAndCollectMore();
                    }
                }, 2000);
            } else {
                // Scroll didn't move — end of list
                log('Scroll position unchanged — end of list');
                reportDone();
            }
        }, 2000);
    };

    // ── Page detection (JS-owned, not reliant on C#) ────────────────────────

    var detectPageState = function () {
        var url = window.location.href.toLowerCase();

        // Login page detection
        if (url.indexOf('/login') !== -1) {
            return 'login';
        }

        // Check if we're on the messages page
        if (url.indexOf('/messages') !== -1 || url.indexOf('/message') !== -1) {
            return 'messages';
        }

        // Unknown page
        return 'unknown';
    };

    // ── Entry point ─────────────────────────────────────────────────────────

    var init = function () {
        try {
            log('Starting friend collection...');
            log('Current URL: ' + window.location.href);

            var page = detectPageState();

            if (page === 'login') {
                reportError('Not logged in. Please log in to TikTok first.');
                return;
            }

            if (page === 'unknown') {
                log('Unknown page, but will still attempt to find chat items...');
            }

            // Wait 3s for the SPA to render (same as tiktok_automation.js)
            setTimeout(function () {
                chatItems = findChatItems();
                log('Found ' + chatItems.length + ' chat items');

                if (chatItems.length === 0) {
                    dumpPageDiagnostics();
                    // Retry once after 5 more seconds (page might still be loading)
                    log('Retrying in 5 seconds...');
                    setTimeout(function () {
                        chatItems = findChatItems();
                        log('Retry: Found ' + chatItems.length + ' chat items');
                        if (chatItems.length === 0) {
                            dumpPageDiagnostics();
                            reportError('No chat items found. Make sure you are on the Messages page and have DM conversations.');
                            return;
                        }
                        updateState('collecting');
                        collectNextChat();
                    }, 5000);
                    return;
                }

                updateState('collecting');
                collectNextChat();
            }, 3000);

        } catch (e) {
            log('Error: ' + e.message);
            reportError('Unexpected error: ' + e.message);
        }
    };

    // Start
    init();
})();
