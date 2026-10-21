// The host bridge (WebView2) or, in a plain browser, a mock backend for previews.
if (window.chrome?.webview) await import('../bridge.js');
else await import('../mock-api.js');

export const api = window.couchhud;
