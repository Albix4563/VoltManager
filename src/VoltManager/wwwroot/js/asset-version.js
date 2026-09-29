(function () {
    'use strict';
    window.VM_ASSET_VERSION = '20260929-1';
    window.VM_ASSET_URL = function (path) {
        return path + '?v=' + encodeURIComponent(window.VM_ASSET_VERSION);
    };
})();
