// TikTok Friend Collection Script
// Runs inside a native Android WebView via CollectFriendsService.
// Communicates results via the StreakApp bridge (same as tiktok_automation.js):
//   StreakApp.onFriendFound(username)   — called for each discovered friend
//   StreakApp.onCollectComplete(total)  — called when collection is done
//   StreakApp.onCollectError(error)     — called on fatal error
//   StreakApp.log(msg)                  — console logging to Android

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

    // ── Copied from tiktok_automation.js (proven, battle-tested) ────────────

    var findChatItems = function () {
        var items = document.querySelectorAll("[data-e2e*='chat-list-item']");
        if (items.length > 0) {
            log('Found ' + items.length + ' items via primary: chat-list-item');
            return items;
        }
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

        var links = document.querySelectorAll('[class*="StyledLink"]');
        for (var i = 0; i < links.length; i++) {
            var link = links[i];
            var parent = link.closest('[data-e2e]');
            var parentAttr = parent ? parent.getAttribute('data-e2e') : '';
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
        log('Total data-e2e elements: ' + allE2e.length + ', Unique: ' + keys.length);
        var chunk = [];
        for (var j = 0; j < keys.length; j++) {
            chunk.push(keys[j]);
            if (chunk.length >= 15 || j === keys.length - 1) {
                log('data-e2e: ' + chunk.join(', '));
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
        collected[key] = username.trim();
        log('Collected: @' + username + ' (' + Object.keys(collected).length + ' total)');
        // Report each found friend back to the service immediately
        if (typeof StreakApp !== 'undefined') {
            StreakApp.onFriendFound(username.trim());
        }
        return true;
    };

    var collectNextChat = function () {
        if (chatIndex >= chatItems.length) {
            log('Processed all ' + chatItems.length + ' visible items. Trying scroll...');
            scrollAndCollectMore();
            return;
        }

        var chatItem = chatItems[chatIndex];
        log('Clicking chat item ' + (chatIndex + 1) + '/' + chatItems.length);
        chatItem.click();

        setTimeout(function () {
            var username = findCurrentChatUsername();
            if (username) {
                addFriend(username);
            } else {
                log('Could not read username from chat header for item ' + (chatIndex + 1));
            }
            chatIndex++;
            collectNextChat();
        }, 1500);
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
                }, 2000);
            } else {
                log('Scroll position unchanged — end of list');
                reportDone();
            }
        }, 2000);
    };

    var reportDone = function () {
        var total = Object.keys(collected).length;
        log('Collection complete. Found ' + total + ' unique friends.');
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

    // ── Entry point ─────────────────────────────────────────────────────────

    var init = function () {
        try {
            log('Starting friend collection...');
            log('Current URL: ' + window.location.href);

            var url = window.location.href.toLowerCase();
            if (url.indexOf('/login') !== -1) {
                reportError('Not logged in. Please log in to TikTok first.');
                return;
            }

            setTimeout(function () {
                chatItems = findChatItems();
                log('Found ' + chatItems.length + ' chat items');

                if (chatItems.length === 0) {
                    dumpPageDiagnostics();
                    log('Retrying in 5 seconds...');
                    setTimeout(function () {
                        chatItems = findChatItems();
                        log('Retry: Found ' + chatItems.length + ' chat items');
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
            log('Error: ' + e.message);
            reportError('Unexpected error: ' + e.message);
        }
    };

    init();
})();
