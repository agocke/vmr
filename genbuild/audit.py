#!/usr/bin/env python3
"""Opt-in Linux strace read auditor. See audit-help.txt; this is not a sandbox."""

import argparse
import copy
import glob
import hashlib
import json
import os
from pathlib import Path
import re
import shlex
import subprocess
import sys


class AuditError(ValueError):
    pass


def absolute(path, cwd):
    return os.path.normpath(path if os.path.isabs(path) else os.path.join(cwd, path))


def digest(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def ninja_tokens(text):
    """Tokenize emitted Ninja paths, retaining escaped separators as path bytes."""
    tokens, word = [], []
    i = 0
    while i < len(text):
        char = text[i]
        if char == "$":
            i += 1
            if i == len(text) or text[i] not in "$ :":
                raise AuditError("Unsupported Ninja variable/escape in path: " + text)
            word.append(text[i])
        elif char.isspace() or char in ":|":
            if word:
                tokens.append("".join(word))
                word = []
            if char in ":|":
                token = char
                if char == "|" and text[i:i + 2] == "||":
                    token = "||"
                    i += 1
                tokens.append(token)
        elif char == "#":
            break
        else:
            word.append(char)
        i += 1
    if word:
        tokens.append("".join(word))
    return tokens


def action_args(argv):
    for flag in ("--invocation", "--materialize-tree"):
        if flag in argv:
            index = argv.index(flag)
            count = 2 if flag == "--materialize-tree" else 1
            if index + count >= len(argv):
                raise AuditError("Truncated action arguments")
            return flag, argv[index + 1], argv[index + 2] if count == 2 else None
    return None


def items(descriptor, name):
    parameters = descriptor.get("parameters", {})
    value = next((v for k, v in parameters.items() if k.lower() == name.lower()), [])
    if isinstance(value, dict):
        value = value.get("items") or []
    if not isinstance(value, list):
        raise AuditError("Expected item list for " + name)
    return value


def load_graph(path, cwd):
    """Read the standalone emitter subset, never rewrite or execute Ninja text."""
    text = Path(path).read_text()
    # Ninja continuations consume following indentation.
    text = re.sub(r"\$\r?\n[ \t]*", "", text)
    rules, edges = {}, []
    rule = None
    for line in text.splitlines():
        if line.startswith("rule "):
            rule = line[5:].strip()
        elif line.startswith("  command = ") and rule:
            command = line.split("=", 1)[1].strip()
            if "$" in command.replace("$$", ""):
                raise AuditError("Unsupported Ninja command variable")
            rules[rule] = shlex.split(command.replace("$$", "$"))
        elif line.startswith("build "):
            tokens = ninja_tokens(line[6:])
            if ":" not in tokens:
                raise AuditError("Missing build separator")
            split = tokens.index(":")
            name, rest = tokens[split + 1], tokens[split + 2:]
            outputs = [p for p in tokens[:split] if p != "|"]
            content, order = [], []
            destination = content
            for token in rest:
                if token == "||":
                    destination = order
                elif token != "|":
                    destination.append(token)
            edges.append((name, outputs, content, order))
        elif line and not line[0].isspace():
            rule = None
            if line.startswith(("include ", "subninja ")):
                raise AuditError("Included Ninja graphs are not supported")
    actions = {}
    for rule, outputs, content, order in edges:
        identity = action_args(rules.get(rule, []))
        if not identity:
            if rule not in ("phony", "msbuild_clean"):
                raise AuditError("Unsupported non-action Ninja rule: " + rule)
            continue
        flag, descriptor_path, tree = identity
        descriptor_path = absolute(descriptor_path, cwd)
        descriptor = json.loads(Path(descriptor_path).read_text())
        work = absolute(descriptor["workingDirectory"], cwd)
        output_paths = [absolute(p, cwd) for p in outputs]
        output_paths += [absolute(i["itemSpec"], work) for i in items(descriptor, "DeclaredOutputs")]
        trees = []
        for item in items(descriptor, "DeclaredOutputTrees"):
            metadata = {k.lower(): v for k, v in item.get("metadata", {}).items()}
            trees.append({
                "root": absolute(item["itemSpec"], work),
                **{key: absolute(metadata[field.lower()], work)
                   for key, field in (("archive", "ArchiveFile"), ("dyndep", "DyndepFile"),
                                      ("receipt", "ReceiptFile"))},
            })
        key = (flag, descriptor_path, absolute(tree, cwd) if tree else None)
        if key in actions:
            raise AuditError("Ambiguous action identity: " + descriptor_path)
        actions[key] = {
            "id": rule, "mode": flag[2:], "invocation": descriptor_path,
            "taskType": descriptor.get("taskType"),
            "taskAssembly": descriptor.get("taskAssembly"),
            "workingDirectory": work,
            "contentInputs": sorted(set(absolute(p, cwd) for p in content) | {descriptor_path}),
            "declaredInputs": [absolute(i["itemSpec"], work) for i in items(descriptor, "DeclaredInputs")],
            "outputs": sorted(set(output_paths)), "trees": trees,
            "orderOnly": [absolute(p, cwd) for p in order],
        }
    if not actions:
        raise AuditError("No supported actions found")
    return actions


def c_string(text):
    """Decode strace's C byte strings (including octal and hex path escapes)."""
    match = re.match(r'"((?:\\.|[^"\\])*)"', text)
    if not match or text[match.end():].startswith("..."):
        raise AuditError("Missing or truncated C string: " + text)
    return c_bytes(match[1])


def c_bytes(value):
    result = bytearray()
    escapes = {"a": 7, "b": 8, "t": 9, "n": 10, "v": 11, "f": 12, "r": 13,
               "\\": 92, '"': 34, "'": 39}
    i = 0
    while i < len(value):
        if value[i] != "\\":
            result.extend(os.fsencode(value[i]))
        else:
            i += 1
            if i >= len(value):
                raise AuditError("Truncated string escape")
            if value[i] in "01234567":
                number = re.match(r"[0-7]{1,3}", value[i:])[0]
                result.append(int(number, 8))
                i += len(number) - 1
            elif value[i] == "x":
                number = value[i + 1:i + 3]
                result.append(int(number, 16))
                i += 2
            elif value[i] in escapes:
                result.append(escapes[value[i]])
            else:
                raise AuditError("Unknown string escape: " + value[i])
        i += 1
    return os.fsdecode(bytes(result))


def split_args(text):
    parts, start, depth, quoted, escaped, angle = [], 0, 0, False, False, False
    for i, char in enumerate(text):
        if angle:
            if char == ">":
                angle = False
        elif quoted:
            if escaped:
                escaped = False
            elif char == "\\":
                escaped = True
            elif char == '"':
                quoted = False
        elif char == '"':
            quoted = True
        elif char == "<":
            angle = True
        else:
            if char in "[{(":
                depth += 1
            elif char in "]})":
                depth -= 1
            elif char == "," and depth == 0:
                parts.append(text[start:i].strip())
                start = i + 1
    if quoted or depth or angle:
        raise AuditError("Unbalanced/truncated arguments")
    return parts + [text[start:].strip()]


def fd_path(text):
    match = re.match(r"(?:-?\d+|AT_FDCWD)<(.+)>$", text)
    if not match:
        return None
    path = match[1]
    path = re.sub(r"<(?:char|block) \d+:\d+>$", "", path)
    if path.endswith(" (deleted)"):
        raise AuditError("Deleted fd path cannot be resolved: " + path)
    if not path.startswith("/"):
        raise AuditError("Non-file fd annotation: " + path)
    # -yy annotations escape nonprintable bytes but do not surround them with quotes.
    return c_bytes(path)


def resolve_path(raw, base, cwd):
    path = c_string(raw)
    if os.path.isabs(path):
        return path
    directory = fd_path(base) if base else None
    if directory is None:
        if base and not base.startswith("AT_FDCWD"):
            raise AuditError("Relative access without -yy dirfd annotation: " + base)
        directory = cwd
    if directory is None:
        raise AuditError("Unknown current directory")
    # Preserve symlink/.. semantics before normalizing the logical spelling.
    return os.path.join(directory, path)


def read_events(prefix):
    events, errors = [], []
    paths = sorted(glob.glob(str(prefix) + ".*"))
    paths = [p for p in paths if p.rsplit(".", 1)[-1].isdigit()]
    if not paths:
        raise AuditError("No per-process strace logs found: " + str(prefix))
    for path in paths:
        pid = int(path.rsplit(".", 1)[-1])
        ended = False
        pending = None
        for number, line in enumerate(Path(path).read_text(errors="surrogateescape").splitlines(), 1):
            match = re.match(r"(\d+\.\d+)\s+(.*)", line)
            location = {"pid": pid, "file": path, "line": number}
            if not match:
                errors.append({**location, "reason": "Missing -ttt timestamp", "raw": line})
                continue
            stamp, body = match.groups()
            if body.startswith("+++"):
                ended = True
                if body.startswith(("+++ exited with ", "+++ killed by ")):
                    events.append({**location, "timestamp": stamp, "syscall": "@exit",
                                   "args": [], "result": body})
                else:
                    errors.append({**location, "reason": "Unsupported process identity transition", "raw": body})
                continue
            if body.startswith("---"):
                continue
            if body.endswith("<unfinished ...>"):
                if pending is not None:
                    errors.append({**location, "reason": "Nested unfinished syscall", "raw": body})
                pending = (stamp, location, body[:-len("<unfinished ...>")])
                continue
            resumed = re.match(r"<\.\.\. (\w+) resumed>(.*)", body)
            if resumed:
                if pending is None or not pending[2].startswith(resumed[1] + "("):
                    errors.append({**location, "reason": "Unmatched resumed syscall", "raw": body})
                    continue
                stamp, location, start = pending
                pending = None
                body = start + resumed[2]
            syscall = re.match(r"(\w+)\((.*)\)\s+=\s+(.*)$", body)
            if not syscall:
                errors.append({**location, "reason": "Unparsed trace line", "raw": body})
                continue
            try:
                args = split_args(syscall[2])
            except AuditError as error:
                errors.append({**location, "reason": str(error), "raw": body})
                continue
            events.append({**location, "timestamp": stamp, "syscall": syscall[1],
                           "args": args, "result": syscall[3]})
        if not ended:
            errors.append({"pid": pid, "file": path, "reason": "Missing process exit marker"})
        if pending is not None:
            errors.append({**pending[1], "reason": "Incomplete unfinished syscall", "raw": pending[2]})
    events.sort(key=lambda e: (e["timestamp"], e["pid"], e["line"]))
    return events, errors


def exact_catalog(paths):
    result = {}
    for path in paths:
        data = json.loads(Path(path).read_text())
        for entry in data.get("files", []):
            if isinstance(entry, str):
                if re.match(r"^/proc/(?:self|thread-self)(?:/|$)", entry):
                    raise AuditError("Use --allow-runtime-proc for process-relative proc aliases")
                entry = {"path": entry, "canonicalPath": os.path.realpath(entry)}
            logical, canonical = entry["path"], entry["canonicalPath"]
            if re.match(r"^/proc/(?:self|thread-self)(?:/|$)", logical):
                raise AuditError("Use --allow-runtime-proc for process-relative proc aliases")
            if not os.path.isabs(logical) or not os.path.isabs(canonical):
                raise AuditError("Catalog paths must be absolute")
            if os.path.isdir(logical):
                raise AuditError("Catalog directories are not file allowances: " + logical)
            result[os.path.normpath(logical)] = (os.path.normpath(canonical), str(path))
    return result


def directory_catalog(paths):
    result, mismatches = {}, []
    for path in paths:
        for rule in json.loads(Path(path).read_text()).get("directories", []):
            logical, canonical, entries = rule["path"], rule["canonicalPath"], rule["entries"]
            if re.match(r"^/proc/(?:self|thread-self)(?:/|$)", logical):
                raise AuditError("Process-relative proc aliases are not fixed directory identities")
            if not os.path.isabs(logical) or not os.path.isabs(canonical):
                raise AuditError("Directory catalog paths must be absolute")
            if not isinstance(entries, list) or not all(
                    isinstance(name, str) and name not in ("", ".", "..") and "/" not in name
                    for name in entries) or len(entries) != len(set(entries)):
                raise AuditError("Directory catalog requires unique immediate entry names")
            rule = {"path": os.path.normpath(logical), "canonicalPath": os.path.normpath(canonical),
                    "entries": sorted(entries), "catalog": str(path), "validAtAnalysis": True}
            try:
                actual = sorted(os.listdir(logical))
                if os.path.realpath(logical) != canonical or actual != rule["entries"]:
                    raise AuditError("Directory identity or membership changed")
            except (OSError, AuditError) as error:
                rule["validAtAnalysis"] = False
                mismatches.append({"path": logical, "catalog": str(path), "reason": str(error)})
            if rule["path"] in result and result[rule["path"]] != rule:
                raise AuditError("Conflicting directory catalog rules: " + logical)
            result[rule["path"]] = rule
    return result, mismatches


def directory_rule(rules, logical, canonical):
    rule = rules.get(logical)
    if rule and rule["canonicalPath"] == canonical:
        return rule
    if logical == canonical:
        return next((r for r in rules.values() if r["canonicalPath"] == canonical), None)
    return None


def traced_canonical(path, state, pid):
    """Never resolve a proc magic link in the analyzer's process namespace."""
    path = re.sub(r"^/proc/self(?=/|$)", "/proc/" + str(state["tgid"]), path)
    path = re.sub(r"^/proc/thread-self(?=/|$)",
                  f"/proc/{state['tgid']}/task/{pid}", path)
    if path == "/proc" or path.startswith("/proc/"):
        return os.path.normpath(path)
    return os.path.realpath(path)


def runtime_proc_rule(logical, canonical, kind, state, states, pid):
    if traced_canonical(logical, state, pid) != canonical:
        return None
    match = re.fullmatch(r"/proc/(\d+)(?:/(maps|mountinfo|cgroup|exe)|/task/(\d+)/comm)?", canonical)
    if not match:
        return None
    owner = states.get(int(match[1]))
    if (not owner or owner["action"] != state["action"]
            or owner["family"] != state["family"]):
        return None
    if not any(member["alive"] and member["tgid"] == owner["tgid"]
               and member["family"] == state["family"] and member["action"] == state["action"]
               for member in states.values()):
        return None
    leaf, tid = match[2], match[3]
    if tid:
        thread = states.get(int(tid))
        if (not thread or not thread["alive"] or thread["tgid"] != owner["tgid"] or thread["family"] != state["family"]
                or thread["action"] != state["action"]):
            return None
        return "task/own-thread/comm" if kind in ("open", "metadata", "negative") else None
    if leaf == "exe":
        return "own-process/exe" if kind == "readlink" else None
    if leaf:
        return "own-process/" + leaf if kind in ("open", "metadata", "negative") else None
    return "self-identity" if logical == "/proc/self" and kind == "readlink" else None


def directory_names(argument):
    if not argument.startswith("[") or not argument.endswith("]"):
        raise AuditError("Directory entries abbreviated; recapture with strace -v")
    names = []
    for item in split_args(argument[1:-1]):
        if not item:
            continue
        matches = re.findall(r'\bd_name=("(?:\\.|[^"\\])*")', item)
        if not item.startswith("{") or not item.endswith("}") or len(matches) != 1:
            raise AuditError("Incomplete directory entry names; recapture with strace -v")
        name = c_string(matches[0])
        if name not in (".", ".."):
            names.append(name)
    return names


def within(path, root):
    return path == root or path.startswith(root.rstrip("/") + "/")


class Policy:
    def __init__(self, action, all_trees):
        self.action = action
        self.inputs = self.pairs(action["contentInputs"])
        self.outputs = self.pairs(action["outputs"])
        self.trees = [(tree["root"], os.path.realpath(tree["root"]), "selfOutputTree")
                      for tree in action["trees"]]
        for tree in all_trees:
            if tree["root"] in action["contentInputs"]:
                self.trees.append((tree["root"], os.path.realpath(tree["root"]), "generatedTreeInput"))
        if action["mode"] == "materialize-tree":
            self.outputs.update(self.pairs([t["receipt"] for t in action["trees"]]))
        self.input_canonical = frozenset(self.inputs.values())
        self.output_canonical = frozenset(self.outputs.values())

    @staticmethod
    def pairs(paths):
        return {p: os.path.normpath(p) if p.startswith("/proc/") else os.path.realpath(p) for p in paths}

    @staticmethod
    def matches(paths, canonical_paths, logical, canonical):
        return (logical in paths and canonical == paths[logical]) or (
            logical == canonical and canonical in canonical_paths)

    def admit(self, logical, canonical, kind):
        if kind != "directory" and self.matches(self.inputs, self.input_canonical, logical, canonical):
            return "contentInput"
        if kind != "directory" and self.matches(self.outputs, self.output_canonical, logical, canonical):
            return "selfOutput"
        for root, real, category in self.trees:
            if (within(logical, root) or logical == canonical) and within(canonical, real):
                return category
        return None


METADATA = {"stat", "lstat", "stat64", "lstat64", "access", "readlink",
            "statfs", "statfs64", "getxattr", "lgetxattr", "listxattr", "llistxattr"}
AT_METADATA = {"newfstatat", "fstatat64", "statx", "faccessat", "faccessat2", "readlinkat"}
WRITES = {"rename", "renameat", "renameat2", "unlink", "unlinkat", "mkdir", "mkdirat",
          "rmdir", "chmod", "fchmodat", "fchmodat2", "chown", "lchown", "fchownat",
          "utime", "utimes", "utimensat", "futimesat", "truncate", "truncate64",
          "symlink", "symlinkat", "mknod", "mknodat", "creat",
          "setxattr", "lsetxattr", "removexattr", "lremovexattr"}
PROCESS = {"fork", "vfork", "clone", "clone3"}
IGNORED = {"wait4", "waitid", "exit", "exit_group", "kill", "tgkill", "getpid", "getppid"}


def analyze(graph, cwd, prefix, runtime_catalogs=(), platform_catalogs=(), strict=False,
            allow_runtime_proc=False, allow_case_sensitivity_probe=False):
    actions = load_graph(graph, cwd)
    events, incomplete = read_events(prefix)
    runtime = exact_catalog(runtime_catalogs)
    platform = exact_catalog(platform_catalogs)
    runtime_canonical = {real: source for real, source in runtime.values()}
    platform_canonical = {real: source for real, source in platform.values()}
    runtime_directories, runtime_mismatches = directory_catalog(runtime_catalogs)
    platform_directories, platform_mismatches = directory_catalog(platform_catalogs)
    trees = [t for a in actions.values() for t in a["trees"]]
    policies = {key: Policy(action, trees) for key, action in actions.items()}
    report = {
        "schemaVersion": 1, "graph": str(Path(graph).absolute()), "graphSha256": digest(graph),
        "strictProbes": strict, "actions": [], "undeclaredReads": [], "metadataProbes": [],
        "negativeProbes": [], "runtimeAllowanceUsage": [], "platformAllowanceUsage": [],
        "directoryHandleOpens": [], "directoryAllowanceUsage": [], "runtimeProcAllowanceUsage": [],
        "selfCreatedProbes": [], "runtimeScratchAllowanceUsage": [],
        "directoryCatalogMismatches": runtime_mismatches + platform_mismatches,
        "allowanceRules": {
            "runtime": [{"path": p, "canonicalPath": real, "catalog": source}
                        for p, (real, source) in sorted(runtime.items())],
            "platform": [{"path": p, "canonicalPath": real, "catalog": source}
                         for p, (real, source) in sorted(platform.items())],
            "runtimeDirectories": list(runtime_directories.values()),
            "platformDirectories": list(platform_directories.values()),
            "runtimeProc": {"enabled": allow_runtime_proc,
                            "leaves": ["maps", "mountinfo", "cgroup", "exe", "task/own-thread/comm", "self"]},
            "caseSensitivityProbe": {"enabled": allow_case_sensitivity_probe,
                                    "basename": "CASESENSITIVETEST[0-9a-f]{32}",
                                    "requires": "successful O_CREAT|O_EXCL followed by same-family unlink"},
        },
        "unresolved": [], "incompleteness": incomplete, "controller": {"events": 0, "pids": []},
        "limitations": [
            "Observation, not sandbox enforcement; content opens are conservative read candidates.",
            "Only traced path opens, exec, readlink and getdents64 are audited; inherited descriptors, "
            "mmap/read on pre-existing fds, io_uring and network are not covered.",
            "Canonical fallback uses the analysis-time filesystem; keep symlinks and graph unchanged.",
            "Generated-tree admissions are dynamic subtrees, not fixed member inventories.",
            "Unmatched/incomplete unfinished syscalls and unknown traced syscalls fail closed.",
            "Fixed directory rules require traced entry names (-v), EOF, and matching analysis-time "
            "membership; changes between observations or file contents are not thereby authorized.",
        ],
    }
    states, seen, controller, enumerations, scratch = {}, {}, set(), [], []
    children = set()
    for event in events:
        if event["syscall"] in PROCESS and not event["result"].startswith("-"):
            match = re.match(r"\d+", event["result"])
            if match:
                children.add(int(match[0]))
    roots = {e["pid"] for e in events} - children
    log_pids = {int(p.rsplit(".", 1)[-1]) for p in glob.glob(str(prefix) + ".*")
                if p.rsplit(".", 1)[-1].isdigit()}
    if len(roots) != 1:
        incomplete.append({"reason": "Expected exactly one traced controller root", "roots": sorted(roots)})
    for pid in sorted(children - log_pids):
        incomplete.append({"reason": "Missing child trace log", "pid": pid})
    for pid in roots:
        states[pid] = {"cwd": [cwd], "action": None, "family": None, "tgid": pid, "dirfds": {}, "alive": True}

    def unresolved(event, reason):
        key = states.get(event["pid"], {}).get("action")
        report["unresolved"].append({**event, "action": actions[key]["id"] if key else None,
                                     "reason": reason})

    def access(event, state, logical, kind, canonical=None):
        key = state["action"]
        if key is None:
            return
        action = actions[key]
        # realpath must see the uncollapsed spelling (symlink/..).
        canonical = (traced_canonical(canonical, state, event["pid"]) if canonical else
                     traced_canonical(logical, state, event["pid"]))
        logical = os.path.normpath(logical)
        category = policies[key].admit(logical, canonical, kind)
        source = None
        for catalog, canonical_catalog, name in ((runtime, runtime_canonical, "runtime"),
                                                  (platform, platform_canonical, "platform")):
            if category is None:
                entry = catalog.get(logical)
                if entry and entry[0] == canonical:
                    category, source = name, entry[1]
                elif logical == canonical and canonical in canonical_catalog:
                    category, source = name, canonical_catalog[canonical]
        selected_directory = None
        if category is None and kind in ("directory", "directoryHandle", "metadata"):
            for rules, name in ((runtime_directories, "runtimeDirectory"),
                                (platform_directories, "platformDirectory")):
                selected_directory = directory_rule(rules, logical, canonical)
                if selected_directory and selected_directory["validAtAnalysis"]:
                    category, source = name, selected_directory["catalog"]
                    break
        proc_rule = (runtime_proc_rule(logical, canonical, kind, state, states, event["pid"])
                     if allow_runtime_proc else None)
        if category is None and proc_rule:
            category, source = "runtimeProc", "--allow-runtime-proc"
        entry = {"action": action["id"], "pid": event["pid"], "timestamp": event["timestamp"],
                 "syscall": event["syscall"], "path": logical, "canonicalPath": canonical,
                 "kind": kind, "admission": category, "result": event["result"],
                 "processId": state["tgid"], "actionFamily": state["family"]}
        if category in ("runtime", "platform"):
            entry["catalog"] = source
            report[category + "AllowanceUsage"].append(entry)
        if category in ("runtimeDirectory", "platformDirectory"):
            entry["catalog"] = source
            entry["membershipEvidence"] = ("pending" if kind == "directory" else
                                           "handle-only" if kind == "directoryHandle" else "analysis-only-metadata")
            report["directoryAllowanceUsage"].append(entry)
            report["runtimeAllowanceUsage" if category == "runtimeDirectory"
                   else "platformAllowanceUsage"].append(entry)
        if category == "runtimeProc":
            entry["rule"] = proc_rule
            report["runtimeProcAllowanceUsage"].append(entry)
            report["runtimeAllowanceUsage"].append(entry)
        seen[key]["counts"][category or "undeclared"] = seen[key]["counts"].get(category or "undeclared", 0) + 1
        if kind == "negative":
            report["negativeProbes"].append(entry)
        elif kind == "metadata":
            report["metadataProbes"].append(entry)
        elif kind == "directoryHandle":
            report["directoryHandleOpens"].append(entry)
        elif category is None:
            report["undeclaredReads"].append(entry)
        return entry

    for event in events:
        pid, call, args, result = event["pid"], event["syscall"], event["args"], event["result"]
        state = states.get(pid)
        if state is None:
            unresolved(event, "Missing fork/clone inheritance")
            continue
        if call == "@exit":
            state["alive"] = False
            continue
        success = not result.startswith(("-", "?"))
        try:
            if result.startswith("?") and call not in ("exit", "exit_group"):
                raise AuditError("Indeterminate syscall result")
            if call in ("execve", "execveat"):
                if success:
                    # exec unshares an inherited CLONE_FS context.
                    state["cwd"] = list(state["cwd"])
                    state["dirfds"] = dict(state["dirfds"])
                    state["tgid"] = pid
                argv_text = args[1 if call == "execve" else 2]
                if not argv_text.startswith("[") or not argv_text.endswith("]"):
                    raise AuditError("Truncated exec argv")
                argv = [c_string(a) for a in split_args(argv_text[1:-1]) if a]
                if "--clean-trees" in argv and success:
                    raise AuditError("Cleanup execution is outside the action audit profile")
                identity = action_args(argv)
                if success and identity:
                    flag, descriptor, tree = identity
                    key = (flag, absolute(descriptor, state["cwd"][0]),
                           absolute(tree, state["cwd"][0]) if tree else None)
                    if key not in actions:
                        raise AuditError("Action invocation absent from graph: " + descriptor)
                    state["action"] = key
                    state["family"] = f"{pid}@{event['timestamp']}"
                    if key not in seen:
                        seen[key] = {**copy.deepcopy(actions[key]), "pids": [], "counts": {},
                                     "dynamicTrees": policies[key].trees}
                offset = 0 if call == "execve" else 1
                logical = resolve_path(args[offset], args[0] if offset else None, state["cwd"][0])
                access(event, state, logical, "exec" if success else "negative")
            elif call in PROCESS:
                if success:
                    child = int(re.match(r"\d+", result)[0])
                    states[child] = {"action": state["action"],
                                     "family": state["family"],
                                     "alive": True,
                                     "tgid": state["tgid"] if "CLONE_THREAD" in ",".join(args) else child,
                                     "dirfds": state["dirfds"] if "CLONE_FILES" in ",".join(args)
                                     else dict(state["dirfds"]),
                                     "cwd": state["cwd"] if "CLONE_FS" in ",".join(args)
                                     else list(state["cwd"])}
            elif call in ("chdir", "fchdir"):
                path = (resolve_path(args[0], None, state["cwd"][0])
                        if call == "chdir" else fd_path(args[0]))
                if path is None:
                    raise AuditError("fchdir lacks -yy annotation")
                access(event, state, path, "metadata" if success else "negative")
                if success:
                    state["cwd"][0] = path
            elif call == "getcwd":
                if success:
                    path = c_string(args[0])
                    if not os.path.isabs(path):
                        raise AuditError("getcwd did not return an absolute path")
                    access(event, state, path, "metadata")
                    state["cwd"][0] = path
                elif state["cwd"][0]:
                    access(event, state, state["cwd"][0], "negative")
                else:
                    raise AuditError("Failed getcwd with no known current directory")
            elif call in ("open", "openat", "openat2"):
                offset = 0 if call == "open" else 1
                logical = resolve_path(args[offset], args[0] if offset else None, state["cwd"][0])
                flags = args[offset + 1]
                writing = "O_WRONLY" in flags or "O_PATH" in flags
                kind = ("negative" if not success else "metadata" if "O_PATH" in flags
                        else "directoryHandle" if "O_DIRECTORY" in flags else "open")
                if not writing or not success or "O_PATH" in flags:
                    annotation = re.match(r"\d+<.*>", result)
                    canonical = fd_path(annotation[0]) if annotation and success else None
                    entry = access(event, state, logical, kind, canonical)
                    if success and kind == "open" and entry and "O_CREAT" in flags and "O_EXCL" in flags:
                        if re.fullmatch(r"CASESENSITIVETEST[0-9a-f]{32}", os.path.basename(entry["path"])):
                            scratch.append({"entry": entry, "family": state["family"], "unlinked": None})
                if success:
                    fd = int(re.match(r"\d+", result)[0])
                    state["dirfds"].pop(fd, None)
                    if kind == "directoryHandle":
                        canonical = traced_canonical(canonical or logical, state, pid)
                        rule = (directory_rule(runtime_directories, os.path.normpath(logical), canonical) or
                                directory_rule(platform_directories, os.path.normpath(logical), canonical))
                        state["dirfds"][fd] = {"path": canonical, "rule": rule, "names": set(),
                                               "observations": [], "eof": False, "used": False}
            elif call == "getdents64":
                path = fd_path(args[0])
                if path is None:
                    raise AuditError("getdents64 lacks -yy fd path annotation")
                entry = access(event, state, path, "directory" if success else "negative", path)
                if success and entry:
                    canonical = entry["canonicalPath"]
                    fd = int(args[0].split("<", 1)[0])
                    session = state["dirfds"].get(fd)
                    if (session is None or session["path"] != canonical
                            or (session["eof"] and result.split()[0] != "0")):
                        rule = (directory_rule(runtime_directories, path, canonical) or
                                directory_rule(platform_directories, path, canonical))
                        session = {"path": canonical, "rule": rule, "names": set(),
                                   "observations": [], "eof": False, "used": False}
                        state["dirfds"][fd] = session
                    if session["rule"]:
                        if not session["used"]:
                            enumerations.append(session)
                            session["used"] = True
                        session["observations"].append(entry)
                        if result.split()[0] == "0":
                            session["eof"] = True
                        else:
                            session["names"].update(directory_names(args[1]))
            elif call in METADATA or call in AT_METADATA:
                offset = 1 if call in AT_METADATA else 0
                # AT_EMPTY_PATH refers to the annotated fd itself.
                logical = resolve_path(args[offset], args[0] if offset else None, state["cwd"][0])
                kind = ("readlink" if call.startswith("readlink") else "metadata") if success else "negative"
                access(event, state, logical, kind)
            elif call in ("link", "linkat"):
                offset = 1 if call == "linkat" else 0
                logical = resolve_path(args[offset], args[0] if offset else None, state["cwd"][0])
                access(event, state, logical, "metadata" if success else "negative")
            elif call in ("unlink", "unlinkat") and success:
                offset = 1 if call == "unlinkat" else 0
                path = resolve_path(args[offset], args[0] if offset else None, state["cwd"][0])
                path = traced_canonical(path, state, pid)
                for candidate in scratch:
                    if (candidate["family"] == state["family"] and candidate["unlinked"] is None
                            and candidate["entry"]["canonicalPath"] == path):
                        candidate["unlinked"] = event["timestamp"]
            elif call in ("exit", "exit_group"):
                for other in states.values():
                    if other is state or (call == "exit_group" and other["tgid"] == state["tgid"]):
                        other["alive"] = False
            elif call not in WRITES | IGNORED:
                raise AuditError("Unsupported traced syscall: " + call)
        except (AuditError, IndexError, ValueError, TypeError) as error:
            unresolved(event, str(error))
        if state["action"] is None:
            controller.add(pid)
            report["controller"]["events"] += 1
        elif state["action"] in seen:
            pids = seen[state["action"]]["pids"]
            if pid not in pids:
                pids.append(pid)
    report["actions"] = list(seen.values())
    report["controller"]["pids"] = sorted(controller)
    for session in enumerations:
        rule = session["rule"]
        valid = (rule["validAtAnalysis"] and session["eof"] and
                 session["names"] == set(rule["entries"]))
        if not valid:
            report["directoryCatalogMismatches"].append({
                "path": session["path"], "catalog": rule["catalog"],
                "reason": "Observed membership differs or enumeration did not reach EOF",
                "reachedEof": session["eof"],
                "missing": sorted(set(rule["entries"]) - session["names"]),
                "extra": sorted(session["names"] - set(rule["entries"])),
            })
        for entry in session["observations"]:
            entry["membershipEvidence"] = "trace-and-analysis" if valid else "mismatch"
            if not valid and entry["admission"] in ("runtimeDirectory", "platformDirectory"):
                counts = next(a["counts"] for a in report["actions"] if a["id"] == entry["action"])
                counts[entry["admission"]] -= 1
                counts["undeclared"] = counts.get("undeclared", 0) + 1
                entry["admission"] = None
                report["undeclaredReads"].append(entry)
    report["directoryAllowanceUsage"] = [e for e in report["directoryAllowanceUsage"] if e["admission"]]
    for bucket in ("runtimeAllowanceUsage", "platformAllowanceUsage"):
        report[bucket] = [e for e in report[bucket] if e["admission"]]
    for candidate in scratch:
        entry = candidate["entry"]
        admitted = bool(allow_case_sensitivity_probe and candidate["unlinked"])
        report["selfCreatedProbes"].append({
            "action": entry["action"], "pid": entry["pid"], "path": entry["path"],
            "canonicalPath": entry["canonicalPath"], "created": entry["timestamp"],
            "unlinked": candidate["unlinked"], "allowanceEnabled": allow_case_sensitivity_probe,
            "eligible": bool(candidate["unlinked"]),
        })
        if admitted and entry["admission"] is None:
            counts = next(a["counts"] for a in report["actions"] if a["id"] == entry["action"])
            counts["undeclared"] -= 1
            counts["caseSensitivityProbe"] = counts.get("caseSensitivityProbe", 0) + 1
            entry["admission"] = "caseSensitivityProbe"
            entry["rule"] = "--allow-case-sensitivity-probe"
            report["undeclaredReads"].remove(entry)
            report["runtimeScratchAllowanceUsage"].append(entry)
            report["runtimeAllowanceUsage"].append(entry)
    if not seen:
        report["incompleteness"].append({"reason": "No action process observed"})
    undeclared_probes = any(e["admission"] is None
                            for bucket in ("negativeProbes", "metadataProbes", "directoryHandleOpens")
                            for e in report[bucket])
    report["ok"] = not (report["undeclaredReads"] or report["unresolved"] or
                        report["incompleteness"] or report["directoryCatalogMismatches"]
                        or (strict and undeclared_probes))
    return report


def write_json(path, data):
    Path(path).write_text(json.dumps(data, indent=2, ensure_ascii=True) + "\n")


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    catalog = commands.add_parser("catalog", help="Snapshot exact files or explicitly named fixed-membership directories")
    catalog.add_argument("--root")
    catalog.add_argument("--directory", action="append", default=[])
    catalog.add_argument("--output", required=True)
    trace = commands.add_parser("trace", help="Capture an unmodified command; analyze separately")
    trace.add_argument("--strace", required=True)
    trace.add_argument("--graph", required=True)
    trace.add_argument("--cwd", default=os.getcwd())
    trace.add_argument("--output", required=True, help="New capture directory (must not exist)")
    trace.add_argument("argv", nargs=argparse.REMAINDER)
    analyze_parser = commands.add_parser("analyze", help="Analyze a capture without executing products")
    analyze_parser.add_argument("--capture", required=True)
    analyze_parser.add_argument("--runtime-catalog", action="append", default=[])
    analyze_parser.add_argument("--platform-catalog", action="append", default=[])
    analyze_parser.add_argument("--strict-probes", action="store_true")
    analyze_parser.add_argument("--allow-runtime-proc", action="store_true")
    analyze_parser.add_argument("--allow-case-sensitivity-probe", action="store_true")
    analyze_parser.add_argument("--report", required=True)
    args = parser.parse_args(argv)
    try:
        if args.command == "catalog":
            if not args.root and not args.directory:
                raise AuditError("Catalog needs --root and/or --directory")
            files, directories = [], []
            if args.root:
                root = Path(args.root).absolute()
                if within(os.path.realpath(args.output), os.path.realpath(root)):
                    raise AuditError("Catalog output must be outside its input root")
                files = [root] if root.is_file() else sorted(p for p in root.rglob("*") if p.is_file())
                if not files:
                    raise AuditError("Catalog root has no files")
            for path in args.directory:
                directory = Path(path).absolute()
                if within(os.path.realpath(args.output), os.path.realpath(directory)):
                    raise AuditError("Catalog output must be outside its listed directories")
                directories.append({"path": str(directory), "canonicalPath": os.path.realpath(directory),
                                    "entries": sorted(os.listdir(directory))})
            write_json(args.output, {"schemaVersion": 1,
                                    "files": [{"path": str(p), "canonicalPath": os.path.realpath(p)}
                                              for p in files], "directories": directories})
            return 0
        if args.command == "trace":
            cwd = os.path.abspath(args.cwd)
            graph = absolute(args.graph, cwd)
            actions = load_graph(graph, cwd)
            command = args.argv[1:] if args.argv[:1] == ["--"] else args.argv
            if not command:
                raise AuditError("Expected command after --")
            output = Path(args.output).absolute()
            output.mkdir(parents=True, exist_ok=False)
            files = {graph} | {a["invocation"] for a in actions.values()}
            manifest = {"schemaVersion": 1, "cwd": cwd, "graph": graph, "argv": command,
                        "files": {p: digest(p) for p in sorted(files)}, "complete": False}
            write_json(output / "capture.json", manifest)
            code = subprocess.call([args.strace, "-ff", "-ttt", "-yy", "-v", "-s", "65535",
                                    "-e", "trace=%file,%process,getdents64",
                                    "-o", str(output / "trace"), "--", *command], cwd=cwd)
            manifest.update(complete=True, commandExitCode=code)
            write_json(output / "capture.json", manifest)
            return code if code >= 0 else 128 - code
        capture = Path(args.capture).absolute()
        manifest = json.loads((capture / "capture.json").read_text())
        protected = list(manifest["files"]) + [str(capture / "capture.json")]
        protected += glob.glob(str(capture / "trace") + ".*")
        protected += args.runtime_catalog + args.platform_catalog
        if os.path.realpath(args.report) in {os.path.realpath(p) for p in protected}:
            raise AuditError("Report output would overwrite a capture/graph/catalog input")
        report = analyze(manifest["graph"], manifest["cwd"], capture / "trace",
                         args.runtime_catalog, args.platform_catalog, args.strict_probes,
                         args.allow_runtime_proc, args.allow_case_sensitivity_probe)
        for path, expected in manifest["files"].items():
            if not Path(path).is_file() or digest(path) != expected:
                report["incompleteness"].append({"reason": "Capture input changed", "path": path})
                report["ok"] = False
        report["commandExitCode"] = manifest.get("commandExitCode")
        if not manifest.get("complete") or manifest.get("commandExitCode") != 0:
            report["incompleteness"].append({"reason": "Capture interrupted or command failed"})
            report["ok"] = False
        write_json(args.report, report)
        return 0 if report["ok"] else 1
    except (AuditError, OSError, ValueError, KeyError) as error:
        print("audit: " + str(error), file=sys.stderr)
        return 2


if __name__ == "__main__":
    sys.exit(main())
