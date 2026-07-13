const { contextBridge } = require('electron');

contextBridge.exposeInMainWorld('tiaApi', {
  baseUrl: 'http://127.0.0.1:5005',

  async fetch(path, opts = {}) {
    const url = this.baseUrl + path;
    const headers = { ...opts.headers };

    // POST/PUT 无 body 时 HttpListener 要求 Content-Length: 0
    if (opts.method && !opts.body) {
      headers['Content-Length'] = '0';
      delete headers['Content-Type'];
    } else if (opts.body && !headers['Content-Type']) {
      headers['Content-Type'] = 'application/json';
    }

    const res = await fetch(url, { ...opts, headers });
    const text = await res.text();
    try { return JSON.parse(text); } catch { return text; }
  },

  async health() { return this.fetch('/api/health'); },
});
