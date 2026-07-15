// Live conversion progress on the edit page. It polls the same /api/media/{slug}/status endpoint the
// dashboard already uses; when the response carries a `progress` block, it shows a bar. There is no realtime
// channel: the worker writes progress to an in-memory store as it encodes, and the endpoint reads it.
//
// Its element uses data-conv-* attributes on purpose, NOT data-slug/data-status: upload.js watches any
// [data-status="processing"][data-slug] and reloads it, which would both double-poll and bypass the
// unsaved-edits guard below.
(function () {
    'use strict';

    var el = document.querySelector('.conv-progress[data-conv-slug]');
    if (!el) {
        return;
    }

    var slug = el.getAttribute('data-conv-slug');
    var bar = el.querySelector('.conv-progress-bar');
    var label = el.querySelector('.conv-progress-label');
    var pct = el.querySelector('.conv-progress-pct');
    var done = document.querySelector('.conv-done');

    var POLL_MS = 1500;
    var sawActivity = false;
    var timer = null;

    // Track whether the owner has touched any form field, so completing a conversion never silently reloads
    // over unsaved metadata edits. Any input the user is likely mid-editing sets this; when it's set we offer
    // a refresh instead of reloading.
    var formDirty = false;
    document.querySelectorAll('form input, form select, form textarea').forEach(function (field) {
        field.addEventListener('input', function () { formDirty = true; });
        field.addEventListener('change', function () { formDirty = true; });
    });

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

    function stop() {
        el.hidden = true;
        if (timer) {
            clearTimeout(timer);
            timer = null;
        }
    }

    function finish() {
        stop();
        if (!sawActivity) {
            return; // nothing was happening (a ready item with no re-encode): leave the page untouched
        }
        // A conversion we were watching just ended. Reload to show the finished result - unless the owner has
        // edits in the form, in which case reloading would throw them away, so offer a refresh instead.
        if (!formDirty) {
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
            // The item is gone (deleted, or no longer visible): stop, don't reload into a 404.
            if (r.status === 404 || r.status === 410) {
                stop();
                return null;
            }

            return r.ok ? r.json() : 'retry';
        }).then(function (data) {
            if (data === null) {
                return; // stopped
            }
            if (data === 'retry') {
                timer = setTimeout(poll, POLL_MS); // transient non-OK: try again
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
