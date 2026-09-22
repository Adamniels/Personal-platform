#!/usr/bin/env python3
"""PreToolUse hook: a change to a file in this repository that is not Markdown waits for approval.

Code here is written deliberately, see "AI and code" in AGENTS.md. Markdown follows the session's
permission mode. Every other file asks, one change at a time, including this script and
.claude/settings.json, so the guard cannot be loosened without approval either.

Edits through the edit tools are caught exactly. Writes through the shell are caught by a
heuristic: redirects, commands that write to the files they are given, in-place edits, and inline
interpreter code run from inside the repository. The written rule covers what it misses.

Anything this script cannot make sense of asks, since a hook that crashes lets the call through.
"""

import json
import os
import shlex
import sys

PROJECT = os.path.realpath(os.environ.get("CLAUDE_PROJECT_DIR") or os.getcwd())

WRITERS = {"cp", "mv", "install", "tee", "touch", "truncate", "dd", "ln", "patch", "rsync"}
IN_PLACE = {"sed", "perl"}  # write only with -i, and their first operand is a script, not a file
INTERPRETERS = {"python", "python3", "node", "ruby", "bash", "sh", "zsh"}
SEPARATORS = {";", "&&", "||", "|", "&", "(", ")"}


def ask(reason):
    print(json.dumps({
        "hookSpecificOutput": {
            "hookEventName": "PreToolUse",
            "permissionDecision": "ask",
            "permissionDecisionReason": reason,
        }
    }))
    sys.exit(0)


def resolve(path, base):
    return os.path.realpath(os.path.join(base, os.path.expanduser(path)))


def in_project(path, base):
    p = resolve(path, base)
    return p == PROJECT or p.startswith(PROJECT + os.sep)


def is_code(path, base):
    return in_project(path, base) and not path.lower().endswith(".md")


def check_edit(tool_input):
    path = tool_input.get("file_path") or tool_input.get("notebook_path")
    if path and is_code(path, PROJECT):
        ask(f"Edits {os.path.relpath(resolve(path, PROJECT), PROJECT)}, which is not Markdown. "
            "Code changes here are approved one at a time, see AGENTS.md.")


def segments(command):
    lexer = shlex.shlex(command, posix=True, punctuation_chars=True)
    lexer.whitespace_split = True
    segment = []
    for token in lexer:
        if token in SEPARATORS:
            if segment:
                yield segment
            segment = []
        else:
            segment.append(token)
    if segment:
        yield segment


def check_bash(command):
    base = PROJECT
    for seg in segments(command):
        # Redirect targets: '>' or '>>' followed by a path, skipping /dev/null and '&1'.
        for i, token in enumerate(seg):
            if token in (">", ">>") and i + 1 < len(seg):
                target = seg[i + 1]
                if target not in ("/dev/null", "&", ">") and is_code(target, base):
                    ask(f"Shell redirect writes {target}, which is not Markdown. See AGENTS.md.")

        words = [t for t in seg if "=" not in t.split("/")[0]] or seg  # drop VAR=value prefixes
        name = os.path.basename(words[0])
        # Empty tokens are skipped: macOS `sed -i ''` passes one, and it would resolve to the cwd.
        operands = [t for t in words[1:] if t and not t.startswith("-") and t not in (">", ">>", "<")]

        if name == "cd" and operands:
            base = resolve(operands[0], base)
        elif name in WRITERS:
            if any(is_code(t, base) for t in operands):
                ask(f"`{name}` writes a file in the repository that is not Markdown. See AGENTS.md.")
        elif name in IN_PLACE and any(t.startswith("-i") for t in words[1:]):
            if any(is_code(t, base) for t in operands[1:]):
                ask(f"`{name} -i` edits a file in the repository that is not Markdown. See AGENTS.md.")
        elif name in INTERPRETERS:
            inline = "-c" in words or "-" in operands or "<" in seg
            script_in_repo = bool(operands) and operands[0] != "-" and is_code(operands[0], base)
            if (inline and in_project(".", base)) or script_in_repo:
                ask(f"`{name}` runs code that can write files in the repository. See AGENTS.md.")


def main():
    event = json.load(sys.stdin)
    tool, tool_input = event.get("tool_name"), event.get("tool_input") or {}
    if tool in ("Edit", "Write", "NotebookEdit"):
        check_edit(tool_input)
    elif tool == "Bash":
        check_bash(tool_input.get("command", ""))


if __name__ == "__main__":
    try:
        main()
    except SystemExit:
        raise
    except Exception as error:  # fail towards asking, never towards letting through
        ask(f"The code guard hook could not check this call ({error}), so it asks.")
