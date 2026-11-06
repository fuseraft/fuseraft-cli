---
title: "VS Code Chat Panel"
description: "Using the fuseraft chat panel in VS Code: following a turn, handing the agent your code, approving its changes, and the keyboard shortcuts."
---

The chat panel works like the CLI's full-screen [REPL](repl.md), with the editor wired in. This page goes through it in the order you'll meet it: starting a chat, following a turn, giving the agent your code, and staying in control of what it changes.

---

## Starting a chat

Open a chat from the **fuseraft** status bar item, **+** in the **Chats** view, or **Ctrl+Shift+Alt+F** (**Cmd+Shift+Alt+F** on macOS).

A new chat offers up to three places to start, depending on what's open:

- **Explain `<file>`** — the file you're in.
- **Review my uncommitted changes** — when `git status` reports any, with the count.
- **Continue "<title>"** — your last chat in this workspace.

Clicking one fills in the prompt for you to edit. **Continue** resumes that chat in the same panel.

Below them, a line shows where the agent may work: the folder it's sandboxed to, or a warning when `repl.yolo` has turned the sandbox off. See [Security & Sandbox](security.md).

A chat that's open when VS Code closes or reloads is resumed in its tab when it comes back.

---

## Following a turn

- **Thinking** streams in dimmed under `∴`, and folds to one line when the model moves on.
- **Tool calls** are one line each. A line counts up while the call runs and turns ✓ or ✗ with how long it took. A sub-agent's calls are nested under it.
- **Changed files** show as `✎ path +added -removed`. Click one to open it.
- **Details:** click a tool line for its arguments and output, or press **Ctrl+O** to page through recent tool output, thinking, and shell output in full.

The status line under the prompt shows the model, the turn, how full the context is (yellow as auto-compaction nears), the session's tokens and how many came from the provider's cache, active modes such as `hitl`, and a timer.

Switch models from the panel's header at any time without losing the conversation.

---

## Working with your code

**Add a selection.** Select code and press **Ctrl+Shift+Alt+A** (**Cmd+Shift+Alt+A**), or right-click and choose **fuseraft: Add to Chat**. The code goes into your message, so the agent doesn't have to read the file. With nothing selected, the whole file is attached.

**Fix a problem.** On an error or warning, open the quick fixes (the light bulb, or **Ctrl+.**) and choose **Fix with fuseraft**. The problem goes in the prompt, with the code from five lines above it to five below attached, ready to send. With several problems under the cursor, you get up to three to choose from.

**Attach files.** Right-click files or folders in the Explorer, or an editor tab, and choose **fuseraft: Add File to Chat**. You can also hold **Shift** and drop them on the panel, or type `@` and a path in the prompt. Files ignored by git and `.env` files aren't offered.

**The file you're in** shows as a chip above the prompt and is named with each message, with any lines you've selected. So you can ask about "this function" without attaching anything. Click the chip to turn it off or on, or set `fuseraft.includeActiveFile`.

**Replies link back to your code.**
- File paths like `src/app.ts:42` open the file at that line.
- Code blocks can be inserted at your cursor (replacing the selection), opened in a new editor, or copied.

**Ctrl+Shift+Alt+F** in the chat takes you back to your file.

---

## Talking to it

- **Type while it works.** **Send** holds a message as a chip above the prompt until the work is done. The chip's send arrow (or Ctrl+Enter instead of Enter) hands it to the model at its next call without stopping anything: after its current tool calls, or as the next turn of a `/goal` run starts. Its pencil (or **↑** on an empty prompt, for the last chip) takes it back into the prompt. `/title` and `/hitl on|off` apply at once, for the whole of a `/goal` run too. Other `/commands`, `!commands`, and `$skills` wait as a chip. Stopping the turn drops the chips.
- **Completions** for `/commands`, `$skills`, and `@paths`.
- **Images:** paste a screenshot, or attach images with the paperclip.
- **Long pastes** (more than 10 lines or 2,000 characters) show as `[pasted #1 · 40 lines]` and are sent in full. **Ctrl+O** shows them.
- **History:** **↑/↓** walk your earlier messages, **Ctrl+R** searches them.
- **Long drafts:** **Ctrl+G** opens the draft in an editor tab beside the panel. Each save puts it back in the prompt.
- **Run something yourself:** `!git status` runs a command unsandboxed and streams its output into the panel. It's never shown to the model.

The REPL's `/commands` work here too, except the few that need a terminal, such as `/provider setup` (use the setup screen instead). See [CLI Reference — `fuseraft repl`](cli-reference.md#fuseraft-repl).

---

## Staying in control

**Approvals.** With HITL on (the default), shell commands, file writes, and other changes ask first, in a bar above the prompt. A file write opens as a real diff in an editor tab. Answer with **Allow** or **Deny**, or press **y** or **n**; **Esc** denies.

**Questions.** When the agent asks you something, the choices appear above the prompt. Pick one, type your own answer, or **Skip** to let the agent decide.

**Stopping.** **Stop** or **Ctrl+C** interrupts a turn. Press again to kill commands it's stuck on.

**Notifications.** While you're elsewhere, you get a notification when a reply finishes or an approval or question is waiting. Turn them off with `fuseraft.notifications`. The status bar item also turns to **approval needed** or **question for you**.

---

## Keyboard shortcuts

| Key | Action |
|-----|--------|
| **Enter** | Send |
| **Shift+Enter** / **Alt+Enter** | New line |
| **↑ / ↓** | Previous / next message in your history (on an empty prompt, **↑** first takes back a queued message) |
| **Ctrl+R** | Search your history |
| **Ctrl+G** | Edit the draft in an editor tab |
| **Ctrl+O** | Page through recent tool output, thinking, shell output, and long pastes |
| **Ctrl+C** | Interrupt the turn (again to kill stuck commands) |
| **y** / **n** / **Esc** | Allow / deny / deny a pending approval |
| **PgUp / PgDn** | Scroll |
| **Alt+↑ / Alt+↓** | Jump between the messages you sent |
| **Ctrl+End** | Follow new output again |
| **Ctrl+F** | Search the conversation |
| **F2** | Rename the chat |
| **Ctrl+Shift+Alt+F** | Open the chat, or go back to your file |
| **Ctrl+Shift+Alt+A** | Add the selection (or file) to the chat, from the editor |

On macOS, use **Cmd** in place of **Ctrl** for the last two. `/copy` puts the last reply on the clipboard.
