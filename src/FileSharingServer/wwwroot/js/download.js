// Track active blob URLs so we can release them when the page is torn down.
// We must NOT revoke them immediately after triggering the download: the browser's
// download manager reads blob data asynchronously and writes it to disk, which can
// take many seconds for large files. Revoking too early cuts the data stream and
// the download stalls partway through.
var _blobUrls = [];

// Stores partial download state per file so interrupted downloads can be resumed.
var _downloadStates = {};

window.addEventListener('beforeunload', function () {
    for (var i = 0; i < _blobUrls.length; i++) {
        try { URL.revokeObjectURL(_blobUrls[i]); } catch (e) { }
    }
    _blobUrls = [];
});

window.downloadFile = function (url, fileName, dotNetHelper, resume) {
    return new Promise(function (resolve, reject) {
        var stateKey = url + '|' + fileName;
        var state = _downloadStates[stateKey];

        // If not resuming, discard any previous partial state.
        if (!resume && state) {
            if (state.xhr) { try { state.xhr.abort(); } catch (e) { } }
            state = null;
            delete _downloadStates[stateKey];
        }

        if (!state) {
            state = {
                chunks: [],
                bytesReceived: 0,
                totalSize: 0,
                xhr: null
            };
            _downloadStates[stateKey] = state;
        }

        var xhr = new XMLHttpRequest();
        state.xhr = xhr;

        // When resuming, ask the server for only the bytes we still need.
        if (resume && state.bytesReceived > 0) {
            xhr.open('GET', url, true);
            xhr.setRequestHeader('Range', 'bytes=' + state.bytesReceived + '-');
        } else {
            xhr.open('GET', url, true);
            if (resume) resume = false;
            state.chunks = [];
            state.bytesReceived = 0;
            state.totalSize = 0;
        }
        xhr.responseType = 'blob';

        var lastProgressTime = Date.now();
        var lastLoaded = 0;
        var speed = 0;
        var _lastProgressPromise = Promise.resolve();

        xhr.onprogress = function (e) {
            if (!e.lengthComputable) return;

            // On resume, loaded/total refer only to the partial response.
            // Convert them to absolute values for the whole file.
            var totalLoaded, totalSize;
            if (resume) {
                totalLoaded = state.bytesReceived + e.loaded;
                totalSize = state.totalSize;
            } else {
                totalLoaded = e.loaded;
                totalSize = e.total;
                state.totalSize = e.total;
            }

            var percent = parseFloat(((totalLoaded / totalSize) * 100).toFixed(2));

            // Measure speed over a rolling 300ms window.  The value persists
            // between reports so the speed display never blanks out.
            var now = Date.now();
            var elapsed = (now - lastProgressTime) / 1000;
            if (elapsed >= 0.3) {
                speed = Math.round((e.loaded - lastLoaded) / elapsed);
                lastProgressTime = now;
                lastLoaded = e.loaded;
            }

            // Throttle callbacks to ~4 per second, but always send the final one.
            if (now - (state._lastSendTime || 0) >= 250 || percent >= 99.99) {
                state._lastSendTime = now;
                _lastProgressPromise = dotNetHelper.invokeMethodAsync('OnProgress', percent, speed);
            }
        };

        xhr.onload = async function () {
            // If the server ignored our Range header (returned 200 instead of
            // 206), discard partial state and treat as a full download.
            if (resume && xhr.status === 200) {
                state.chunks = [];
                state.bytesReceived = 0;
                state.totalSize = 0;
                resume = false;
            }

            if (xhr.status === 200 || xhr.status === 206) {
                var responseBlob = xhr.response;

                if (resume && state.chunks.length > 0) {
                    // Convert the partial response to a Uint8Array and merge
                    // with previously accumulated chunks into a single Blob.
                    try {
                        var buffer = await responseBlob.arrayBuffer();
                        state.chunks.push(new Uint8Array(buffer));
                    } catch (e) {
                        state.chunks = [];
                        state.bytesReceived = 0;
                        delete _downloadStates[stateKey];
                        dotNetHelper.invokeMethodAsync('OnError', 'Failed to read partial data');
                        reject(new Error('Failed to read partial data'));
                        return;
                    }
                } else {
                    state.chunks.push(responseBlob);
                }

                var finalBlob = new Blob(state.chunks);

                // Trigger browser download via a temporary anchor element.
                var objectUrl = URL.createObjectURL(finalBlob);
                _blobUrls.push(objectUrl);

                var a = document.createElement('a');
                a.href = objectUrl;
                a.download = fileName;
                a.style.display = 'none';
                document.body.appendChild(a);
                a.click();
                document.body.removeChild(a);

                // Ensure the final OnProgress arrives before OnComplete.
                await _lastProgressPromise;

                delete _downloadStates[stateKey];
                state.xhr = null;
                dotNetHelper.invokeMethodAsync('OnComplete');
                resolve();
            } else {
                // Keep partial state so the user can resume later.
                state.xhr = null;
                dotNetHelper.invokeMethodAsync('OnError', 'HTTP ' + xhr.status);
                reject(new Error('HTTP ' + xhr.status));
            }
        };

        xhr.onerror = function () {
            state.xhr = null;
            dotNetHelper.invokeMethodAsync('OnError', 'Network error');
            reject(new Error('Network error'));
        };

        xhr.onabort = function () {
            state.xhr = null;
            dotNetHelper.invokeMethodAsync('OnError', 'Aborted');
            reject(new Error('Aborted'));
        };

        xhr.send();
    });
};

// Check whether a resumable partial download exists for the given file.
window.hasPartialDownload = function (url, fileName) {
    var stateKey = url + '|' + fileName;
    var state = _downloadStates[stateKey];
    return !!(state && state.bytesReceived > 0);
};

// Abort an in-flight download and discard all partial state.
window.abortDownload = function (url, fileName) {
    var stateKey = url + '|' + fileName;
    var state = _downloadStates[stateKey];
    if (state) {
        if (state.xhr) {
            try { state.xhr.abort(); } catch (e) { }
        }
        delete _downloadStates[stateKey];
    }
};
