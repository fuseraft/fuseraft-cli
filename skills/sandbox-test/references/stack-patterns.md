# Stack Patterns

Per-stack reference for sandbox harness creation. For each stack: detection markers, scaffold command, build command, run command, debug output idiom, and notes.

Use `<harness_dir>` as a placeholder for the full harness path. Form it as:
- **bash / zsh:** `$TMPDIR/harness-<name>-<ts>` or `$(python3 -c "import tempfile; print(tempfile.gettempdir())")/harness-<name>-<ts>`
- **PowerShell:** `"$env:TEMP\harness-<name>-<ts>"`

The `detect_stack` script resolves the correct temp directory and shell automatically — read its output instead of constructing paths manually.

**stderr capture:** `2>&1` works on bash, zsh, and PowerShell. Append it to any run command to merge stderr with stdout.

---

## .NET (C#)

**Markers:** `*.csproj`, `*.sln`, `global.json`, `Directory.Build.props`

**Scaffold:**
```
# bash
dotnet new console -o <harness_dir> --force

# PowerShell
dotnet new console -o '<harness_dir>' --force
```

`dotnet` accepts both forward and back slashes on all platforms; only the quoting convention differs.

**Build:** `dotnet build`
**Run:** `dotnet run`
**Cleanup:**
```
rm -rf <harness_dir>          # bash
Remove-Item -Recurse -Force '<harness_dir>'   # PowerShell
```

**Debug idiom:**
```csharp
Console.WriteLine($"[DBG] label={value}");
Console.WriteLine($"[DBG] obj={System.Text.Json.JsonSerializer.Serialize(obj)}");
```

**Notes:**
- The generated `.csproj` includes `<ImplicitUsings>enable</ImplicitUsings>` and `<Nullable>enable</Nullable>` by default in .NET 6+; no manual edits needed for basic harnesses.
- Use `dotnet add package <Package>` if a lightweight dependency is unavoidable. Prefer inlining relevant logic instead.
- Exceptions go to stderr; `2>&1` ensures they appear in captured output.

---

## Node.js / TypeScript

**Markers:** `package.json`, `.nvmrc`, `tsconfig.json`, `.node-version`

**Scaffold (JavaScript ESM):**
```
# bash
mkdir -p <harness_dir>
# then write <harness_dir>/package.json with content: {"type":"module"}
# then write <harness_dir>/index.mjs

# PowerShell
New-Item -ItemType Directory -Force '<harness_dir>'
# then write <harness_dir>/package.json with content: {"type":"module"}
# then write <harness_dir>/index.mjs
```

Write `package.json` using the agent's file-write tool rather than shell redirection — avoids quoting differences between bash and PowerShell.

**Scaffold (TypeScript):**
```
# bash
mkdir -p <harness_dir> && cd <harness_dir> && npm init -y && npm install -D tsx

# PowerShell
New-Item -ItemType Directory -Force '<harness_dir>'; Set-Location '<harness_dir>'; npm init -y; npm install -D tsx
```

**Build:** Not needed for ESM scripts; `npx tsc --noEmit` for a type-check pass
**Run:** `node index.mjs` (ESM) or `npx tsx index.ts` (TypeScript)
**Cleanup:**
```
rm -rf <harness_dir>          # bash
Remove-Item -Recurse -Force '<harness_dir>'   # PowerShell
```

**Debug idiom:**
```js
console.log('[DBG]', 'label:', value);
console.log('[DBG]', 'obj:', JSON.stringify(obj, null, 2));
```

**Notes:**
- `tsx` executes TypeScript directly without a separate compile step; it is the fastest path for a one-shot harness.
- Unhandled promise rejections go to stderr; `2>&1` captures them.
- `npm` and `npx` are cross-platform; no shell-specific variants needed for those commands.

---

## Python

**Markers:** `pyproject.toml`, `setup.py`, `setup.cfg`, `requirements.txt`, `Pipfile`, `*.py`

**Scaffold:** No directory needed — write a single file directly using the agent's file-write tool:
```
<harness_dir>.py    (e.g. harness_<name>_<ts>.py in the system temp directory)
```

**Build:** None
**Run:**
```
python3 harness_<name>_<ts>.py     # bash / macOS / Linux
python harness_<name>_<ts>.py      # PowerShell / Windows
```

Use the full path to the file when invoking from outside the temp directory.

**Debug idiom:**
```python
print(f"[DBG] label={value!r}")
import json; print(f"[DBG] obj={json.dumps(obj, default=str)}")
```

**Notes:**
- Use `!r` (repr) for values where quoting and type matter; use `json.dumps` for structured objects.
- Tracebacks go to stderr; `2>&1` captures them alongside debug output.
- If a dependency is unavoidable, prefer `import` from the stdlib over `pip install`.

---

## Go

**Markers:** `go.mod`, `go.sum`

**Scaffold:**
```
# bash
mkdir -p <harness_dir> && cd <harness_dir> && go mod init harness

# PowerShell
New-Item -ItemType Directory -Force '<harness_dir>'; Set-Location '<harness_dir>'; go mod init harness
```

**Build:** `go build ./...`
**Run:** `go run .`
**Cleanup:**
```
rm -rf <harness_dir>          # bash
Remove-Item -Recurse -Force '<harness_dir>'   # PowerShell
```

**Debug idiom:**
```go
import "fmt"
fmt.Printf("[DBG] label=%v\n", value)
fmt.Printf("[DBG] obj=%+v\n", obj)
```

**Notes:**
- `go run .` compiles and runs in one step; prefer it over `go build + ./harness` for scratch work.
- Use `%#v` (Go syntax representation) when type and field names matter.
- Panic output goes to stderr; `2>&1` captures it.

---

## Rust

**Markers:** `Cargo.toml`

**Scaffold:**
```
# bash
cargo new <harness_dir> --name harness

# PowerShell
cargo new '<harness_dir>' --name harness
```

**Build:** `cargo build`
**Run:** `cargo run`
**Cleanup:**
```
rm -rf <harness_dir>          # bash
Remove-Item -Recurse -Force '<harness_dir>'   # PowerShell
```

**Debug idiom:**
```rust
println!("[DBG] label={:?}", value);
eprintln!("[DBG] err context={:?}", err);  // stderr path for errors
```

**Notes:**
- Derive `#[derive(Debug)]` on any struct or enum that needs to appear in debug output.
- `cargo run` implicitly builds; running build then run as separate steps makes the failure boundary explicit.
- Rust panics go to stderr; include `2>&1` to capture them.

---

## Java

**Markers:** `pom.xml`, `build.gradle`, `build.gradle.kts`, `settings.gradle`, `*.java`

**Scaffold:**
```
# bash
mkdir -p <harness_dir>

# PowerShell
New-Item -ItemType Directory -Force '<harness_dir>'
```

Then write `Main.java` into `<harness_dir>` using the agent's file-write tool.

**Build:** `javac Main.java`
**Run:** `java Main`
**Cleanup:**
```
rm -rf <harness_dir>          # bash
Remove-Item -Recurse -Force '<harness_dir>'   # PowerShell
```

**Debug idiom:**
```java
System.out.println("[DBG] label=" + value);
System.out.println("[DBG] arr=" + java.util.Arrays.toString(arr));
System.out.println("[DBG] obj=" + obj);  // relies on toString()
```

**Notes:**
- For scratch work, a single `Main.java` with `javac` + `java` is faster than setting up Maven or Gradle.
- If the project uses Maven, `mvn exec:java` can run a scratch main class without generating a JAR.
- Stack traces go to stderr; `2>&1` captures them.
