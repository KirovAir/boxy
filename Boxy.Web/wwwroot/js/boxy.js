// Boxy - small site-wide behaviours shared across every page.
//
//   Boxy.confirm(message, opts) -> Promise<boolean>   a themed replacement for window.confirm
//   Boxy.lightbox(slug, kind)                          preview an image/video in an overlay
//
// Plus two delegated handlers so markup stays declarative:
//   <form data-confirm="…">           asks before it submits
//   <li data-lightbox data-slug data-kind>   opens a preview when the row (not a control) is clicked
(function () {
    'use strict';

    var Boxy = window.Boxy = window.Boxy || {};

    // ── Themed confirm ───────────────────────────────────────────────────────
    // Resolves true if the user confirms, false otherwise. Escape or a backdrop
    // click cancels; the confirm button is focused so Enter accepts, like the
    // native dialog it replaces.
    Boxy.confirm = function (message, opts) {
        opts = opts || {};
        var title = opts.title || 'Are you sure?';
        var confirmLabel = opts.confirmLabel || 'Delete';
        var danger = opts.danger !== false;

        return new Promise(function (resolve) {
            var lastFocus = document.activeElement;
            var backdrop = document.createElement('div');
            backdrop.className = 'bx-modal-backdrop';
            backdrop.setAttribute('role', 'dialog');
            backdrop.setAttribute('aria-modal', 'true');

            var modal = document.createElement('div');
            modal.className = 'bx-modal';

            var h = document.createElement('div');
            h.className = 'bx-modal__title';
            h.textContent = title;

            var p = document.createElement('div');
            p.className = 'bx-modal__msg';
            p.textContent = message;

            var actions = document.createElement('div');
            actions.className = 'bx-modal__actions';

            var cancel = document.createElement('button');
            cancel.type = 'button';
            cancel.className = 'btn btn-sm btn-outline-secondary';
            cancel.textContent = 'Cancel';

            var ok = document.createElement('button');
            ok.type = 'button';
            ok.className = 'btn btn-sm ' + (danger ? 'btn-danger' : 'btn-primary');
            ok.textContent = confirmLabel;

            actions.appendChild(cancel);
            actions.appendChild(ok);
            modal.appendChild(h);
            modal.appendChild(p);
            modal.appendChild(actions);
            backdrop.appendChild(modal);

            function done(result) {
                document.removeEventListener('keydown', onKey, true);
                backdrop.remove();
                if (lastFocus && lastFocus.focus) {
                    try {
                        lastFocus.focus();
                    } catch (e) {
                    }
                }
                resolve(result);
            }

            function onKey(e) {
                if (e.key === 'Escape') {
                    e.preventDefault();
                    done(false);
                } else if (e.key === 'Tab') {
                    // Trap focus between the two buttons.
                    e.preventDefault();
                    (document.activeElement === ok ? cancel : ok).focus();
                }
            }

            cancel.addEventListener('click', function () {
                done(false);
            });
            ok.addEventListener('click', function () {
                done(true);
            });
            backdrop.addEventListener('click', function (e) {
                if (e.target === backdrop) done(false);
            });
            document.addEventListener('keydown', onKey, true);

            document.body.appendChild(backdrop);
            ok.focus();
        });
    };

    // Any form carrying data-confirm asks first, then submits natively (so the
    // antiforgery token and normal redirect flow are untouched).
    document.addEventListener('submit', function (e) {
        var form = e.target;
        if (!(form instanceof HTMLFormElement)) return;
        var message = form.getAttribute('data-confirm');
        if (!message) return;
        e.preventDefault();
        Boxy.confirm(message, {
            title: form.getAttribute('data-confirm-title') || undefined,
            confirmLabel: form.getAttribute('data-confirm-label') || undefined,
            danger: form.getAttribute('data-confirm-danger') !== 'false'
        }).then(function (okd) {
            // form.submit() bypasses this listener, so there's no recursion.
            if (okd) form.submit();
        });
    }, true);

    // ── Lightbox ─────────────────────────────────────────────────────────────
    // Image/video preview in an overlay; anything else opens in a new tab.
    Boxy.lightbox = function (slug, kind) {
        if (kind !== 'image' && kind !== 'video') {
            window.open('/f/' + slug, '_blank', 'noopener');
            return;
        }
        var ov = document.createElement('div');
        ov.className = 'bx-lightbox';
        var media = kind === 'image'
            ? '<img src="/f/' + slug + '" alt="">'
            : '<video src="/f/' + slug + '" controls autoplay playsinline></video>';
        ov.innerHTML = '<button type="button" class="bx-close" aria-label="Close">×</button>' + media;

        function close() {
            var v = ov.querySelector('video');
            if (v) {
                try {
                    v.pause();
                } catch (e) {
                }
            }
            ov.remove();
            document.removeEventListener('keydown', onKey);
        }

        function onKey(e) {
            if (e.key === 'Escape') close();
        }

        ov.addEventListener('click', function (e) {
            if (e.target === ov || e.target.classList.contains('bx-close')) close();
        });
        document.addEventListener('keydown', onKey);
        document.body.appendChild(ov);
    };

    // Click a row marked data-lightbox to preview it - but let its own controls
    // (download link, delete button/form) work normally.
    document.addEventListener('click', function (e) {
        var row = e.target.closest('[data-lightbox]');
        if (!row) return;
        if (e.target.closest('a, button, form, input, label')) return;
        var slug = row.getAttribute('data-slug');
        if (!slug) return;
        Boxy.lightbox(slug, row.getAttribute('data-kind') || 'file');
    });

    // ── Copy to clipboard ─────────────────────────────────────────────────────
    // <button data-copy="text" [data-copy-label="Copied!"]> copies and shows brief feedback.
    document.addEventListener('click', function (e) {
        var btn = e.target.closest('[data-copy]');
        if (!btn) return;
        e.preventDefault();
        var text = btn.getAttribute('data-copy');
        if (navigator.clipboard && navigator.clipboard.writeText) {
            navigator.clipboard.writeText(text).then(function () {
                copyFeedback(btn);
            }, function () {
                legacyCopy(text);
                copyFeedback(btn);
            });
        } else {
            legacyCopy(text);
            copyFeedback(btn);
        }
    });

    function copyFeedback(btn) {
        if (btn.dataset.copyBusy) return;
        btn.dataset.copyBusy = '1';
        var original = btn.innerHTML;
        btn.classList.add('is-copied');
        btn.textContent = btn.getAttribute('data-copy-label') || 'Copied!';
        setTimeout(function () {
            btn.innerHTML = original;
            btn.classList.remove('is-copied');
            delete btn.dataset.copyBusy;
        }, 1400);
    }

    function legacyCopy(text) {
        try {
            var ta = document.createElement('textarea');
            ta.value = text;
            ta.style.cssText = 'position:fixed;top:0;opacity:0';
            document.body.appendChild(ta);
            ta.focus();
            ta.select();
            document.execCommand('copy');
            ta.remove();
        } catch (e) {
        }
    }

    // ── Bulk select ────────────────────────────────────────────────────────────
    // A [data-bulk] scope contains .bulk-check item boxes, an optional .bulk-all toggle,
    // a .bulk-bar (revealed once something is selected) with [data-bulk-count], and
    // [data-bulk-action] forms. On every change the checked ids are mirrored into each
    // action form as hidden `ids` fields, so a plain (or data-confirm) submit just works.
    function syncBulk(scope) {
        var checks = scope.querySelectorAll('.bulk-check');
        var ids = [];
        checks.forEach(function (c) {
            if (c.checked) ids.push(c.value);
        });

        var bar = scope.querySelector('.bulk-bar');
        if (bar) bar.hidden = ids.length === 0;
        var count = scope.querySelector('[data-bulk-count]');
        if (count) count.textContent = ids.length + ' selected';
        var all = scope.querySelector('.bulk-all');
        if (all) {
            all.checked = ids.length > 0 && ids.length === checks.length;
            all.indeterminate = ids.length > 0 && ids.length < checks.length;
        }

        scope.querySelectorAll('[data-bulk-action]').forEach(function (form) {
            form.querySelectorAll('input[data-bulk-id]').forEach(function (n) {
                n.remove();
            });
            ids.forEach(function (id) {
                var i = document.createElement('input');
                i.type = 'hidden';
                i.name = 'ids';
                i.value = id;
                i.setAttribute('data-bulk-id', '');
                form.appendChild(i);
            });
        });
    }

    document.addEventListener('change', function (e) {
        var scope = e.target.closest('[data-bulk]');
        if (!scope) return;
        if (e.target.classList.contains('bulk-all')) {
            var on = e.target.checked;
            scope.querySelectorAll('.bulk-check').forEach(function (c) {
                c.checked = on;
            });
        }
        if (e.target.classList.contains('bulk-check') || e.target.classList.contains('bulk-all')) {
            syncBulk(scope);
        }
    });
})();
