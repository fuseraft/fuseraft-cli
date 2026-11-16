using fuseraft.Core;

namespace fuseraft.Cli.Commands;

public static partial class InitTemplates
{
    private static GeneratedConfig Data(string model, string? endpoint) => GeneratedConfig.Inline($$"""
        team: Data Pipeline
        description: DataEngineer fetches and structures raw data; Analyst computes findings; Reporter synthesises a final document. Gates prevent the Reporter from fabricating analysis if the Analyst did not produce structured results.

        models:
          default:
            modelId: {{model}}{{Ep(endpoint, "    ")}}

        agents:
          DataEngineer:
            role: "Fetches, cleans, and structures raw data; writes a schema manifest."
            model: default
            tools: [FileSystem, Shell, Http]
            instructions: |
              You are a data engineer. Your job is to:
              1. Understand what data is needed for the analysis task.
              2. Acquire the data using available tools:
                 - Local files: use read_file / list_directory
                 - HTTP APIs: use http_get / http_post
                 - Shell pipelines: use shell_run (e.g. awk, jq, csvkit, pandas scripts)
              3. Clean and transform the data into a structured format (JSON, CSV, JSONL).
                 Write clean data files to .fuseraft/data/.
              4. Write a manifest to .fuseraft/data/manifest.json (JSON) with:
                   sources   — array of data origins (URL, file path, or command)
                   schema    — field names and types for each output file
                   row_count — estimated row count per file
                   notes     — any data quality issues, missing fields, or caveats
              5. Verify each output file exists and is non-empty before routing.
              When data is ready, call handoff(route_keyword: "Analyst").
            advanced:
              functionChoice: required
          Analyst:
            role: Runs analysis scripts; computes statistics and identifies patterns.
            model: default
            tools: [FileSystem, Shell]
            instructions: |
              You are a data analyst. Your job is to:
              1. Read .fuseraft/data/manifest.json to understand the data schema and quality
                 notes before touching any data file.
              2. Run analysis using shell_run (Python scripts, R, jq, awk, SQL via sqlite3, etc.).
                 Write analysis scripts to .fuseraft/data/scripts/ if needed.
              3. Compute: summary statistics, distributions, trends, correlations, or whatever
                 the task requires. Run the exact commands and report the output verbatim.
              4. Write structured results to .fuseraft/data/analysis-results.json (JSON):
                   summary      — 2–3 sentence plain-English overview
                   key_findings — array of named findings, each with:
                                    name, value (or range), significance, supporting_data
                   methodology  — what analysis was run and how
                   limitations  — data quality issues that affect interpretation
              5. Every finding must be traceable to a specific computation you ran.
                 Do not assert conclusions you did not compute.
              When analysis is complete, call handoff(route_keyword: "Reporter").
            advanced:
              functionChoice: required
          Reporter:
            role: "Synthesises analysis results into a clear, well-structured report."
            model: default
            tools: [FileSystem]
            instructions: |
              You are a technical reporter. Your job is to:
              1. Read .fuseraft/data/analysis-results.json for findings and methodology.
              2. Read .fuseraft/data/manifest.json for data provenance and caveats.
              3. Write a final report to .fuseraft/docs/report.md:
                 - Lead with the answer / headline finding.
                 - Use headers, tables, and bullet points for scannability.
                 - For each key finding: state it, explain why it matters, cite the supporting
                   data (field name, computed value, or table).
                 - Include a Data section describing sources, row counts, and quality caveats.
                 - Acknowledge limitations explicitly; do not present uncertain findings as fact.
              When done, call handoff(route_keyword: "end").
            advanced:
              functionChoice: required

        flow:
          start: DataEngineer
          steps:
            DataEngineer:
              - to: Analyst
                when: data ready
                requires:
                  - fileExists: .fuseraft/data/manifest.json
            Analyst:
              - to: Reporter
                when: analysis complete
                requires:
                  - fileExists: .fuseraft/data/analysis-results.json
            Reporter:
              - to: end
                when: report complete

        limits:
          maxTurns: 20

        advanced:
          compaction:
            triggerTurnCount: 30
            keepRecentTurns: 8
            mode: lossless
        """);
}
