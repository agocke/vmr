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
        environment = descriptor.get("environment") or {}
        if not isinstance(environment, dict):
            raise AuditError("Expected a captured environment dictionary")
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
            "declaredOutputs": [absolute(i["itemSpec"], work) for i in items(descriptor, "DeclaredOutputs")],
            "outputs": sorted(set(output_paths)), "trees": trees,
            "stagingTempParent": next((environment[n] for n in ("TMPDIR", "TMP", "TEMP")
                                       if environment.get(n) is not None), None),
            "orderOnly": [absolute(p, cwd) for p in order],
            "scratchRoots": sorted({os.path.normpath(value)
                                    for name, value in environment.items()
                                    if name in ("TMPDIR", "TMP", "TEMP") and
                                    isinstance(value, str) and os.path.isabs(value)}),
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
            completed = stamp
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
            result = syscall[3]
            duration = re.search(r"\s+<(\d+\.\d{9})>$", result)
            timing = {}
            if duration:
                nanoseconds = time_ns(duration[1])
                timing["durationNs"] = nanoseconds
                completed = ns_time(time_ns(stamp) + nanoseconds)
                result = result[:duration.start()]
            events.append({**location, "timestamp": stamp, "syscall": syscall[1],
                           "completedTimestamp": completed, "args": args, "result": result, **timing})
        if not ended:
            errors.append({"pid": pid, "file": path, "reason": "Missing process exit marker"})
        if pending is not None:
            errors.append({**pending[1], "reason": "Incomplete unfinished syscall", "raw": pending[2]})
    events.sort(key=lambda e: (e["timestamp"], e["pid"], e["line"]))
    return events, errors


def time_ns(value):
    seconds, fraction = value.split(".")
    if len(fraction) > 9:
        raise AuditError("Unsupported timestamp precision")
    return int(seconds) * 10**9 + int(fraction.ljust(9, "0"))


def ns_time(value):
    return f"{value // 10**9}.{value % 10**9:09d}"


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
FD_LIFECYCLE = {"ftruncate", "ftruncate64", "close", "close_range", "dup", "dup2", "dup3", "fcntl",
                "fchmod", "fchown"}
RESPONSE_TRACE = "ftruncate,ftruncate64,close,close_range,dup,dup2,dup3,fcntl,fchmod,fchown"
FD_ALLOCATE = {
    "pipe", "pipe2", "socket", "socketpair", "accept", "accept4", "eventfd", "eventfd2",
    "epoll_create", "epoll_create1", "inotify_init", "inotify_init1", "signalfd", "signalfd4",
    "timerfd_create", "pidfd_open", "memfd_create", "memfd_secret",
}
# These can import/create descriptors or mutate fd tables in ways this profile
# cannot reconstruct. Capture them and reject successful uses, not silently omit.
FD_UNSUPPORTED = {
    "recvmsg", "recvmmsg", "pidfd_getfd", "io_uring_setup", "io_uring_enter", "bpf",
    "perf_event_open", "fanotify_init", "userfaultfd", "open_by_handle_at",
    "open_tree", "fsopen", "fsmount", "fspick", "landlock_create_ruleset", "seccomp", "ioctl",
}
FD_TRACE = RESPONSE_TRACE + "," + ",".join(sorted(FD_ALLOCATE | FD_UNSUPPORTED))
TIMING_FLAGS = ["--absolute-timestamps=unix,ns", "--syscall-times=ns"]


def fd_lifetimes(events):
    """Conservative interval proof; annotation equality never selects a generation.

    An allocation is usable only after its observed completion. A successful
    close consumes the already completed generation at entry. An overlapping
    ordinary allocation cannot replace that live fd; it may complete after that
    close frees it. Uses before allocation completion, competing replacements,
    and close/use overlap are ambiguous and rejected.
    """
    errors, timeline, tables, pending, generations = [], [], {}, {}, 0
    proof = {"version": 2, "allocations": 0, "reusesAfterClose": 0, "verified": False}
    previous_end = {}
    for index, event in enumerate(events):
        if event["syscall"] == "@exit":
            continue
        if (("durationNs" not in event or len(event["timestamp"].split(".")[1]) != 9)
                and event["syscall"] not in ("exit", "exit_group")):
            errors.append({**HostStaging.evidence(event), "reason": "Missing nanosecond syscall duration"})
            continue
        begin, end = time_ns(event["timestamp"]), time_ns(event.get("completedTimestamp", event["timestamp"]))
        if previous_end.get(event["pid"], 0) > begin:
            errors.append({**HostStaging.evidence(event),
                           "reason": "Overlapping same-thread syscall intervals or timing discontinuity"})
        previous_end[event["pid"]] = end
        timeline.extend([(begin, 1, index), (end, 0 if end > begin else 2, index)])
    if errors:
        return proof, errors

    def fail(event, reason):
        errors.append({**HostStaging.evidence(event), "reason": "FD lifetime ambiguity: " + reason})

    def number(argument):
        match = re.match(r"\d+", argument)
        if not match:
            raise AuditError("Missing numeric fd")
        return int(match[0])

    def allocations(event):
        call, args, result = event["syscall"], event["args"], event["result"]
        if result.startswith(("-", "?")):
            return []
        if call in ("pipe", "pipe2", "socketpair"):
            value = args[3 if call == "socketpair" else 0]
            if not value.startswith("[") or not value.endswith("]"):
                raise AuditError("Missing complete returned descriptor pair")
            pair = [number(p) for p in split_args(value[1:-1])]
            if len(pair) != 2 or pair[0] == pair[1]:
                raise AuditError("Invalid returned descriptor pair")
            return pair
        if call in FD_ALLOCATE | {"open", "openat", "openat2", "creat", "dup", "dup2", "dup3"} or (
                call == "fcntl" and args[1] in ("F_DUPFD", "F_DUPFD_CLOEXEC")):
            # signalfd can reuse its supplied descriptor without allocation.
            if call in ("signalfd", "signalfd4") and not args[0].startswith("-1"):
                raise AuditError("In-place signalfd replacement is unsupported")
            return [number(result)]
        return []

    def refs(event):
        call, args = event["syscall"], event["args"]
        if call in FD_LIFECYCLE | {"getdents64", "fchdir"} and call != "close_range":
            return [number(args[0])]
        if call in (AT_METADATA | {"openat", "openat2", "mkdirat", "unlinkat", "symlinkat",
                                  "fchmodat", "fchmodat2", "fchownat", "utimensat", "futimesat"}):
            offset = 2 if call == "symlinkat" else 1
            base = args[offset - 1]
            if not os.path.isabs(c_string(args[offset])) and re.match(r"\d+", base):
                return [number(base)]
        return []

    for moment, phase, index in sorted(timeline):
        event = events[index]
        pid, call, args = event["pid"], event["syscall"], event["args"]
        success = not event["result"].startswith(("-", "?"))
        table = tables.setdefault(pid, {"fds": {}, "uses": {}, "closed": {}, "locked": {}, "copies": set()})
        fds = table["fds"]
        try:
            if phase == 1:
                if call in FD_UNSUPPORTED and success:
                    # Common ioctl queries do not allocate/import file descriptors.
                    if call != "ioctl" or args[1] not in ("TCGETS", "TCGETS2", "TIOCGWINSZ", "FIONREAD", "FIONBIO"):
                        raise AuditError("Unsupported successful descriptor transition: " + call)
                if call == "close_range" and success:
                    raise AuditError("close_range needs an explicitly supported interval contract")
                created = allocations(event)
                references = refs(event)
                if table["copies"] and (created or references):
                    raise AuditError("Descriptor transition overlaps fd-table copy")
                if call in PROCESS and success:
                    child = number(event["result"])
                    if "CLONE_PIDFD" in ",".join(args):
                        raise AuditError("clone pidfd allocation is unsupported")
                    if "CLONE_FILES" in ",".join(args):
                        tables[child] = table
                    else:
                        if table["uses"] or any(p["table"] is table and (p["created"] or p["refs"])
                                                for p in pending.values()):
                            raise AuditError("Fd-table copy overlaps descriptor transitions")
                        table["copies"].add(index)
                        tables[child] = {"fds": {fd: dict(g) for fd, g in fds.items()},
                                         "uses": {}, "closed": {}, "locked": {}, "copies": set()}
                if call in ("execve", "execveat") and success:
                    if table["uses"] or any(p["table"] is table and (p["created"] or p["refs"])
                                            for p in pending.values()):
                        raise AuditError("exec fd-table unshare overlaps descriptor transitions")
                    table = {"fds": {fd: dict(g) for fd, g in fds.items() if not g["cloexec"]},
                             "uses": {}, "closed": {}, "locked": {}, "copies": set()}
                    tables[pid], fds = table, table["fds"]
                event["_fdBindings"] = {fd: fds[fd]["id"] for fd in references if fd in fds}
                if any(fd in table["locked"] for fd in references + created):
                    raise AuditError("Descriptor access overlaps atomic target replacement")
                source = fds.get(references[0]) if references else None
                if call == "close" and success:
                    fd = references[0]
                    if table["uses"].get(fd):
                        raise AuditError("Close overlaps an unfinished descriptor use")
                    if fd not in fds:
                        if any(fd in p["created"] and p["table"] is table for p in pending.values()):
                            raise AuditError("Close precedes allocation completion")
                    else:
                        table["closed"][fd] = {**HostStaging.evidence(event), "generation": fds.pop(fd)["id"]}
                elif call == "close" and references[0] in fds:
                    raise AuditError("Failed close of a tracked generation")
                elif references:
                    fd = references[0]
                    if source is None and any(fd in p["created"] and p["table"] is table for p in pending.values()):
                        raise AuditError("Fd use precedes allocation completion")
                    table["uses"].setdefault(fd, set()).add(index)
                replacement = call in ("dup2", "dup3") and success and created[0] != references[0]
                if replacement:
                    target = created[0]
                    if table["uses"].get(target) or any(
                            target in p["created"] and p["table"] is table for p in pending.values()):
                        raise AuditError("Atomic replacement overlaps a target transition")
                    displaced = fds.pop(target, None)
                    if displaced:
                        event["_fdDisplaced"] = displaced["id"]
                    table["locked"][target] = index
                pending[index] = {"table": table, "created": created, "refs": references,
                                  "source": source, "replacement": replacement}
            else:
                operation = pending.pop(index, None)
                if operation is None:
                    continue
                table, created, references = operation["table"], operation["created"], operation["refs"]
                table["copies"].discard(index)
                fds = table["fds"]
                for fd in references:
                    table["uses"].get(fd, set()).discard(index)
                    if not table["uses"].get(fd):
                        table["uses"].pop(fd, None)
                if call == "fcntl" and success and references and references[0] in fds:
                    if args[1] == "F_SETFD":
                        fds[references[0]]["cloexec"] = "FD_CLOEXEC" in args[2]
                    elif args[1] not in ("F_GETFD", "F_GETFL", "F_SETFL", "F_DUPFD", "F_DUPFD_CLOEXEC"):
                        raise AuditError("Unsupported successful fcntl command")
                event["_fdCreated"] = {}
                for fd in created:
                    if call in ("dup2", "dup3") and references and fd == references[0]:
                        event["_fdCreated"][fd] = event["_fdBindings"].get(fd)
                        continue
                    if fd in fds:
                        raise AuditError("Allocation would overwrite a live generation")
                    if fd in table["closed"]:
                        event.setdefault("_fdAfterClose", {})[fd] = table["closed"].pop(fd)
                        proof["reusesAfterClose"] += 1
                    generations += 1
                    flags = ",".join(args)
                    source = operation["source"]
                    is_dup = call.startswith("dup") or call == "fcntl"
                    cloexec = ("CLOEXEC" in flags or
                               (call == "fcntl" and args[1] == "F_DUPFD_CLOEXEC"))
                    fds[fd] = {"id": generations, "cloexec": cloexec}
                    event["_fdCreated"][fd] = generations
                    if is_dup and source:
                        event.setdefault("_fdOrigins", {})[generations] = source["id"]
                    proof["allocations"] += 1
                    if operation["replacement"]:
                        table["locked"].pop(fd, None)
        except (AuditError, ValueError, IndexError, TypeError) as error:
            fail(event, str(error))
    proof["verified"] = not errors
    return proof, errors


class HostStaging:
    """Reconstruct only the namespace born in a common action host's private root."""

    def __init__(self, actions, policies, versioned_fds=False):
        self.actions, self.policies = actions, policies
        self.roots, self.active, self.by_family, self.tables, self.families = {}, {}, {}, {}, {}
        self.reads = []
        self.executable_aliases = {}
        self.versioned_fds, self.generations = versioned_fds, {}

    def handle(self, event, fd):
        if self.versioned_fds:
            return self.generations.get(event.get("_fdBindings", {}).get(fd))
        return self.tables.get(event["pid"], {}).get(fd)

    @staticmethod
    def evidence(event):
        return {k: event[k] for k in ("pid", "file", "line", "timestamp", "completedTimestamp", "syscall",
                                      "durationNs")
                if k in event}

    @staticmethod
    def done(event):
        return event.get("completedTimestamp", event["timestamp"])

    def root_for(self, path):
        if not path.startswith("/"):
            return None
        while path:
            if path in self.roots:
                return self.roots[path]
            path = path.rsplit("/", 1)[0]
        return None

    def fail(self, root, event, reason):
        root["rejections"].append({**self.evidence(event), "reason": reason})

    def locate(self, path, state, event):
        root = self.root_for(path)
        if root is None:
            return None, os.path.normpath(path), None
        if ".." in path[len(root["path"]):].split("/"):
            raise AuditError("Staging dot-dot traversal is unsupported, not normalized into an allowance")
        path = os.path.normpath(path)
        if (state["family"] != root["family"] or root["removed"]
                or root["created"] >= event["timestamp"]):
            self.fail(root, event, "Foreign, removed or not-yet-established staging namespace")
        parent = os.path.dirname(path)
        while within(parent, root["path"]) and parent != path:
            node = root["nodes"].get(parent)
            if node is not None and node["kind"] != "directory":
                raise AuditError("Staging path traverses a non-directory or input alias")
            if parent == root["path"]:
                break
            parent = os.path.dirname(parent)
        node = root["nodes"].get(path)
        if node and node["changed"] >= event["timestamp"] and node["event"] != self.evidence(event):
            self.fail(root, event, "Object lifecycle overlaps this access")
        return root, path, node

    def change(self, root, path, event, adding):
        parent = root["nodes"].get(os.path.dirname(path))
        if not parent or parent["kind"] != "directory" or parent["changed"] >= event["timestamp"]:
            self.fail(root, event, "Namespace mutation lacks a completed, proven parent directory")
        for session in root["enumerations"]:
            if session["path"] == os.path.dirname(path) and not session["eof"]:
                if adding or os.path.basename(path) not in session["seen"]:
                    self.fail(root, event, "Directory membership changed before it was enumerated")

    def add(self, root, path, kind, event, **fields):
        if path in root["nodes"]:
            self.fail(root, event, "Creation replaces an existing namespace object")
        self.change(root, path, event, True)
        if path in root["aliasPaths"]:
            self.fail(root, event, "Immutable input alias path was recreated")
        node = {"id": len(root["objects"]), "kind": kind, "path": path, "history": [path],
                "event": self.evidence(event), "changed": self.done(event), **fields}
        root["nodes"][path] = node
        root["objects"].append(node)
        root["members"].setdefault(os.path.dirname(path), set()).add(os.path.basename(path))
        if kind == "directory":
            root["members"][path] = set()
        if kind == "alias":
            root["aliasPaths"].add(path)
        return node

    def remove(self, root, path, event):
        self.change(root, path, event, False)
        node = root["nodes"].pop(path, None)
        if node is None:
            self.fail(root, event, "Removal of an unobserved object")
        elif node["kind"] == "directory" and root["members"].get(path):
            self.fail(root, event, "Directory removal lost unobserved children")
        root["members"].get(os.path.dirname(path), set()).discard(os.path.basename(path))
        if node:
            node["deleted"] = self.evidence(event)
            node["changed"] = self.done(event)
        return node

    def observe(self, event, state):
        call, args, result = event["syscall"], event["args"], event["result"]
        success = not result.startswith(("-", "?"))
        table = self.tables.setdefault(event["pid"], {})
        if state["family"] and state["action"]:
            self.families.setdefault(state["family"], {"key": state["action"], "exits": set(), "failed": False})
        if call == "@exit":
            if state["family"]:
                family = self.families[state["family"]]
                family["exits"].add(event["pid"])
                family["failed"] |= result != "+++ exited with 0 +++"
            self.tables.pop(event["pid"], None)
            return
        if call in PROCESS and success:
            child = int(re.match(r"\d+", result)[0])
            self.tables[child] = table if "CLONE_FILES" in ",".join(args) else {
                fd: dict(handle) for fd, handle in table.items()}
            return
        if call in ("execve", "execveat") and success:
            self.tables[event["pid"]] = {fd: h for fd, h in table.items() if not h["cloexec"]}
            return
        if call in FD_LIFECYCLE:
            displaced = self.generations.get(event.get("_fdDisplaced")) if self.versioned_fds else None
            if displaced:
                self.fail(displaced["root"], event, "Atomic replacement of a private fd is unsupported")
            if call == "close_range":
                if self.versioned_fds:
                    return  # The interval pass rejects this unsupported transition.
                if table:
                    for root in self.roots.values():
                        if any(h["root"] is root for h in table.values()):
                            self.fail(root, event, "Unsupported staging close_range lifecycle")
                return
            match = re.match(r"\d+", args[0])
            handle = self.handle(event, int(match[0])) if match else None
            if not handle:
                if call in ("dup2", "dup3") and success:
                    target = int(re.match(r"\d+", args[1])[0])
                    displaced = table.pop(target, None)
                    if displaced:
                        self.fail(displaced["root"], event, "Untracked fd replaced a staging fd")
                # An annotated private fd with no captured open must not become evidence.
                if "<" in args[0]:
                    annotation = c_bytes(args[0].split("<", 1)[1].removesuffix(">"))
                    if self.root_for(annotation.removesuffix(" (deleted)")):
                        raise AuditError("Staging fd operation lacks a captured fd binding")
                return
            root, node = handle["root"], handle["node"]
            if state["family"] != root["family"]:
                self.fail(root, event, "Foreign-family staging fd lifecycle")
            annotation = re.match(r"\d+<(.+)>$", args[0])
            if not annotation:
                self.fail(root, event, "Staging fd lifecycle lacks -yy identity")
            else:
                spelling = c_bytes(annotation[1])
                deleted = spelling.endswith(" (deleted)")
                spelling = spelling.removesuffix(" (deleted)")
                expected = ([node["canonicalTarget"]] if node["kind"] == "alias" else node["history"])
                if spelling not in expected or (deleted and not node.get("deleted")):
                    self.fail(root, event, "Staging fd identity disagrees with its object history")
            if not success:
                self.fail(root, event, "Failed staging fd lifecycle")
                return
            if call == "close":
                fd = int(match[0])
                session = handle.get("enumeration")
                if session and not session["eof"]:
                    self.fail(root, event, "Directory enumeration closed before EOF")
                if not self.versioned_fds:
                    table.pop(fd)
            elif call.startswith("ftruncate"):
                if node["kind"] != "file" or not handle["writable"] or int(args[1]) < 0:
                    self.fail(root, event, "Unsupported staging fd truncation")
            elif call == "fchmod" and node["kind"] == "file" and re.fullmatch(r"0[0-7]{3}", args[1]):
                pass
            elif call in ("fchmod", "fchown"):
                self.fail(root, event, "Staging permission/ownership mutation is unsupported")
            elif call == "fcntl" and args[1] in ("F_GETFD", "F_GETFL"):
                pass
            elif call == "fcntl" and args[1] == "F_SETFD":
                handle["cloexec"] = "FD_CLOEXEC" in args[2]
            elif call in ("dup", "dup2", "dup3") or (call == "fcntl" and "F_DUPFD" in args[1]):
                target = int(re.match(r"\d+", result)[0])
                if target != int(match[0]):
                    duplicate = {**handle, "cloexec": "CLOEXEC" in ",".join(args)}
                    if self.versioned_fds:
                        generation = event.get("_fdCreated", {}).get(target)
                        if generation is None:
                            self.fail(root, event, "Duplicate lacks a proven fd generation")
                        else:
                            self.generations[generation] = duplicate
                    else:
                        table[target] = duplicate
            else:
                self.fail(root, event, "Unsupported staging fd control")
            return
        if call in ("mkdir", "mkdirat"):
            offset = int(call == "mkdirat")
            path = resolve_path(args[offset], args[0] if offset else None, state["cwd"][0])
            parent, name = os.path.dirname(path), os.path.basename(path)
            if (state["action"] and self.actions[state["action"]]["mode"] == "invocation"
                    and name.startswith("msbuild-stage-") and self.root_for(path) is None):
                action = self.actions[state["action"]]
                root = {"path": path, "family": state["family"], "owner": state["tgid"],
                        "key": state["action"], "created": self.done(event), "removed": False,
                        "nodes": {}, "objects": [], "rejections": [], "aliases": set(),
                        "aliasPaths": set(), "members": {path: set()}, "enumerations": []}
                self.roots[path] = root
                self.active[path] = root
                self.by_family.setdefault(state["family"], []).append(root)
                expected = action["stagingTempParent"]
                if (not success or args[offset + 1] != "0700" or
                        not re.fullmatch(r"msbuild-stage-[0-9a-f]{32}", name) or
                        not isinstance(expected, str) or not os.path.isabs(expected) or
                        parent != expected or os.path.realpath(parent) != parent or
                        state["tgid"] != int(state["family"].split("@")[0])):
                    self.fail(root, event, "Root lacks exclusive 0700 host creation in effective captured absolute TMP")
                node = {"id": 0, "kind": "directory", "path": path, "history": [path],
                        "event": self.evidence(event), "changed": self.done(event)}
                root["nodes"][path] = node
                root["objects"].append(node)
                return
        operands = []
        if call in ("open", "openat", "openat2", "mkdir", "mkdirat", "unlink", "unlinkat",
                    "rmdir", "chdir", "creat"):
            offset = int(call.endswith("at") or call == "openat2")
            operands = [(args[offset], args[0] if offset else None)]
        elif call == "symlink":
            operands = [(args[1], None)]
        elif call == "symlinkat":
            operands = [(args[2], args[1])]
        elif call in ("rename", "link"):
            operands = [(args[0], None), (args[1], None)]
        elif call in ("renameat", "renameat2", "linkat"):
            operands = [(args[1], args[0]), (args[3], args[2])]
        elif call in WRITES:
            offset = int(call in ("fchmodat", "fchmodat2", "fchownat", "utimensat", "futimesat"))
            operands = [(args[offset], args[0] if offset else None)]
        resolved = []
        for raw, base in operands:
            located = self.locate(resolve_path(raw, base, state["cwd"][0]), state, event)
            if (self.versioned_fds and located[0] and base and re.match(r"\d+", base)
                    and not os.path.isabs(c_string(raw))):
                handle = self.handle(event, int(re.match(r"\d+", base)[0]))
                if (not handle or handle["node"]["kind"] != "directory"
                        or fd_path(base) != handle["node"]["path"]):
                    self.fail(located[0], event, "Relative dirfd access lacks a proven directory generation")
            resolved.append(located)
        for root in self.active.values() if any(item[0] is None for item in resolved) else ():
            if success and call in WRITES | {"link", "linkat"}:
                for other, path, _ in resolved:
                    if other is None and within(root["path"], path):
                        self.fail(root, event, "An ancestor outside the staging namespace was mutated")
        if not any(root for root, _, _ in resolved):
            return
        root, path, node = next(item for item in resolved if item[0])
        if not success:
            return
        if call in ("mkdir", "mkdirat"):
            self.add(root, path, "directory", event)
        elif call in ("symlink", "symlinkat"):
            target = c_string(args[0])
            action, policy = self.actions[root["key"]], self.policies[root["key"]]
            mapped = root["path"] + "/execroot/root" + target
            canonical = os.path.realpath(target)
            if (state["tgid"] != root["owner"] or path != mapped or
                    target not in action["declaredInputs"] or
                    policy.admit(target, canonical, "open") not in ("contentInput", "generatedTreeInput")):
                self.fail(root, event, "Input alias lacks exact host mapping, DeclaredInputs and Ninja content edge")
            self.add(root, path, "alias", event, target=target, canonicalTarget=canonical)
            root["aliases"].add(target)
        elif call in ("open", "openat", "openat2", "creat"):
            offset = 0 if call in ("open", "creat") else 1
            flags = args[offset + 1] if call != "creat" else "O_WRONLY|O_CREAT|O_TRUNC"
            writable = "O_WRONLY" in flags or "O_RDWR" in flags
            if node is None and writable and "O_CREAT" in flags and "O_PATH" not in flags:
                node = self.add(root, path, "file", event, initialization="new-private-file")
            if node is None:
                self.fail(root, event, "Open of an old or unobserved private object")
                return
            if node["kind"] == "alias" and (writable or "O_TRUNC" in flags):
                self.fail(root, event, "Write-capable open of a staged input alias")
            annotation = re.match(r"(\d+)<.*>$", result)
            identity = fd_path(result) if annotation else None
            expected = node.get("canonicalTarget", path)
            if identity != expected:
                self.fail(root, event, "Open fd identity does not match the trace-born object")
            if annotation:
                handle = {"root": root, "node": node, "writable": writable,
                          "cloexec": "O_CLOEXEC" in flags}
                if self.versioned_fds:
                    generation = event.get("_fdCreated", {}).get(int(annotation[1]))
                    if generation is None:
                        self.fail(root, event, "Open lacks a proven fd generation")
                    else:
                        self.generations[generation] = handle
                else:
                    table[int(annotation[1])] = handle
        elif call in ("unlink", "unlinkat", "rmdir"):
            if path == root["path"]:
                if call != "rmdir" or set(root["nodes"]) != {path}:
                    self.fail(root, event, "Root cleanup is not an observed empty-directory removal")
                root["nodes"].pop(path, None)
                root["removed"] = True
                self.active.pop(path, None)
            else:
                self.remove(root, path, event)
        elif call in ("rename", "renameat", "renameat2"):
            source, destination = resolved
            if (source[0] is not root or destination[0] is not root or not source[2]
                    or source[2]["kind"] != "file" or
                    (destination[2] and destination[2]["kind"] != "file") or
                    (call == "renameat2" and args[4] not in ("0", "RENAME_NOREPLACE"))):
                self.fail(root, event, "Unsupported directory/alias/import/export rename")
                return
            if destination[1] in root["aliasPaths"]:
                self.fail(root, event, "Rename reuses an immutable input alias path")
            if source[1] == destination[1]:
                self.fail(root, event, "Ambiguous self rename")
                return
            node = self.remove(root, source[1], event)
            if destination[2]:
                self.remove(root, destination[1], event)
            self.change(root, destination[1], event, True)
            node.pop("deleted", None)
            node["path"] = destination[1]
            node["history"].append(destination[1])
            node["event"] = self.evidence(event)
            root["nodes"][destination[1]] = node
            root["members"].setdefault(os.path.dirname(destination[1]), set()).add(
                os.path.basename(destination[1]))
        elif call != "chdir":
            self.fail(root, event, "Unsupported private namespace mutation: " + call)

    def access(self, event, state, logical, kind, canonical):
        root, path, node = self.locate(logical, state, event)
        if root is None:
            return None
        if node is None:
            if kind != "negative":
                self.fail(root, event, "Successful access lacks a trace-born object")
            return root, node, path, "hostStagingProbe"
        actual = node.get("canonicalTarget", path)
        if canonical is not None and canonical != actual:
            self.fail(root, event, "Access fd canonical identity disagrees with namespace")
        category = ("hostStagingInputAlias" if node["kind"] == "alias" else
                    "hostStagingDirectory" if kind == "directory" else
                    "hostStagingProbe" if kind in ("metadata", "negative", "directoryHandle") else
                    "hostStagingOutput" if path in {
                        root["path"] + "/execroot/root" + p
                        for p in self.actions[root["key"]]["declaredOutputs"]} else "hostStagingScratch")
        if kind == "readlink":
            offset = 1 if event["syscall"] == "readlink" else 2
            if node["kind"] != "alias" or c_string(event["args"][offset]) != node["target"]:
                self.fail(root, event, "readlink result disagrees with the exact input alias")
        if kind == "directory":
            fd = int(re.match(r"\d+", event["args"][0])[0])
            handle = self.handle(event, fd)
            if not handle or handle["node"] is not node or node["kind"] != "directory":
                self.fail(root, event, "Directory enumeration lacks its captured directory fd")
            else:
                session = handle.get("enumeration")
                if session is None or (session["eof"] and event["result"].split()[0] != "0"):
                    session = {"root": root, "path": path,
                               "expected": set(root["members"].get(path, ())),
                        "seen": set(), "eof": False}
                    if any(root["nodes"][path + "/" + name]["changed"] >= event["timestamp"]
                           for name in session["expected"]):
                        self.fail(root, event, "Directory membership overlaps enumeration")
                    handle["enumeration"] = session
                    root["enumerations"].append(session)
                names = directory_names(event["args"][1])
                if len(names) != len(set(names)) or session["seen"] & set(names):
                    self.fail(root, event, "Duplicate directory members")
                session["seen"].update(names)
                if not set(names) <= session["expected"]:
                    self.fail(root, event, "Unobserved directory members")
                if event["result"].split()[0] == "0":
                    session["eof"] = True
                    if names or session["seen"] != session["expected"]:
                        self.fail(root, event, "Directory EOF does not match exact trace-born membership")
        return root, node, actual, category

    def finish(self, report, profile, complete):
        report["hostStagingCandidates"], report["hostStagingAllowanceUsage"] = [], []
        report["hostStagingViolations"] = []
        for family, facts in self.families.items():
            if self.actions[facts["key"]]["mode"] == "invocation":
                owned = self.by_family.get(family, [])
                if len(owned) != 1:
                    report["hostStagingViolations"].append({
                        "actionFamily": family, "reason": "Expected exactly one common-host staging root"})
                    for root in owned:
                        root["rejections"].append({"reason": "Multiple staging roots in one action family"})
        for root in self.roots.values():
            reasons = root["rejections"]
            action, facts = self.actions[root["key"]], self.families[root["family"]]
            directories = {n["path"] for n in root["objects"] if n["kind"] == "directory"}
            structure = {root["path"] + "/scratch",
                         os.path.normpath(root["path"] + "/execroot/root" + action["workingDirectory"])}
            for condition, reason in (
                    (not profile, "Capture lacks the full fd lifecycle profile"),
                    (not complete or report["unresolved"] or report["incompleteness"],
                     "Capture has incomplete, unresolved, failed or changed inputs"),
                    (facts["failed"] or root["owner"] not in facts["exits"], "Action family did not complete successfully"),
                    (not root["removed"] or root["nodes"], "Staging cleanup is incomplete"),
                    (root["aliases"] != set(action["declaredInputs"]), "Incomplete declared-input alias materialization"),
                    (not structure <= directories, "Missing common-host scratch/working-directory structure"),
                    (any(not s["eof"] for s in root["enumerations"]),
                     "Directory enumeration did not reach EOF")):
                if condition:
                    reasons.append({"reason": reason})
            root["eligible"] = not reasons
            evidence = {"root": root["path"], "created": root["created"],
                        "action": action["id"], "actionFamily": root["family"],
                        "ownerProcessId": root["owner"], "eligible": root["eligible"],
                        "removed": root["removed"], "rejections": reasons,
                        "objects": [{k: n[k] for k in
                                     ("id", "kind", "history", "event", "deleted", "target",
                                      "canonicalTarget", "initialization") if k in n}
                                    for n in root["objects"]],
                        "enumerations": [{"path": s["path"], "expected": sorted(s["expected"]),
                                          "observed": sorted(s["seen"]), "reachedEof": s["eof"]}
                                         for s in root["enumerations"]]}
            report["hostStagingCandidates"].append(evidence)
        counts_by_action = {a["id"]: a["counts"] for a in report["actions"]}
        for entry, root, node, category in self.reads:
            # Response files retain their separate, stricter writer/child-reader proof.
            response = (category == "hostStagingScratch" and node and
                        any(p.endswith(".rsp") for p in node["history"]))
            if not root.get("eligible") or response or entry["admission"] is not None:
                continue
            counts = counts_by_action[entry["action"]]
            counts["undeclared"] -= 1
            counts[category] = counts.get(category, 0) + 1
            entry.update(admission=category, rule="--allow-host-staging", stagingRoot=root["path"],
                         objectId=node["id"] if node else None)
            report["hostStagingAllowanceUsage"].append(entry)
            if category == "hostStagingInputAlias" and entry["kind"] == "exec":
                self.executable_aliases[(entry["actionFamily"], entry["pid"],
                                         entry["timestamp"], entry["path"])] = {
                    "stagingRoot": root["path"], "objectId": node["id"],
                    "target": node["target"], "canonicalTarget": node["canonicalTarget"],
                    "creation": node["event"]}
        report["undeclaredReads"] = [e for e in report["undeclaredReads"] if e["admission"] is None]

    def response_parent(self, family, parent):
        return any(r.get("eligible") and r["family"] == family and parent == r["path"] + "/scratch"
                   and any(n["kind"] == "directory" and parent in n["history"] for n in r["objects"])
                   for r in self.by_family.get(family, []))

    def response_directory(self, family, directory):
        for root in self.by_family.get(family, []):
            if root.get("eligible"):
                node = next((n for n in root["objects"]
                             if n["kind"] == "directory" and n["path"] == directory), None)
                if node:
                    return {"stagingRoot": root["path"], "directory": directory,
                            "objectId": node["id"], "creation": node["event"]}
        return None


class ResponseScratch:
    """Prove a single private, initialized writer/child-reader/unlink lifecycle."""

    def __init__(self, actions, policies):
        self.actions, self.policies = actions, policies
        self.roots = {root for action in actions.values() for root in action["scratchRoots"]}
        self.files, self.directories, self.mutations, self.execs = {}, {}, [], []
        self.known_fds, self.root_exits, self.failed_families = {}, set(), set()

    def relevant(self, path):
        return any(within(path, root) or within(root, path) for root in self.roots)

    def record(self, event, state, path, operation, **details):
        logical = os.path.normpath(path)
        return {"operation": operation, "path": logical,
                "canonicalPath": traced_canonical(path, state, event["pid"]),
                "pid": event["pid"], "processId": state["tgid"], "family": state["family"],
                "action": self.actions[state["action"]]["id"] if state["action"] else None,
                "traceFile": event["file"], "traceLine": event["line"], "result": event["result"],
                "timestamp": event["timestamp"], "completed": event.get("completedTimestamp", event["timestamp"]),
                "success": not event["result"].startswith(("-", "?")), **details}

    def observe(self, event, state):
        call, args, result = event["syscall"], event["args"], event["result"]
        success = not result.startswith(("-", "?"))
        if call == "@exit":
            family = state["family"]
            if family:
                if result != "+++ exited with 0 +++":
                    self.failed_families.add(family)
                if event["pid"] == int(family.split("@", 1)[0]):
                    self.root_exits.add(family)
            return
        if call in ("execve", "execveat") and success and state["action"]:
            offset = 0 if call == "execve" else 1
            argv = [c_string(a) for a in split_args(args[offset + 1][1:-1]) if a]
            executable = resolve_path(args[offset], args[0] if offset else None, state["cwd"][0])
            declared = self.policies[state["action"]].admit(
                os.path.normpath(executable), traced_canonical(executable, state, event["pid"]), "exec")
            for argument in argv:
                if argument.startswith("@") and len(argument) > 1:
                    path = absolute(argument[1:], state["cwd"][0])
                    self.execs.append(self.record(event, state, path, "exec",
                                                  executable=os.path.normpath(executable),
                                                  declared=declared == "contentInput"))
            return
        if call in ("open", "openat", "openat2"):
            offset = 0 if call == "open" else 1
            path = resolve_path(args[offset], args[0] if offset else None, state["cwd"][0])
            if not path.endswith(".rsp") and ".rsp" not in result:
                return
            item = self.record(event, state, path, "open", flags=args[offset + 1])
            if success:
                fd = int(re.match(r"\d+", result)[0])
                annotation = re.match(r"\d+<.*>", result)
                annotated = fd_path(annotation[0]) if annotation else None
                item.update(fd=fd, canonicalPath=annotated, annotated=annotated is not None)
            identity = item["canonicalPath"] if success and item["canonicalPath"] else item["path"]
            if not identity.endswith(".rsp") or not self.relevant(identity):
                return
            if success:
                self.known_fds[(state["tgid"], fd)] = identity
            self.files.setdefault(identity, []).append(item)
            return
        if call in FD_LIFECYCLE:
            if call in ("fchmod", "fchown"):
                path = fd_path(args[0])
                if path is None:
                    raise AuditError("Fd permission/ownership change lacks -yy annotation")
                if self.relevant(path):
                    self.mutations.append(self.record(event, state, path, call))
                return
            if call == "close_range":
                # Ranges are uncommon in this profile. Do not invent per-file close evidence.
                for (process, fd), path in list(self.known_fds.items()):
                    if process == state["tgid"]:
                        self.files[path].append(self.record(event, state, path, "unsupported-fd-range"))
                return
            if call == "fcntl" and "F_DUPFD" not in args[1]:
                return
            for argument in args[:2] if call in ("dup2", "dup3") else args[:1]:
                match = re.match(r"(\d+)(?:<.*>)?$", argument)
                if not match:
                    continue
                fd = int(match[1])
                path = self.known_fds.get((state["tgid"], fd))
                if path is None and ".rsp" not in argument:
                    continue
                annotated = fd_path(argument)
                if path is None:
                    path = annotated
                if path is None or not path.endswith(".rsp") or not self.relevant(path):
                    continue
                item = self.record(event, state, path, call, fd=fd,
                                   canonicalPath=annotated, annotated=annotated is not None)
                if call.startswith("ftruncate"):
                    item["length"] = args[1]
                self.files.setdefault(path, []).append(item)
                if call == "close" and success:
                    self.known_fds.pop((state["tgid"], fd), None)
            return
        paths = []
        if call in ("mkdir", "mkdirat"):
            offset = int(call == "mkdirat")
            path = resolve_path(args[offset], args[0] if offset else None, state["cwd"][0])
            if success and self.relevant(os.path.normpath(path)):
                item = self.record(event, state, path, "mkdir", mode=args[offset + 1])
                self.directories.setdefault(item["path"], []).append(item)
            return
        if call in ("rename", "link"):
            paths = [(args[0], None), (args[1], None)]
        elif call in ("renameat", "renameat2", "linkat"):
            paths = [(args[1], args[0]), (args[3], args[2])]
        elif call == "symlink":
            paths = [(args[1], None)]
        elif call == "symlinkat":
            paths = [(args[2], args[1])]
        elif call in ("unlinkat", "fchmodat", "fchmodat2", "fchownat", "utimensat", "futimesat"):
            paths = [(args[1], args[0])]
        elif call in WRITES:
            paths = [(args[0], None)]
        for raw, base in paths:
            path = resolve_path(raw, base, state["cwd"][0])
            if self.relevant(os.path.normpath(path)):
                self.mutations.append(self.record(event, state, path, call))

    def finish(self, report, enabled, profile, complete, staging=None):
        if staging:
            for execution in self.execs:
                proof = staging.executable_aliases.get((
                    execution["family"], execution["pid"], execution["timestamp"], execution["executable"]))
                if not execution["declared"] and proof:
                    execution.update(declared=True, declarationProof=proof)
        observations = {(e["pid"], e["timestamp"], e["path"]): e for e in report["undeclaredReads"]
                        if e["kind"] == "open"}
        counts = {a["id"]: a["counts"] for a in report["actions"]}
        protected = {p for a in self.actions.values()
                     for p in a["contentInputs"] + a["declaredInputs"] + a["outputs"]}
        protected |= {os.path.realpath(p) for p in protected}
        trees = [t["root"] for a in self.actions.values() for t in a["trees"]]
        trees += [os.path.realpath(root) for root in trees]
        admitted = set()
        for path, records in self.files.items():
            opens = [r for r in records if r["operation"] == "open"]
            if not opens:
                continue
            writers = [r for r in opens if "O_WRONLY" in r["flags"]]
            readers = [r for r in opens if "O_WRONLY" not in r["flags"]]
            if not readers:
                continue
            reasons = []
            if not profile:
                reasons.append("Capture lacks the response-file fd-lifecycle profile")
            if not complete or report["incompleteness"] or report["unresolved"]:
                reasons.append("Capture has incomplete, unresolved, failed or changed inputs")
            if (path in protected or any(str(parent) in protected for parent in Path(path).parents)
                    or any(within(path, root) for root in trees)):
                reasons.append("Path is a declared input/output or generated tree, not private transient scratch")
            if len(writers) != 1:
                reasons.append("Expected exactly one write-only open")
            writer = writers[0] if len(writers) == 1 else opens[0]
            family, owner = writer["family"], writer["processId"]
            if (not family or owner != int(family.split("@", 1)[0]) or
                    family not in self.root_exits or family in self.failed_families):
                reasons.append("Writer is not a successfully completed action-host process")
            if any(not r["success"] or r["family"] != family or r["path"] != path or
                   r["canonicalPath"] != path for r in records):
                reasons.append("Failed, cross-family, unannotated or symlinked file lifecycle")
            if any(r["operation"] not in ("open", "close", "ftruncate", "ftruncate64") for r in records):
                reasons.append("Unsupported fd duplication/range lifecycle")
            directory = os.path.dirname(path)
            creations = self.directories.get(directory, [])
            action = next((a for a in self.actions.values() if a["id"] == writer["action"]), None)
            direct_stage = staging.response_directory(family, directory) if staging else None
            if direct_stage and (len(creations) != 1 or
                                 direct_stage["creation"]["file"] != creations[0]["traceFile"] or
                                 direct_stage["creation"]["line"] != creations[0]["traceLine"]):
                direct_stage = None
            parent_allowed = action and (
                os.path.dirname(directory) in action["scratchRoots"] or
                (staging and staging.response_parent(family, os.path.dirname(directory))) or direct_stage)
            if (len(creations) != 1 or not parent_allowed
                    or creations[0]["family"] != family or creations[0]["processId"] != owner
                    or creations[0]["canonicalPath"] != directory
                    or (creations[0]["mode"] != "0700" and not (
                        direct_stage and creations[0]["mode"] == "0777"))
                    or creations[0]["completed"] >= writer["timestamp"]):
                reasons.append("Missing fresh same-host 0700 directory directly inside captured TMPDIR/TMP/TEMP")
            flags = writer["flags"]
            initialization = ("O_EXCL" if "O_EXCL" in flags else "O_TRUNC" if "O_TRUNC" in flags else None)
            truncations = [r for r in records if r["operation"] in ("ftruncate", "ftruncate64")]
            if truncations:
                if (len(truncations) != 1 or truncations[0]["length"] != "0"
                        or truncations[0]["processId"] != owner
                        or truncations[0].get("fd") != writer.get("fd")
                        or writer["completed"] >= truncations[0]["timestamp"]):
                    reasons.append("Truncation is not a single successful same-writer fd reset to zero")
                else:
                    initialization = initialization or "ftruncate(fd,0)"
            if not initialization or "O_CREAT" not in flags:
                reasons.append("No exclusive/truncating initialization; O_CREAT alone is insufficient")
            closes = [r for r in records if r["operation"] == "close"]
            writer_closes = [r for r in closes if r["processId"] == owner and r.get("fd") == writer.get("fd")]
            if len(writer_closes) != 1:
                reasons.append("Missing unique writer close")
            closed = writer_closes[0]["completed"] if len(writer_closes) == 1 else writer["timestamp"]
            if len(writer_closes) == 1 and writer["completed"] >= writer_closes[0]["timestamp"]:
                reasons.append("Writer open did not finish before writer close")
            if truncations and truncations[-1]["completed"] >= (
                    writer_closes[0]["timestamp"] if len(writer_closes) == 1 else closed):
                reasons.append("Initialization did not finish before writer close")
            unlinks = [r for r in self.mutations if r["path"] == path and r["operation"] in ("unlink", "unlinkat")]
            if (len(unlinks) != 1 or not unlinks[0]["success"] or unlinks[0]["family"] != family
                    or unlinks[0]["processId"] != owner or unlinks[0]["canonicalPath"] != path):
                reasons.append("Missing unique successful same-host unlink")
            deleted = unlinks[0]["timestamp"] if len(unlinks) == 1 else closed
            start = creations[0]["timestamp"] if len(creations) == 1 else writer["timestamp"]
            for mutation in self.mutations:
                if not mutation["success"] or mutation in unlinks:
                    continue
                if (mutation["path"] == path or
                        (within(path, mutation["path"]) and start <= mutation["timestamp"] <= deleted)):
                    reasons.append("Conflicting path/ancestor mutation: " + mutation["operation"])
            read_entries = []
            for reader in readers:
                executions = [e for e in self.execs if e["path"] == path and e["family"] == family
                              and e["processId"] == reader["processId"] and e["declared"]
                              and closed < e["timestamp"] and e["completed"] < reader["timestamp"]]
                reopened = min((r["timestamp"] for r in opens
                                if r["processId"] == reader["processId"] and r.get("fd") == reader.get("fd")
                                and r["timestamp"] > reader["timestamp"]), default=deleted)
                reader_closes = [r for r in closes if r["processId"] == reader["processId"]
                                 and r.get("fd") == reader.get("fd")
                                 and reader["completed"] < r["timestamp"] and r["completed"] < min(deleted, reopened)]
                if (reader["processId"] == owner or "O_RDONLY" not in reader["flags"]
                        or any(flag in reader["flags"] for flag in ("O_CREAT", "O_TRUNC", "O_PATH", "O_DIRECTORY"))
                        or not executions or len(reader_closes) != 1):
                    reasons.append("Read lacks closed-writer -> declared child exec @path -> read/close -> unlink ordering")
                entry = observations.get((reader["pid"], reader["timestamp"], path))
                if entry:
                    read_entries.append(entry)
            evidence = {"path": path, "action": writer["action"], "actionFamily": family,
                        "privateDirectory": directory, "initialization": initialization,
                        "directoryProtection": direct_stage or "fresh-host-0700-temporary-directory",
                        "directoryCreation": creations[0] if len(creations) == 1 else None,
                        "unlink": unlinks[0] if len(unlinks) == 1 else None,
                        "childExecutions": [e for e in self.execs if e["path"] == path and e["family"] == family],
                        "writerProcessId": owner, "readerProcessIds": sorted({r["processId"] for r in readers}),
                        "writerClosed": closed, "unlinked": unlinks[0]["completed"] if len(unlinks) == 1 else None,
                        "allowanceEnabled": enabled, "eligible": not reasons,
                        "rejections": sorted(set(reasons)), "lifecycle": records}
            report["responseFileScratchCandidates"].append(evidence)
            if enabled and not reasons:
                for entry in read_entries:
                    entry.update(admission="responseFileScratch", rule="--allow-response-file-scratch",
                                 privateDirectory=directory, initialization=initialization)
                    counts[entry["action"]]["undeclared"] -= 1
                    counts[entry["action"]]["responseFileScratch"] = counts[entry["action"]].get("responseFileScratch", 0) + 1
                    admitted.add(id(entry))
                    report["responseFileScratchAllowanceUsage"].append(entry)
        report["undeclaredReads"] = [e for e in report["undeclaredReads"] if id(e) not in admitted]


def analyze(graph, cwd, prefix, runtime_catalogs=(), platform_catalogs=(), strict=False,
            allow_runtime_proc=False, allow_case_sensitivity_probe=False,
            allow_response_file_scratch=False, response_file_profile=False, capture_complete=True,
            allow_host_staging=False, fd_lifetime_profile=False):
    actions = load_graph(graph, cwd)
    events, incomplete = read_events(prefix)
    fd_proof, fd_errors = fd_lifetimes(events) if fd_lifetime_profile else (None, [])
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
        "responseFileScratchCandidates": [], "responseFileScratchAllowanceUsage": [],
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
            "responseFileScratch": {"enabled": allow_response_file_scratch,
                                    "traceProfile": response_file_profile,
                                    "requires": "fresh private 0700 directory or exact trace-born directory under "
                                    "a verified exclusive 0700 host-staging root; initialized write-only host fd; "
                                    "writer close; content-declared child exec (direct or verified input alias) "
                                    "@file/read/close; host unlink; complete trace"},
        },
        "unresolved": fd_errors, "incompleteness": incomplete, "controller": {"events": 0, "pids": []},
        "limitations": [
            "Observation, not sandbox enforcement; content opens are conservative read candidates.",
            "Only traced path opens, exec, readlink and getdents64 are audited; inherited descriptors, "
            "mmap/read on pre-existing fds, io_uring and network are not covered.",
            "Canonical fallback uses the analysis-time filesystem except verified host staging; "
            "keep original input symlinks and graph unchanged.",
            "Generated-tree admissions are dynamic subtrees, not fixed member inventories.",
            "Unmatched/incomplete unfinished syscalls and unknown traced syscalls fail closed.",
            "Fixed directory rules require traced entry names (-v), EOF, and matching analysis-time "
            "membership; changes between observations or file contents are not thereby authorized.",
        ],
    }
    states, seen, controller, enumerations, scratch = {}, {}, set(), [], []
    responses = ResponseScratch(actions, policies) if allow_response_file_scratch or response_file_profile else None
    staging = HostStaging(actions, policies, fd_lifetime_profile) if allow_host_staging else None
    if fd_proof is not None:
        report["fdLifetimeProof"] = fd_proof
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
        staged = staging.access(event, state, logical, kind, canonical) if staging else None
        if staged:
            canonical = staged[2]
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
        if fd_lifetime_profile:
            entry["completedTimestamp"] = event.get("completedTimestamp", event["timestamp"])
            if event["syscall"] in ("open", "openat", "openat2") and not event["result"].startswith("-"):
                fd = int(re.match(r"\d+", event["result"])[0])
                entry["fdGeneration"] = event.get("_fdCreated", {}).get(fd)
            elif event["syscall"] == "getdents64":
                fd = int(re.match(r"\d+", event["args"][0])[0])
                entry["fdGeneration"] = event.get("_fdBindings", {}).get(fd)
        if staged:
            staging.reads.append((entry, staged[0], staged[1], staged[3]))
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
            if staging:
                staging.observe(event, state)
            if responses:
                responses.observe(event, state)
            state["alive"] = False
            continue
        success = not result.startswith(("-", "?"))
        try:
            if staging:
                staging.observe(event, state)
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
            elif call not in WRITES | IGNORED | FD_LIFECYCLE and not (
                    fd_lifetime_profile and call in FD_ALLOCATE | FD_UNSUPPORTED):
                raise AuditError("Unsupported traced syscall: " + call)
            if responses:
                responses.observe(event, state)
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
    if staging:
        staging.finish(report, response_file_profile, capture_complete)
        report["allowanceRules"]["hostStaging"] = {
            "version": 1, "enabled": True, "requires": "exclusive same-host 0700 root; exact declared-input aliases "
            "with Ninja content edges; trace-born objects; exact directory membership/EOF; complete cleanup"}
        report["limitations"].append(
            "Host staging reconstructs a supported private namespace, not OS confinement. Dot-dot "
            "staging paths, hardlinks, directory renames and unrecognized mutations fail closed. "
            "Untraced actors, inherited fds and mount races remain outside the observation contract.")
    if responses:
        responses.finish(report, allow_response_file_scratch, response_file_profile, capture_complete, staging)
    undeclared_probes = any(e["admission"] is None
                            for bucket in ("negativeProbes", "metadataProbes", "directoryHandleOpens")
                            for e in report[bucket])
    report["ok"] = not (report["undeclaredReads"] or report["unresolved"] or
                        report["incompleteness"] or report["directoryCatalogMismatches"]
                        or report.get("hostStagingViolations") or
                        any(not r["eligible"] for r in report.get("hostStagingCandidates", []))
                        or (strict and undeclared_probes))
    return report


def write_json(path, data):
    Path(path).write_text(json.dumps(data, indent=2, ensure_ascii=True) + "\n")


SECCOMP_FAILURE = re.compile(
    r"seccomp.*(?:disabled|unavailable|not enabled|not supported|cannot|overflow|oversiz|fail)",
    re.IGNORECASE)


def seccomp_probe(tracer, expression, output, policy):
    """Verify live stops from this probe, not just strace's capability test."""
    directory = output / "seccomp-preflight"
    directory.mkdir()
    executable = os.path.realpath(sys.executable)
    argv = [tracer, "-ff", "-ttt", "-yy", "-v", "-s", "65535", "-d", "--seccomp-bpf",
            "-e", "trace=" + expression, "-o", str(directory / "trace"),
            "--", executable, "-I", "-S", "-B", "-c", "pass"]
    if policy.get("fdLifetimeTraceProfile") == 2:
        argv[2:3] = TIMING_FLAGS
    evidence = policy["preflight"] = {"argv": argv, "verified": False}
    try:
        with (directory / "diagnostics.log").open("wb") as stream:
            result = subprocess.run(argv, stdout=subprocess.DEVNULL, stderr=stream,
                                    cwd=output, timeout=15)
    except subprocess.TimeoutExpired as error:
        raise AuditError("Seccomp activation probe exceeded 15 seconds") from error
    evidence["exitCode"] = result.returncode
    if (directory / "diagnostics.log").stat().st_size > 4 * 1024 * 1024:
        raise AuditError("Seccomp activation probe exceeded its diagnostic budget; evidence retained")
    diagnostics = (directory / "diagnostics.log").read_text(errors="replace")
    events, errors = read_events(directory / "trace")
    if policy.get("fdLifetimeTraceProfile") == 2:
        errors += [{**HostStaging.evidence(e), "reason": "Probe lacks nanosecond timing evidence"}
                   for e in events if e["syscall"] not in ("@exit", "exit", "exit_group")
                   and ("durationNs" not in e or len(e["timestamp"].split(".")[1]) != 9)]
    roots = {e["pid"] for e in events if e["syscall"] == "execve"
             and e["args"] and c_string(e["args"][0]) == executable and e["result"] == "0"}
    exits = {e["pid"] for e in events if e["syscall"] == "@exit"
             and e["result"] == "+++ exited with 0 +++"}
    live = [line for line in diagnostics.splitlines()
            if any(re.search(r"\[wait\([^]]*\)\s*=\s*" + str(pid)
                             + r"\].*EVENT_SECCOMP", line) for pid in roots)]
    prefixes = sorted({line[:-len(": seccomp filter enabled")]
                       for line in diagnostics.splitlines()
                       if line.endswith(": seccomp filter enabled")})
    evidence.update(rootProcessIds=sorted(roots), liveSeccompStops=len(live),
                    parseErrors=errors, filterEnabled=bool(prefixes), diagnosticPrefixes=prefixes,
                    rejectedDiagnostics=[line for line in diagnostics.splitlines()
                                         if SECCOMP_FAILURE.search(line)])
    if (result.returncode or errors or len(roots) != 1 or not roots <= exits or not live
            or not evidence["filterEnabled"] or evidence["rejectedDiagnostics"]):
        raise AuditError("Seccomp activation not established; see seccomp-preflight evidence")
    evidence["verified"] = True


def seccomp_trace(tracer, argv, cwd, output, manifest, expression):
    policy = manifest["seccompBpf"] = {
        "requested": True, "verified": False, "selection": expression,
        "tracerSha256": digest(tracer),
        "fdLifetimeTraceProfile": manifest.get("fdLifetimeTraceProfile"),
        "sourceContract": "strace v7.3 src/filter_seccomp.c:571-604,679-724; "
                          "src/strace.c:2988-3011,3152-3158",
        "limitations": [
            "Selected-syscall observation, not sandbox enforcement.",
            "Higher-priority competing seccomp filters can suppress notifications; "
            "this profile does not monitor filter changes.",
            "A startup probe is not a proof against later kernel/tracer failures; "
            "actual diagnostics, command failure and incomplete raw events remain failures.",
        ],
    }
    write_json(output / "capture.json", manifest)
    try:
        seccomp_probe(tracer, expression, output, policy)
        if digest(tracer) != policy["tracerSha256"]:
            raise AuditError("Tracer changed during seccomp activation probe")
        command = [argv[0], "--seccomp-bpf", *argv[1:]]
        policy["argv"] = command
        policy["rejectedDiagnostics"] = []
        names = {tracer, os.path.realpath(tracer), os.path.basename(tracer), "strace"}
        names.update(policy["preflight"]["diagnosticPrefixes"])
        with (output / "strace-stderr.log").open("wb") as stream:
            with subprocess.Popen(command, cwd=cwd, stderr=subprocess.PIPE) as process:
                for raw in process.stderr:
                    stream.write(raw)
                    line = raw.decode(errors="backslashreplace")
                    sys.stderr.write(line)
                    owned = next((line[len(name) + 2:].strip() for name in names
                                  if line.startswith(name + ": ")), None)
                    if owned is not None and not re.fullmatch(r"Process \d+ attached", owned):
                        policy["rejectedDiagnostics"].append(line.rstrip())
                code = process.wait()
        manifest["commandExitCode"] = code
        if policy["rejectedDiagnostics"]:
            raise AuditError("Actual strace diagnostics prevent seccomp capture verification")
        if digest(tracer) != policy["tracerSha256"]:
            raise AuditError("Tracer changed during seccomp capture")
        policy["verified"] = code == 0
        manifest["complete"] = code == 0
        return code if code >= 0 else 128 - code
    except (AuditError, OSError) as error:
        policy["failure"] = str(error)
        raise
    finally:
        evidence = list((output / "seccomp-preflight").glob("*"))
        evidence.append(output / "strace-stderr.log")
        manifest["files"].update({str(p): digest(p) for p in evidence if p.is_file()})
        write_json(output / "capture.json", manifest)


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
    trace.add_argument("--response-file-provenance", action="store_true",
                       help="Also capture fd lifecycles for response-file and common-host staging proofs")
    trace.add_argument("--seccomp-bpf", action="store_true",
                       help="Require a verified same-filter seccomp probe and reject actual tracer diagnostics")
    trace.add_argument("argv", nargs=argparse.REMAINDER)
    analyze_parser = commands.add_parser("analyze", help="Analyze a capture without executing products")
    analyze_parser.add_argument("--capture", required=True)
    analyze_parser.add_argument("--runtime-catalog", action="append", default=[])
    analyze_parser.add_argument("--platform-catalog", action="append", default=[])
    analyze_parser.add_argument("--strict-probes", action="store_true")
    analyze_parser.add_argument("--allow-runtime-proc", action="store_true")
    analyze_parser.add_argument("--allow-case-sensitivity-probe", action="store_true")
    analyze_parser.add_argument("--allow-response-file-scratch", action="store_true")
    analyze_parser.add_argument("--allow-host-staging", action="store_true",
                                help="Prove common-host private namespace and exact file-alias lifecycles")
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
            if args.response_file_provenance:
                manifest["responseFileTraceProfile"] = 2
                manifest["fdLifetimeTraceProfile"] = 2
            write_json(output / "capture.json", manifest)
            expression = "%file,%process,getdents64"
            if args.response_file_provenance:
                expression += "," + FD_TRACE
                manifest["fdLifetimeSelection"] = expression
            tracer_argv = [args.strace, "-ff", "-ttt", "-yy", "-v", "-s", "65535",
                           "-e", "trace=" + expression,
                           "-o", str(output / "trace"), "--", *command]
            if args.response_file_provenance:
                tracer_argv[2:3] = TIMING_FLAGS
            if args.seccomp_bpf:
                return seccomp_trace(args.strace, tracer_argv, cwd, output, manifest, expression)
            code = subprocess.call(tracer_argv, cwd=cwd)
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
        changed = [path for path, expected in manifest["files"].items()
                   if not Path(path).is_file() or digest(path) != expected]
        policy = manifest.get("seccompBpf")
        seccomp_ok = policy is None or (
            isinstance(policy, dict) and policy.get("requested") is True
            and policy.get("verified") is True
            and isinstance(policy.get("preflight"), dict)
            and policy.get("preflight", {}).get("verified") is True
            and not policy.get("rejectedDiagnostics"))
        complete = bool(manifest.get("complete") and manifest.get("commandExitCode") == 0
                        and not changed and seccomp_ok)
        fd_profile = manifest.get("fdLifetimeTraceProfile") == 2
        if fd_profile and manifest.get("fdLifetimeSelection") != "%file,%process,getdents64," + FD_TRACE:
            raise AuditError("Fd lifetime profile syscall selection differs from the supported contract")
        report = analyze(manifest["graph"], manifest["cwd"], capture / "trace",
                         args.runtime_catalog, args.platform_catalog, args.strict_probes,
                         args.allow_runtime_proc, args.allow_case_sensitivity_probe,
                         args.allow_response_file_scratch, manifest.get("responseFileTraceProfile") in (1, 2),
                         complete, args.allow_host_staging, fd_profile)
        for path in changed:
            report["incompleteness"].append({"reason": "Capture input changed", "path": path})
            report["ok"] = False
        report["commandExitCode"] = manifest.get("commandExitCode")
        if policy is not None:
            report["capturePolicy"] = {"seccompBpf": policy}
        if not seccomp_ok:
            report["incompleteness"].append({"reason": "Seccomp capture verification failed"})
            report["ok"] = False
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
