/* Boxy chunked uploader.
 * Splits each file into fixed-size chunks and uploads several in parallel (fast on high-latency
 * links and multi-GB files), with per-chunk retry and per-file cancel. Chunks are stored as
 * separate parts server-side and concatenated on completion. Live per-file + aggregate progress
 * with transfer rate and ETA. Falls back to a plain multipart form post when JS is off. */
(function () {
    var form = document.getElementById('uploadForm');
    if (!form) return;
    // The chunked engine needs these APIs. If any is missing, do NOT touch the page - returning here leaves
    // the native <form> POST (and its visible submit button) working as the fallback, instead of hiding the
    // button and then throwing on a browser that can't run the engine. crypto is deliberately NOT required:
    // randomId() falls back to Math.random, so a missing crypto never blocks an upload.
    if (!(window.XMLHttpRequest && window.Promise && Array.prototype.find
        && window.Blob && Blob.prototype.slice)) return;

    var CHUNK = 16 * 1024 * 1024; // 16 MB - small, resumable requests that pass any sane reverse-proxy body limit
    var CONCURRENCY = 6;          // chunks in flight per file - more parallelism to fill a high-latency pipe
    // A chunk gets 8 retries backing off to 30s, so a drop-out has a couple of minutes to come back before
    // the file is called failed. The old budget was 4 tries over 7.5s, which a tunnel or a lift outlasts.
    var RETRIES = 8;
    var MAX_BACKOFF = 30000;
    var OFFLINE_WAIT = 15000;     // longest we sit waiting for an 'online' event before trying anyway
    var STALL_MS = 30000;         // abort+retry a chunk whose upload stops making progress this long (half-open link)
    var REQUEST_TIMEOUT = 60000;  // hard timeout on the small JSON requests (resume probe, complete, poll, status)
    var POLL_MS = 2000;           // how often we ask whether a detached assembly has finished

    var chunkUrl = form.dataset.chunkUrl;
    var completeUrl = form.dataset.completeUrl;
    var abortUrl = chunkUrl.replace(/\/chunk$/, '/abort');
    var chunksUrl = chunkUrl.replace(/\/chunk$/, '/chunks');
    var deleteBase = form.dataset.deleteBase || null;
    // The server-rendered "your uploads" row endpoint base: /u/{slug}/delete -> /u/{slug}/mine.
    var rowBase = deleteBase ? deleteBase.replace(/\/delete$/, '/mine') : null;
    var input = form.querySelector('input[type=file]');
    var dropzone = form.querySelector('.dropzone');
    var submitBtn = form.querySelector('button[type=submit]');
    var queueEl = document.getElementById('queue');
    var readoutEl = document.getElementById('readout');
    var mineEl = document.getElementById('mine');

    var jobs = [];
    var running = false;

    // Keep the screen awake while uploading. Two mechanisms, because iOS is fussy:
    //  1. Screen Wake Lock API - works on Android and iOS 16.4+, but iOS ignores it in Low Power Mode.
    //  2. A muted, looping, inline <video> - iOS keeps the display on during active playback, including
    //     in Low Power Mode and on older iOS where the API is missing.
    // Both only defeat the AUTO-lock timeout. A manual lock or app-switch suspends the page and pauses
    // the upload; the browser can't run in the background (an iOS platform limit, not something we control).
    var wakeLock = null, keepVideo = null;
    // Tiny silent looping H.264 clip (baseline/yuv420p/faststart) played invisibly to hold the screen.
    var KEEP_AWAKE_VIDEO = 'data:video/mp4;base64,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';

    function acquireWake() {
        if (navigator.wakeLock && !wakeLock) {
            navigator.wakeLock.request('screen').then(function (w) {
                wakeLock = w;
                // The system can release the lock on its own (e.g. brief blank); clear it so we re-take.
                w.addEventListener('release', function () {
                    wakeLock = null;
                });
            }).catch(function () {
            });
        }
        playKeepAwakeVideo();
    }

    function releaseWake() {
        if (wakeLock) {
            try {
                wakeLock.release();
            } catch (e) {
            }
            wakeLock = null;
        }
        if (keepVideo) {
            try {
                keepVideo.pause();
            } catch (e) {
            }
        }
    }

    function playKeepAwakeVideo() {
        if (!keepVideo) {
            keepVideo = document.createElement('video');
            keepVideo.muted = true;
            keepVideo.loop = true;
            keepVideo.setAttribute('muted', '');            // iOS honours the attribute form
            keepVideo.setAttribute('playsinline', '');
            keepVideo.setAttribute('webkit-playsinline', '');
            keepVideo.style.cssText = 'position:fixed;left:0;bottom:0;width:1px;height:1px;pointer-events:none';
            keepVideo.src = KEEP_AWAKE_VIDEO;
            (document.body || document.documentElement).appendChild(keepVideo);
        }
        var p = keepVideo.play();
        if (p && p.catch) p.catch(function () {
        });
    }

    document.addEventListener('visibilitychange', function () {
        // Both mechanisms drop when the tab is hidden - re-take them on return if still uploading.
        if (document.visibilityState === 'visible' && activeJobs().length) acquireWake();
    });

    if (submitBtn) submitBtn.style.display = 'none';
    // When JS is active, never let the browser do a native multipart POST - chunked always wins.
    form.addEventListener('submit', function (e) {
        e.preventDefault();
    });
    input.addEventListener('change', function () {
        addFiles(input.files);
        input.value = '';
    });

    if (dropzone) {
        ['dragover', 'dragenter'].forEach(function (e) {
            dropzone.addEventListener(e, function (ev) {
                ev.preventDefault();
                dropzone.classList.add('over');
            });
        });
        ['dragleave', 'dragend'].forEach(function (e) {
            dropzone.addEventListener(e, function () {
                dropzone.classList.remove('over');
            });
        });
        dropzone.addEventListener('drop', function (ev) {
            ev.preventDefault();
            dropzone.classList.remove('over');
            if (ev.dataTransfer && ev.dataTransfer.files.length) addFiles(ev.dataTransfer.files);
        });
    }

    window.addEventListener('beforeunload', function (e) {
        if (activeJobs().length) {
            e.preventDefault();
            e.returnValue = '';
        }
    });

    // If a previous upload was interrupted (phone locked, tab discarded), nudge the user to re-pick.
    showResumeHint();

    function activeJobs() {
        return jobs.filter(function (j) {
            return j.state === 'queued' || j.state === 'uploading' || j.state === 'finishing';
        });
    }

    // ── Resumable-upload memory ─────────────────────────────────────────────
    // Remember each in-flight upload's server id in localStorage, keyed by the completion target +
    // the file's signature (name/size/mtime). If the page reloads (or a phone locks and Safari
    // discards the tab) the user can re-pick the same file and we skip the chunks the server already
    // has. Scoping the key to completeUrl (not the shared chunk endpoint) keeps each destination's
    // resume state separate - so a partial "replace this share" can never resume onto a different
    // share or the new-upload dashboard, which all post chunks to the same endpoint.
    var LS_KEY = 'boxy_uploads', MAX_AGE = 12 * 3600 * 1000;

    // Key resume memory on name+size+lastModified. lastModified is what makes this key SAFE: without it, two
    // different files that happen to share a name and an exact byte size would resume onto each other's
    // staged parts (the server only checks each part's LENGTH, and no content hash is compared), silently
    // assembling a corrupt file. The cost is that if iOS re-exports a picked clip with a fresh lastModified,
    // the same file re-picked won't match and re-sends from scratch - safe, just not resumed. (Sending a
    // content fingerprint to /complete would let us drop lastModified safely; until then, correctness wins.)
    function fileSig(f) {
        return completeUrl + '::' + f.name + '|' + f.size + '|' + (f.lastModified || 0);
    }

    function loadStore() {
        try {
            return JSON.parse(localStorage.getItem(LS_KEY) || '{}') || {};
        } catch (e) {
            return {};
        }
    }

    function saveStore(s) {
        try {
            localStorage.setItem(LS_KEY, JSON.stringify(s));
        } catch (e) {
        }
    }

    function prune(s) {
        var cutoff = Date.now() - MAX_AGE;
        for (var k in s) {
            if (!s[k] || !s[k].ts || s[k].ts < cutoff) delete s[k];
        }
        return s;
    }

    function rememberUpload(job) {
        var s = prune(loadStore());
        s[fileSig(job.file)] = {
            uploadId: job.uploadId,
            total: job.total,
            name: job.file.name,
            size: job.file.size,
            ts: Date.now()
        };
        saveStore(s);
    }

    function forgetUpload(job) {
        var s = loadStore();
        delete s[fileSig(job.file)];
        saveStore(s);
    }

    function recallUpload(f) {
        var e = prune(loadStore())[fileSig(f)];
        return (e && e.uploadId) ? e : null;
    }

    function pendingUploads() {
        var s = prune(loadStore());
        saveStore(s);
        var out = [];
        for (var k in s) {
            if (k.indexOf(completeUrl + '::') === 0) out.push(s[k]);
        }
        return out;
    }

    // The server needs the exact layout we cut the file into, so it can check every stored part is the
    // right length for its slot before reporting it back as one we can skip.
    function layoutQuery(job) {
        return '&size=' + job.file.size + '&chunkSize=' + CHUNK;
    }

    // Ask which parts the server already holds. This is worth being stubborn about: giving up and reporting
    // "none" means re-sending the entire file, so a blip here costs gigabytes. It waits for the network and
    // retries on the same budget a chunk gets, and only ever gives up when the server says no (a 4xx) or the
    // retries run out - in which case re-sending everything is all that's left.
    function fetchExisting(job) {
        return whenOnline().then(function () {
            return probe(0);
        });

        function probe(tries) {
            return request('GET', chunksUrl + '?uploadId=' + encodeURIComponent(job.uploadId) + layoutQuery(job),
                {accept: 'application/json'})
                .then(function (r) {
                    if (r.ok) return r.json();
                    throw {permanent: isPermanent(r.status)};
                })
                .then(function (d) {
                    return (d && d.have) || [];
                })
                .catch(function (e) {
                    if (job.cancelled || (e && e.permanent) || tries >= RETRIES) return [];
                    return delay(backoffMs(tries)).then(whenOnline).then(function () {
                        return probe(tries + 1);
                    });
                });
        }
    }

    function delay(ms) {
        return new Promise(function (resolve) {
            setTimeout(resolve, ms);
        });
    }

    // Minimal XHR-based request, so the whole uploader depends only on XMLHttpRequest and never on
    // window.fetch. Some in-app webviews (WhatsApp/Instagram) and old iOS Safari have XHR but not fetch;
    // when the finalize/resume/status calls used fetch, such a browser could upload every chunk and then be
    // unable to assemble the file. Returns a fetch-like response ({status, ok, json(), text()}); json()
    // throws on a non-JSON body, matching fetch's reject, which the callers already handle.
    function request(method, url, opts) {
        opts = opts || {};
        return new Promise(function (resolve, reject) {
            var xhr = new XMLHttpRequest();
            try {
                xhr.open(method, url);
            } catch (e) {
                reject(e);
                return;
            }
            if (opts.accept) xhr.setRequestHeader('Accept', opts.accept);
            xhr.timeout = REQUEST_TIMEOUT;   // ontimeout below is dead code without this; a stalled probe/poll must not hang
            xhr.onload = function () {
                var body = xhr.responseText;
                resolve({
                    status: xhr.status,
                    ok: xhr.status >= 200 && xhr.status < 300,
                    text: function () {
                        return body;
                    },
                    json: function () {
                        return JSON.parse(body);
                    }
                });
            };
            xhr.onerror = function () {
                reject(new Error('network error'));
            };
            xhr.ontimeout = function () {
                reject(new Error('timeout'));
            };
            xhr.send(opts.body != null ? opts.body : null);
        });
    }

    function chunkBytes(job, idx) {
        return Math.min(CHUNK, job.file.size - idx * CHUNK);
    }

    var resumeHintEl = null;

    function dismissResumeHint() {
        if (resumeHintEl) {
            try {
                resumeHintEl.remove();
            } catch (e) {
            }
            resumeHintEl = null;
        }
    }

    function showResumeHint() {
        var pend = pendingUploads();
        if (!pend.length || !queueEl || resumeHintEl) return;
        var names = pend.map(function (p) {
            return p.name;
        }).join(', ');
        var one = pend.length === 1;
        resumeHintEl = document.createElement('div');
        resumeHintEl.className = 'alert alert-secondary py-2 small';
        resumeHintEl.textContent = (one ? 'Unfinished upload: ' : 'Unfinished uploads: ') + names +
            '. Pick the same ' + (one ? 'file' : 'files') + ' again to carry on where it left off.';
        queueEl.parentNode.insertBefore(resumeHintEl, queueEl);
    }

    var maxBytes = parseInt(form.dataset.maxBytes || '0', 10);

    function addFiles(fileList) {
        var added = 0, tooBig = [], unreadable = [];
        for (var i = 0; i < fileList.length; i++) {
            var f = fileList[i];
            // A file the picker reports as 0 bytes can't be uploaded. On iPhones this is usually an iCloud
            // photo/video not yet downloaded to the device. Don't drop it silently (that reads to the user
            // as "nothing happened") - collect it and say how to fix it, below.
            if (!f.size) {
                unreadable.push(f.name);
                continue;
            }
            // Reject over-limit files up front, so a huge file never starts uploading.
            if (maxBytes > 0 && f.size > maxBytes) {
                tooBig.push(f.name);
                continue;
            }
            var prev = recallUpload(f);   // same file picked again → resume its server-side parts
            var job = {
                file: f, sent: 0, state: 'queued', el: null, rate: 0, lastT: 0, lastB: 0,
                xhrs: [], loaded: {}, cancelled: false, error: null,
                uploadId: prev ? prev.uploadId : randomId(),
                total: Math.max(1, Math.ceil(f.size / CHUNK)),
                resume: !!prev
            };
            jobs.push(job);
            rememberUpload(job);
            renderJob(job);
            added++;
        }
        var notes = [];
        if (tooBig.length) {
            var mb = Math.round(maxBytes / 1048576);
            notes.push((tooBig.length === 1 ? '“' + tooBig[0] + '” is' : tooBig.length + ' files are')
                + ' over the ' + mb + ' MB limit for this box and ' + (tooBig.length === 1 ? 'was' : 'were') + ' skipped.');
        }
        if (unreadable.length) {
            notes.push((unreadable.length === 1 ? '“' + unreadable[0] + '” couldn’t be read' : unreadable.length + ' files couldn’t be read')
                + ' (0 bytes). If it’s an iPhone photo or video stored in iCloud, open it once in Photos so it downloads to this device, then pick it again.');
        }
        if (notes.length) {
            showNotice(notes.join(' '));
        } else if (added) {
            hideNotice();   // a clean batch clears any stale skip notice
        }
        if (added) {
            dismissResumeHint();
        }
        if (added && queueEl.firstElementChild) {
            try {
                queueEl.firstElementChild.scrollIntoView({behavior: 'smooth', block: 'nearest'});
            } catch (e) {
            }
        }
        // Only pump when something was queued - otherwise finishSweep would clear the over-limit warning.
        if (added && !running) pump();
    }

    // Skipped-file notices (over-limit, 0-byte) live in their own element, NOT the progress readout - an
    // in-flight upload's live progress would otherwise overwrite the notice within a tick and the user would
    // never learn why a file was skipped.
    var noticeEl = null;

    function showNotice(text) {
        if (!queueEl) return;
        if (!noticeEl) {
            noticeEl = document.createElement('div');
            queueEl.parentNode.insertBefore(noticeEl, queueEl);
        }
        noticeEl.className = 'alert alert-warning py-2 small';
        noticeEl.textContent = text;
    }

    function hideNotice() {
        if (noticeEl) {
            try {
                noticeEl.remove();
            } catch (e) {
            }
            noticeEl = null;
        }
    }

    function pump() {
        var next = jobs.find(function (j) {
            return j.state === 'queued';
        });
        if (!next) {
            running = false;
            releaseWake();
            finishSweep();
            return;
        }
        running = true;
        acquireWake();
        uploadFile(next).then(pump);
    }

    function uploadFile(job) {
        job.state = 'uploading';
        job.lastT = Date.now();
        updateJob(job);

        return new Promise(function (resolve) {
            var have = {};   // chunk indices the server already has (for a resumed upload)
            function begin() {
                runChunks(job, have, resolve);
            }

            if (job.resume) {
                // fetchExisting settles either way: an empty list just means every chunk gets sent.
                fetchExisting(job).then(function (list) {
                    list.forEach(function (idx) {
                        if (idx >= 0 && idx < job.total) {
                            have[idx] = true;
                            job.loaded[idx] = chunkBytes(job, idx);
                        }
                    });
                    onProgress(job);   // reflect the already-uploaded bytes in the bar
                    begin();
                });
            } else {
                begin();
            }
        });
    }

    function runChunks(job, have, resolve) {
        var total = job.total, nextIdx = 0, active = 0, done = 0, failed = false, finalizing = false;
        for (var i = 0; i < total; i++) {
            if (have[i]) done++;
        }

        function launch() {
            if (job.cancelled) {
                resolve();
                return;
            }
            if (done >= total) {
                finalize();
                return;
            }
            while (active < CONCURRENCY && nextIdx < total && !failed) {
                var idx = nextIdx++;
                if (have[idx]) continue;   // already on the server - skip it
                (function (idx) {
                    active++;
                    sendChunk(job, idx).then(function () {
                        active--;
                        done++;
                        if (job.cancelled) {
                            resolve();
                            return;
                        }
                        if (done >= total) finalize();
                        else launch();
                    }).catch(function () {
                        if (!failed && !job.cancelled) {
                            failed = true;
                            job.state = 'failed';
                            updateJob(job);
                            resolve();
                        }
                    });
                })(idx);
            }
        }

        function finalize() {
            if (finalizing) return;
            finalizing = true;
            job.state = 'finishing';
            updateJob(job);
            complete(job).then(resolve);
        }

        launch();
    }

    // A 4xx is the server's considered answer (too big, box full, bad request) - re-sending the same bytes
    // will get the same answer, so don't burn retries on it. Everything else (5xx, a proxy hiccup, a dropped
    // connection) is worth another go. 408/429 are explicit "try again" codes; 507 is "the disk is full",
    // which hammering will not help.
    function isPermanent(status) {
        return status === 507 || (status >= 400 && status < 500 && status !== 408 && status !== 429);
    }

    // Exponential backoff with jitter, so six parallel chunks don't all retry on the same beat and
    // re-collapse a link that is only just back.
    function backoffMs(tries) {
        var base = Math.min(MAX_BACKOFF, 500 * Math.pow(2, tries));
        return Math.round(base * (0.75 + Math.random() * 0.5));
    }

    // Parse a Retry-After header (delta-seconds or an HTTP-date) into ms, or 0 when absent/unparseable.
    function retryAfterMs(xhr) {
        var h = xhr.getResponseHeader && xhr.getResponseHeader('Retry-After');
        if (!h) return 0;
        h = ('' + h).trim();
        if (/^\d+$/.test(h)) return parseInt(h, 10) * 1000;
        var when = Date.parse(h);
        return isNaN(when) ? 0 : Math.max(0, when - Date.now());
    }

    // Resolves once the browser believes it's online again, or after a cap - never trust the event alone.
    function whenOnline() {
        if (navigator.onLine !== false) return Promise.resolve();
        return new Promise(function (resolve) {
            var timer = setTimeout(go, OFFLINE_WAIT);

            function go() {
                window.removeEventListener('online', go);
                clearTimeout(timer);
                resolve();
            }

            window.addEventListener('online', go);
        });
    }

    function sendChunk(job, idx) {
        var start = idx * CHUNK;
        var blob = job.file.slice(start, Math.min(start + CHUNK, job.file.size));
        return attempt(0);

        function attempt(tries) {
            return new Promise(function (resolve, reject) {
                if (job.cancelled) {
                    resolve();
                    return;
                }
                var xhr = new XMLHttpRequest();
                var stalled = false, watchdog = null;

                // A half-open link (cell/wifi handoff, a captive portal, a middlebox) can stop delivering
                // bytes without ever firing onerror, which would hang this chunk forever and pin its
                // concurrency slot so the upload never finalizes. Guard every chunk with a watchdog that
                // aborts it once its upload makes no progress for STALL_MS, then routes through fail() so it
                // retries like any transient error. The timer is re-armed on each progress event, so a
                // slow-but-advancing chunk is never killed.
                function arm() {
                    clearTimeout(watchdog);
                    watchdog = setTimeout(function () {
                        stalled = true;
                        try {
                            xhr.abort();
                        } catch (e) {
                        }
                    }, STALL_MS);
                }

                function disarm() {
                    clearTimeout(watchdog);
                }

                xhr.open('POST', chunkUrl + '?uploadId=' + job.uploadId + '&index=' + idx);
                xhr.setRequestHeader('Content-Type', 'application/octet-stream');
                job.xhrs.push(xhr);
                xhr.upload.onprogress = function (e) {
                    arm();
                    if (e.lengthComputable) {
                        job.loaded[idx] = e.loaded;
                        onProgress(job);
                    }
                };
                xhr.onload = function () {
                    disarm();
                    detach(job, xhr);
                    if (job.cancelled) {
                        resolve();
                        return;
                    }
                    if (xhr.status >= 200 && xhr.status < 300) {
                        job.loaded[idx] = blob.size;
                        onProgress(job);
                        resolve();
                        return;
                    }
                    if (isPermanent(xhr.status)) {
                        var body = errorOf(xhr);
                        if (body.restart) restart(job);
                        job.error = body.error || null;
                        reject(new Error('chunk ' + idx + ' rejected'));
                        return;
                    }
                    // Transient (5xx / 408 / 429). Honour Retry-After when the server - or a proxy/WAF like
                    // CrowdSec throttling the chunk burst - sends one, so we don't retry ahead of its window.
                    fail(retryAfterMs(xhr));
                };
                xhr.onerror = function () {
                    disarm();
                    detach(job, xhr);
                    fail(0);
                };
                xhr.onabort = function () {
                    disarm();
                    detach(job, xhr);
                    // A watchdog abort is a stall - retry it. A user cancel (job.cancelled) just stops.
                    if (stalled && !job.cancelled) fail(0);
                    else resolve();
                };
                arm();
                xhr.send(blob);

                function fail(minDelay) {
                    if (job.cancelled) {
                        resolve();
                        return;
                    }
                    if (tries >= RETRIES) {
                        reject(new Error('chunk ' + idx + ' failed'));
                        return;
                    }
                    // Sit out the backoff (or the server's Retry-After, whichever is longer), then wait for
                    // the network before spending the next attempt.
                    setTimeout(function () {
                        whenOnline().then(function () {
                            attempt(tries + 1).then(resolve, reject);
                        });
                    }, Math.max(backoffMs(tries), minDelay || 0));
                }
            });
        }
    }

    // The chosen video conversion, if the page offers the choice. Read here, at finalize, rather than when
    // the file was picked: the upload starts the moment a file lands, so the value is still the uploader's
    // to change while a multi-GB clip is on its way up. Absent means "whatever the server defaults to".
    function profileQuery() {
        var el = form.querySelector('select[name=profile], input[name=profile]:checked');
        return el && el.value ? '&profile=' + encodeURIComponent(el.value) : '';
    }

    // Keep the help text on whatever is selected: each option costs something different in disk and in
    // waiting, and that is the whole basis for choosing between them.
    var profileSelect = form.querySelector('select[name=profile]');
    var profileHelp = document.getElementById('profileHelp');
    if (profileSelect && profileHelp) {
        profileSelect.addEventListener('change', function () {
            var opt = profileSelect.options[profileSelect.selectedIndex];
            profileHelp.textContent = (opt && opt.getAttribute('data-help')) || '';
        });
    }

    // Ask the server to assemble the staged parts into the finished file.
    //
    // Concatenating and hashing a multi-GB file takes minutes, which is longer than a reverse proxy will
    // hold a silent connection open, so the server does the work detached from the request and answers 202.
    // From here that means: ask, then poll until it says done. Asking is idempotent - the server replays a
    // finished result - so a lost answer just gets asked for again rather than re-uploading anything.
    function complete(job) {
        var askUrl = completeUrl + '?uploadId=' + job.uploadId + '&total=' + job.total +
            '&name=' + encodeURIComponent(job.file.name) + layoutQuery(job) + profileQuery();
        var pollUrl = completeUrl + '/status?uploadId=' + job.uploadId;

        return ask(0);

        function ask(tries) {
            if (job.cancelled) return Promise.resolve();
            return whenOnline()
                .then(function () {
                    return request('POST', askUrl, {accept: 'application/json'});
                })
                .then(read)
                .then(function (body) {
                    return handle(body, function () {
                        return poll(0);
                    });
                })
                .catch(function () {
                    return retryOr(tries, ask, 'Could not finish that upload.');
                });
        }

        function poll(tries) {
            if (job.cancelled) return Promise.resolve();
            return delay(POLL_MS)
                .then(whenOnline)
                .then(function () {
                    return request('GET', pollUrl, {accept: 'application/json'});
                })
                .then(read)
                .then(function (body) {
                    // 'unknown' means the server forgot the job (a restart). The parts are still staged, so
                    // just ask for the assembly again.
                    if (body.status === 'unknown') return ask(0);
                    return handle(body, function () {
                        return poll(0);
                    });
                })
                .catch(function () {
                    return retryOr(tries, poll, 'Could not finish that upload.');
                });
        }

        function read(r) {
            if (r.status === 202) return {status: 'assembling'};
            if (!r.ok && isPermanent(r.status)) return {status: 'error', error: null, gone: true};
            if (!r.ok) throw new Error('finalize http ' + r.status);
            return r.json();
        }

        function handle(body, keepWaiting) {
            if (body.status === 'assembling') return keepWaiting();
            if (body.status === 'done') return succeed(body);
            if (body.restart) restart(job);
            return fail(body.error);
        }

        function retryOr(tries, again, message) {
            if (job.cancelled) return Promise.resolve();
            if (tries >= RETRIES) return fail(message);
            return delay(backoffMs(tries)).then(function () {
                return again(tries + 1);
            });
        }

        function succeed(body) {
            forgetUpload(job);   // done - drop the resume memory
            job.sent = job.file.size;
            job.state = 'done';
            removeRow(job);
            try {
                onUploaded(body);
            } catch (e) {
            }
        }

        function fail(message) {
            job.error = message || null;
            job.state = 'failed';
            updateJob(job);
        }
    }

    // The JSON body of a failed upload response, or {} when there isn't one.
    function errorOf(xhr) {
        try {
            return JSON.parse(xhr.responseText) || {};
        } catch (e) {
            return {};
        }
    }

    // Throw away everything we know about this job's server-side parts and give it a fresh upload id, so
    // the next attempt re-sends the whole file instead of resuming onto parts the server has discarded.
    // The new id is remembered straight away: the attempt that follows stages chunks under it, and a reload
    // part-way through that attempt should still be able to pick them up.
    function restart(job) {
        job.uploadId = randomId();
        job.resume = false;
        job.loaded = {};
        job.sent = 0;
        rememberUpload(job);
    }

    function cancel(job) {
        // Once the server is assembling, the upload is out of our hands: deleting its parts would fail the
        // very file the user is waiting on. The server refuses the abort at that point too.
        if (job.state === 'done' || job.state === 'finishing') return;
        job.cancelled = true;
        job.state = 'cancelled';
        job.xhrs.slice().forEach(function (x) {
            try {
                x.abort();
            } catch (e) {
            }
        });
        job.xhrs = [];
        forgetUpload(job);
        if (job.uploadId) {
            try {
                request('POST', abortUrl + '?uploadId=' + job.uploadId).catch(function () {
                });
            } catch (e) {
            }
        }
        removeRow(job);
        updateReadout();
        if (!running) finishSweep();
    }

    function retry(job) {
        job.cancelled = false;
        job.state = 'queued';
        job.sent = 0;
        job.loaded = {};
        job.xhrs = [];
        job.error = null;
        job.rate = 0;
        // Carry on from what the server already holds. Without this a file that failed at 95% re-sent every
        // byte from the start, which on a multi-GB upload is the difference between seconds and an hour.
        job.resume = true;
        rememberUpload(job);
        updateJob(job);
        if (!running) pump();
    }

    function onProgress(job) {
        var s = 0;
        for (var k in job.loaded) s += job.loaded[k];
        job.sent = s;
        var now = Date.now(), dt = (now - job.lastT) / 1000;
        if (dt > 0.3) {
            var inst = (job.sent - job.lastB) / dt;
            job.rate = job.rate ? job.rate * 0.7 + inst * 0.3 : inst;
            job.lastT = now;
            job.lastB = job.sent;
        }
        updateJob(job);
        updateReadout();
    }

    // ── Rendering ───────────────────────────────────────────────────────────
    function renderJob(job) {
        var li = document.createElement('li');
        li.className = 'list-group-item';
        li.innerHTML =
            '<div class="d-flex justify-content-between align-items-center gap-2">' +
            '<span class="text-truncate job-name"></span>' +
            '<span class="d-flex align-items-center gap-2 flex-shrink-0">' +
            '<span class="small text-secondary job-state"></span>' +
            '<button type="button" class="btn-close job-cancel" aria-label="Cancel"></button></span></div>' +
            '<div class="progress my-1" style="height:6px;"><div class="progress-bar job-fill" style="width:0%"></div></div>' +
            '<div class="d-flex justify-content-between small text-secondary">' +
            '<span class="j-size"></span><span class="j-rate"></span></div>';
        li.querySelector('.job-name').textContent = job.file.name;
        li.querySelector('.j-size').textContent = fmtSize(job.file.size);
        li.querySelector('.job-cancel').addEventListener('click', function () {
            if (job.state === 'failed' || job.state === 'cancelled') removeRow(job);
            else cancel(job);
        });
        job.el = li;
        queueEl.appendChild(li);
        updateJob(job);
    }

    function updateJob(job) {
        var el = job.el;
        if (!el) return;
        var pct = job.file.size ? Math.min(100, Math.round((job.sent / job.file.size) * 100)) : 0;
        var bar = el.querySelector('.job-fill');
        bar.style.width = (job.state === 'finishing' ? 100 : pct) + '%';
        bar.className = 'progress-bar job-fill' +
            (job.state === 'failed' ? ' bg-danger' : job.state === 'finishing' ? ' progress-bar-striped progress-bar-animated' : '');
        var st = el.querySelector('.job-state');
        st.className = 'small job-state ' + (job.state === 'failed' ? 'text-danger' : 'text-secondary');
        st.textContent = job.state === 'failed' ? 'Failed'
            : job.state === 'finishing' ? 'Finishing…'
                : job.state === 'queued' ? 'Queued'
                    : pct + '%';
        // The rate slot doubles as the place to say why a file failed, when the server told us.
        var rate = el.querySelector('.j-rate');
        rate.className = 'j-rate' + (job.state === 'failed' && job.error ? ' text-danger' : '');
        rate.textContent = job.state === 'failed' && job.error ? job.error
            : job.state === 'uploading' && job.rate ? fmtRate(job.rate)
                : '';
        var x = el.querySelector('.job-cancel');
        if (x) {
            x.hidden = job.state === 'finishing';   // nothing left to cancel once the server has the bytes
            x.title = job.state === 'failed' ? 'Dismiss' : 'Cancel';
        }
    }

    function removeRow(job) {
        if (job.el && job.el.parentNode) job.el.parentNode.removeChild(job.el);
        job.el = null;
    }

    function updateReadout() {
        if (!readoutEl) return;
        var live = jobs.filter(function (j) {
            return j.state !== 'cancelled';
        });
        var total = live.reduce(function (s, j) {
            return s + j.file.size;
        }, 0);
        var sent = live.reduce(function (s, j) {
            return s + j.sent;
        }, 0);
        var rate = jobs.filter(function (j) {
            return j.state === 'uploading';
        }).reduce(function (s, j) {
            return s + (j.rate || 0);
        }, 0);
        var pending = activeJobs().length;
        if (!pending) return;
        var eta = rate > 0 ? (total - sent) / rate : 0;
        readoutEl.className = 'alert alert-secondary py-2 small';
        readoutEl.textContent = fmtSize(sent) + ' / ' + fmtSize(total) +
            (rate ? ', ' + fmtRate(rate) + ', ' + fmtEta(eta) + ' left' : '') +
            (pending > 1 ? ', ' + pending + ' files' : '');
    }

    function finishSweep() {
        if (!readoutEl || activeJobs().length) return;
        var done = jobs.filter(function (j) {
            return j.state === 'done';
        }).length;
        var failed = jobs.filter(function (j) {
            return j.state === 'failed';
        }).length;
        // A conversion that finished mid-upload deferred its full-page reload to here: pollOnce won't reload
        // while an upload is in flight, so it clears its timer and leaves reloadWhenDone set for us. Now that
        // nothing is uploading, honour it - but never when an upload failed, since a reload would wipe the
        // "tap to retry" affordance. The flag stays set in that case and the next clean sweep (e.g. after a
        // successful retry) consumes it, so the stale card is refreshed as soon as the errors are cleared.
        if (reloadWhenDone && !failed) {
            reloadWhenDone = false;
            setTimeout(function () {
                location.reload();
            }, 900);
            return;
        }
        if (done === 0 && failed === 0) {
            readoutEl.className = 'alert py-2 small d-none';
            return;
        }
        readoutEl.className = 'alert py-2 small ' + (failed ? 'alert-warning' : 'alert-success');
        readoutEl.textContent = failed
            ? done + ' uploaded, ' + failed + ' failed. Tap ↻ to retry'
            : 'Uploaded ' + done + ' file' + (done === 1 ? '' : 's') + '.';
        if (!failed) {
            jobs = jobs.filter(function (j) {
                return j.state !== 'done';
            });
            setTimeout(function () {
                if (!activeJobs().length && readoutEl) readoutEl.className = 'alert py-2 small d-none';
            }, 5000);
        }
        if (!failed && form.dataset.reload) setTimeout(function () {
            location.reload();
        }, 900);
    }

    function onUploaded(res) {
        if (!mineEl || !rowBase || !res || !res.slug) return;
        var section = document.getElementById('mineSection');
        if (section) section.classList.remove('d-none');
        // Insert the server-rendered row (the same _MineRow partial the page uses), then poll it to Ready -
        // no row markup is built here, so it always matches the server's icon/metadata.
        insertOrReplaceRow(res.slug, true);
    }

    // Fetch the server-rendered "your uploads" row for a slug and prepend it, or replace it if present.
    function insertOrReplaceRow(slug, prepend) {
        if (!mineEl || !rowBase) return;
        request('GET', rowBase + '/' + slug + '/row', {accept: 'text/html'})
            .then(function (r) { return r.ok ? r.text() : null; })
            .then(function (html) {
                if (!html) return;
                var tmp = document.createElement('template');
                tmp.innerHTML = html.trim();
                var fresh = tmp.content.firstElementChild;
                if (!fresh) return;
                var existing = mineEl.querySelector('.mine-item[data-slug="' + slug + '"]');
                if (existing) existing.replaceWith(fresh);
                else if (prepend) mineEl.insertBefore(fresh, mineEl.firstChild);
                startPolling();
            }).catch(function () { });
    }

    if (mineEl) {
        mineEl.addEventListener('submit', function (ev) {
            var f = ev.target.closest('.del-mine');
            if (!f) return;
            ev.preventDefault();
            window.Boxy.confirm('Delete this upload? This can’t be undone.').then(function (ok) {
                if (!ok) return;
                request('POST', f.action).then(function (r) {
                    if (r.ok) {
                        var li = f.closest('li');
                        if (li) li.remove();
                    } else {
                        showNotice('Couldn’t delete that upload. Please try again.');
                    }
                }).catch(function () {
                    showNotice('Couldn’t delete that upload - check your connection and try again.');
                });
            });
        });
        // Click a row (not its delete button) to preview it in the shared lightbox.
        mineEl.addEventListener('click', function (ev) {
            if (ev.target.closest('.del-mine')) return;
            var row = ev.target.closest('.mine-item');
            if (!row) return;
            var slug = row.getAttribute('data-slug');
            if (!slug) return;
            window.Boxy.lightbox(slug, row.getAttribute('data-kind') || 'video');
        });
    }

    // Anything on this page still converting - a drop-off row or a dashboard share card - gets watched.
    if (document.querySelector('[data-status="processing"][data-slug]')) {
        startPolling();
    }

    // Poll anything still converting until it goes terminal, so a row flips to its poster without a
    // reload. Deliberately not scoped to the drop-off list any more: the dashboard's share cards carry the
    // same two attributes and need this more, because an H.265 upload used to be a stream copy that
    // finished in seconds and is now a full encode that can run for minutes.
    var pollTimer = null;
    var reloadWhenDone = false;

    function startPolling() {
        if (pollTimer) return;
        pollTimer = setInterval(pollOnce, 2500);
        pollOnce();
    }

    function pollOnce() {
        var rows = document.querySelectorAll('[data-status="processing"][data-slug]');
        if (!rows.length) {
            clearInterval(pollTimer);
            pollTimer = null;
            // A page whose rows can't be swapped in place (the dashboard has no row endpoint) still has to
            // show the finished thumbnail, so once nothing is converting any more, reload once - but only
            // when nothing is uploading. finishSweep's reload carries the same activeJobs() guard; without
            // it here, a background conversion finishing mid-upload would tear the page down under an
            // in-flight chunked file. While an upload is active the reload is deferred to finishSweep, which
            // fires (and reloads) the moment that upload finishes.
            if (reloadWhenDone && !activeJobs().length) {
                reloadWhenDone = false;
                window.location.reload();
            }

            return;
        }
        Array.prototype.forEach.call(rows, function (row) {
            var slug = row.getAttribute('data-slug');
            if (!slug) return;
            request('GET', '/api/media/' + slug + '/status', {accept: 'application/json'})
                .then(function (r) {
                    return r.ok ? r.json() : null;
                })
                .then(function (s) {
                    if (!s || !(s.ready || s.failed)) return;
                    // Terminal: stop this row polling, then swap in the fresh server-rendered row - now
                    // with its poster/icon and full metadata - so no markup is patched here.
                    row.setAttribute('data-status', s.ready ? 'ready' : 'failed');
                    if (rowBase && row.classList.contains('mine-item')) {
                        insertOrReplaceRow(slug, false);
                    } else {
                        reloadWhenDone = true;
                    }
                }).catch(function () {
            });
        });
    }

    function detach(job, xhr) {
        var i = job.xhrs.indexOf(xhr);
        if (i >= 0) job.xhrs.splice(i, 1);
    }

    // Expose retry for failed rows (the cancel button becomes a dismiss; double-tap name to retry).
    queueEl.addEventListener('click', function (ev) {
        var li = ev.target.closest('.list-group-item');
        if (!li) return;
        var job = jobs.find(function (j) {
            return j.el === li;
        });
        if (job && job.state === 'failed' && !ev.target.closest('.job-cancel')) retry(job);
    });

    // An opaque routing token, not a secret. Prefer crypto for a good spread, but fall back to Math.random
    // when crypto is missing (very old/stripped engines, some privacy webviews) so that picking a file can
    // never throw here and silently drop the upload. Either way it is 32 hex chars, matching the server's
    // ^[a-zA-Z0-9]{8,64}$.
    function randomId() {
        try {
            if (window.crypto && crypto.getRandomValues && window.Uint8Array) {
                var b = new Uint8Array(16);
                crypto.getRandomValues(b);
                return Array.prototype.map.call(b, function (x) {
                    return ('0' + x.toString(16)).slice(-2);
                }).join('');
            }
        } catch (e) {
        }
        var s = '';
        for (var i = 0; i < 32; i++) s += Math.floor(Math.random() * 16).toString(16);
        return s;
    }

    function fmtSize(n) {
        if (n < 1024) return n + ' B';
        var u = ['KB', 'MB', 'GB', 'TB'], i = -1;
        do {
            n /= 1024;
            i++;
        } while (n >= 1024 && i < u.length - 1);
        return n.toFixed(n < 10 ? 1 : 0) + ' ' + u[i];
    }

    function fmtRate(bps) {
        return fmtSize(bps) + '/s';
    }

    function fmtEta(s) {
        s = Math.round(s);
        if (s < 60) return s + 's';
        var m = Math.floor(s / 60);
        if (m < 60) return m + 'm ' + (s % 60) + 's';
        return Math.floor(m / 60) + 'h ' + (m % 60) + 'm';
    }
})();
