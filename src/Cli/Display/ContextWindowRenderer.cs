using System.Text;
using System.Text.Json;
using fuseraft.Orchestration;

namespace fuseraft.Cli.Display;

/// <summary>
/// Reads context-window snapshot and event JSONL files and writes a self-contained
/// Chart.js HTML file with a per-turn token bar chart (top) and a cumulative input
/// token line chart (bottom).  Event annotations (validation_fail, tool_blocked) are
/// overlaid on both charts when an events file is present.
/// </summary>
public static class ContextWindowRenderer
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy        = JsonNamingPolicy.SnakeCaseLower,
    };

    private static readonly HashSet<string> UsefulEventTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        EventTypes.TurnEnd, EventTypes.ValidationFail, EventTypes.ToolBlocked, EventTypes.ContextAssembly,
    };

    /// <summary>
    /// Reads <paramref name="snapshotsPath"/> (and optionally <paramref name="eventsPath"/>),
    /// filters to <paramref name="sessionId"/>, and writes a Chart.js HTML visualization
    /// to <paramref name="outputPath"/>.  Returns true if the file was written.
    /// </summary>
    public static async Task<bool> RenderAsync(
        string  snapshotsPath,
        string  outputPath,
        string  sessionId,
        string? eventsPath = null)
    {
        try
        {
            var snapshots = await LoadSnapshotsAsync(snapshotsPath, sessionId);
            if (snapshots.Count == 0) return false;

            var events = await LoadEventsAsync(eventsPath, sessionId);
            var html   = BuildHtml(snapshots, events, sessionId);

            var dir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            await File.WriteAllTextAsync(outputPath, html, Encoding.UTF8);
            return true;
        }
        catch { return false; }
    }

    private static async Task<List<Snapshot>> LoadSnapshotsAsync(string path, string sessionId)
    {
        if (!File.Exists(path)) return [];

        var result = new List<Snapshot>();
        foreach (var line in await File.ReadAllLinesAsync(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var s = JsonSerializer.Deserialize<Snapshot>(line, JsonOpts);
                if (s is not null && string.Equals(s.Session, sessionId, StringComparison.OrdinalIgnoreCase))
                    result.Add(s);
            }
            catch { /* skip malformed lines */ }
        }
        return result;
    }

    private static async Task<List<EventEntry>> LoadEventsAsync(string? path, string sessionId)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return [];

        var result = new List<EventEntry>();
        foreach (var line in await File.ReadAllLinesAsync(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var e = JsonSerializer.Deserialize<EventEntry>(line, JsonOpts);
                if (e is not null
                    && string.Equals(e.Session, sessionId, StringComparison.OrdinalIgnoreCase)
                    && e.EventType is { } et
                    && UsefulEventTypes.Contains(et))
                    result.Add(e);
            }
            catch { /* skip malformed lines */ }
        }
        return result;
    }

    private static string BuildHtml(List<Snapshot> snapshots, List<EventEntry> events, string sessionId)
    {
        var snapshotsJson = JsonSerializer.Serialize(snapshots, new JsonSerializerOptions
        {
            PropertyNamingPolicy   = JsonNamingPolicy.SnakeCaseLower,
            WriteIndented          = false,
        });

        var eventsJson = events.Count > 0
            ? JsonSerializer.Serialize(events, new JsonSerializerOptions
              {
                  PropertyNamingPolicy   = JsonNamingPolicy.SnakeCaseLower,
                  WriteIndented          = false,
              })
            : "[]";

        var warnAt    = snapshots.FirstOrDefault(s => s.WarnAt    is > 0)?.WarnAt    ?? 0;
        var cutoverAt = snapshots.FirstOrDefault(s => s.CutoverAt is > 0)?.CutoverAt ?? 0;

        return $$"""
            <!DOCTYPE html>
            <html lang="en">
            <head>
              <meta charset="UTF-8">
              <meta name="viewport" content="width=device-width, initial-scale=1.0">
              <title>Context Window &#8212; {{sessionId}}</title>
              <script src="https://cdn.jsdelivr.net/npm/chart.js@4.4.4/dist/chart.umd.min.js"></script>
              <script src="https://cdn.jsdelivr.net/npm/chartjs-plugin-annotation@3.0.1/dist/chartjs-plugin-annotation.min.js"></script>
              <style>
                *, *::before, *::after { box-sizing: border-box; margin: 0; padding: 0; }
                body {
                  font-family: ui-monospace, "Cascadia Code", "Fira Code", monospace;
                  background: #0d1117;
                  color: #e6edf3;
                  padding: 24px;
                  min-height: 100vh;
                }
                header { margin-bottom: 20px; }
                header h1 { font-size: 15px; font-weight: 600; color: #e6edf3; }
                header p  { font-size: 12px; color: #8b949e; margin-top: 4px; }
                .section { margin-bottom: 20px; }
                .section-header {
                  display: flex;
                  align-items: center;
                  gap: 12px;
                  margin-bottom: 10px;
                }
                .section-title {
                  font-size: 12px;
                  font-weight: 600;
                  color: #8b949e;
                  text-transform: uppercase;
                  letter-spacing: 0.05em;
                }
                .toggles { display: flex; gap: 6px; }
                .toggle {
                  font-family: inherit;
                  font-size: 11px;
                  padding: 3px 10px;
                  border-radius: 4px;
                  border: 1px solid #30363d;
                  background: #161b22;
                  color: #8b949e;
                  cursor: pointer;
                  transition: background 0.15s, color 0.15s, border-color 0.15s;
                }
                .toggle.active { background: #21262d; color: #e6edf3; border-color: #484f58; }
                .toggle:hover  { background: #21262d; color: #e6edf3; }
                .chart-wrap {
                  background: #161b22;
                  border: 1px solid #21262d;
                  border-radius: 6px;
                  padding: 20px;
                  position: relative;
                }
                .chart-wrap.bar-chart  { height: 340px; }
                .chart-wrap.line-chart { height: 520px; }
                footer { margin-top: 12px; font-size: 11px; color: #484f58; }
              </style>
            </head>
            <body>
              <header>
                <h1>Context Window Visualization</h1>
                <p>Session <code>{{sessionId}}</code> &mdash; per-turn tokens and cumulative input token growth per agent</p>
              </header>

              <div class="section">
                <div class="section-header">
                  <span class="section-title">Per-Turn Tokens</span>
                  <div class="toggles">
                    <button id="btn-input"  class="toggle active">Input</button>
                    <button id="btn-output" class="toggle active">Output</button>
                  </div>
                </div>
                <div class="chart-wrap bar-chart">
                  <canvas id="barChart"></canvas>
                </div>
              </div>

              <div class="section">
                <div class="section-header">
                  <span class="section-title">Cumulative Input Tokens</span>
                </div>
                <div class="chart-wrap line-chart">
                  <canvas id="lineChart"></canvas>
                </div>
              </div>

              <footer>
                Generated by fuseraft-cli &mdash; compaction events shown as vertical markers.
                Requires internet for Chart.js CDN.
              </footer>

              <script>
            Chart.register(window['chartjs-plugin-annotation']);

            const SNAPSHOTS = {{snapshotsJson}};
            const EVENTS    = {{eventsJson}};
            const WARN_AT    = {{warnAt}};
            const CUTOVER_AT = {{cutoverAt}};

            const PALETTE = [
              '#58a6ff', '#3fb950', '#d2a8ff', '#ffa657',
              '#f78166', '#79c0ff', '#56d364', '#e3b341',
            ];

            // ── Bar chart: per-turn input / output tokens ─────────────────────────────

            const turnAgg = new Map(); // turn → { input, output, agents[] }
            for (const s of SNAPSHOTS) {
              if (s.agent === 'system') continue;
              if (!turnAgg.has(s.turn)) turnAgg.set(s.turn, { input: 0, output: 0, agents: [] });
              const t = turnAgg.get(s.turn);
              t.input  += s.turn_input_tokens;
              t.output += s.turn_output_tokens;
              if (!t.agents.includes(s.agent)) t.agents.push(s.agent);
            }
            const sortedTurns = [...turnAgg.keys()].sort((a, b) => a - b);
            const barLabels   = sortedTurns.map(String);

            // context_assembly lookup: "agent|turn" → payload, for tooltip enrichment
            const ctxAssembly = {};
            for (const e of EVENTS) {
              if (e.event_type === 'context_assembly' && e.turn != null && e.agent && e.payload) {
                ctxAssembly[e.agent + '|' + e.turn] = e.payload;
              }
            }

            // Event annotations shared across both charts (validation_fail / tool_blocked)
            function buildEvtAnnotations(useStringX) {
              const out = {};
              // Deduplicate by turn+type so we don't stack multiple identical markers.
              const seen = new Set();
              EVENTS.filter(e => e.turn != null && (e.event_type === 'validation_fail' || e.event_type === 'tool_blocked'))
                .forEach((e, i) => {
                  const dedup = e.event_type + '|' + e.turn;
                  if (seen.has(dedup)) return;
                  seen.add(dedup);
                  const isFail  = e.event_type === 'validation_fail';
                  const color   = isFail ? '#f85149' : '#e3b341';
                  const icon    = isFail ? '⚠' : '⛔';
                  const xVal    = useStringX ? String(e.turn) : e.turn;
                  out['evt_' + i] = {
                    type: 'line',
                    xMin: xVal, xMax: xVal,
                    borderColor: color,
                    borderWidth: 1,
                    borderDash: [3, 3],
                    label: {
                      display: true,
                      content: icon + ' ' + e.event_type,
                      position: 'start',
                      color: color,
                      backgroundColor: '#0d1117cc',
                      font: { size: 9 },
                      yAdjust: isFail ? 0 : 16,
                    },
                  };
                });
              return out;
            }

            const barChart = new Chart(document.getElementById('barChart'), {
              type: 'bar',
              data: {
                labels: barLabels,
                datasets: [
                  {
                    label: 'Input Tokens',
                    data: sortedTurns.map(t => turnAgg.get(t).input),
                    backgroundColor: '#58a6ff30',
                    borderColor: '#58a6ff',
                    borderWidth: 1,
                    borderRadius: 3,
                  },
                  {
                    label: 'Output Tokens',
                    data: sortedTurns.map(t => turnAgg.get(t).output),
                    backgroundColor: '#3fb95030',
                    borderColor: '#3fb950',
                    borderWidth: 1,
                    borderRadius: 3,
                  },
                ],
              },
              options: {
                responsive: true,
                maintainAspectRatio: false,
                interaction: { mode: 'index', intersect: false },
                scales: {
                  x: {
                    title: { display: true, text: 'Turn', color: '#8b949e', font: { size: 12 } },
                    ticks: { color: '#8b949e' },
                    grid:  { color: '#21262d' },
                  },
                  y: {
                    title: { display: true, text: 'Tokens', color: '#8b949e', font: { size: 12 } },
                    ticks: { color: '#8b949e', callback: v => v.toLocaleString() },
                    grid:  { color: '#21262d' },
                    beginAtZero: true,
                  },
                },
                plugins: {
                  legend: {
                    labels: { color: '#e6edf3', font: { size: 12 }, boxWidth: 12, padding: 16 },
                  },
                  tooltip: {
                    backgroundColor: '#161b22',
                    borderColor: '#30363d',
                    borderWidth: 1,
                    titleColor: '#e6edf3',
                    bodyColor: '#8b949e',
                    callbacks: {
                      title: items => {
                        const turn   = sortedTurns[items[0].dataIndex];
                        const agents = turnAgg.get(turn)?.agents ?? [];
                        return 'Turn ' + turn + (agents.length ? '  ·  ' + agents.join(', ') : '');
                      },
                      label: ctx => ctx.dataset.label + ': ' + ctx.parsed.y.toLocaleString(),
                      afterBody: items => {
                        const turn   = sortedTurns[items[0].dataIndex];
                        const agents = turnAgg.get(turn)?.agents ?? [];
                        const lines  = [];
                        for (const agent of agents) {
                          const ca = ctxAssembly[agent + '|' + turn];
                          if (!ca) continue;
                          lines.push('');
                          if (ca.context_chars   != null) lines.push('  context: ' + ca.context_chars.toLocaleString() + ' chars');
                          if (ca.tool_count      != null) lines.push('  tools: '   + ca.tool_count);
                          if (ca.assembly_ms     != null) lines.push('  assembly: '+ ca.assembly_ms + ' ms');
                        }
                        return lines;
                      },
                    },
                  },
                  annotation: { annotations: buildEvtAnnotations(true) },
                },
              },
            });

            // Toggle buttons
            document.getElementById('btn-input').addEventListener('click', function () {
              const meta = barChart.getDatasetMeta(0);
              meta.hidden = !meta.hidden;
              barChart.update();
              this.classList.toggle('active', !meta.hidden);
            });
            document.getElementById('btn-output').addEventListener('click', function () {
              const meta = barChart.getDatasetMeta(1);
              meta.hidden = !meta.hidden;
              barChart.update();
              this.classList.toggle('active', !meta.hidden);
            });

            // ── Line chart: cumulative input tokens per agent ─────────────────────────

            const agentMap = {};
            for (const s of SNAPSHOTS) {
              if (s.agent === 'system') continue;
              (agentMap[s.agent] ??= []).push(s);
            }

            const lineDatasets = Object.entries(agentMap).map(([agent, snaps], i) => {
              const color = PALETTE[i % PALETTE.length];
              return {
                label: agent,
                data: snaps.map(s => ({ x: s.turn, y: s.cumulative_input_tokens })),
                borderColor: color,
                backgroundColor: color + '18',
                pointBackgroundColor: color,
                pointRadius: 4,
                pointHoverRadius: 6,
                borderWidth: 2,
                tension: 0.25,
                fill: false,
              };
            });

            const compactionTurns = SNAPSHOTS
              .filter(s => s.agent === 'system' && s.compaction_occurred)
              .map(s => s.turn);

            const lineAnnotations = buildEvtAnnotations(false);

            if (WARN_AT > 0) {
              lineAnnotations.warnLine = {
                type: 'line',
                yMin: WARN_AT, yMax: WARN_AT,
                borderColor: '#e3b341',
                borderWidth: 1,
                borderDash: [5, 4],
                label: {
                  display: true,
                  content: 'warn_at ' + WARN_AT.toLocaleString(),
                  position: 'start',
                  color: '#e3b341',
                  backgroundColor: 'transparent',
                  font: { size: 11 },
                },
              };
            }

            if (CUTOVER_AT > 0) {
              lineAnnotations.cutoverLine = {
                type: 'line',
                yMin: CUTOVER_AT, yMax: CUTOVER_AT,
                borderColor: '#f85149',
                borderWidth: 1,
                borderDash: [5, 4],
                label: {
                  display: true,
                  content: 'cutover_at ' + CUTOVER_AT.toLocaleString(),
                  position: 'start',
                  color: '#f85149',
                  backgroundColor: 'transparent',
                  font: { size: 11 },
                },
              };
            }

            compactionTurns.forEach((turn, i) => {
              lineAnnotations['compaction_' + i] = {
                type: 'line',
                xMin: turn, xMax: turn,
                borderColor: '#8b949e',
                borderWidth: 1,
                borderDash: [3, 3],
                label: {
                  display: true,
                  content: '⚡ compact',
                  position: 'start',
                  color: '#8b949e',
                  backgroundColor: '#0d1117cc',
                  font: { size: 10 },
                  yAdjust: 8,
                },
              };
            });

            new Chart(document.getElementById('lineChart'), {
              type: 'line',
              data: { datasets: lineDatasets },
              options: {
                responsive: true,
                maintainAspectRatio: false,
                interaction: { mode: 'index', intersect: false },
                scales: {
                  x: {
                    type: 'linear',
                    title: {
                      display: true,
                      text: 'Turn',
                      color: '#8b949e',
                      font: { size: 12 },
                    },
                    ticks: { color: '#8b949e', stepSize: 1 },
                    grid:  { color: '#21262d' },
                  },
                  y: {
                    title: {
                      display: true,
                      text: 'Cumulative Input Tokens',
                      color: '#8b949e',
                      font: { size: 12 },
                    },
                    ticks: {
                      color: '#8b949e',
                      callback: v => v.toLocaleString(),
                    },
                    grid: { color: '#21262d' },
                    beginAtZero: true,
                  },
                },
                plugins: {
                  legend: {
                    labels: {
                      color: '#e6edf3',
                      font: { size: 12 },
                      boxWidth: 12,
                      padding: 16,
                    },
                  },
                  tooltip: {
                    backgroundColor: '#161b22',
                    borderColor: '#30363d',
                    borderWidth: 1,
                    titleColor: '#e6edf3',
                    bodyColor: '#8b949e',
                    callbacks: {
                      title: items => 'Turn ' + items[0].parsed.x,
                      label: ctx => {
                        const s = SNAPSHOTS.find(s => s.agent === ctx.dataset.label && s.turn === ctx.parsed.x);
                        if (!s) return ctx.dataset.label + ': ' + ctx.parsed.y.toLocaleString();
                        const lines = [
                          ctx.dataset.label + ': ' + s.cumulative_input_tokens.toLocaleString() + ' cumulative',
                          '  └ this turn: in=' + s.turn_input_tokens.toLocaleString()
                                + '  out=' + s.turn_output_tokens.toLocaleString(),
                        ];
                        const ca = ctxAssembly[s.agent + '|' + s.turn];
                        if (ca) {
                          if (ca.context_chars != null) lines.push('  context: ' + ca.context_chars.toLocaleString() + ' chars');
                          if (ca.tool_count    != null) lines.push('  tools: '   + ca.tool_count);
                        }
                        return lines;
                      },
                    },
                  },
                  annotation: { annotations: lineAnnotations },
                },
              },
            });
              </script>
            </body>
            </html>
            """;
    }

    private sealed record Snapshot(
        string?  Ts,
        string?  Session,
        string   Agent,
        int      Turn,
        int      TurnInputTokens,
        int      TurnOutputTokens,
        int      CumulativeInputTokens,
        int?     WarnAt,
        int?     CutoverAt,
        bool?    CompactionOccurred);

    private sealed record EventEntry(
        string?       Ts,
        string?       Session,
        string?       Agent,
        int?          Turn,
        string?       EventType,
        JsonElement?  Payload);
}
