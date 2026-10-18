using System.Text.Json;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace OpenOutlook.Desktop;

/// <summary>What the editor reports about the text at the cursor, so the ribbon can show it (bold pressed, font and size in their boxes, ...).</summary>
public sealed record HtmlEditorState(bool Bold = false, bool Italic = false, bool Underline = false, bool Strike = false, bool Subscript = false,
    bool Superscript = false, bool Bullets = false, bool Numbering = false, string Align = "left", string Font = "", double Size = 12, string Block = "div",
    bool Marks = false);

/// <summary>
/// The HTML body of the compose window. The real one (<see cref="WebHtmlEditor"/>) is an editable page in the embedded web view, edited in place
/// like Outlook's message body; the ribbon drives it through <see cref="Run"/>. Where the embedded view cannot draw (Wayland, no WebKitGTK, the
/// headless test host) <see cref="PreviewHtmlEditor"/> shows a read-only preview and edits in a separate window.
/// </summary>
internal interface IHtmlEditorHost
{
    Control View { get; }
    /// <summary>False for the preview fallback: the ribbon's formatting commands cannot reach it.</summary>
    bool EditsInPlace { get; }
    void Load(string html);
    /// <summary>One editor command (see the command list in <see cref="HtmlEditorDocument"/>); queued until the page is ready.</summary>
    void Run(string command, string? value = null);
    /// <summary>The body as the page has it now (not sanitized), or null when the page could not answer.</summary>
    Task<string?> ReadHtmlAsync();
    /// <summary>Find the next match in the body; false when there is none.</summary>
    Task<bool> FindAsync(string text);
    /// <summary>The selected text (for Cut / Copy on the ribbon).</summary>
    Task<string?> SelectedTextAsync();
    void FocusBody();
    /// <summary>The user changed the body (typing, a command, a paste); carries the page's HTML (sent a moment after the last change).</summary>
    event Action<string>? HtmlChanged;
    /// <summary>Raised at once on every change, before <see cref="HtmlChanged"/>: the draft has unsaved changes.</summary>
    event Action? Edited;
    event Action<HtmlEditorState>? StateChanged;
}

/// <summary>The default font of new messages and the wrapper that carries it to the recipients.</summary>
public static partial class HtmlEditorDocument
{
    public const string DefaultFont = "Aptos";
    public const double DefaultSizePt = 12;
    public const string FontStack = "Aptos,Calibri,Arial,Helvetica,sans-serif";
    private const string WrapperClass = "oo-compose";

    /// <summary>The sizes in the Font Size box and the steps of Grow Font / Shrink Font, as in Outlook.</summary>
    public static readonly double[] Sizes = [8, 9, 10, 10.5, 11, 12, 14, 16, 18, 20, 22, 24, 26, 28, 36, 48, 72];

    /// <summary>
    /// The body as it is sent: the editor's content inside one div that sets the default font, because the editor's own page style does not
    /// travel with the message. Blank content stays empty.
    /// </summary>
    public static string Wrap(string inner) => IsBlank(inner) ? "" :
        $"<div class=\"{WrapperClass}\" style=\"font-family:{FontStack};font-size:{DefaultSizePt}pt\">{inner}</div>";

    /// <summary>Takes the wrapper of <see cref="Wrap"/> off again (draft reopened), so it does not nest on every save.</summary>
    public static string Unwrap(string html)
    {
        var trimmed = html.Trim();
        var match = WrapperRegex().Match(trimmed);
        return match.Success && trimmed.EndsWith("</div>", StringComparison.OrdinalIgnoreCase) && CountDivs(trimmed) == 0
            ? trimmed[match.Length..^"</div>".Length]
            : html;
    }

    /// <summary>Balance of div tags inside the outer one: the wrapper is only taken off when the last &lt;/div&gt; is its own.</summary>
    private static int CountDivs(string html)
    {
        var inner = html[(html.IndexOf('>') + 1)..^"</div>".Length];
        var depth = 0;
        foreach (Match m in DivTagRegex().Matches(inner))
        {
            depth += m.Value.StartsWith("</", StringComparison.Ordinal) ? -1 : 1;
            if (depth < 0) return -1;
        }
        return depth;
    }

    /// <summary>No text and no picture, table or line: what an empty editor holds (&lt;div&gt;&lt;br&gt;&lt;/div&gt;, &amp;nbsp;).</summary>
    public static bool IsBlank(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return true;
        if (Regex.IsMatch(html, "<(img|table|hr)\\b", RegexOptions.IgnoreCase)) return false;
        var text = System.Net.WebUtility.HtmlDecode(Regex.Replace(html, "<[^>]*>", ""));
        return string.IsNullOrWhiteSpace(text.Replace(' ', ' '));
    }

    /// <summary>Plain text to the editor's HTML: one div per line, empty lines kept.</summary>
    public static string FromPlainText(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var lines = text.Replace("\r\n", "\n").Split('\n');
        return string.Concat(lines.Select(l => l.Length == 0 ? "<div><br></div>" : "<div>" + System.Net.WebUtility.HtmlEncode(l) + "</div>"));
    }

    /// <summary>The value of an InvokeScript result: WebView2 returns JSON (a quoted string), WebKitGTK may return the bare text.</summary>
    public static string? ScriptString(string? result)
    {
        if (result is null || result == "null" || result == "undefined") return null;
        if (result.Length >= 2 && result[0] == '"')
        {
            try { return JsonSerializer.Deserialize<string>(result); }
            catch (JsonException) { return result; }
        }
        return result;
    }

    public static HtmlEditorState? ParseState(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            bool B(string n) => r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.True;
            string S(string n, string d) => r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? d : d;
            var size = r.TryGetProperty("size", out var sz) && sz.ValueKind == JsonValueKind.Number ? sz.GetDouble() : DefaultSizePt;
            return new HtmlEditorState(B("b"), B("i"), B("u"), B("s"), B("sub"), B("sup"), B("ul"), B("ol"), S("al", "left"), S("font", ""), size, S("block", "div"), B("marks"));
        }
        catch (JsonException) { return null; }
    }

    /// <summary>The script call for one editor command; the arguments are JSON-encoded, so no text can break out of the string.</summary>
    public static string Call(string command, string? value) =>
        "window.oo&&window.oo.run(" + JsonSerializer.Serialize(command) + "," + (value is null ? "null" : JsonSerializer.Serialize(value)) + ")";

    public static string SetCall(string html) => "window.oo&&window.oo.set(" + JsonSerializer.Serialize(html) + ")";

    [GeneratedRegex("^<div class=\"oo-compose\"[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex WrapperRegex();
    [GeneratedRegex("</?div\\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex DivTagRegex();

    /// <summary>
    /// The editor page. Commands for <c>oo.run(name, value)</c>: bold, italic, underline, strikeThrough, subscript, superscript, fontName (family),
    /// fontSize (points), grow, shrink, foreColor (#rrggbb or "auto"), hiliteColor (#rrggbb or "none"), justifyLeft / Center / Right / Full,
    /// bullets / numbering (optional list-style-type; "none" ends the list), indent, outdent, clear, changeCase (sentence, lower, upper,
    /// capitalize, toggle), lineSpacing (1, 1.15, 1.5, 2, 2.5, 3), shading (#rrggbb or "none"), style (normal, h1, h2, h3, title, quote, code),
    /// link (url, with the text to show after a newline), unlink, table ("rows,columns"), line, image (a data: picture), insertText, delete, selectAll, undo, redo, zoom (factor), marks.
    /// The page posts "dirty" at once and "html:" + its body a moment after every change, and "state:" + JSON when the cursor moves.
    /// </summary>
    public const string Shell = """
        <!doctype html><html><head><meta charset="utf-8">
        <meta http-equiv="Content-Security-Policy" content="default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; img-src https: data:; base-uri 'none'; form-action 'none'">
        <style>
        html,body{margin:0;min-height:100%;background:#fff}
        #editor{min-height:100vh;box-sizing:border-box;padding:10px 14px 40px;outline:none;font-family:Aptos,Calibri,Arial,Helvetica,sans-serif;font-size:12pt;color:#000;overflow-wrap:break-word}
        #editor img{max-width:100%}
        #editor table{border-collapse:collapse}
        #editor.marks :is(div,p,li,h1,h2,h3,h4,h5,h6,blockquote,pre):not(:has(div,p,li,h1,h2,h3,blockquote,pre,table))::after{content:'\00b6';color:#5b84c9;font-family:Arial}
        </style></head><body><div id="editor" contenteditable="true" spellcheck="true"></div>
        <script>
        (function(){
        const ed=document.getElementById('editor');
        const BL='div,p,li,h1,h2,h3,h4,h5,h6,blockquote,pre,td,th';
        const SIZES=[8,9,10,10.5,11,12,14,16,18,20,22,24,26,28,36,48,72];
        const STYLES={h1:'font-size:20pt;font-weight:normal;color:#0F4761;margin:12pt 0 4pt',h2:'font-size:16pt;font-weight:normal;color:#0F4761;margin:8pt 0 4pt',
          h3:'font-size:14pt;font-weight:normal;color:#0F4761;margin:8pt 0 4pt',title:'font-size:28pt;font-weight:normal;margin:0 0 4pt',
          quote:'font-style:italic;color:#404040;margin:10pt 43pt;text-align:center',code:'font-family:Consolas,Courier New,monospace;font-size:10.5pt;margin:0'};
        let saved=null,stateTimer=0,htmlTimer=0,pendingPt=null;
        function post(m){try{if(window.webkit&&window.webkit.messageHandlers&&window.webkit.messageHandlers.postAvWebViewMessage)window.webkit.messageHandlers.postAvWebViewMessage.postMessage(m);
          else if(typeof window.invokeCSharpAction==='function')window.invokeCSharpAction(m);else if(window.chrome&&window.chrome.webview)window.chrome.webview.postMessage(m);}catch(e){}}
        function inEd(n){return !!n&&(n===ed||ed.contains(n));}
        try{document.execCommand('defaultParagraphSeparator',false,'div');}catch(e){}
        document.addEventListener('selectionchange',()=>{const s=getSelection();if(s.rangeCount&&inEd(s.anchorNode)){saved=s.getRangeAt(0).cloneRange();queueState();}});
        function restore(){if(document.activeElement!==ed)ed.focus();const s=getSelection();
          if(saved&&inEd(saved.startContainer)){s.removeAllRanges();s.addRange(saved);}
          else if(!(s.rangeCount&&inEd(s.anchorNode))){const r=document.createRange();r.selectNodeContents(ed);r.collapse(false);s.removeAllRanges();s.addRange(r);}}
        function css(on){try{document.execCommand('styleWithCSS',false,on);}catch(e){}}
        function ex(c,v){document.execCommand(c,false,v===undefined?null:v);}
        function anchorEl(){const s=getSelection();let n=s.rangeCount?s.anchorNode:null;if(!inEd(n))return ed;if(n.nodeType===3)n=n.parentElement;return n||ed;}
        function closestTag(t){let n=anchorEl();while(n&&n!==ed){if(n.tagName===t)return n;n=n.parentElement;}return null;}
        function blockTag(){let n=anchorEl();while(n&&n!==ed){if(/^(H1|H2|H3|H4|BLOCKQUOTE|PRE|P|DIV|LI)$/.test(n.tagName))return n.tagName.toLowerCase();n=n.parentElement;}return 'div';}
        function curPt(){const px=parseFloat(getComputedStyle(anchorEl()).fontSize)||16;return Math.round(px*0.75*2)/2;}
        function normalizeSizes(){if(pendingPt==null)return;ed.querySelectorAll('[style*="xxx-large"],font[size="7"]').forEach(el=>{if(el.tagName==='FONT')el.removeAttribute('size');el.style.fontSize=pendingPt+'pt';});}
        function setSize(pt){pt=Number(pt);if(!(pt>=1&&pt<=1638))return;pendingPt=pt;css(true);ex('fontSize','7');normalizeSizes();}
        function leafBlocks(){const s=getSelection();if(!s.rangeCount)return [];const r=s.getRangeAt(0);return [...ed.querySelectorAll(BL)].filter(b=>r.intersectsNode(b)&&!b.querySelector(BL));}
        function eachBlock(f){let bs=leafBlocks();if(!bs.length){ex('formatBlock','div');bs=leafBlocks();}bs.forEach(f);}
        function list(kind,style){const cmd=kind==='ol'?'insertOrderedList':'insertUnorderedList',tag=kind.toUpperCase();
          if(style==='none'){if(closestTag(tag))ex(cmd);return;}
          if(!style||!closestTag(tag))ex(cmd);if(style){const l=closestTag(tag);if(l)l.style.listStyleType=style;}}
        function changeCase(mode){const s=getSelection();if(!s.rangeCount||s.isCollapsed)return;const r=s.getRangeAt(0);
          const sc=r.startContainer,so=r.startOffset,ec=r.endContainer,eo=r.endOffset;
          const w=document.createTreeWalker(ed,NodeFilter.SHOW_TEXT);const nodes=[];while(w.nextNode()){if(r.intersectsNode(w.currentNode))nodes.push(w.currentNode);}
          let prev=' ',start=true;
          for(const n of nodes){const a=n===sc?so:0,b=n===ec?eo:n.data.length;let out='';
            for(const ch of n.data.slice(a,b)){let c=ch;
              if(mode==='lower')c=ch.toLowerCase();else if(mode==='upper')c=ch.toUpperCase();
              else if(mode==='toggle')c=ch===ch.toUpperCase()?ch.toLowerCase():ch.toUpperCase();
              else if(mode==='capitalize')c=/\s/.test(prev)?ch.toUpperCase():ch.toLowerCase();
              else if(mode==='sentence'){if(start&&/\p{L}/u.test(ch)){c=ch.toUpperCase();start=false;}else c=ch.toLowerCase();if(/[.!?]/.test(ch))start=true;}
              if(c.length!==ch.length)c=ch;out+=c;prev=ch;}
            n.data=n.data.slice(0,a)+out+n.data.slice(b);}
          const nr=document.createRange();nr.setStart(sc,so);nr.setEnd(ec,eo);s.removeAllRanges();s.addRange(nr);}
        function esc(t){const d=document.createElement('div');d.textContent=t;return d.innerHTML;}
        function link(v){const nl=v.indexOf('\n');const url=nl<0?v:v.slice(0,nl),text=nl<0?'':v.slice(nl+1);
          if(!/^(https?:|mailto:)/i.test(url))return;const s=getSelection();
          if(s.isCollapsed||text)ex('insertHTML','<a href="'+esc(url).replace(/"/g,'&quot;')+'">'+esc(text||url)+'</a>');else ex('createLink',url);}
        function table(v){const p=String(v).split(',').map(Number);const rows=p[0],cols=p[1];if(!(rows>=1&&rows<=50&&cols>=1&&cols<=20))return;
          let h='<table style="border-collapse:collapse"><tbody>';for(let r=0;r<rows;r++){h+='<tr>';for(let c=0;c<cols;c++)h+='<td style="border:1px solid #000;padding:2px 6px;min-width:60px">&nbsp;</td>';h+='</tr>';}
          ex('insertHTML',h+'</tbody></table><div><br></div>');}
        function run(c,v){
          if(c==='zoom'){const z=Number(v);if(z>=0.1&&z<=5)document.body.style.zoom=z;return;}
          if(c==='marks'){ed.classList.toggle('marks');queueState();return;}
          restore();css(true);
          switch(c){
            case 'fontSize':setSize(v);break;
            case 'grow':{const p=curPt();setSize(SIZES.find(x=>x>p)||Math.min(1638,p+10));break;}
            case 'shrink':{const p=curPt();const sm=SIZES.filter(x=>x<p);setSize(sm.length?sm[sm.length-1]:Math.max(1,p-1));break;}
            case 'fontName':if(v)ex('fontName',v);break;
            case 'foreColor':ex('foreColor',v==='auto'?'#000000':v);break;
            case 'hiliteColor':ex('hiliteColor',v==='none'?'transparent':v);break;
            case 'bullets':list('ul',v);break;
            case 'numbering':list('ol',v);break;
            case 'clear':ex('removeFormat');ex('unlink');ex('formatBlock','div');break;
            case 'changeCase':changeCase(v);break;
            case 'lineSpacing':eachBlock(b=>b.style.lineHeight=String(v));break;
            case 'shading':eachBlock(b=>b.style.backgroundColor=v==='none'?'':v);break;
            case 'style':{const tag=v==='title'?'h1':v==='quote'?'blockquote':v==='code'?'pre':v==='normal'?'div':v;
              if(!/^(div|h1|h2|h3|blockquote|pre)$/.test(tag))break;ex('formatBlock',tag);
              leafBlocks().forEach(b=>{if(b.tagName.toLowerCase()===tag){b.removeAttribute('style');if(STYLES[v])b.setAttribute('style',STYLES[v]);}});break;}
            case 'link':link(String(v||''));break;
            case 'table':table(v);break;
            case 'line':ex('insertHorizontalRule');break;
            case 'insertText':ex('insertText',String(v||''));break;
            case 'image':if(/^data:image\/(png|jpeg|gif|webp);base64,[A-Za-z0-9+\/=]+$/.test(String(v)))ex('insertImage',v);break;
            case 'delete':ex('delete');break;
            case 'selectAll':{const r=document.createRange();r.selectNodeContents(ed);const s=getSelection();s.removeAllRanges();s.addRange(r);break;}
            default:if(/^(bold|italic|underline|strikeThrough|subscript|superscript|justifyLeft|justifyCenter|justifyRight|justifyFull|indent|outdent|unlink|undo|redo)$/.test(c))ex(c);else return;
          }
          changed();}
        function q(c){try{return document.queryCommandState(c);}catch(e){return false;}}
        function state(){const fam=(getComputedStyle(anchorEl()).fontFamily||'').split(',')[0].replace(/["']/g,'').trim();
          return {b:q('bold'),i:q('italic'),u:q('underline'),s:q('strikeThrough'),sub:q('subscript'),sup:q('superscript'),
            ul:!!closestTag('UL'),ol:!!closestTag('OL'),
            al:q('justifyCenter')?'center':q('justifyRight')?'right':q('justifyFull')?'justify':'left',font:fam,size:curPt(),block:blockTag(),marks:ed.classList.contains('marks')};}
        function queueState(){clearTimeout(stateTimer);stateTimer=setTimeout(()=>post('state:'+JSON.stringify(state())),60);}
        function changed(){post('dirty');clearTimeout(htmlTimer);htmlTimer=setTimeout(()=>post('html:'+ed.innerHTML),250);queueState();}
        ed.addEventListener('input',()=>{normalizeSizes();changed();});
        ed.addEventListener('click',e=>{if(e.target.closest&&e.target.closest('a'))e.preventDefault();});
        window.oo={run:run,get:()=>ed.innerHTML,set:h=>{ed.innerHTML=h;saved=null;queueState();},state:state,
          find:t=>{restore();return window.find(String(t),false,false,true,false,false,false);},focus:()=>{restore();},
          selText:()=>{const s=getSelection();return s.rangeCount&&inEd(s.anchorNode)&&!s.isCollapsed?s.toString():(saved?saved.toString():'');}};
        })();
        </script></body></html>
        """;
}

/// <summary>The in-place editor: the editable page in the embedded web view (WebView2 on Windows, WebKitGTK on X11).</summary>
internal sealed class WebHtmlEditor : IHtmlEditorHost
{
    private readonly NativeWebView _view = new() { HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch };
    private readonly OwnDocumentNavigation _own = new();
    private readonly List<string> _queued = [];
    private bool _navigated, _ready;
    private string? _pendingHtml;

    public WebHtmlEditor()
    {
        _view.NavigationStarted += (_, a) => { if (!_own.Allows(a.Request)) a.Cancel = true; };     // links in the body never open inside the editor
        _view.NewWindowRequested += (_, a) => a.Handled = true;
        _view.NavigationCompleted += async (_, _) =>
        {
            if (_ready) return;
            _ready = true;
            if (_pendingHtml is { } html) { _pendingHtml = null; await Script(HtmlEditorDocument.SetCall(html)); }
            foreach (var script in _queued.ToList()) await Script(script);
            _queued.Clear();
        };
        _view.WebMessageReceived += (_, a) =>
        {
            var body = a.Body ?? "";
            if (body == "dirty") Edited?.Invoke();
            else if (body.StartsWith("html:", StringComparison.Ordinal)) HtmlChanged?.Invoke(body[5..]);
            else if (body.StartsWith("state:", StringComparison.Ordinal) && HtmlEditorDocument.ParseState(body[6..]) is { } state) StateChanged?.Invoke(state);
        };
        _view.AttachedToVisualTree += (_, _) =>
        {
            if (_navigated) return;
            _navigated = true;
            _own.Expect();
            _view.NavigateToString(HtmlEditorDocument.Shell);
        };
    }

    public Control View => _view;
    public bool EditsInPlace => true;
    /// <summary>The page has loaded and has the body (commands and reads go to it from now on).</summary>
    internal bool IsReady => _ready && _pendingHtml is null;
    public event Action<string>? HtmlChanged;
    public event Action? Edited;
    public event Action<HtmlEditorState>? StateChanged;

    // The body goes in by script, not inside the page: NavigateToString refuses documents over 2 MB (pictures as data URIs).
    public void Load(string html)
    {
        if (_ready) _ = Script(HtmlEditorDocument.SetCall(html));
        else _pendingHtml = html;
    }

    public void Run(string command, string? value = null)
    {
        var script = HtmlEditorDocument.Call(command, value);
        if (_ready) _ = Script(script);
        else _queued.Add(script);
    }

    public async Task<string?> ReadHtmlAsync() => _ready ? HtmlEditorDocument.ScriptString(await Script("window.oo&&window.oo.get()")) : _pendingHtml;

    public async Task<bool> FindAsync(string text) =>
        _ready && string.Equals(HtmlEditorDocument.ScriptString(await Script("window.oo&&window.oo.find(" + JsonSerializer.Serialize(text) + ")")), "true", StringComparison.OrdinalIgnoreCase);

    public async Task<string?> SelectedTextAsync() => _ready ? HtmlEditorDocument.ScriptString(await Script("window.oo&&window.oo.selText()")) : null;

    public void FocusBody()
    {
        _view.Focus();
        if (_ready) _ = Script("window.oo&&window.oo.focus()");
    }

    private async Task<string?> Script(string script)
    {
        try { return await _view.InvokeScript(script); }
        catch (Exception e) when (e is not OutOfMemoryException) { AppLog.Note("compose", "editor script failed: " + e.Message); return null; }
    }

    /// <summary>The embedded view draws on Windows and macOS, and on Linux under X11 with WebKitGTK; Wayland gets the preview (the view paints nothing there).</summary>
    public static bool Supported()
    {
        if (!ReaderEngineChoice.PreferEmbedded()) return false;
        if (!OperatingSystem.IsLinux()) return true;
        foreach (var name in new[] { "libwebkit2gtk-4.1.so.0", "libwebkit2gtk-4.0.so.37" })
            if (System.Runtime.InteropServices.NativeLibrary.TryLoad(name, out var handle))
            {
                System.Runtime.InteropServices.NativeLibrary.Free(handle);
                return true;
            }
        return false;
    }
}

/// <summary>
/// Where the embedded view cannot draw: a read-only preview of the formatted body and "Edit message..." opening the editor in its own window
/// (<see cref="RichComposeEditor"/>). The ribbon's formatting commands are recorded (tests read them) but cannot reach that window.
/// </summary>
internal sealed class PreviewHtmlEditor : IHtmlEditorHost
{
    private readonly TextBox _preview = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, BorderThickness = new Thickness(0) };
    private readonly DockPanel _panel = new();
    private string _html = "";

    public PreviewHtmlEditor(Func<TopLevel?> owner, Action<string> status)
    {
        var edit = new Button { Content = "Edit message…", Margin = new Thickness(0, 4, 0, 4), HorizontalAlignment = HorizontalAlignment.Left };
        ToolTip.SetTip(edit, "This desktop cannot show the editor inside the message window; it opens in its own window.");
        edit.Click += (_, _) =>
        {
            if (owner() is Window w)
                RichComposeEditor.Open(w, _html, edited => { _html = edited; Show(); Edited?.Invoke(); HtmlChanged?.Invoke(edited); }, status);
        };
        DockPanel.SetDock(edit, Dock.Top);
        _panel.Children.Add(edit);
        _panel.Children.Add(_preview);
    }

    public List<(string Command, string? Value)> Commands { get; } = [];
    public Control View => _panel;
    public bool EditsInPlace => false;
    public event Action<string>? HtmlChanged;
    public event Action? Edited;
#pragma warning disable CS0067                                                 // the preview never reports a cursor
    public event Action<HtmlEditorState>? StateChanged;
#pragma warning restore CS0067

    public void Load(string html) { _html = html; Show(); }
    public void Run(string command, string? value = null) => Commands.Add((command, value));
    public Task<string?> ReadHtmlAsync() => Task.FromResult<string?>(_html);
    public Task<bool> FindAsync(string text) => Task.FromResult(_preview.Text?.Contains(text, StringComparison.OrdinalIgnoreCase) == true);
    public Task<string?> SelectedTextAsync() => Task.FromResult<string?>(_preview.SelectedText);
    public void FocusBody() => _preview.Focus();

    /// <summary>Test seam: what typing into the body would report.</summary>
    internal void SimulateEdit(string html) { _html = html; Show(); Edited?.Invoke(); HtmlChanged?.Invoke(html); }

    private void Show()
    {
        try { _preview.Text = string.Concat(SafeHtmlPreview.Parse(_html).Select(run => run.Text)); }
        catch (Exception) { _preview.Text = "(Formatted message)"; }
    }
}
