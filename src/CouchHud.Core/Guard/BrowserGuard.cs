namespace CouchHud.Core.Guard;

/// <summary>
/// The couch browser must not lead out of the couch: no Windows file or print dialogs from web pages and no
/// download windows. Pure rules and the script; the WebView2 wiring lives in the Shell project.
/// </summary>
public static class BrowserGuard
{
    /// <summary>The web APIs (and DOM calls) a page can use to make Windows show a file picker or a print dialog.</summary>
    public static readonly IReadOnlyList<string> NeutralizedApis =
        ["print", "showOpenFilePicker", "showSaveFilePicker", "showDirectoryPicker", "input[type=file]", "input[type=color]",
         "EyeDropper", "PaymentRequest", "share", "credentials", "requestPictureInPicture", "documentPictureInPicture"];

    /// <summary>
    /// Command line of the page's browser: no picture-in-picture of any kind. A PiP window floats above every window, also above the HUD
    /// and its full-screen strip, stays after the guide button, and Document PiP even takes key presses: a page could show a fake code
    /// prompt in it. (One --disable-features only: Chromium takes the last one of a switch.)
    /// </summary>
    public const string BrowserArguments =
        "--disable-blink-features=PictureInPictureAPI,DocumentPictureInPictureAPI " +
        "--disable-features=DocumentPictureInPictureAPI,MediaSessionEnterPictureInPicture";

    /// <summary>Downloads never get the browser's own UI. Unlocked they are asked about in the HUD (then into Downloads), locked they are cancelled.</summary>
    public static (bool Handled, bool Cancel) Download(bool allowed) => (true, !allowed);

    /// <summary>
    /// Runs in every document and frame before the page's own scripts. WebView2 adds it to each navigation of the page and of its frames
    /// (AddScriptToExecuteOnDocumentCreated); the initial document of a frame a script makes (about:blank, srcdoc) is not a navigation, so
    /// the script also patches every frame it can reach from its parent (the getters of contentWindow and contentDocument, a watch on added
    /// frames, the load event), and the Shell runs it again from each frame's ContentLoading event. This is the first layer only: the
    /// native layers (file chooser interception, <see cref="BrowserDialogRules"/>) are the ones that do not depend on the page.
    /// </summary>
    public const string Script = """
        (() => {
          const done = new WeakSet();
          // Patches one window (this one, or the realm of a same-origin frame); a window of another origin throws and is left to its own run.
          const guard = (w) => {
            try {
              const realm = w.Element.prototype;
              if (done.has(realm)) return;
              done.add(realm);
              // The same window is reached by more than one run of this script (the navigation script, the frame's ContentLoading run,
              // the parent's getter): a marker the page cannot remove or change (non-configurable, non-writable) makes the later runs do
              // nothing, so nothing is wrapped twice. Only a page that runs before the guard in that very window could set it first.
              const mark = Symbol.for('couchhud.guard');
              if (Object.prototype.hasOwnProperty.call(w, mark)) return;
              Object.defineProperty(w, mark, { value: true, configurable: false, writable: false, enumerable: false });
              apply(w);
            } catch { /* another origin, or gone: that document runs this script itself */ }
          };
          const apply = (w) => {
            const refuse = () => Promise.reject(new w.DOMException('Not allowed', 'NotAllowedError'));
            const lock = (name, value) => {
              try { Object.defineProperty(w, name, { value, configurable: false, writable: false }); } catch { /* already fixed */ }
            };
            lock('showOpenFilePicker', refuse);
            lock('showSaveFilePicker', refuse);
            lock('showDirectoryPicker', refuse);
            lock('print', () => {});
            // The eye dropper, payment sheets, the Windows share window and the passkey or password windows (WebAuthn, credential
            // manager) are windows of their own as well; none of them has a place on the couch.
            lock('EyeDropper', undefined);
            lock('PaymentRequest', undefined);
            const nav = (name, value) => {
              try { Object.defineProperty(w.Navigator.prototype, name, { value, configurable: false, writable: false }); } catch { /* already fixed */ }
            };
            nav('share', refuse);
            nav('canShare', () => false);
            nav('credentials', Object.freeze({ get: refuse, create: refuse, store: refuse, preventSilentAccess: () => Promise.resolve() }));
            if (w.PublicKeyCredential) lock('PublicKeyCredential', undefined);
            // Picture-in-picture floats above every window, the HUD and its full-screen strip too (Document PiP even takes keys): never.
            // (Also switched off on the browser's command line; this is the second lock.)
            lock('documentPictureInPicture', undefined);
            try {
              const video = w.HTMLVideoElement.prototype;
              Object.defineProperty(video, 'requestPictureInPicture', { value: refuse, configurable: false, writable: false });
              Object.defineProperty(video, 'disablePictureInPicture', { get: () => true, set: () => {}, configurable: false });
            } catch { /* already fixed */ }
            try { Object.defineProperty(w.Document.prototype, 'pictureInPictureEnabled', { get: () => false, configurable: false }); } catch { /* already fixed */ }
            // A file input opens the Windows file dialog, a color input the Windows color dialog: both are windows of their own
            // that take the controller away from the couch.
            const isFile = (e) => !!e && e.nodeType === 1 && e.localName === 'input' && (e.type === 'file' || e.type === 'color');
            // The whole composed path, not event.target: inside a shadow root the target is retargeted to the host, and the path is what
            // still lists the input (and a label around it). Closed shadow roots are not in the path of an outside listener: attachShadow
            // below puts the same listener on every root a script makes.
            const blocked = (e) => {
              let path;
              try { path = e.composedPath ? e.composedPath() : [e.target]; } catch { path = [e.target]; }
              for (const n of path) {
                if (isFile(n)) return true;
                if (n && n.nodeType === 1 && n.localName === 'label' && isFile(n.control)) return true;
              }
              return false;
            };
            const onClick = (e) => {
              if (blocked(e)) {
                e.preventDefault();
                e.stopImmediatePropagation();
              }
            };
            // Files dragged in from the desktop are never taken (the web view refuses external drops as well).
            const onDrag = (e) => {
              const types = e.dataTransfer && e.dataTransfer.types;
              if (types && Array.prototype.indexOf.call(types, 'Files') >= 0) {
                e.preventDefault();
                e.stopImmediatePropagation();
              }
            };
            const listen = (target) => {
              target.addEventListener('click', onClick, true);
              for (const type of ['dragenter', 'dragover', 'drop']) target.addEventListener(type, onDrag, true);
            };
            listen(w);
            try {
              const attach = w.Element.prototype.attachShadow;
              Object.defineProperty(w.Element.prototype, 'attachShadow', {
                value: function (init) {
                  const root = attach.call(this, init);
                  try { listen(root); } catch { /* the root is returned anyway */ }
                  return root;
                },
                configurable: false,
                writable: false
              });
            } catch { /* already fixed */ }
            const click = w.HTMLInputElement.prototype.click;
            w.HTMLInputElement.prototype.click = function () { return isFile(this) ? undefined : click.call(this); };
            const picker = w.HTMLInputElement.prototype.showPicker;
            if (picker) w.HTMLInputElement.prototype.showPicker = function () { return isFile(this) ? undefined : picker.call(this); };
            // Frames this window can reach: the first read of contentWindow or contentDocument patches the frame's own realm, so a
            // script-made about:blank or srcdoc frame (which no navigation script ran in) is guarded before the page can use it.
            const viaGetter = (proto, prop, pick) => {
              try {
                const d = Object.getOwnPropertyDescriptor(proto, prop);
                if (!d || !d.get) return;
                Object.defineProperty(proto, prop, {
                  get() { const r = d.get.call(this); try { guard(pick(r)); } catch { /* not reachable */ } return r; },
                  set: d.set, enumerable: d.enumerable, configurable: false
                });
              } catch { /* already fixed */ }
            };
            for (const kind of ['HTMLIFrameElement', 'HTMLFrameElement', 'HTMLObjectElement']) {
              if (!w[kind]) continue;
              viaGetter(w[kind].prototype, 'contentWindow', (r) => r);
              viaGetter(w[kind].prototype, 'contentDocument', (r) => r && r.defaultView);
            }
            const touch = (n) => {
              if (n && n.nodeType === 1) {
                if (n.localName === 'iframe' || n.localName === 'frame' || n.localName === 'object') guard(n.contentWindow);
                if (n.querySelectorAll) for (const f of n.querySelectorAll('iframe,frame,object')) guard(f.contentWindow);
              }
            };
            try {
              new w.MutationObserver((records) => { for (const r of records) for (const n of r.addedNodes) touch(n); }).observe(w.document, { childList: true, subtree: true });
            } catch { /* no document yet */ }
            // A frame that loaded something new: its realm is a new one.
            w.addEventListener('load', (e) => touch(e.target), true);
          };
          guard(window);
        })();
        """;
}
