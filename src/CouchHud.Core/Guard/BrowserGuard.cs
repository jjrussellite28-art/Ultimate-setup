namespace CouchHud.Core.Guard;

/// <summary>
/// The couch browser must not lead out of the couch: no Windows file or print dialogs from web pages and no
/// download windows. Pure rules and the script; the WebView2 wiring lives in the Shell project.
/// </summary>
public static class BrowserGuard
{
    /// <summary>The web APIs (and DOM calls) a page can use to make Windows show a file picker or a print dialog.</summary>
    public static readonly IReadOnlyList<string> NeutralizedApis =
        ["print", "showOpenFilePicker", "showSaveFilePicker", "showDirectoryPicker", "input[type=file]"];

    /// <summary>Downloads never get the browser's own UI. Unlocked they go to the default folder, locked they are cancelled.</summary>
    public static (bool Handled, bool Cancel) Download(bool allowed) => (true, !allowed);

    /// <summary>Runs in every document and frame before the page's own scripts.</summary>
    public const string Script = """
        (() => {
          const refuse = () => Promise.reject(new DOMException('Not allowed', 'NotAllowedError'));
          const lock = (name, value) => {
            try { Object.defineProperty(window, name, { value, configurable: false, writable: false }); } catch { /* already fixed */ }
          };
          lock('showOpenFilePicker', refuse);
          lock('showSaveFilePicker', refuse);
          lock('showDirectoryPicker', refuse);
          lock('print', () => {});
          const isFile = (e) => e instanceof HTMLInputElement && e.type === 'file';
          // A click on a file input, or on the label of one, would open the Windows file dialog.
          document.addEventListener('click', (e) => {
            const t = e.target;
            const label = t && t.closest ? t.closest('label') : null;
            if (isFile(t) || (label && isFile(label.control))) {
              e.preventDefault();
              e.stopImmediatePropagation();
            }
          }, true);
          const click = HTMLInputElement.prototype.click;
          HTMLInputElement.prototype.click = function () { return isFile(this) ? undefined : click.call(this); };
          const picker = HTMLInputElement.prototype.showPicker;
          if (picker) HTMLInputElement.prototype.showPicker = function () { return isFile(this) ? undefined : picker.call(this); };
        })();
        """;
}
