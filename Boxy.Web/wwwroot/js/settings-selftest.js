// Admin "Server setup" upload self-test: POST increasing body sizes straight through the reverse proxy to
// prove it forwards large uploads. A too-low nginx client_max_body_size, or a WAF that buffers/blocks big
// bodies, shows up as a failed or short POST at a specific size. Uses the same XHR the real uploader does.
(function () {
    var btn = document.getElementById('selftestBtn');
    var out = document.getElementById('selftestResult');
    if (!btn || !out) return;

    var SIZES_MB = [16, 50, 100];

    btn.addEventListener('click', function () {
        btn.disabled = true;
        show('secondary', 'Testing the upload path through your reverse proxy…');
        run(0, 0);
    });

    function run(i, maxOk) {
        if (i >= SIZES_MB.length) {
            show('success', 'Reverse proxy passed every test size up to ' + maxOk + ' MB. Real uploads (16 MB chunks) get through cleanly.');
            btn.disabled = false;
            return;
        }
        var mb = SIZES_MB[i];
        show('secondary', 'Testing ' + mb + ' MB…');
        post(mb).then(function (res) {
            if (res.ok && res.received === mb * 1024 * 1024) run(i + 1, mb);
            else fail(mb, maxOk, res, null);
        }).catch(function (e) {
            fail(mb, maxOk, null, e);
        });
    }

    function post(mb) {
        var bytes = mb * 1024 * 1024;
        var body = new Blob([new Uint8Array(bytes)]);
        return new Promise(function (resolve, reject) {
            var xhr = new XMLHttpRequest();
            xhr.open('POST', '/settings/server/selftest');
            xhr.onload = function () {
                var received = -1;
                try {
                    received = JSON.parse(xhr.responseText).received;
                } catch (e) {
                }
                resolve({ok: xhr.status >= 200 && xhr.status < 300, status: xhr.status, received: received});
            };
            xhr.onerror = function () {
                reject(new Error('network'));
            };
            xhr.send(body);
        });
    }

    function fail(mb, maxOk, res, err) {
        btn.disabled = false;
        var cause = err
            ? 'the connection dropped (a reverse-proxy body-size limit, or a WAF buffering large bodies, is the likely cause)'
            : (res && res.status === 413)
                ? 'it was rejected with 413 Payload Too Large'
                : 'it failed with HTTP ' + (res ? res.status : '?') + ' (' + (res ? res.received : '?') + ' of ' + (mb * 1024 * 1024) + ' bytes arrived)';
        // 16 MB is the real chunk size, so once it passes, uploads work - larger sizes are only a headroom
        // check and a failure there is informational, not a problem to fix.
        if (maxOk >= SIZES_MB[0]) {
            show('success', 'Uploads will work: 16 MB chunks (the size real uploads use) pass cleanly. Your reverse proxy stops accepting bodies around ' + mb + ' MB - ' + cause + '. That is fine for Boxy; it only matters if you also rely on the no-JS whole-file fallback for very large files.');
        } else {
            show('danger', 'Uploads are blocked: even a 16 MB chunk failed - ' + cause + '. Raise client_max_body_size on this host (0 = unlimited) and check any WAF/CrowdSec.');
        }
    }

    function show(kind, text) {
        out.className = 'alert alert-' + kind + ' py-2 small mt-2 mb-0';
        out.textContent = text;
    }
})();
