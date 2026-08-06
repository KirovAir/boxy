// The view log is server-rendered in UTC, the only clock the server has. Regroup it here into the
// viewer's local days and rewrite the times, so a late-evening view doesn't sit under yesterday's
// header. With no script the UTC rendering stands, which is why the page carries the UTC note.
(function () {
    'use strict';

    var log = document.querySelector('[data-view-log]');
    if (!log) {
        return;
    }
    var chips = Array.prototype.slice.call(log.querySelectorAll('time[data-view]'));
    if (!chips.length) {
        return;
    }

    var days = new Map();
    chips.forEach(function (t) {
        var d = new Date(t.getAttribute('datetime'));
        t.textContent = d.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
        var key = d.getFullYear() * 10000 + (d.getMonth() + 1) * 100 + d.getDate();
        var day = days.get(key);
        if (!day) {
            day = {
                label: d.toLocaleDateString([], { weekday: 'short', day: 'numeric', month: 'short', year: 'numeric' }),
                stamps: []
            };
            days.set(key, day);
        }
        day.stamps.push({ at: d.getTime(), node: t.parentElement });
    });

    log.textContent = '';
    Array.from(days.keys()).sort(function (a, b) { return b - a; }).forEach(function (key) {
        var day = days.get(key);
        var wrap = document.createElement('div');
        wrap.className = 'mb-2';
        var head = document.createElement('span');
        head.className = 'small fw-bold';
        head.textContent = day.label;
        var count = document.createElement('span');
        count.className = 'small text-secondary';
        count.textContent = ' · ' + day.stamps.length + ' view' + (day.stamps.length === 1 ? '' : 's');
        var list = document.createElement('div');
        list.className = 'small text-secondary mono';
        day.stamps.sort(function (a, b) { return a.at - b.at; });
        day.stamps.forEach(function (s) { list.appendChild(s.node); });
        wrap.appendChild(head);
        wrap.appendChild(count);
        wrap.appendChild(list);
        log.appendChild(wrap);
    });

    var note = document.querySelector('[data-utc-note]');
    if (note) {
        note.remove();
    }
})();
