// Live conversion progress on the edit page. It polls the same /api/media/{slug}/status endpoint the
// dashboard already uses; when the response carries a `progress` block, it shows a bar. There is no realtime
// channel: the worker writes progress to an in-memory store as it encodes, and the endpoint reads it.
(function () {
    'use strict';

    var el = document.querySelector('.conv-progress[data-slug]');
    if (!el) {
        return;
    }

    var slug = el.getAttribute('data-slug');
    var initialStatus = el.getAttribute('data-status'); // ready | failed | processing (at page load)
    var bar = el.querySelector('.conv-progress-bar');
    var label = el.querySelector('.conv-progress-label');
    var pct = el.querySelector('.conv-progress-pct');
    var done = document.querySelector('.conv-done');

    var POLL_MS = 1500;
    var sawActivity = false;
    var timer = null;

    var STAGE_LABEL = {
        queued: 'Queued',
        preparing: 'Preparing…',
        converting: 'Converting',
        finishing: 'Finishing…'
    };

    function show(p) {
        sawActivity = true;
        el.hidden = false;
        label.textContent = STAGE_LABEL[p.stage] || 'Processing…';
        if (typeof p.percent === 'number') {
            bar.classList.remove('progress-bar-striped', 'progress-bar-animated');
            bar.style.width = p.percent + '%';
            var speed = (typeof p.speed === 'number' && p.speed > 0) ? ' · ' + p.speed.toFixed(1) + '×' : '';
            pct.textContent = p.percent + '%' + speed;
        } else {
            // Unknown percent (unknown duration, or a prepare/finish stage): an indeterminate, animated bar.
            bar.classList.add('progress-bar-striped', 'progress-bar-animated');
            bar.style.width = '100%';
            pct.textContent = '';
        }
    }

    function finish() {
        el.hidden = true;
        if (timer) {
            clearTimeout(timer);
            timer = null;
        }
        if (!sawActivity) {
            return; // nothing was happening (a ready item with no re-encode): leave the page untouched
        }
        // A conversion we were watching just ended. A page loaded mid-processing (a fresh upload) is now
        // stale, so reload it to show the finished result. For a re-encode of an item that stayed live the
        // whole time, the owner may be mid-edit, so offer a refresh instead of yanking the page away.
        if (initialStatus === 'processing') {
            window.location.reload();
        } else if (done) {
            done.hidden = false;
        }
    }

    function poll() {
        fetch('/api/media/' + encodeURIComponent(slug) + '/status', {
            headers: { 'Accept': 'application/json' },
            cache: 'no-store'
        }).then(function (r) {
            return r.ok ? r.json() : null;
        }).then(function (data) {
            if (!data) {
                timer = setTimeout(poll, POLL_MS);
                return;
            }

            if (data.progress) {
                show(data.progress);
            } else if (!data.ready && !data.failed) {
                // Processing, but no live detail yet (queued, or between stages): an indeterminate bar.
                show({ stage: 'processing', percent: null });
            } else {
                el.hidden = true;
            }

            if ((data.ready || data.failed) && !data.progress) {
                finish();
            } else {
                timer = setTimeout(poll, POLL_MS);
            }
        }).catch(function () {
            timer = setTimeout(poll, POLL_MS);
        });
    }

    if (done) {
        var refresh = done.querySelector('.conv-refresh');
        if (refresh) {
            refresh.addEventListener('click', function () {
                window.location.reload();
            });
        }
    }

    poll();
})();
