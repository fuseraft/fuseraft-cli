#!/usr/bin/env python3
"""
Detect project stack and available DOCX libraries.
Usage: python3 detect_docx_stack.py <project-root>
Output: JSON with language, available_libraries, recommended
"""
import json
import os
import subprocess
import sys


def check_python_lib(name):
    try:
        subprocess.run(
            [sys.executable, "-c", f"import {name}"],
            capture_output=True, check=True
        )
        return True
    except subprocess.CalledProcessError:
        return False


def check_node_lib(name, project_root):
    nm = os.path.join(project_root, "node_modules", name)
    return os.path.isdir(nm)


def detect(project_root):
    files = set()
    for root, _, fnames in os.walk(project_root):
        if any(skip in root for skip in (".git", "node_modules", "bin", "obj")):
            continue
        for f in fnames:
            files.add(f.lower())

    has_csproj = any(f.endswith(".csproj") or f.endswith(".sln") for f in files)
    has_package_json = "package.json" in files
    has_python = any(f in files for f in ("pyproject.toml", "setup.py", "requirements.txt"))
    has_go = "go.mod" in files

    if has_csproj:
        language = "dotnet"
        available = []
        # Check .csproj files for package references
        for root, _, fnames in os.walk(project_root):
            for f in fnames:
                if f.endswith(".csproj"):
                    content = open(os.path.join(root, f)).read()
                    if "DocumentFormat.OpenXml" in content:
                        available.append("DocumentFormat.OpenXml")
                    if "DocX" in content:
                        available.append("DocX")
        recommended = "DocX" if "DocX" in available else "DocumentFormat.OpenXml"
    elif has_package_json:
        language = "nodejs"
        available = [n for n in ("docx",) if check_node_lib(n, project_root)]
        recommended = "docx"
    elif has_python:
        language = "python"
        available = [n for n in ("docx",) if check_python_lib("docx")]
        recommended = "python-docx"
    elif has_go:
        language = "go"
        available = ["python-docx (helper script)"] if check_python_lib("docx") else []
        recommended = "python-docx (helper script)"
    else:
        language = "unknown"
        available = ["python-docx (helper script)"] if check_python_lib("docx") else []
        recommended = "python-docx (helper script)"

    print(json.dumps({
        "language": language,
        "available_libraries": available,
        "recommended": recommended,
    }, indent=2))


if __name__ == "__main__":
    root = sys.argv[1] if len(sys.argv) > 1 else "."
    detect(os.path.abspath(root))
