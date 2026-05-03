namespace fuseraft.Cli.DevUI;

/// <summary>
/// Self-contained single-page application served by DevUIServer at GET /.
/// No external dependencies — all CSS and JS is inlined.
///
/// Connects to GET /api/stream (Server-Sent Events) and renders agent messages
/// as they arrive. Each unique (agentName, turnIndex) pair is upserted into a
/// card so progressive/partial messages update the same card rather than creating
/// duplicates. Refreshing the page replays the full session from the beginning.
/// </summary>
internal static class DevUIHtml
{
    public static readonly string Page = """
        <!DOCTYPE html>
        <html lang="en">
        <head>
        <meta charset="UTF-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <title>Fuseraft DevUI</title>
        <style>
        *, *::before, *::after { box-sizing: border-box; margin: 0; padding: 0; }

        :root {
          --bg:      #0d1117;
          --surface: #161b22;
          --border:  #30363d;
          --text:    #c9d1d9;
          --muted:   #8b949e;
          --accent:  #58a6ff;
          --ok:      #3fb950;
          --err:     #f85149;
          --warn:    #d29922;
        }

        html, body {
          height: 100%;
          font-family: 'Cascadia Code', 'Fira Mono', 'SF Mono', Menlo, Consolas, monospace;
          font-size: 13px;
          line-height: 1.5;
          background: var(--bg);
          color: var(--text);
          display: flex;
          flex-direction: column;
          overflow: hidden;
        }

        /* Header */
        header {
          flex-shrink: 0;
          display: flex;
          align-items: center;
          gap: 12px;
          padding: 10px 18px;
          border-bottom: 1px solid var(--border);
          background: var(--surface);
        }

        .logo { color: var(--accent); font-weight: 700; font-size: 14px; }
        .hdiv { color: var(--border); }

        #session-info {
          display: none;
          align-items: center;
          gap: 10px;
          flex: 1;
          min-width: 0;
        }

        #task-str {
          color: var(--muted);
          font-size: 11px;
          overflow: hidden;
          white-space: nowrap;
          text-overflow: ellipsis;
          flex: 1;
        }

        .chip {
          font-size: 11px;
          padding: 2px 8px;
          border-radius: 4px;
          white-space: nowrap;
          border: 1px solid transparent;
        }
        .chip-blue { background: rgba(88,166,255,.12); border-color: rgba(88,166,255,.3); color: var(--accent); }
        .chip-ok   { background: rgba(63,185,80,.12);  border-color: rgba(63,185,80,.3);  color: var(--ok);     }
        .chip-err  { background: rgba(248,81,73,.12);  border-color: rgba(248,81,73,.3);  color: var(--err);    }
        .chip-warn { background: rgba(210,153,34,.12); border-color: rgba(210,153,34,.3); color: var(--warn);   }

        #status-chip { margin-left: auto; }

        /* Feed */
        main {
          flex: 1;
          min-height: 0;        /* prevent flex from overriding overflow-y */
          overflow-y: auto;
          padding: 14px 18px;
        }

        /* Thinking indicator */
        #thinking {
          display: none;
          align-items: center;
          gap: 8px;
          color: var(--muted);
          padding: 4px 0;
        }

        .spin {
          width: 13px;
          height: 13px;
          border: 2px solid var(--border);
          border-top-color: var(--accent);
          border-radius: 50%;
          animation: spin .8s linear infinite;
          flex-shrink: 0;
        }
        @keyframes spin { to { transform: rotate(360deg); } }

        /* Message cards */
        .card {
          background: var(--surface);
          border: 1px solid var(--border);
          border-radius: 6px;
          overflow: hidden;
          margin-bottom: 10px;
        }

        .card-top {
          display: flex;
          align-items: center;
          gap: 8px;
          padding: 7px 12px;
          background: rgba(255,255,255,.02);
          border-bottom: 1px solid var(--border);
        }

        .agent-pill {
          font-size: 11px;
          font-weight: 700;
          padding: 2px 9px;
          border-radius: 12px;
          flex-shrink: 0;
        }

        .turn-meta { color: var(--muted); font-size: 11px; }

        .card-stats {
          margin-left: auto;
          display: flex;
          gap: 10px;
          color: var(--muted);
          font-size: 11px;
        }

        .card-body {
          padding: 10px 12px;
          white-space: pre-wrap;
          word-break: break-word;
          line-height: 1.65;
        }

        .card-body pre {
          background: var(--bg);
          border: 1px solid var(--border);
          border-radius: 4px;
          padding: 9px 11px;
          overflow-x: auto;
          white-space: pre;
          margin: 6px 0;
          line-height: 1.4;
        }

        .card-body code { font-family: inherit; }

        .card.user-card .card-top {
          background: rgba(210,153,34,.04);
          border-color: rgba(210,153,34,.2);
        }
        </style>
        </head>
        <body>

        <header>
          <span class="logo">⚡ fuseraft devui</span>
          <span class="hdiv">|</span>
          <div id="session-info">
            <span class="chip chip-blue" id="sid-chip">—</span>
            <span id="task-str"></span>
          </div>
          <span class="chip chip-warn" id="status-chip">● connecting</span>
        </header>

        <main id="feed">
          <div id="thinking">
            <div class="spin"></div>
            <span id="think-lbl">starting…</span>
          </div>
        </main>

        <script>
        const feed     = document.getElementById('feed');
        const thinking = document.getElementById('thinking');
        const thinkLbl = document.getElementById('think-lbl');
        const sidChip  = document.getElementById('sid-chip');
        const taskStr  = document.getElementById('task-str');
        const sessInfo = document.getElementById('session-info');
        const statusCh = document.getElementById('status-chip');

        // Agent color palette
        const PALETTE = [
          '#7c9ef8','#f89c7c','#7cf8b3','#f8d87c',
          '#c07cf8','#f87ca7','#7cf8e0','#f8b87c',
        ];

        function agentColor(name) {
          let h = 0;
          for (const c of name) h = (h * 31 + c.charCodeAt(0)) & 0xffff;
          return PALETTE[h % PALETTE.length];
        }

        // Formatting helpers
        function fmtMs(ms) {
          return ms < 1000 ? ms + 'ms' : (ms / 1000).toFixed(1) + 's';
        }

        function fmtTokens(inp, out) {
          const parts = [];
          if (inp != null) parts.push('in:'  + Number(inp).toLocaleString());
          if (out != null) parts.push('out:' + Number(out).toLocaleString());
          return parts.join(' ');
        }

        // Content renderer (handles fenced code blocks)
        // Splits on ``` fences; odd indices are code blocks, even are plain text.
        const FENCE = /```([\s\S]*?)```/g;

        function renderContent(text, el) {
          el.textContent = '';
          FENCE.lastIndex = 0;
          let last = 0, m;

          while ((m = FENCE.exec(text)) !== null) {
            // Plain text before this code block
            if (m.index > last) {
              const span = document.createElement('span');
              span.textContent = text.slice(last, m.index);
              el.appendChild(span);
            }

            // Code block: strip the optional language identifier on the first line
            const nl   = m[1].indexOf('\n');
            const code = nl >= 0 ? m[1].slice(nl + 1) : m[1];
            const pre  = document.createElement('pre');
            const c    = document.createElement('code');
            c.textContent = code;
            pre.appendChild(c);
            el.appendChild(pre);

            last = m.index + m[0].length;
          }

          // Remaining plain text after the last fence
          if (last < text.length) {
            const span = document.createElement('span');
            span.textContent = text.slice(last);
            el.appendChild(span);
          }
        }

        // Card upsert
        function cardId(agentName, turnIndex) {
          return 'card-' + agentName.replace(/\W+/g, '_') + '-' + turnIndex;
        }

        function upsert(data) {
          const id     = cardId(data.agentName, data.turnIndex);
          const isUser = data.role === 'user';
          const color  = isUser ? null : agentColor(data.agentName);
          let   card   = document.getElementById(id);

          if (!card) {
            card = document.createElement('div');
            card.className = 'card' + (isUser ? ' user-card' : '');
            card.id = id;

            // Card header
            const top = document.createElement('div');
            top.className = 'card-top';

            const pill = document.createElement('span');
            pill.className = 'agent-pill';
            if (color) {
              pill.style.background = color + '22';
              pill.style.color      = color;
            }
            pill.textContent = data.agentName;
            top.appendChild(pill);

            const meta = document.createElement('span');
            meta.className = 'turn-meta';
            meta.textContent = 'Turn ' + (data.turnIndex + 1);
            top.appendChild(meta);

            const stats = document.createElement('div');
            stats.className = 'card-stats';

            const timing = document.createElement('span');
            timing.id = id + '-t';
            stats.appendChild(timing);

            const toks = document.createElement('span');
            toks.id = id + '-k';
            stats.appendChild(toks);

            top.appendChild(stats);

            // Card body
            const body = document.createElement('div');
            body.className = 'card-body';
            body.id = id + '-b';

            card.appendChild(top);
            card.appendChild(body);

            // Insert before the thinking indicator so new cards appear above it.
            feed.insertBefore(card, thinking);
          }

          // Update timing
          const timing = document.getElementById(id + '-t');
          if (timing && data.elapsedMs != null) timing.textContent = fmtMs(data.elapsedMs);

          // Update token stats
          const toks = document.getElementById(id + '-k');
          if (toks) {
            const s = fmtTokens(data.inputTokens, data.outputTokens);
            if (s) toks.textContent = s;
          }

          // Update content
          const body = document.getElementById(id + '-b');
          if (body) renderContent(data.content, body);

          feed.scrollTop = feed.scrollHeight;
        }

        // SSE connection
        let sessionEnded = false;

        const es = new EventSource('/api/stream');

        es.addEventListener('open', () => {
          statusCh.className   = 'chip chip-warn';
          statusCh.textContent = '● live';
        });

        es.addEventListener('error', () => {
          // Only show disconnected when the session did not end cleanly —
          // the server closes the SSE stream on session_end, which fires
          // this handler even on a successful completion.
          if (!sessionEnded) {
            statusCh.className   = 'chip chip-err';
            statusCh.textContent = '● disconnected';
          }
        });

        es.onmessage = function (e) {
          let evt;
          try { evt = JSON.parse(e.data); } catch { return; }
          const d = evt.data;

          switch (evt.type) {
            case 'session_start':
              sidChip.textContent  = d.sessionId;
              taskStr.textContent  = d.task;
              sessInfo.style.display = 'flex';
              document.title       = 'DevUI \u00b7 ' + d.sessionId;
              statusCh.className   = 'chip chip-ok';
              statusCh.textContent = '● live';
              break;

            case 'agent_starting':
              thinkLbl.textContent   = d.agentName + ' thinking\u2026';
              thinking.style.display = 'flex';
              feed.scrollTop         = feed.scrollHeight;
              break;

            case 'message':
              thinking.style.display = 'none';
              upsert(d);
              break;

            case 'session_end':
              sessionEnded = true;
              thinking.style.display = 'none';
              statusCh.className   = 'chip ' + (d.succeeded ? 'chip-ok' : 'chip-err');
              statusCh.textContent = d.succeeded ? '\u2713 complete' : '\u2717 failed';
              break;
          }
        };
        </script>

        </body>
        </html>
        """;
}
