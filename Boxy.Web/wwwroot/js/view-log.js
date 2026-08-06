// The view log is server-rendered in UTC, the only clock the server has. Regroup it here into the
// viewer's local days, rewrite the times, and draw the last-30-days chart from the same entries.
// With no script the UTC day list stands, which is why the page carries the UTC note.
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
        list.className = 'small text-secondary mono mt-1';
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

    // ── Views per day, the last 30 ─────────────────────────────────────────────
    // One series on the kraft card, so the bars wear brand-deep (validated against the surface) and
    // every label stays in ink. A single day of data has nothing to plot; the list already says it.
    var chart = document.querySelector('[data-view-chart]');
    if (!chart || days.size < 2) {
        return;
    }

    var DAYS = 30;
    var tip = document.createElement('div');
    tip.className = 'vlog-tip';
    tip.hidden = true;

    function series() {
        var today = new Date();
        today.setHours(0, 0, 0, 0);
        var out = [];
        for (var i = DAYS - 1; i >= 0; i--) {
            var d = new Date(today);
            d.setDate(today.getDate() - i);
            var key = d.getFullYear() * 10000 + (d.getMonth() + 1) * 100 + d.getDate();
            out.push({ date: d, count: days.has(key) ? days.get(key).stamps.length : 0 });
        }
        return out;
    }

    function shortDate(d) {
        return d.toLocaleDateString([], { day: 'numeric', month: 'short' });
    }

    function render() {
        var data = series();
        var max = Math.max.apply(null, data.map(function (p) { return p.count; }));
        if (max === 0) {
            chart.hidden = true; // every logged view is older than the window
            return;
        }

        // The chart div itself is hidden until first render, so measure the visible parent - its
        // content box, not clientWidth, which would include the card body's padding and bleed.
        var parent = chart.parentElement;
        var pad = getComputedStyle(parent);
        var w = Math.max(parent.clientWidth - parseFloat(pad.paddingLeft) - parseFloat(pad.paddingRight), 280);
        var plotH = 84;
        var h = plotH + 18; // room for the date labels under the baseline
        var gap = 2;
        var barW = (w - (DAYS - 1) * gap) / DAYS;
        var svgNs = 'http://www.w3.org/2000/svg';
        var svg = document.createElementNS(svgNs, 'svg');
        svg.setAttribute('width', w);
        svg.setAttribute('height', h);
        svg.setAttribute('role', 'img');
        svg.setAttribute('aria-label', 'Views per day, last 30 days');

        function el(name, attrs, parent) {
            var node = document.createElementNS(svgNs, name);
            Object.keys(attrs).forEach(function (k) { node.setAttribute(k, attrs[k]); });
            (parent || svg).appendChild(node);
            return node;
        }

        // Recessive frame: a dashed hairline where a max-count bar tops out, and the baseline.
        el('line', { x1: 0, y1: 14.5, x2: w, y2: 14.5, class: 'vlog-grid' });
        el('line', { x1: 0, y1: plotH + 0.5, x2: w, y2: plotH + 0.5, class: 'vlog-axis' });
        var maxLabel = el('text', { x: 0, y: 11, class: 'vlog-label' });
        maxLabel.textContent = String(max);

        data.forEach(function (p, i) {
            var x = i * (barW + gap);
            if (p.count > 0) {
                var bh = Math.max(2, (p.count / max) * (plotH - 14));
                var y = plotH - bh;
                var r = Math.min(3, barW / 2, bh);
                // Rounded at the data end only; square on the baseline.
                el('path', {
                    class: 'vlog-bar',
                    'data-day': i,
                    d: 'M' + x + ',' + plotH
                        + ' L' + x + ',' + (y + r)
                        + ' Q' + x + ',' + y + ' ' + (x + r) + ',' + y
                        + ' L' + (x + barW - r) + ',' + y
                        + ' Q' + (x + barW) + ',' + y + ' ' + (x + barW) + ',' + (y + r)
                        + ' L' + (x + barW) + ',' + plotH + ' Z'
                });
            }

            // The hit target is the whole column, well beyond the bar itself.
            var hit = el('rect', { x: x - gap / 2, y: 0, width: barW + gap, height: plotH, fill: 'transparent' });
            hit.addEventListener('mouseenter', function () {
                var bar = svg.querySelector('.vlog-bar[data-day="' + i + '"]');
                if (bar) {
                    bar.classList.add('is-hover');
                }
                tip.textContent = p.date.toLocaleDateString([], { weekday: 'short', day: 'numeric', month: 'short' })
                    + ' · ' + p.count + ' view' + (p.count === 1 ? '' : 's');
                tip.hidden = false;
                var left = Math.min(Math.max(x + barW / 2 - 40, 0), w - 90);
                tip.style.left = left + 'px';
            });
            hit.addEventListener('mouseleave', function () {
                var bar = svg.querySelector('.vlog-bar[data-day="' + i + '"]');
                if (bar) {
                    bar.classList.remove('is-hover');
                }
                tip.hidden = true;
            });
        });

        var from = el('text', { x: 0, y: h - 3, class: 'vlog-label' });
        from.textContent = shortDate(data[0].date);
        var to = el('text', { x: w, y: h - 3, 'text-anchor': 'end', class: 'vlog-label' });
        to.textContent = shortDate(data[DAYS - 1].date);

        chart.textContent = '';
        chart.appendChild(svg);
        chart.appendChild(tip);
        chart.hidden = false;
    }

    render();

    var raf = 0;
    window.addEventListener('resize', function () {
        cancelAnimationFrame(raf);
        raf = requestAnimationFrame(render);
    });
})();
