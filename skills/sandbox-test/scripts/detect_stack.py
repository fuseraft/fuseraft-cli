#!/usr/bin/env python3
"""Detect the primary language stack of a project directory.

Usage: python3 detect_stack.py [path]
  path  Directory to scan (default: current working directory)

Output: JSON to stdout with keys:
  stack        Short identifier (dotnet, nodejs, typescript, go, rust, python, java, unknown)
  display      Human-readable name
  markers      List of marker filenames that triggered detection
  shell        Shell assumed for command strings (bash or powershell)
  temp_dir     Platform temp directory — use as the parent for the harness directory
  scaffold     Command to initialize the harness project; substitute <name> and <ts>
  build        Build command (empty string if no build step needed)
  run          Run command; substitute <name> and <ts> for Python
  cleanup      Command to delete the harness directory; substitute <harness_dir>
  debug_idiom  One-line debug print example using the [DBG] prefix
"""

import glob
import json
import os
import sys
import tempfile


def _is_windows():
    return sys.platform == "win32"


# Each entry has unix and win variants for commands that differ across platforms.
# {temp} is substituted with the actual temp directory at runtime.
# <name>, <ts>, and <harness_dir> remain as agent-filled placeholders.
_STACKS = [
    {
        "stack": "dotnet",
        "display": ".NET (C#)",
        "glob_markers": ["*.csproj", "*.sln"],
        "exact_markers": ["global.json", "Directory.Build.props"],
        "scaffold_unix": "dotnet new console -o {temp}/harness-<name>-<ts> --force",
        "scaffold_win": "dotnet new console -o '{temp}\\harness-<name>-<ts>' --force",
        "build": "dotnet build",
        "run_unix": "dotnet run",
        "run_win": "dotnet run",
        "debug_idiom": 'Console.WriteLine($"[DBG] label={value}");',
    },
    {
        "stack": "typescript",
        "display": "TypeScript (Node.js)",
        "glob_markers": [],
        "exact_markers": ["tsconfig.json"],
        # TypeScript: scaffold creates the dir and installs tsx; file writing is done by the agent
        "scaffold_unix": "mkdir -p {temp}/harness-<name>-<ts> && cd {temp}/harness-<name>-<ts> && npm init -y && npm install -D tsx",
        "scaffold_win": "New-Item -ItemType Directory -Force '{temp}\\harness-<name>-<ts>'; Set-Location '{temp}\\harness-<name>-<ts>'; npm init -y; npm install -D tsx",
        "build": "",
        "run_unix": "npx tsx index.ts",
        "run_win": "npx tsx index.ts",
        "debug_idiom": "console.log('[DBG]', 'label:', value);",
    },
    {
        "stack": "nodejs",
        "display": "Node.js",
        "glob_markers": [],
        "exact_markers": ["package.json"],
        # Node.js: scaffold creates the dir; agent writes package.json and index.mjs
        "scaffold_unix": "mkdir -p {temp}/harness-<name>-<ts>",
        "scaffold_win": "New-Item -ItemType Directory -Force '{temp}\\harness-<name>-<ts>'",
        "build": "",
        "run_unix": "node index.mjs",
        "run_win": "node index.mjs",
        "debug_idiom": "console.log('[DBG]', 'label:', value);",
    },
    {
        "stack": "go",
        "display": "Go",
        "glob_markers": [],
        "exact_markers": ["go.mod"],
        "scaffold_unix": "mkdir -p {temp}/harness-<name>-<ts> && cd {temp}/harness-<name>-<ts> && go mod init harness",
        "scaffold_win": "New-Item -ItemType Directory -Force '{temp}\\harness-<name>-<ts>'; Set-Location '{temp}\\harness-<name>-<ts>'; go mod init harness",
        "build": "go build ./...",
        "run_unix": "go run .",
        "run_win": "go run .",
        "debug_idiom": 'fmt.Printf("[DBG] label=%v\\n", value)',
    },
    {
        "stack": "rust",
        "display": "Rust",
        "glob_markers": [],
        "exact_markers": ["Cargo.toml"],
        "scaffold_unix": "cargo new {temp}/harness-<name>-<ts> --name harness",
        "scaffold_win": "cargo new '{temp}\\harness-<name>-<ts>' --name harness",
        "build": "cargo build",
        "run_unix": "cargo run",
        "run_win": "cargo run",
        "debug_idiom": 'println!("[DBG] label={:?}", value);',
    },
    {
        "stack": "python",
        "display": "Python",
        "glob_markers": ["*.py"],
        "exact_markers": ["pyproject.toml", "setup.py", "setup.cfg", "requirements.txt", "Pipfile"],
        # Python: no scaffold — agent writes a single file directly
        "scaffold_unix": "",
        "scaffold_win": "",
        "build": "",
        "run_unix": "python3 {temp}/harness_<name>_<ts>.py",
        "run_win": "python {temp}\\harness_<name>_<ts>.py",
        "debug_idiom": "print(f'[DBG] label={value!r}')",
    },
    {
        "stack": "java",
        "display": "Java",
        "glob_markers": ["*.java"],
        "exact_markers": ["pom.xml", "build.gradle", "build.gradle.kts"],
        "scaffold_unix": "mkdir -p {temp}/harness-<name>-<ts>",
        "scaffold_win": "New-Item -ItemType Directory -Force '{temp}\\harness-<name>-<ts>'",
        "build": "javac Main.java",
        "run_unix": "java Main",
        "run_win": "java Main",
        "debug_idiom": 'System.out.println("[DBG] label=" + value);',
    },
]


def _find_markers(directory, stack):
    found = []
    for name in stack["exact_markers"]:
        if os.path.exists(os.path.join(directory, name)):
            found.append(name)
    for pattern in stack["glob_markers"]:
        matches = glob.glob(os.path.join(directory, pattern))
        found.extend(os.path.basename(m) for m in matches)
    return found


def detect(directory):
    directory = os.path.abspath(directory)
    is_win = _is_windows()
    temp = tempfile.gettempdir()
    shell = "powershell" if is_win else "bash"
    cleanup = (
        'Remove-Item -Recurse -Force "<harness_dir>"'
        if is_win else
        "rm -rf <harness_dir>"
    )

    for stack in _STACKS:
        markers = _find_markers(directory, stack)
        if markers:
            scaffold = stack["scaffold_win" if is_win else "scaffold_unix"].replace("{temp}", temp)
            run = stack["run_win" if is_win else "run_unix"].replace("{temp}", temp)
            return {
                "stack": stack["stack"],
                "display": stack["display"],
                "markers": markers[:5],
                "shell": shell,
                "temp_dir": temp,
                "scaffold": scaffold,
                "build": stack["build"],
                "run": run,
                "cleanup": cleanup,
                "debug_idiom": stack["debug_idiom"],
            }

    return {
        "stack": "unknown",
        "display": "Unknown",
        "markers": [],
        "shell": shell,
        "temp_dir": temp,
        "scaffold": "",
        "build": "",
        "run": "",
        "cleanup": cleanup,
        "debug_idiom": "",
        "error": (
            f"No recognized stack markers found in {directory}. "
            "Read references/stack-patterns.md and identify the stack manually."
        ),
    }


def main():
    path = sys.argv[1] if len(sys.argv) > 1 else os.getcwd()
    print(json.dumps(detect(path), indent=2))


if __name__ == "__main__":
    main()
