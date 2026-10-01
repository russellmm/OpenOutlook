using System.Globalization;
using Avalonia.Controls;

namespace OpenOutlook.Desktop;

/// <summary>Visual HTML editing in a native browser window with an in-page move handle.</summary>
internal static class RichComposeEditor
{
    private const int MaxLength = 512 * 1024;
    private static readonly List<NativeWebDialog> OpenDialogs = [];

    public static void Open(Window owner, string html, Action<string> apply, Action<string> error)
    {
        try
        {
            if (html.Length > MaxLength) throw new ArgumentException("Message body is too large to edit.");
            var initial = ComposeHtml.Sanitize(html);
            var dialog = new NativeWebDialog { Title = "Visual editor — OpenOutlook", CanUserResize = true };
            var screen = owner.Screens.ScreenFromWindow(owner) ?? owner.Screens.Primary;
            var scale = screen is { Scaling: > 0 } ? screen.Scaling : 1;
            // Avalonia's window Position/WorkingArea are physical pixels, while the
            // native browser dialog uses scaled desktop coordinates for Move/Resize.
            var area = screen?.WorkingArea;
            var width = area is { } bounds
                ? Math.Min(1020, Math.Max(480, (int)(bounds.Width / scale * 0.82))) : 1020;
            var height = area is { } usable
                ? Math.Min(760, Math.Max(360, (int)(usable.Height / scale * 0.82))) : 760;
            var x = area is { } horizontal
                ? (int)Math.Round((horizontal.X + horizontal.Width / 2.0) / scale - width / 2.0)
                : owner.Position.X;
            var y = area is { } vertical
                ? (int)Math.Round((vertical.Y + vertical.Height / 2.0) / scale - height / 2.0)
                : owner.Position.Y;
            var closing = false;
            var applying = false;
            dialog.NavigationStarted += (_, args) =>
            {
                if (args.Request?.Scheme != "about") args.Cancel = true;
            };
            dialog.NewWindowRequested += (_, args) => args.Handled = true;
            dialog.WebMessageReceived += (_, args) =>
            {
                if (closing) return;
                var signal = args.Body;
                if (signal == "apply")
                {
                    if (!applying) { applying = true; _ = ApplyAsync(); }
                }
                else if (signal == "cancel") dialog.Close();
                else if (signal?.StartsWith("move:", StringComparison.Ordinal) == true &&
                         TryMove(signal.AsSpan(5), out var targetX, out var targetY))
                {
                    // Follow the pointer immediately; the window manager controls
                    // movement across every monitor in the virtual desktop.
                    x = targetX;
                    y = targetY;
                    dialog.Move(x, y);
                }
            };
            dialog.Closing += (_, _) =>
            {
                closing = true;
                owner.Closed -= OwnerClosed;
                OpenDialogs.Remove(dialog);
            };
            OpenDialogs.Add(dialog);
            // An owned native web dialog is treated as an attached modal dialog by
            // GNOME/Mutter, which constrains horizontal movement to its parent.
            dialog.Show();
            dialog.Resize(width, height);
            dialog.Move(x, y);
            dialog.NavigateToString(Document(initial));
            owner.Closed += OwnerClosed;
            return;

            void OwnerClosed(object? sender, EventArgs args) => dialog.Close();

            async Task ApplyAsync()
            {
                try
                {
                    var edited = await dialog.InvokeScript("window.openoutlookReadHtml()");
                    if (edited is null || edited.Length > MaxLength)
                        throw new ArgumentException("The formatted body is too large or invalid.");
                    apply(ComposeHtml.Sanitize(edited));
                    dialog.Close();
                }
                catch (Exception)
                {
                    applying = false;
                    error("Could not read the formatted message. Your draft is unchanged.");
                }
            }
        }
        catch (Exception) { error("The visual editor is unavailable. Your draft is unchanged."); }
    }

    private static bool TryMove(ReadOnlySpan<char> signal, out int targetX, out int targetY)
    {
        targetX = targetY = 0;
        var separator = signal.IndexOf(',');
        if (separator < 1) return false;
        return int.TryParse(signal[..separator], NumberStyles.AllowLeadingSign,
                   CultureInfo.InvariantCulture, out targetX) &&
               int.TryParse(signal[(separator + 1)..], NumberStyles.AllowLeadingSign,
                   CultureInfo.InvariantCulture, out targetY) &&
               // These are desktop positions from the embedded browser, not deltas.
               Math.Abs((long)targetX) <= 131_072 && Math.Abs((long)targetY) <= 131_072;
    }

    private static string Document(string body) => """
        <!doctype html><html><head><meta charset="utf-8"><meta http-equiv="Content-Security-Policy"
        content="default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; img-src https: data:; base-uri 'none'; form-action 'none'">
        <style>
        *{box-sizing:border-box}body{font:14px sans-serif;margin:0;background:#eef1f5;color:#182334}
        #titlebar{display:flex;align-items:center;background:#204d79;color:white;gap:8px;padding:4px 8px}
        #drag{flex:1;padding:8px;font-weight:600;cursor:move;user-select:none;touch-action:none}
        #toolbar{position:sticky;top:0;background:#f6f8fb;border-bottom:1px solid #aab7c5;padding:8px;z-index:2}
        .row{display:flex;align-items:center;flex-wrap:wrap;gap:5px;margin:3px 0}
        button,select,input[type=color]{height:31px;border:1px solid #a9b5c3;background:white;border-radius:4px;padding:3px 8px;color:#182334}
        button:hover{background:#e0ebf7}button:focus-visible,select:focus-visible{outline:2px solid #1269ae}
        .spacer{flex:1}.label{font-size:12px;color:#52647a;margin:0 4px}
        #editor{min-height:calc(100vh - 150px);margin:14px;padding:18px;background:white;border:1px solid #aab7c5;outline:none;overflow:auto;font:16px Arial,sans-serif;line-height:1.4}
        #editor:focus{border-color:#1269ae}
        img{max-width:100%}table{border-collapse:collapse}td,th{border:1px solid #aaa;padding:5px}
        </style></head><body>
        <div id="titlebar"><div id="drag">Visual editor — drag here to move this window</div>
          <button onclick="go('cancel')">Cancel</button><button onclick="go('apply')" style="background:#dcecfb;font-weight:600">Apply to message</button></div>
        <div id="toolbar">
          <div class="row">
            <button title="Undo" onclick="cmd('undo')">↶ Undo</button><button title="Redo" onclick="cmd('redo')">↷ Redo</button>
            <span class="label">Font</span><select onchange="cmd('fontName',this.value);this.selectedIndex=0"><option>Choose font</option><option>Arial</option><option>Georgia</option><option>Times New Roman</option><option>Courier New</option></select>
            <select onchange="cmd('fontSize',this.value);this.selectedIndex=0"><option>Size</option><option value="2">Small</option><option value="3">Normal</option><option value="4">Large</option><option value="5">Very large</option></select>
            <button title="Bold (Ctrl+B)" onclick="cmd('bold')"><b>B</b></button><button title="Italic (Ctrl+I)" onclick="cmd('italic')"><i>I</i></button><button title="Underline (Ctrl+U)" onclick="cmd('underline')"><u>U</u></button><button title="Strikethrough" onclick="cmd('strikeThrough')"><s>S</s></button>
            <span class="label">Text</span><input type="color" value="#182334" title="Text color" onchange="cmd('foreColor',this.value)">
            <span class="label">Highlight</span><input type="color" value="#ffff99" title="Highlight color" onchange="cmd('hiliteColor',this.value)">
          </div>
          <div class="row">
            <button title="Align left" onclick="cmd('justifyLeft')">Left</button><button title="Center" onclick="cmd('justifyCenter')">Center</button><button title="Align right" onclick="cmd('justifyRight')">Right</button>
            <button title="Bulleted list" onclick="cmd('insertUnorderedList')">Bullets</button><button title="Numbered list" onclick="cmd('insertOrderedList')">Numbers</button>
            <button title="Decrease indent" onclick="cmd('outdent')">Outdent</button><button title="Increase indent" onclick="cmd('indent')">Indent</button>
            <button onclick="insertLink()">Link</button><button onclick="cmd('unlink')">Unlink</button><button onclick="insertImage()">Image URL</button><button onclick="insertTable()">Table</button>
            <button onclick="cmd('removeFormat')">Clear formatting</button>
          </div>
        </div>
        <div id="editor" contenteditable="true">
        """ + body + """
        </div>
        <script>
        const editor=document.getElementById('editor');
        function post(signal){
          if(window.webkit?.messageHandlers?.postAvWebViewMessage)
            window.webkit.messageHandlers.postAvWebViewMessage.postMessage(signal);
          else if(typeof window.invokeCSharpAction==='function')window.invokeCSharpAction(signal);
          else if(window.chrome?.webview?.postMessage)window.chrome.webview.postMessage(signal);
        }
        function go(action){post(action);}
        function cmd(name,value){editor.focus();document.execCommand(name,false,value||null);}
        window.openoutlookReadHtml=()=>editor.innerHTML;
        function insertLink(){let url=prompt('Enter an http, https or mailto link:');if(!url)return;try{let parsed=new URL(url);if(!['http:','https:','mailto:'].includes(parsed.protocol))return;}catch{return;}cmd('createLink',url);}
        function insertImage(){let url=prompt('Enter an HTTPS image URL:');if(!url)return;try{let parsed=new URL(url);if(parsed.protocol!=='https:')return;}catch{return;}cmd('insertImage',url);}
        function insertTable(){let rows=Number(prompt('Rows (1–10):','2')),cols=Number(prompt('Columns (1–8):','2'));if(!Number.isInteger(rows)||!Number.isInteger(cols)||rows<1||rows>10||cols<1||cols>8)return;
          let html='<table><tbody>';for(let r=0;r<rows;r++){html+='<tr>';for(let c=0;c<cols;c++)html+='<td>&nbsp;</td>';html+='</tr>';}cmd('insertHTML',html+'</tbody></table>');}
        const drag=document.getElementById('drag');let dragging=false,startClientX=0,startClientY=0;
        let moveQueued=false,targetX=0,targetY=0;
        function queueMove(x,y){targetX=x;targetY=y;if(moveQueued)return;moveQueued=true;
          requestAnimationFrame(()=>{moveQueued=false;post('move:'+targetX+','+targetY);});}
        // WebKitGTK can report stale PointerEvent.screenX/Y after this window moves.
        // client coordinates plus the current window origin stay stable on screen.
        drag.addEventListener('pointerdown',e=>{dragging=true;startClientX=e.clientX;startClientY=e.clientY;drag.setPointerCapture(e.pointerId);e.preventDefault();});
        drag.addEventListener('pointerup',()=>dragging=false);
        drag.addEventListener('pointercancel',()=>dragging=false);
        drag.addEventListener('pointermove',e=>{if(!dragging)return;
          queueMove(Math.round(e.clientX+window.screenX-startClientX),Math.round(e.clientY+window.screenY-startClientY));});
        </script></body></html>
        """;
}
