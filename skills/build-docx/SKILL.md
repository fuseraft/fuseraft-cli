---
name: build-docx
description: Generate a DOCX file from structured content, a template, or a description. Trigger when the user wants to produce a Word document, export content to .docx, fill in a DOCX template, or convert markdown/JSON/outline data to a formatted document.
---

# Build DOCX

Detect the project stack, pick the right DOCX library, gather content requirements, generate the code to produce the file, run it, and report the output path.

## When to Use

Use this skill when the user wants to:
- Generate a Word document (`.docx`) from data, an outline, or a description
- Export agent output — briefs, reports, changelogs, specs — to a DOCX file
- Fill in a DOCX template with variable content
- Convert a Markdown or JSON source into a formatted Word document

Do **not** use this skill for:
- PDF generation (use a PDF skill or pipeline instead)
- Editing an existing DOCX in place when a simple patch is sufficient — call `write_file` directly
- Generating HTML or plain-text documents

## Workflow

### Step 1: Detect the Stack

Run the detection script to identify the project language and available DOCX libraries:

```bash
python3 scripts/detect_docx_stack.py <project-root>
```

Returns JSON with `language`, `available_libraries`, and `recommended`.

If the script is unavailable, infer from project files:
- `.csproj` / `.sln` → .NET → recommend `DocumentFormat.OpenXml` or `DocX`
- `package.json` → Node.js → recommend `docx` (npm)
- `pyproject.toml` / `requirements.txt` / `setup.py` → Python → recommend `python-docx`
- `go.mod` → Go → recommend `unioffice` or shell out to a Python helper script
- No match → default to a standalone Python helper script using `python-docx`

### Step 2: Gather Content Requirements

Ask these questions. Extract answers from the user's description if already provided.

1. **Output path** — where should the `.docx` be written? Default: `output/<slug>.docx`
2. **Content source** — is the content already in a file (Markdown, JSON, plain text), or should the skill generate it from a description?
3. **Document structure** — which elements are needed?
   - Title / subtitle
   - Headings (H1, H2, H3)
   - Paragraphs of body text
   - Bulleted or numbered lists
   - Tables (rows × columns)
   - Images (file paths)
   - Code blocks / monospace sections
   - Page breaks
4. **Styling** — should it match a corporate template? If yes, ask for the template `.docx` path (the library will clone its styles).
5. **Variable substitution** — if a template is provided, does it contain `{{placeholders}}`? If yes, collect the variable map.

### Step 3: Choose the Approach

| Situation | Approach |
|---|---|
| Template `.docx` provided | Clone the template, replace placeholders, append dynamic sections |
| Markdown source file | Parse headings/paragraphs/lists, map to document elements |
| JSON / structured data | Iterate records, build tables or repeated sections |
| Free-form description | Generate content inline, write directly to a new document |

### Step 4: Generate the Builder Code

Write a self-contained script (Python helper preferred for portability; native language module otherwise) that:

1. Accepts the output path and any data source as arguments or embedded constants.
2. Creates or opens the document.
3. Appends all required elements in order.
4. Saves the file.

**Python (`python-docx`) snippet reference — load `references/python-docx-patterns.md` for the full pattern library.**

**Node.js (`docx`) snippet reference — load `references/node-docx-patterns.md` for the full pattern library.**

**.NET (`DocumentFormat.OpenXml`) snippet reference — load `references/dotnet-openxml-patterns.md` for the full pattern library.**

Keep the script focused: one function per element type, one `main()` entry point that wires them together.

### Step 5: Install the Dependency (If Needed)

Check whether the required library is already installed before running any install command.

| Library | Check | Install |
|---|---|---|
| `python-docx` | `python3 -c "import docx"` | `pip install python-docx` |
| `docx` (npm) | `node -e "require('docx')"` | `npm install docx` |
| `DocumentFormat.OpenXml` | check `.csproj` for package ref | `dotnet add package DocumentFormat.OpenXml` |
| `DocX` | check `.csproj` for package ref | `dotnet add package DocX` |

If installation requires elevated permissions or is disallowed by policy, write a portable Python helper instead and call it via `shell_run`.

### Step 6: Run the Builder

Call `shell_run` to execute the script:

```bash
python3 scripts/build_docx.py  # or node build_docx.js, etc.
```

Capture stdout and stderr. If the command fails:
- Check for missing imports → re-run Step 5.
- Check for path errors → verify the output directory exists; create it with `mkdir -p` if needed.
- Check for content errors (empty tables, missing image paths) → fix the script and retry.

Do not exceed 3 retry attempts. If the document still fails to generate, report the error to the user with the full stderr output.

### Step 7: Verify and Report

1. Confirm the output file exists: `ls -lh <output-path>`
2. Report the absolute path to the user.
3. If the file is under 5 MB, offer to describe the document structure (element count by type).
4. If a template was used, note any placeholders that were left unfilled.

## References

- `references/python-docx-patterns.md` — Common `python-docx` patterns: headings, tables, images, styles, template cloning
- `references/node-docx-patterns.md` — Common `docx` (npm) patterns: Paragraph, Table, ImageRun, styles
- `references/dotnet-openxml-patterns.md` — Common `DocumentFormat.OpenXml` patterns: body elements, table builder, style parts
