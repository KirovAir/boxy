/* Boxy chunked uploader.
 * Splits each file into fixed-size chunks and uploads several in parallel (fast on high-latency
 * links and multi-GB files), with per-chunk retry and per-file cancel. Chunks are stored as
 * separate parts server-side and concatenated on completion. Live per-file + aggregate progress
 * with transfer rate and ETA. Falls back to a plain multipart form post when JS is off. */
(function () {
    var form = document.getElementById('uploadForm');
    if (!form || !window.XMLHttpRequest) return;

    var CHUNK = 16 * 1024 * 1024; // 16 MB - few requests, safe under the 100 MB proxy/tunnel limit
    var CONCURRENCY = 6;          // chunks in flight per file - more parallelism to fill a high-latency pipe
    var RETRIES = 4;

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

    function fetchExisting(uploadId) {
        return fetch(chunksUrl + '?uploadId=' + encodeURIComponent(uploadId), {headers: {'Accept': 'application/json'}})
            .then(function (r) {
                return r.ok ? r.json() : {have: []};
            })
            .then(function (d) {
                return (d && d.have) || [];
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
        var added = 0, tooBig = [];
        for (var i = 0; i < fileList.length; i++) {
            var f = fileList[i];
            if (!f.size) continue;
            // Reject over-limit files up front, so a huge file never starts uploading.
            if (maxBytes > 0 && f.size > maxBytes) {
                tooBig.push(f.name);
                continue;
            }
            var prev = recallUpload(f);   // same file picked again → resume its server-side parts
            var job = {
                file: f, sent: 0, state: 'queued', el: null, rate: 0, lastT: 0, lastB: 0,
                xhrs: [], loaded: {}, cancelled: false,
                uploadId: prev ? prev.uploadId : randomId(),
                total: Math.max(1, Math.ceil(f.size / CHUNK)),
                resume: !!prev
            };
            jobs.push(job);
            rememberUpload(job);
            renderJob(job);
            added++;
        }
        if (tooBig.length && readoutEl) {
            var mb = Math.round(maxBytes / 1048576);
            readoutEl.className = 'alert alert-warning py-2 small';
            readoutEl.textContent = (tooBig.length === 1 ? '“' + tooBig[0] + '” is' : tooBig.length + ' files are')
                + ' over the ' + mb + ' MB limit and ' + (tooBig.length === 1 ? 'was' : 'were') + ' skipped.';
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
                fetchExisting(job.uploadId).then(function (list) {
                    list.forEach(function (idx) {
                        if (idx >= 0 && idx < job.total) {
                            have[idx] = true;
                            job.loaded[idx] = chunkBytes(job, idx);
                        }
                    });
                    onProgress(job);   // reflect the already-uploaded bytes in the bar
                    begin();
                }, begin);             // if the query fails, just upload everything
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

    function sendChunk(job, idx) {
        var start = idx * CHUNK;
        var blob = job.file.slice(start, Math.min(start + CHUNK, job.file.size));
        return attempt(RETRIES);

        function attempt(left) {
            return new Promise(function (resolve, reject) {
                if (job.cancelled) {
                    resolve();
                    return;
                }
                var xhr = new XMLHttpRequest();
                xhr.open('POST', chunkUrl + '?uploadId=' + job.uploadId + '&index=' + idx);
                xhr.setRequestHeader('Content-Type', 'application/octet-stream');
                job.xhrs.push(xhr);
                xhr.upload.onprogress = function (e) {
                    if (e.lengthComputable) {
                        job.loaded[idx] = e.loaded;
                        onProgress(job);
                    }
                };
                xhr.onload = function () {
                    detach(job, xhr);
                    if (job.cancelled) {
                        resolve();
                        return;
                    }
                    if (xhr.status >= 200 && xhr.status < 300) {
                        job.loaded[idx] = blob.size;
                        onProgress(job);
                        resolve();
                    } else fail();
                };
                xhr.onerror = function () {
                    detach(job, xhr);
                    fail();
                };
                xhr.onabort = function () {
                    detach(job, xhr);
                    resolve();
                };
                xhr.send(blob);

                function fail() {
                    if (job.cancelled) {
                        resolve();
                        return;
                    }
                    if (left > 0) {
                        var backoff = 500 * Math.pow(2, RETRIES - left); // exponential: 0.5s, 1s, 2s, 4s
                        setTimeout(function () {
                            attempt(left - 1).then(resolve, reject);
                        }, backoff);
                    } else reject(new Error('chunk ' + idx + ' failed'));
                }
            });
        }
    }

    function complete(job) {
        return new Promise(function (resolve) {
            if (job.cancelled) {
                resolve();
                return;
            }
            var xhr = new XMLHttpRequest();
            var keep = form.querySelector('input[name=keepOriginal]:checked') ? '&keepOriginal=true' : '';
            xhr.open('POST', completeUrl + '?uploadId=' + job.uploadId + '&total=' + job.total + '&name=' + encodeURIComponent(job.file.name) + keep);
            xhr.onload = function () {
                if (xhr.status >= 200 && xhr.status < 300) {
                    forgetUpload(job);   // done - drop the resume memory
                    job.sent = job.file.size;
                    job.state = 'done';
                    removeRow(job);
                    try {
                        onUploaded(JSON.parse(xhr.responseText), job.file.type);
                    } catch (e) {
                    }
                } else {
                    job.state = 'failed';
                    updateJob(job);
                }
                resolve();
            };
            xhr.onerror = function () {
                job.state = 'failed';
                updateJob(job);
                resolve();
            };
            xhr.send();
        });
    }

    function cancel(job) {
        if (job.state === 'done') return;
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
                fetch(abortUrl + '?uploadId=' + job.uploadId, {method: 'POST'});
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
        el.querySelector('.j-rate').textContent = job.state === 'uploading' && job.rate ? fmtRate(job.rate) : '';
        var x = el.querySelector('.job-cancel');
        if (x) x.title = job.state === 'failed' ? 'Dismiss' : 'Cancel';
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
        fetch(rowBase + '/' + slug + '/row', {headers: {'Accept': 'text/html'}})
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
                fetch(f.action, {method: 'POST'}).then(function (r) {
                    if (r.ok) {
                        var li = f.closest('li');
                        if (li) li.remove();
                    }
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
        startPolling();
    }

    // Poll processing rows until they flip to a thumbnail - no reload needed.
    var pollTimer = null;

    function startPolling() {
        if (pollTimer) return;
        pollTimer = setInterval(pollOnce, 2500);
        pollOnce();
    }

    function pollOnce() {
        var rows = mineEl ? mineEl.querySelectorAll('.mine-item[data-status="processing"]') : [];
        if (!rows.length) {
            clearInterval(pollTimer);
            pollTimer = null;
            return;
        }
        Array.prototype.forEach.call(rows, function (row) {
            var slug = row.getAttribute('data-slug');
            if (!slug) return;
            fetch('/api/media/' + slug + '/status', {headers: {'Accept': 'application/json'}})
                .then(function (r) {
                    return r.ok ? r.json() : null;
                })
                .then(function (s) {
                    if (!s || !(s.ready || s.failed)) return;
                    // Terminal: stop this row polling, then swap in the fresh server-rendered row - now
                    // with its poster/icon and full metadata - so no markup is patched here.
                    row.setAttribute('data-status', s.ready ? 'ready' : 'failed');
                    insertOrReplaceRow(slug, false);
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

    function randomId() {
        var b = new Uint8Array(16);
        crypto.getRandomValues(b);
        return Array.prototype.map.call(b, function (x) {
            return ('0' + x.toString(16)).slice(-2);
        }).join('');
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
