namespace CloudLauncher.Shared;

/// <summary>The description editor's document: stylesheet, toolbar and script.</summary>
/// <remarks>
/// Written in the default palette's colours so <c>Recolour</c> can map it onto the user's theme,
/// like the read-only stylesheet. The editor talks to its host only through messages
/// (<c>postMessage</c> on WebView2, <c>window.external</c> on the legacy IE host), so the document
/// stays a plain web page.
/// </remarks>
public static partial class PackText
{
    /// <summary>Message types the editor sends to its host. Kept here so both ends name them once.</summary>
    public static class EditorMessage
    {
        public const string Changed = "changed";
        public const string PickImage = "pickImage";
        public const string ApplySource = "applySource";
        public const string OpenLink = "openLink";
    }

    private const string EditorDocumentStyles =
        """
        <style>
        html{margin:0;padding:0;height:100%}
        /* The flex column belongs on body itself, not on a class: toggleSource() assigns
           body.className wholesale, so a layout class would be wiped the first time anyone
           opened the source view. Without it #editor-wrap's flex:1 is inert, its height stays
           auto, overflow:auto never makes a scroll container, and everything below the fold is
           unreachable - the old IE host papered over that by scrolling the div from C#. */
        body{margin:0;padding:0;height:100%;overflow:hidden;display:flex;flex-direction:column;
          background:#0B0D11;color:#A8B0BF;
          font-family:'Segoe UI',sans-serif;font-size:17px;line-height:1.6}
        *{box-sizing:border-box}

        /* ── toolbar ─────────────────────────────────────────────────────────── */
        #toolbar{display:flex;flex-wrap:wrap;align-items:center;gap:2px;padding:6px 8px;
          border-bottom:1px solid #2E3445;background:#0B0D11;flex-shrink:0;width:100%}
        #toolbar button{border:1px solid transparent;background:transparent;color:#C4CAD6;
          border-radius:6px;min-width:30px;height:27px;padding:0 7px;cursor:pointer;font-size:14px;
          font-family:'Segoe UI',sans-serif;font-weight:600;line-height:1;position:relative}
        #toolbar button:hover{background:#181C25;color:#EEF1F7;border-color:#2E3445}
        #toolbar button.on{background:#181C25;color:#5B9DF9;border-color:#3A4254}
        #toolbar button[disabled]{opacity:.35;cursor:default}
        #toolbar button[disabled]:hover{background:transparent;border-color:transparent;color:#C4CAD6}
        #toolbar .wide-btn{min-width:48px;padding:0 9px}
        #toolbar .cmd-btn{color:#5B9DF9;font-weight:700}
        #toolbar .sep{width:1px;height:18px;background:#2E3445;margin:0 4px}
        #toolbar .grow{flex:1}
        #toolbar .swatch-dot{display:inline-block;width:11px;height:3px;border-radius:2px;
          position:absolute;left:50%;bottom:4px;transform:translateX(-50%)}

        /* Row that only appears with the caret inside a table. */
        #tablebar{display:none;align-items:center;gap:2px;padding:4px 8px;background:#11141B;
          border-bottom:1px solid #2E3445;flex-shrink:0;font-size:13px}
        #tablebar.show{display:flex}
        #tablebar span.lbl{color:#5C6478;font-weight:600;margin-right:4px}
        #tablebar button{border:1px solid #2E3445;background:transparent;color:#C4CAD6;
          border-radius:6px;height:24px;padding:0 9px;cursor:pointer;font-size:13px;
          font-family:'Segoe UI',sans-serif}
        #tablebar button:hover{background:#181C25;color:#EEF1F7}

        /* Colour / highlight swatch popover. */
        #swatches{display:none;position:absolute;z-index:900;background:#141820;border:1px solid #2E3445;
          border-radius:10px;padding:8px;box-shadow:0 12px 30px rgba(0,0,0,.45);width:196px}
        #swatches.show{display:block}
        #swatches .row{display:flex;flex-wrap:wrap;gap:6px}
        #swatches i{display:block;width:22px;height:22px;border-radius:6px;cursor:pointer;
          border:1px solid rgba(255,255,255,.14)}
        #swatches i:hover{outline:2px solid #5B9DF9;outline-offset:1px}
        #swatches .clear{margin-top:8px;width:100%;background:transparent;border:1px solid #2E3445;
          color:#C4CAD6;border-radius:7px;height:26px;cursor:pointer;font-size:13px;
          font-family:'Segoe UI',sans-serif}
        #swatches .clear:hover{background:#181C25;color:#EEF1F7}

        /* ── writing surface ─────────────────────────────────────────────────── */
        #editor-wrap{flex:1;min-height:0;overflow:auto;overflow-x:hidden;position:relative}
        #editor{min-height:100%;padding:18px 20px 28px;outline:none;font-size:17px}
        #editor:empty:before{content:attr(data-placeholder);color:#5C6478}
        #editor.drop-target{box-shadow:inset 0 0 0 2px #5B9DF9}

        #source{display:none;width:100%;height:100%;border:0;outline:none;resize:none;
          background:#0B0D11;color:#C4CAD6;font-family:Consolas,monospace;font-size:14px;
          line-height:1.55;padding:18px 20px}
        body.sourcemode #editor{display:none}
        body.sourcemode #source{display:block}
        body.sourcemode #toolbar button:not(.always){opacity:.35;pointer-events:none}

        /* ── content ─────────────────────────────────────────────────────────── */
        #editor img{max-width:100%;height:auto;border-radius:10px;margin:14px 0;display:block}
        #editor img.selected{outline:2px solid #5B9DF9;outline-offset:2px}
        a{color:#5B9DF9;text-decoration:none}a:hover{text-decoration:underline}
        h1{font-size:28px}h2{font-size:23px}h3{font-size:19px}
        h1,h2,h3,h4{color:#EEF1F7;margin:22px 0 10px;font-weight:600;line-height:1.3}
        #editor > *:first-child{margin-top:0}
        p,li{margin:0 0 10px}ul,ol{margin:0 0 14px 22px;padding:0}
        strong,b{color:#EEF1F7;font-weight:600}em,i{color:#C4CAD6}
        hr{border:none;border-top:1px solid #2E3445;margin:20px 0}
        blockquote{margin:14px 0;padding:12px 16px;border-left:3px solid #3A4254;background:#11141B;
          border-radius:0 10px 10px 0;color:#A8B0BF}
        code{font-family:Consolas,monospace;font-size:14px;background:#181C25;padding:2px 6px;
          border-radius:4px;color:#EEF1F7;overflow-wrap:break-word}
        pre{background:#11141B;border:1px solid #2E3445;border-radius:10px;padding:12px 14px;
          overflow-x:auto;margin:14px 0}
        pre code{background:transparent;padding:0;font-size:14px}
        table{border-collapse:collapse;width:100%;margin:14px 0}
        td,th{border:1px solid #2E3445;padding:7px 10px;min-width:40px}
        th{background:#181C25;color:#EEF1F7;font-weight:600;text-align:left}
        table tr:hover td{background:rgba(91,157,249,.04)}

        /* Checklists are spans, not inputs: the description sanitizer drops <input>, so a saved
           checklist built from one would come back as an empty bullet. */
        ul.task-list{list-style:none;margin-left:2px}
        ul.task-list li.task{display:flex;align-items:flex-start;gap:9px;margin:0 0 8px}
        li.task .task-box{flex:0 0 auto;width:17px;height:17px;margin-top:4px;border-radius:5px;
          border:1.5px solid #3A4254;background:#11141B;cursor:pointer;position:relative}
        li.task .task-box:hover{border-color:#5B9DF9}
        li.task.done .task-box{background:#5B9DF9;border-color:#5B9DF9}
        li.task.done .task-box:after{content:'';position:absolute;left:4px;top:1px;width:5px;height:9px;
          border-right:2px solid #0B0D11;border-bottom:2px solid #0B0D11;transform:rotate(40deg)}
        li.task.done .task-text{color:#5C6478;text-decoration:line-through}
        li.task .task-text{flex:1;min-width:0}

        /* ── command blocks (editing form) ───────────────────────────────────── */
        .mc-cmd:not(.mc-cmd-editing){margin:10px 0 14px;border:1px solid #2E3445;border-radius:10px;
          background:#11141B;overflow:hidden}
        .mc-cmd-editing{margin:6px 0;padding:8px 12px;border:1px solid #2E3445;border-radius:10px;
          background:#11141B}
        .mc-cmd-editing .mc-cmd-label{color:#5B9DF9;font-weight:600;cursor:text;outline:none}
        .mc-cmd-editing .mc-cmd-body{display:block;padding:2px 0 0}
        .mc-cmd-editing code{display:inline;outline:none;white-space:pre-wrap;word-break:break-word;
          font-size:13px;color:#5C6478;background:transparent;padding:0}

        /* ── modal ───────────────────────────────────────────────────────────── */
        #modal-overlay{position:fixed;inset:0;background:rgba(7,9,12,.72);display:flex;
          align-items:center;justify-content:center;z-index:1000;padding:18px}
        #modal-overlay.modal-hidden{display:none}
        #modal-dialog{width:520px;max-width:96%;background:#141820;border:1px solid #2E3445;
          border-radius:12px;box-shadow:0 16px 40px rgba(0,0,0,.45);padding:18px 20px 16px;font-size:15px}
        #modal-title{color:#EEF1F7;font-size:17px;font-weight:600;margin:0 0 14px}
        #modal-fields{overflow-y:auto;padding-right:2px}
        .modal-field{margin:0 0 12px}
        .modal-field label{display:block;color:#A8B0BF;font-size:13px;font-weight:600;margin:0 0 6px}
        .modal-field input{width:100%;background:#0B0D11;border:1px solid #2E3445;border-radius:8px;
          color:#EEF1F7;font-size:15px;padding:9px 11px;font-family:'Segoe UI',sans-serif;outline:none}
        .modal-field input.modal-input-mono{font-family:Consolas,monospace;font-size:14px}
        .modal-field input:focus{border-color:#5B9DF9;box-shadow:0 0 0 2px rgba(91,157,249,.25)}
        #modal-actions{display:flex;justify-content:flex-end;gap:8px;margin-top:16px;padding-top:4px}
        #modal-actions button{border-radius:8px;font-size:14px;font-weight:600;padding:8px 14px;
          cursor:pointer;font-family:'Segoe UI',sans-serif;line-height:1.2}
        #modal-cancel{background:transparent;border:1px solid #2E3445;color:#C4CAD6}
        #modal-cancel:hover{background:#181C25;color:#EEF1F7;border-color:#3A4254}
        #modal-ok{background:#5B9DF9;border:1px solid #5B9DF9;color:#0B0D11}
        #modal-ok:hover{background:#7AB2FF;border-color:#7AB2FF}

        ::-webkit-scrollbar{width:10px;height:10px}
        ::-webkit-scrollbar-track{background:#14171F}
        ::-webkit-scrollbar-thumb{background:#2E3445;border-radius:6px}
        ::-webkit-scrollbar-thumb:hover{background:#3A4254}
        </style>
        """;

    private const string EditorDocumentToolbar =
        """
        <div id="toolbar">
          <button type="button" class="always" title="Undo (Ctrl+Z)" onclick="fmt('undo')">&#8630;</button>
          <button type="button" class="always" title="Redo (Ctrl+Y)" onclick="fmt('redo')">&#8631;</button>
          <span class="sep"></span>
          <button type="button" id="b-p"  title="Body text"  onclick="setBlock('P')">&#182;</button>
          <button type="button" id="b-h1" title="Title"      onclick="setBlock('H1')">H1</button>
          <button type="button" id="b-h2" title="Heading"     onclick="setBlock('H2')">H2</button>
          <button type="button" id="b-h3" title="Subheading"  onclick="setBlock('H3')">H3</button>
          <span class="sep"></span>
          <button type="button" id="b-bold"   title="Bold (Ctrl+B)"      onclick="fmt('bold')"><b>B</b></button>
          <button type="button" id="b-italic" title="Italic (Ctrl+I)"    onclick="fmt('italic')"><i>I</i></button>
          <button type="button" id="b-under"  title="Underline (Ctrl+U)" onclick="fmt('underline')"><u>U</u></button>
          <button type="button" id="b-strike" title="Strikethrough"      onclick="fmt('strikeThrough')"><s>S</s></button>
          <button type="button" title="Text colour" onclick="openSwatches(this,'fore',event)">A<span class="swatch-dot" style="background:#5B9DF9"></span></button>
          <button type="button" title="Highlight" onclick="openSwatches(this,'back',event)">&#9646;<span class="swatch-dot" style="background:#E8C15A"></span></button>
          <span class="sep"></span>
          <button type="button" id="b-ul" title="Bullet list"  onclick="fmt('insertUnorderedList')">&#8226;</button>
          <button type="button" id="b-ol" title="Numbered list" onclick="fmt('insertOrderedList')">1.</button>
          <button type="button" title="Checklist" onclick="insertChecklist()">&#9744;</button>
          <span class="sep"></span>
          <button type="button" title="Quote" onclick="setBlock('BLOCKQUOTE')">&ldquo;</button>
          <button type="button" title="Inline code" onclick="wrapInlineCode()">&lt;&gt;</button>
          <button type="button" title="Code block" onclick="insertCodeBlock()">{ }</button>
          <button type="button" title="Divider" onclick="insertDivider()">&#8212;</button>
          <button type="button" title="Table" onclick="insertTable()">&#9638;</button>
          <span class="sep"></span>
          <button type="button" class="wide-btn" title="Insert link (Ctrl+K)" onclick="insertLink()">Link</button>
          <button type="button" class="wide-btn" title="Insert an image from this PC, or paste or drop one straight in" onclick="pickImage()">Image</button>
          <button type="button" class="wide-btn cmd-btn" title="Insert a Minecraft command players can run from the description" onclick="insertCommand()">/cmd</button>
          <span class="grow"></span>
          <button type="button" title="Clear formatting" onclick="clearFormatting()">&#10005;</button>
          <button type="button" id="b-src" class="always wide-btn" title="Edit the source. Paste Markdown here and it is converted on the way back." onclick="toggleSource()">&lt;/&gt;</button>
        </div>
        <div id="tablebar">
          <span class="lbl">Table</span>
          <button type="button" onclick="tableOp('row-after')">+ Row</button>
          <button type="button" onclick="tableOp('col-after')">+ Column</button>
          <button type="button" onclick="tableOp('row-del')">&#8722; Row</button>
          <button type="button" onclick="tableOp('col-del')">&#8722; Column</button>
          <button type="button" onclick="tableOp('header')">Header row</button>
          <button type="button" onclick="tableOp('del')">Delete table</button>
        </div>
        <div id="swatches">
          <div class="row" id="swatch-row"></div>
          <button type="button" class="clear" onclick="applySwatch(null)">Remove colour</button>
        </div>
        """;

    private const string EditorDocumentScript =
        """
        <script>
        var modalCallback = null, swatchMode = 'fore', dirtyTimer = null;

        function ed() { return document.getElementById('editor'); }
        function src() { return document.getElementById('source'); }

        /* ── host bridge ─────────────────────────────────────────────────────── */
        function send(type, payload) {
          var msg = { type: type };
          if (payload) for (var k in payload) msg[k] = payload[k];
          try {
            if (window.chrome && window.chrome.webview) { window.chrome.webview.postMessage(JSON.stringify(msg)); return; }
          } catch (e) {}
          try { if (window.external && window.external.NotifyChanged) window.external.NotifyChanged(); } catch (e) {}
        }

        /* Coalesced: a keystroke should not cross the bridge on every character. */
        function changed() {
          if (dirtyTimer) clearTimeout(dirtyTimer);
          dirtyTimer = setTimeout(function () {
            dirtyTimer = null;
            send('changed', { html: ed() ? ed().innerHTML : '' });
          }, 180);
        }
        function flush() {
          if (dirtyTimer) { clearTimeout(dirtyTimer); dirtyTimer = null; }
          send('changed', { html: ed() ? ed().innerHTML : '' });
        }

        /* Called by the host. */
        function setEditorHtml(html) {
          if (!ed()) return;
          ed().innerHTML = html && html.length ? html : '<p><br></p>';
          exitSource();
          flush();
        }
        function insertImageSrc(url, alt) {
          if (!url) return;
          focusEditor();
          insertHtml('<img src="' + attr(url) + '" alt="' + attr(alt || '') + '">');
        }

        /* ── helpers ─────────────────────────────────────────────────────────── */
        function esc(v) {
          return String(v == null ? '' : v).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
        }
        function attr(v) { return esc(v).replace(/"/g, '&quot;'); }
        function focusEditor() { if (ed()) ed().focus(); }
        function insertHtml(html) {
          focusEditor();
          try { if (document.execCommand('insertHTML', false, html)) { changed(); return; } } catch (e) {}
          ed().innerHTML += html;
          changed();
        }
        function fmt(cmd, value) {
          focusEditor();
          try { document.execCommand(cmd, false, value == null ? null : value); } catch (e) {}
          syncToolbar();
          changed();
        }
        function setBlock(tag) { fmt('formatBlock', '<' + tag + '>'); }
        function selectedText() {
          var s = window.getSelection();
          return s && s.rangeCount ? s.toString() : '';
        }
        function nodeIn(tag) {
          var s = window.getSelection();
          var n = s && s.rangeCount ? s.getRangeAt(0).startContainer : null;
          while (n && n !== ed()) {
            if (n.nodeType === 1 && n.tagName === tag) return n;
            n = n.parentNode;
          }
          return null;
        }

        /* ── blocks ──────────────────────────────────────────────────────────── */
        function insertDivider() { insertHtml('<hr><p><br></p>'); }

        function insertCodeBlock() {
          var text = selectedText();
          insertHtml('<pre><code>' + (text ? esc(text) : '') + '</code></pre><p><br></p>');
        }

        function wrapInlineCode() {
          var existing = nodeIn('CODE');
          if (existing) {                     /* second press unwraps */
            var parent = existing.parentNode;
            while (existing.firstChild) parent.insertBefore(existing.firstChild, existing);
            parent.removeChild(existing);
            changed();
            return;
          }
          var text = selectedText();
          if (!text) { insertHtml('<code>code</code>&nbsp;'); return; }
          insertHtml('<code>' + esc(text) + '</code>');
        }

        function insertChecklist() {
          insertHtml('<ul class="task-list">' + taskItemHtml('') + '</ul><p><br></p>');
          /* insertHTML leaves the caret in the paragraph after the list, so the first thing
             typed landed underneath the item instead of in it. */
          var lists = ed().getElementsByClassName('task-list');
          if (!lists.length) return;
          var texts = lists[lists.length - 1].getElementsByClassName('task-text');
          if (texts.length) placeCaret(texts[texts.length - 1]);
        }
        function taskItemHtml(text) {
          /* A truly empty span has no line box and cannot hold a caret, so a blank item
             needs the <br>. textContent stays empty, which is what the Enter-on-empty
             check in onKeyDown tests. */
          return '<li class="task"><span class="task-box" contenteditable="false"></span>'
               + '<span class="task-text">' + (text ? esc(text) : '<br>') + '</span></li>';
        }

        function insertTable() {
          showModal('Insert table', [
            { label: 'Columns', value: '3' },
            { label: 'Rows (not counting the header)', value: '2' }
          ], 'Insert', function (values) {
            if (!values) return;
            var cols = Math.max(1, Math.min(12, parseInt(values[0], 10) || 3));
            var rows = Math.max(1, Math.min(40, parseInt(values[1], 10) || 2));
            var html = '<table><thead><tr>';
            for (var c = 0; c < cols; c++) html += '<th><br></th>';
            html += '</tr></thead><tbody>';
            for (var r = 0; r < rows; r++) {
              html += '<tr>';
              for (var c2 = 0; c2 < cols; c2++) html += '<td><br></td>';
              html += '</tr>';
            }
            html += '</tbody></table><p><br></p>';
            insertHtml(html);
          });
        }

        function tableOp(op) {
          var cell = nodeIn('TD') || nodeIn('TH');
          var table = nodeIn('TABLE');
          if (!table) return;
          var row = cell ? cell.parentNode : null;
          var index = cell && row ? Array.prototype.indexOf.call(row.children, cell) : 0;

          if (op === 'del') { table.parentNode.removeChild(table); changed(); return; }

          if (op === 'row-after' && row) {
            var copy = row.cloneNode(true);
            for (var i = 0; i < copy.children.length; i++) copy.children[i].innerHTML = '<br>';
            /* A cloned header row would add a second one, so new rows are always body rows. */
            if (copy.children.length && copy.children[0].tagName === 'TH') {
              var body = table.tBodies[0] || table.appendChild(document.createElement('tbody'));
              var tr = document.createElement('tr');
              for (var h = 0; h < copy.children.length; h++) {
                var td = document.createElement('td'); td.innerHTML = '<br>'; tr.appendChild(td);
              }
              body.insertBefore(tr, body.firstChild);
            } else {
              row.parentNode.insertBefore(copy, row.nextSibling);
            }
            changed(); return;
          }

          if (op === 'row-del' && row) {
            var owner = row.parentNode;
            owner.removeChild(row);
            if (!table.rows.length) table.parentNode.removeChild(table);
            changed(); return;
          }

          if (op === 'col-after' || op === 'col-del') {
            for (var r = 0; r < table.rows.length; r++) {
              var tr2 = table.rows[r];
              if (op === 'col-del') {
                if (tr2.children[index]) tr2.removeChild(tr2.children[index]);
              } else {
                var ref = tr2.children[index];
                var cellTag = ref && ref.tagName === 'TH' ? 'th' : 'td';
                var neu = document.createElement(cellTag);
                neu.innerHTML = '<br>';
                tr2.insertBefore(neu, ref ? ref.nextSibling : null);
              }
            }
            if (table.rows.length && !table.rows[0].children.length) table.parentNode.removeChild(table);
            changed(); return;
          }

          if (op === 'header') {
            if (table.tHead) {
              var head = table.tHead;
              /* A table with a thead and no tbody used to lose the header row entirely
                 here: there was nowhere to move the cells to, so they were removed and
                 nothing was inserted. */
              var bodyEl = table.tBodies[0];
              if (!bodyEl) bodyEl = table.appendChild(document.createElement('tbody'));
              while (head.rows.length) {
                var hr = head.rows[0];
                var nr = document.createElement('tr');
                for (var k = 0; k < hr.children.length; k++) {
                  var nd = document.createElement('td');
                  nd.innerHTML = hr.children[k].innerHTML;
                  nr.appendChild(nd);
                }
                head.removeChild(hr);
                if (bodyEl) bodyEl.insertBefore(nr, bodyEl.firstChild);
              }
              table.removeChild(head);
            } else {
              var first = table.rows[0];
              if (!first) return;
              var thead = document.createElement('thead');
              var newRow = document.createElement('tr');
              for (var m = 0; m < first.children.length; m++) {
                var th = document.createElement('th');
                th.innerHTML = first.children[m].innerHTML;
                newRow.appendChild(th);
              }
              thead.appendChild(newRow);
              first.parentNode.removeChild(first);
              table.insertBefore(thead, table.firstChild);
            }
            changed();
          }
        }

        /* ── colour ──────────────────────────────────────────────────────────── */
        var FORE = ['#EEF1F7','#A8B0BF','#5C6478','#5B9DF9','#54C08A','#E8C15A','#E8765A','#C57BE8'];
        var BACK = ['#2E3445','#1F3A52','#1F4536','#4A3F1C','#4A2622','#3A2447','#5B9DF9','#E8C15A'];

        function openSwatches(btn, mode, e) {
          /* Keep this click from reaching onDocClick, which would close the popover again
             in the same event and leave the button looking completely dead. */
          if (e && e.stopPropagation) e.stopPropagation();
          swatchMode = mode;
          var box = document.getElementById('swatches');
          var row = document.getElementById('swatch-row');
          var colours = mode === 'fore' ? FORE : BACK;
          row.innerHTML = '';
          for (var i = 0; i < colours.length; i++) {
            var dot = document.createElement('i');
            dot.style.background = colours[i];
            dot.setAttribute('data-colour', colours[i]);
            dot.onclick = function () { applySwatch(this.getAttribute('data-colour')); };
            row.appendChild(dot);
          }
          var r = btn.getBoundingClientRect();
          box.style.left = Math.max(6, Math.min(r.left, window.innerWidth - 208)) + 'px';
          box.style.top = (r.bottom + 4) + 'px';
          box.className = 'show';
        }
        function closeSwatches() { document.getElementById('swatches').className = ''; }
        function applySwatch(colour) {
          closeSwatches();
          if (swatchMode === 'fore') {
            fmt('foreColor', colour || '#A8B0BF');
          } else if (colour) {
            try { document.execCommand('hiliteColor', false, colour); }
            catch (e) { try { document.execCommand('backColor', false, colour); } catch (e2) {} }
            changed();
          } else {
            try { document.execCommand('hiliteColor', false, 'transparent'); } catch (e) {}
            changed();
          }
        }

        function clearFormatting() {
          focusEditor();
          try {
            document.execCommand('removeFormat', false, null);
            document.execCommand('formatBlock', false, '<P>');
          } catch (e) {}
          syncToolbar();
          changed();
        }

        /* ── links, images, commands ─────────────────────────────────────────── */
        function insertLink() {
          var text = selectedText();
          showModal('Insert link', [
            { label: 'Text', value: text },
            { label: 'Address', placeholder: 'https://', value: 'https://', mono: true }
          ], 'Insert', function (values) {
            if (!values) return;
            var label = values[0] || values[1];
            var href = values[1];
            if (!href) return;
            insertHtml('<a href="' + attr(href) + '">' + esc(label) + '</a>');
          });
        }

        function pickImage() { send('pickImage'); }

        function insertCommand() {
          showModal('Insert command', [
            { label: 'Label players see', value: selectedText() },
            { label: 'Command', placeholder: '/give @s minecraft:stone', value: '/', mono: true }
          ], 'Insert', function (values) {
            if (!values) return;
            var command = values[1];
            if (!command || command.length < 2) return;
            var label = values[0] || command;
            insertHtml('<div class="mc-cmd mc-cmd-editing" contenteditable="false">'
              + '<div class="mc-cmd-head"><span class="mc-cmd-label" contenteditable="true">' + esc(label) + '</span></div>'
              + '<div class="mc-cmd-body"><code contenteditable="true">' + esc(command) + '</code></div></div><p><br></p>');
          });
        }

        /* ── images by paste and drop ────────────────────────────────────────── */
        function readImageFile(file) {
          if (!file || !window.FileReader) return false;
          if (!/^image\//.test(file.type)) return false;
          var reader = new FileReader();
          reader.onload = function () { insertImageSrc(reader.result, file.name); };
          reader.readAsDataURL(file);
          return true;
        }
        function filesFrom(e) {
          var dt = e.dataTransfer || (e.clipboardData || window.clipboardData);
          if (!dt) return null;
          if (dt.files && dt.files.length) return dt.files;
          if (dt.items && dt.items.length) {
            var out = [];
            for (var i = 0; i < dt.items.length; i++) {
              if (dt.items[i].kind === 'file') {
                var f = dt.items[i].getAsFile();
                if (f) out.push(f);
              }
            }
            return out.length ? out : null;
          }
          return null;
        }
        function onPaste(e) {
          var files = filesFrom(e);
          if (files && readImageFile(files[0])) { e.preventDefault(); return; }
          changed();
        }
        function onDrop(e) {
          ed().className = '';
          var files = filesFrom(e);
          if (files) {
            var handled = false;
            for (var i = 0; i < files.length; i++) handled = readImageFile(files[i]) || handled;
            if (handled) { e.preventDefault(); return; }
          }
        }

        /* ── source view ─────────────────────────────────────────────────────── */
        function toggleSource() {
          if (document.body.className.indexOf('sourcemode') >= 0) {
            /* Leaving the source view hands the text to the host: it knows whether that is
               HTML or Markdown, and owns the conversion either way. */
            send('applySource', { text: src().value });
            return;
          }
          src().value = ed().innerHTML;
          document.body.className = 'sourcemode';
          document.getElementById('b-src').className = 'always wide-btn on';
          src().focus();
        }
        function exitSource() {
          document.body.className = '';
          document.getElementById('b-src').className = 'always wide-btn';
        }

        /* ── toolbar state ───────────────────────────────────────────────────── */
        function mark(id, on) {
          var el = document.getElementById(id);
          if (el) el.className = on ? 'on' : (el.className.indexOf('wide-btn') >= 0 ? 'wide-btn' : '');
        }
        function state(cmd) { try { return document.queryCommandState(cmd); } catch (e) { return false; } }
        function syncToolbar() {
          mark('b-bold', state('bold'));
          mark('b-italic', state('italic'));
          mark('b-under', state('underline'));
          mark('b-strike', state('strikeThrough'));
          mark('b-ul', state('insertUnorderedList'));
          mark('b-ol', state('insertOrderedList'));
          mark('b-h1', !!nodeIn('H1'));
          mark('b-h2', !!nodeIn('H2'));
          mark('b-h3', !!nodeIn('H3'));
          var inTable = !!nodeIn('TABLE');
          document.getElementById('tablebar').className = inTable ? 'show' : '';
        }

        /* ── modal ───────────────────────────────────────────────────────────── */
        function showModal(title, fields, okLabel, callback) {
          var overlay = document.getElementById('modal-overlay');
          var fieldsEl = document.getElementById('modal-fields');
          modalCallback = callback;
          document.getElementById('modal-title').textContent = title || '';
          document.getElementById('modal-ok').textContent = okLabel || 'Insert';
          fieldsEl.innerHTML = '';
          for (var i = 0; i < fields.length; i++) {
            var f = fields[i];
            var wrap = document.createElement('div');
            wrap.className = 'modal-field';
            var label = document.createElement('label');
            label.textContent = f.label || '';
            wrap.appendChild(label);
            var input = document.createElement('input');
            input.type = 'text';
            input.id = 'modal-field-' + i;
            if (f.mono) input.className = 'modal-input-mono';
            if (f.placeholder) input.placeholder = f.placeholder;
            input.value = f.value || '';
            input.onkeydown = function (e) {
              var key = e.keyCode || e.which;
              if (key === 13) { submitModal(); if (e.preventDefault) e.preventDefault(); }
              else if (key === 27) { hideModal(null); if (e.preventDefault) e.preventDefault(); }
            };
            wrap.appendChild(input);
            fieldsEl.appendChild(wrap);
          }
          overlay.className = '';
          setTimeout(function () {
            var first = document.getElementById('modal-field-0');
            if (first) { first.focus(); first.select(); }
          }, 0);
        }
        function hideModal(result) {
          document.getElementById('modal-overlay').className = 'modal-hidden';
          var cb = modalCallback;
          modalCallback = null;
          if (cb) cb(result);
        }
        function readModal() {
          var inputs = document.getElementById('modal-fields').getElementsByTagName('input');
          var values = [];
          for (var i = 0; i < inputs.length; i++) values.push(inputs[i].value);
          return values;
        }
        function submitModal() { hideModal(readModal()); }
        function onOverlayClick(e) {
          if (!e || e.target === document.getElementById('modal-overlay')) hideModal(null);
        }

        /* ── wiring ──────────────────────────────────────────────────────────── */
        function onKeyDown(e) {
          var ctrl = e.ctrlKey || e.metaKey;
          var key = e.keyCode || e.which;
          if (ctrl && !e.shiftKey && key === 75) { insertLink(); if (e.preventDefault) e.preventDefault(); return; }
          if (e.shiftKey && key === 9) { /* shift+tab in a table: previous cell */ }
          if (key === 9) {
            var cell = nodeIn('TD') || nodeIn('TH');
            if (cell) {
              var next = e.shiftKey ? cell.previousElementSibling : cell.nextElementSibling;
              if (next) { placeCaret(next); if (e.preventDefault) e.preventDefault(); return; }
            }
          }
          /* Enter on an empty checklist item ends the list instead of adding a dead row. */
          if (key === 13) {
            var li = nodeIn('LI');
            if (li && li.className.indexOf('task') >= 0) {
              var text = li.querySelector('.task-text');
              if (text && !text.textContent.replace(/ |\s/g, '').length) {
                var list = li.parentNode;
                list.removeChild(li);
                if (!list.children.length && list.parentNode) list.parentNode.removeChild(list);
                insertHtml('<p><br></p>');
              } else {
                var item = document.createElement('div');
                item.innerHTML = taskItemHtml('');
                li.parentNode.insertBefore(item.firstChild, li.nextSibling);
                placeCaret(li.nextSibling.querySelector('.task-text'));
                changed();
              }
              if (e.preventDefault) e.preventDefault();
            }
          }
        }
        function placeCaret(node) {
          if (!node) return;
          try {
            var range = document.createRange();
            range.selectNodeContents(node);
            range.collapse(true);
            var sel = window.getSelection();
            sel.removeAllRanges();
            sel.addRange(range);
          } catch (e) {}
        }
        function onEditorClick(e) {
          var t = e.target;
          if (t && t.className && String(t.className).indexOf('task-box') >= 0) {
            var li = t.parentNode;
            li.className = li.className.indexOf('done') >= 0 ? 'task' : 'task done';
            changed();
            return;
          }
          /* A bare image click selects it, so Delete removes the image rather than a character. */
          var imgs = ed().getElementsByTagName('img');
          for (var i = 0; i < imgs.length; i++) imgs[i].className = '';
          if (t && t.tagName === 'IMG') t.className = 'selected';
        }
        /* Closes the popover on any click outside it - including another toolbar button, which
           should put it away. The click that OPENS it is kept from reaching here by
           openSwatches(); without that it closed the popover inside the same event, which made
           the colour and highlight buttons look completely dead. */
        function onDocClick(e) {
          var box = document.getElementById('swatches');
          if (box.className === 'show' && !box.contains(e.target)) closeSwatches();
        }

        window.onload = function () {
          var editor = ed();
          editor.setAttribute('data-placeholder', 'Describe this instance...');
          editor.oninput = changed;
          editor.onkeyup = syncToolbar;
          editor.onmouseup = syncToolbar;
          editor.onkeydown = onKeyDown;
          editor.onpaste = onPaste;
          editor.onclick = onEditorClick;
          editor.onblur = flush;
          editor.ondragover = function (e) { editor.className = 'drop-target'; if (e.preventDefault) e.preventDefault(); };
          editor.ondragleave = function () { editor.className = ''; };
          editor.ondrop = onDrop;
          document.onclick = onDocClick;
          if (document.addEventListener) document.addEventListener('selectionchange', syncToolbar, false);
          syncToolbar();
        };
        </script>
        """;

    /// <summary>Markup for the editor's insert dialog, hidden until a toolbar action opens it.</summary>
    private const string EditorDocumentModal =
        """
        <div id="modal-overlay" class="modal-hidden" tabindex="-1" onclick="onOverlayClick(event)">
          <div id="modal-dialog" onclick="event.stopPropagation();">
            <div id="modal-title"></div>
            <div id="modal-fields"></div>
            <div id="modal-actions">
              <button type="button" id="modal-cancel" onclick="hideModal(null)">Cancel</button>
              <button type="button" id="modal-ok" onclick="submitModal()">Insert</button>
            </div>
          </div>
        </div>
        """;

    /// <summary>The inline handlers of the editor's own toolbar and dialog, which its content
    /// security policy allows by hash.</summary>
    private static IEnumerable<string> EditorInlineHandlers() =>
        System.Text.RegularExpressions.Regex.Matches(EditorDocumentToolbar + EditorDocumentModal, "\\son[a-z]+=\"([^\"]*)\"")
            .Select(m => System.Net.WebUtility.HtmlDecode(m.Groups[1].Value));

    /// <summary>The full HTML page the description editor loads, with the body sanitised.</summary>
    public static string WrapEditorDocument(string? bodyHtml, bool legacyIe = false)
    {
        // The body is sanitised after it is prepared: it can be another collaborator's description,
        // and Markdown turned into HTML here has not been through the sanitiser yet.
        bodyHtml = string.IsNullOrWhiteSpace(bodyHtml)
            ? "<p><br></p>"
            : SanitizeDescriptionHtml(PrepareEditorBodyHtml(bodyHtml)) ?? "<p><br></p>";

        var sb = new System.Text.StringBuilder();
        sb.Append("<!DOCTYPE html><html><head><meta charset=\"utf-8\"/>");
        if (legacyIe) sb.Append("<meta http-equiv=\"X-UA-Compatible\" content=\"IE=edge\"/>");
        else sb.Append(ContentSecurityPolicyMeta(EditorInlineHandlers()));
        sb.Append(Recolour(EditorDocumentStyles));
        // The theme's selection highlight, as in the read-only page. IE has no ::selection.
        if (!legacyIe && HtmlPalette.Current.SelectionStyle() is { Length: > 0 } selection)
            sb.Append("<style>").Append(selection).Append("</style>");
        // The script hard-codes palette colours too (swatch rows, the colour "Remove"), so it goes
        // through Recolour like the stylesheet.
        var script = Recolour(EditorDocumentScript);
        if (!legacyIe) script = script.Replace("<script>", $"<script nonce=\"{ScriptNonce}\">");
        sb.Append(script);
        sb.Append("</head><body>");
        sb.Append(EditorDocumentToolbar);
        sb.Append("<div id=\"editor-wrap\"><div id=\"editor\" class=\"content\" contenteditable=\"true\">");
        sb.Append(bodyHtml);
        sb.Append("</div><textarea id=\"source\" spellcheck=\"false\"></textarea></div>");
        sb.Append(EditorDocumentModal);
        sb.Append("</body></html>");
        return sb.ToString();
    }
}
