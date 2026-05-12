# Context Store

The context store lets you import files and directories from outside the sandbox so that every agent in a session has access to them as reference material — without needing to make a tool call to discover what's available.

## How it works

1. You run `fuseraft context add` before (or between) sessions to copy files into `.fuseraft/context/<name>/`.
2. When a session starts, fuseraft reads the index and appends a compact summary block to every agent's system prompt.
3. Agents call `read_file` with the listed path to access the content.

The context directory lives inside the project working directory, so it is always inside the sandbox. The index is stored at `.fuseraft/context/index.json`.

## The prompt summary

Agents see a block like this at the top of their system prompt:

```
CONTEXT — reference material imported for this session (use read_file to access):
  [db-schema] — Production database schema
    .fuseraft/context/db-schema/schema.sql  (12.4 KB, imported 2026-04-12)

  [specs]  (3 files, 48.2 KB total, imported 2026-04-12)
    .fuseraft/context/specs/
      api.md       (18.1 KB)
      data-model.md  (22.3 KB)
      ux-flows.md    (7.8 KB)
```

This means agents know what reference material exists from turn one, at zero tool-call cost.

## Managing context

### Import a file

```bash
fuseraft context add ~/docs/architecture.pdf
# Stored as: .fuseraft/context/architecture/architecture.pdf
```

```bash
fuseraft context add ~/data/schema.sql --name db-schema --description "Production database schema"
# Stored as: .fuseraft/context/db-schema/schema.sql
```

### Import a directory

```bash
fuseraft context add ~/specs/ --name specs --description "Product specifications"
# Stored as: .fuseraft/context/specs/<all files>
```

### List current context

```bash
fuseraft context list
```

Output:

```
 Name       Files   Size    Imported     Description
 db-schema      1  12.4 KB  2026-04-12   Production database schema
 specs          3  48.2 KB  2026-04-12   Product specifications

2 item(s) stored in .fuseraft/context
```

### Remove an item

```bash
fuseraft context remove db-schema
```

This deletes the files under `.fuseraft/context/db-schema/` and removes the entry from the index. When the last item is removed the `index.json` file is also deleted.

## Naming rules

The `--name` alias must contain only letters, digits, hyphens (`-`), and underscores (`_`). If `--name` is not supplied:

- For a file: the filename without extension is used (`schema.sql` → `schema`)
- For a directory: the directory name is used (`specs/` → `specs`)

Any character outside the allowed set in the derived name is replaced with a hyphen.

## Targeting a different project

All three commands accept `--dir <path>` to target a project directory other than the current working directory:

```bash
fuseraft context add ~/docs/runbook.md --dir ~/projects/my-app
fuseraft context list --dir ~/projects/my-app
fuseraft context remove runbook --dir ~/projects/my-app
```

## Document extraction

When you import a PDF, Word document, PowerPoint presentation, or Excel spreadsheet,
fuseraft automatically extracts the plain text at import time and stores a `.txt` version
in the context directory. Agents can then access the extracted text via `read_file` —
no special plugin required.

```
fuseraft context add ~/docs/architecture.pdf
# ✓ architecture — 1 file(s), 48.2 KB
#   Extracted from architecture.pdf: PDF — 24 page(s) → architecture.txt
```

**Supported formats:** `.pdf`, `.docx`, `.pptx`, `.xlsx`

If text extraction fails (encrypted document, corrupted file), the original binary is stored
instead and a warning is printed. Binary files cannot be read by agents via `read_file`.

For working with documents found *during* a session, or reading individual Excel sheets,
use the [`Document` plugin](plugins.md#document) directly.

## What to import

The context store works well for:

- **Database schemas** — schema SQL, ERDs, or migration history
- **API specifications** — OpenAPI/Swagger YAML, Postman collections
- **Architecture documents** — design docs, ADRs, system diagrams (PDF, DOCX)
- **Slide decks** — PPTX presentations extracted to slide-by-slide text
- **Spreadsheets** — XLSX workbooks with multiple sheets, each extracted as a table
- **Reference data** — seed data, sample payloads, fixture files
- **Task briefs** — detailed specs too long to paste into the task argument

For very large files (tens of MB), consider summarising or splitting the content first. The full file is injected into the prompt summary as a path reference — the actual content is only loaded when an agent calls `read_file`.

## CLI reference

See [CLI Reference — fuseraft context](cli-reference.md#fuseraft-context) for the full command specification.
