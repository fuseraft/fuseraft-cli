using fuseraft.Core;

namespace fuseraft.Cli.Commands;

public static partial class InitTemplates
{
    /// <summary>
    /// Generates the <c>data</c> template (replaces <c>content</c>): DataEngineer → Analyst → Reporter
    /// state-machine pipeline for data analysis tasks. A <c>DataReady</c> contract gates Analysis;
    /// an <c>AnalysisComplete</c> contract gates Reporting, preventing the Reporter from fabricating
    /// analysis if the Analyst did not produce structured results.
    /// </summary>
    private static GeneratedConfig Data(string model, string? endpoint)
    {
        var engineer = $"""
            Name: DataEngineer
            Description: Fetches, cleans, and structures raw data; writes a schema manifest.
            Instructions: |
              You are a data engineer. Your job is to:
              1. Understand what data is needed for the analysis task.
              2. Acquire the data using available tools:
                 - Local files: use read_file / list_directory
                 - HTTP APIs: use http_get / http_post
                 - Shell pipelines: use shell_run (e.g. awk, jq, csvkit, pandas scripts)
              3. Clean and transform the data into a structured format (JSON, CSV, JSONL).
                 Write clean data files to {FuseraftPaths.LocalDataRoot}/.
              4. Write a manifest to {FuseraftPaths.LocalDataManifest} (JSON) with:
                   sources   — array of data origins (URL, file path, or command)
                   schema    — field names and types for each output file
                   row_count — estimated row count per file
                   notes     — any data quality issues, missing fields, or caveats
              5. Verify each output file exists and is non-empty before routing.
              When data is ready, call handoff(route_keyword: "DATA READY").
            Model:
              ModelId: {model}{EpAgent(endpoint)}
            Plugins:
              - FileSystem
              - Shell
              - Http
              - Handoff
            FunctionChoice: required
            {AgentFileOptions}
            """;

        var analyst = $"""
            Name: Analyst
            Description: Runs analysis scripts; computes statistics and identifies patterns.
            Instructions: |
              You are a data analyst. Your job is to:
              1. Read {FuseraftPaths.LocalDataManifest} to understand the data schema and quality
                 notes before touching any data file.
              2. Run analysis using shell_run (Python scripts, R, jq, awk, SQL via sqlite3, etc.).
                 Write analysis scripts to {FuseraftPaths.LocalDataRoot}/scripts/ if needed.
              3. Compute: summary statistics, distributions, trends, correlations, or whatever
                 the task requires. Run the exact commands and report the output verbatim.
              4. Write structured results to {FuseraftPaths.LocalDataAnalysisResults} (JSON):
                   summary      — 2–3 sentence plain-English overview
                   key_findings — array of named findings, each with:
                                    name, value (or range), significance, supporting_data
                   methodology  — what analysis was run and how
                   limitations  — data quality issues that affect interpretation
              5. Every finding must be traceable to a specific computation you ran.
                 Do not assert conclusions you did not compute.
              When analysis is complete, call handoff(route_keyword: "ANALYSIS COMPLETE").
            Model:
              ModelId: {model}{EpAgent(endpoint)}
            Plugins:
              - FileSystem
              - Shell
              - Handoff
            FunctionChoice: required
            {AgentFileOptions}
            """;

        var reporter = $"""
            Name: Reporter
            Description: Synthesises analysis results into a clear, well-structured report.
            Instructions: |
              You are a technical reporter. Your job is to:
              1. Read {FuseraftPaths.LocalDataAnalysisResults} for findings and methodology.
              2. Read {FuseraftPaths.LocalDataManifest} for data provenance and caveats.
              3. Write a final report to {FuseraftPaths.LocalDocs}/report.md:
                 - Lead with the answer / headline finding.
                 - Use headers, tables, and bullet points for scannability.
                 - For each key finding: state it, explain why it matters, cite the supporting
                   data (field name, computed value, or table).
                 - Include a Data section describing sources, row counts, and quality caveats.
                 - Acknowledge limitations explicitly; do not present uncertain findings as fact.
              When done, call handoff(route_keyword: "REPORT COMPLETE").
            Model:
              ModelId: {model}{EpAgent(endpoint)}
            Plugins:
              - FileSystem
              - Handoff
            FunctionChoice: required
            {AgentFileOptions}
            """;

        var mainConfig = $"""
            Orchestration:
              Name: Data Pipeline
              Description: >-
                DataEngineer fetches and structures raw data; Analyst computes findings;
                Reporter synthesises a final document. Contracts prevent the Reporter from
                fabricating analysis if the Analyst did not produce structured results.

              EvidenceStore:
                Path: {FuseraftPaths.LocalEvidence}

              Contracts:
                - Name: DataReady
                  Requires:
                    - Type: FileExists
                      Path: {FuseraftPaths.LocalDataManifest}

                - Name: AnalysisComplete
                  Requires:
                    - Type: FileExists
                      Path: {FuseraftPaths.LocalDataAnalysisResults}

              # Each agent lives in its own YAML file in agents/ — edit, version, or reuse
              # them independently across configs.
              Agents:
                - AgentFile: agents/data-engineer.yaml
                - AgentFile: agents/analyst.yaml
                - AgentFile: agents/reporter.yaml

              Selection:
                Type: statemachine
                StateMachine:
                  Initial: DataEngineering

                  States:
                    DataEngineering:
                      Agent: DataEngineer
                      Transitions:
                        - To: Analysis
                          Signal: "DATA READY"
                          Contract: DataReady

                    Analysis:
                      Agent: Analyst
                      Transitions:
                        - To: Reporting
                          Signal: "ANALYSIS COMPLETE"
                          Contract: AnalysisComplete

                    Reporting:
                      Agent: Reporter
                      Transitions:
                        - To: Done
                          Signal: "REPORT COMPLETE"

                    Done:
                      Agent: Reporter
                      Terminal: true

              Termination:
                Type: composite
                Strategies:
                  - Type: regex
                    Pattern: "REPORT COMPLETE"
                    AgentNames: [Reporter]
                  - Type: maxiterations
                    MaxIterations: 20
            {OptionalSections(model, endpoint)}
            """;

        return new GeneratedConfig(mainConfig, [
            ("agents/data-engineer.yaml", engineer),
            ("agents/analyst.yaml",       analyst),
            ("agents/reporter.yaml",      reporter),
        ]);
    }
}
