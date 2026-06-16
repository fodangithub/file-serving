// Track active blob URLs so we can release them when the page is torn down.
// We must NOT revoke them immediately after triggering the download: the browser's
// download manager reads blob data asynchronously and writes it to disk, which can
// take many seconds for large files. Revoking too early cuts the data stream and
// the download stalls partway through.
var _blobUrls = [];

window.addEventListener('beforeunload', function () {
    for (var i = 0; i < _blobUrls.length; i++) {
        try { URL.revokeObjectURL(_blobUrls[i]); } catch (e) { }
    }
    _blobUrls = [];
});

window.downloadFile = function (url, fileName, dotNetHelper) {
    return new Promise(function (resolve, reject) {
        var xhr = new XMLHttpRequest();
        xhr.open('GET', url, true);
        xhr.responseType = 'blob';

        var lastPercent = -1;
        var lastUpdate = 0;
        var throttleMs = 300;

        xhr.onprogress = function (e) {
            if (!e.lengthComputable) return;
            var percent = Math.round((e.loaded / e.total) * 100);
            var now = Date.now();
            if (percent !== lastPercent && (now - lastUpdate >= throttleMs || percent >= 99)) {
                lastPercent = percent;
                lastUpdate = now;
                dotNetHelper.invokeMethodAsync('OnProgress', percent);
            }
        };

        xhr.onload = function () {
            if (xhr.status === 200) {
                var blob = xhr.response;
                var objectUrl = URL.createObjectURL(blob);
                _blobUrls.push(objectUrl);

                var a = document.createElement('a');
                a.href = objectUrl;
                a.download = fileName;
                a.style.display = 'none';
                document.body.appendChild(a);
                a.click();
                document.body.removeChild(a);

                // NOTE: deliberately no setTimeout(revokeObjectURL, …) here.
                // The browser download manager reads the blob asynchronously;
                // revoking early would stall the write-to-disk step.
                // Cleanup happens on beforeunload instead.

                dotNetHelper.invokeMethodAsync('OnComplete');
                resolve();
            } else {
                dotNetHelper.invokeMethodAsync('OnError', 'HTTP ' + xhr.status);
                reject(new Error('HTTP ' + xhr.status));
            }
        };

        xhr.onerror = function () {
            dotNetHelper.invokeMethodAsync('OnError', 'Network error');
            reject(new Error('Network error'));
        };

        xhr.send();
    });
};
