"""Synthetic strace fixtures; no strace or .NET installation is needed."""

import contextlib
import importlib.util
import io
import json
import os
from pathlib import Path
import shlex
import shutil
import subprocess
import sys
import unittest
from unittest import mock
import uuid


MODULE = Path(__file__).resolve().parents[1] / "audit.py"
SPEC = importlib.util.spec_from_file_location("genbuild_audit", MODULE)
audit = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(audit)


def quote(value):
    return json.dumps(str(value), ensure_ascii=False)


def escape(path):
    return str(path).replace("$", "$$").replace(" ", "$ ").replace(":", "$:")


class AuditTests(unittest.TestCase):
    def setUp(self):
        # Keep every fixture under the project, never a system temp directory.
        self.root = Path(__file__).resolve().parent / (".audit-test-" + uuid.uuid4().hex)
        self.root.mkdir()
        self.addCleanup(shutil.rmtree, self.root)
        self.host = self.file("dotnet")
        self.app = self.file("host.dll")
        self.source = self.file("source.cs")
        self.output = self.root / "output.dll"
        self.sidecar = self.root / "action.json"
        self.graph = self.root / "build.ninja"
        self.prefix = self.root / "trace"
        self.descriptor = {
            "version": 2, "taskAssembly": str(self.app), "taskType": "Compile",
            "workingDirectory": str(self.root),
            "parameters": {
                "DeclaredInputs": {"items": [{"itemSpec": str(self.source), "metadata": {}}]},
                "DeclaredOutputs": {"items": [{"itemSpec": str(self.output), "metadata": {}}]},
            },
        }
        self.write_graph()

    def file(self, name):
        path = self.root / name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text("fixture")
        return path

    def write_graph(self, inputs=None, order=None):
        self.sidecar.write_text(json.dumps(self.descriptor))
        content = [self.source] if inputs is None else inputs
        command = " ".join(shlex.quote(str(p)) for p in
                           [self.host, self.app, "--invocation", self.sidecar]).replace("$", "$$")
        self.graph.write_text(
            "ninja_required_version = 1.10\n"
            "rule action_0\n  command = " + command +
            "\nbuild " + escape(self.output) + ": action_0 " +
            " ".join(escape(p) for p in content) + " | " +
            " ".join(escape(p) for p in [self.sidecar, self.host, self.app]) +
            (" || " + " ".join(escape(p) for p in order) if order else "") +
            "\nrule msbuild_clean\n  command = ninja -t clean\n"
            "build clean-stamp: msbuild_clean\nbuild clean: phony clean-stamp\n")

    def exec(self, flag="--invocation", tree=None):
        args = [self.host, self.app, flag, self.sidecar]
        if tree:
            args.append(tree)
        return "execve(" + quote(self.host) + ", [" + ", ".join(map(quote, args)) + "], 0x0) = 0"

    def log(self, lines, pid=100, start=1):
        text = "\n".join(f"{start + index / 1000:.6f} {line}" for index, line in enumerate(lines))
        Path(str(self.prefix) + "." + str(pid)).write_text(text + "\n")

    def open(self, path, flags="O_RDONLY", result=None, base="AT_FDCWD"):
        if result is None:
            result = "3<" + quote(os.path.realpath(path))[1:-1] + ">"
        return f"openat({base}, {quote(path)}, {flags}) = {result}"

    def analyze(self, **kwargs):
        return audit.analyze(str(self.graph), str(self.root), str(self.prefix), **kwargs)

    def fixed_directory(self, names):
        directory = self.root / "fixed-members"
        directory.mkdir()
        for name in names:
            (directory / name).write_text("not automatically a declared content input")
        catalog = self.root / "directories.json"
        audit.write_json(catalog, {"directories": [{
            "path": str(directory), "canonicalPath": str(directory), "entries": names}]})
        return directory, catalog

    def dents(self, directory, names, fd=7):
        members = ", ".join("{d_ino=1, d_off=1, d_reclen=24, d_type=DT_REG, d_name=" + quote(n) + "}"
                            for n in names)
        return f"getdents64({fd}<{directory}>, [{members}], 32768) = {len(names) * 32}"

    def response_fixture(self, flags="O_WRONLY|O_CREAT", initialize=True):
        temporary = self.root / "captured temporary"
        path = temporary / "MSBuildTemp fixture" / "compiler response.rsp"
        self.descriptor["environment"] = {"TMPDIR": str(temporary)}
        self.write_graph()
        host = [
            self.exec(), f"mkdir({quote(path.parent)}, 0700) = 0",
            self.open(path, flags=flags, result=f"10<{path}>"),
            f"ftruncate(10<{path}>, 0) = 0" if initialize else "getpid() = 100",
            f"close(10<{path}>) = 0", "clone(child_stack=NULL, flags=SIGCHLD) = 101",
            "wait4(101, NULL, 0, NULL) = 101", "getpid() = 100", "getpid() = 100",
            f"unlink({quote(path)}) = 0", "+++ exited with 0 +++",
        ]
        child = [
            f"execve({quote(self.host)}, [{quote(self.host)}, {quote(self.app)}, {quote('@' + str(path))}], []) = 0",
            self.open(path, result=f"11<{path}>"),
            f"close(11<{path}>) = 0", "+++ exited with 0 +++",
        ]
        return path, host, child

    def response_report(self, host, child, **kwargs):
        self.log(host)
        self.log(child, pid=101, start=1.0055)
        return self.analyze(allow_response_file_scratch=True, response_file_profile=True, **kwargs)

    def test_response_scratch_requires_opt_in_and_records_fd_initialization(self):
        path, host, child = self.response_fixture()
        report = self.response_report(host, child)
        self.assertTrue(report["ok"], report)
        self.assertEqual(1, len(report["responseFileScratchAllowanceUsage"]))
        evidence = report["responseFileScratchCandidates"][0]
        self.assertTrue(evidence["eligible"], evidence)
        self.assertEqual("ftruncate(fd,0)", evidence["initialization"])
        self.assertEqual([101], evidence["readerProcessIds"])
        self.assertEqual(str(path.parent), evidence["privateDirectory"])
        self.assertEqual(1, report["actions"][0]["counts"]["responseFileScratch"])
        disabled = self.analyze(response_file_profile=True)
        self.assertFalse(disabled["ok"])
        self.assertTrue(disabled["responseFileScratchCandidates"][0]["eligible"])
        self.assertEqual([], disabled["responseFileScratchAllowanceUsage"])
        self.assertEqual([str(path)], [e["path"] for e in disabled["undeclaredReads"]])

    def test_response_scratch_exclusive_and_truncating_opens_initialize_without_ftruncate(self):
        for flag in ("O_EXCL", "O_TRUNC"):
            with self.subTest(flag=flag):
                path, host, child = self.response_fixture("O_WRONLY|O_CREAT|" + flag, initialize=False)
                report = self.response_report(host, child)
                self.assertTrue(report["ok"], report)
                self.assertEqual(flag, report["responseFileScratchCandidates"][0]["initialization"])

    def test_response_scratch_supports_dirfd_paths_relative_response_argv_and_ftruncate64(self):
        path, host, child = self.response_fixture()
        host[1] = f"mkdirat(13<{path.parent.parent}>, {quote(path.parent.name)}, 0700) = 0"
        host[2] = self.open(path.name, flags="O_WRONLY|O_CREAT", base=f"14<{path.parent}>",
                            result=f"10<{path}>")
        host[3] = host[3].replace("ftruncate(", "ftruncate64(")
        child[0] = child[0].replace(quote("@" + str(path)), quote("@" + os.path.relpath(path, self.root)))
        child[1] = self.open(path.name, base=f"14<{path.parent}>", result=f"11<{path}>")
        report = self.response_report(host, child)
        self.assertTrue(report["ok"], report)
        self.assertEqual(1, len(report["responseFileScratchAllowanceUsage"]))

    def test_response_scratch_rejects_missing_or_unsound_initialization(self):
        for change in ("no-reset", "nonzero", "failed-reset", "wrong-fd", "read-write", "no-create"):
            with self.subTest(change=change):
                path, host, child = self.response_fixture()
                if change == "no-reset":
                    host[3] = "getpid() = 100"
                elif change == "nonzero":
                    host[3] = host[3].replace(", 0)", ", 1)")
                elif change == "failed-reset":
                    host[3] = host[3].replace("= 0", "= -1 EIO (Input/output error)")
                elif change == "wrong-fd":
                    host[3] = host[3].replace("10<", "9<")
                elif change == "read-write":
                    host[2] = host[2].replace("O_WRONLY", "O_RDWR")
                else:
                    host[2] = host[2].replace("|O_CREAT", "")
                report = self.response_report(host, child)
                self.assertFalse(report["ok"], report)
                self.assertEqual([], report["responseFileScratchAllowanceUsage"])

    def test_response_scratch_rejects_weak_ownership_and_incomplete_lifecycles(self):
        for change in ("nonprivate", "preexisting-dir", "missing-writer-close", "missing-reader-close",
                       "missing-unlink", "failed-unlink", "different-family", "missing-argument",
                       "truncated-child", "unknown-call", "failed-action", "unannotated-close"):
            with self.subTest(change=change):
                path, host, child = self.response_fixture()
                if change == "nonprivate":
                    host[1] = host[1].replace("0700", "0777")
                elif change == "preexisting-dir":
                    host[1] = host[1].replace("= 0", "= -1 EEXIST (File exists)")
                elif change == "missing-writer-close":
                    host[4] = "getpid() = 100"
                elif change == "missing-reader-close":
                    child[2] = "getpid() = 101"
                elif change == "missing-unlink":
                    host[9] = "getpid() = 100"
                elif change == "failed-unlink":
                    host[9] = host[9].replace("= 0", "= -1 EACCES (Permission denied)")
                elif change == "different-family":
                    child[0] = self.exec()
                elif change == "missing-argument":
                    child[0] = child[0].replace(quote("@" + str(path)), quote("not-a-response-file"))
                elif change == "truncated-child":
                    child.pop()
                elif change == "unknown-call":
                    host[7] = "unknown_operation() = 0"
                elif change == "failed-action":
                    host[-1] = "+++ exited with 1 +++"
                else:
                    host[4] = "close(10) = 0"
                report = self.response_report(host, child)
                self.assertFalse(report["ok"], report)
                self.assertEqual([], report["responseFileScratchAllowanceUsage"])

    def test_response_scratch_rejects_conflicting_mutations_duplication_and_early_reads(self):
        for change in ("rename", "hardlink", "symlink", "chmod", "fd-chmod", "duplicate-fd", "fd-range",
                       "early-read", "early-unlink", "second-writer", "late-truncate"):
            with self.subTest(change=change):
                path, host, child = self.response_fixture()
                if change == "rename":
                    host[7] = f"rename({quote(path)}, {quote(path.with_suffix('.other'))}) = 0"
                elif change == "hardlink":
                    host[7] = f"link({quote(self.source)}, {quote(path)}) = 0"
                elif change == "symlink":
                    host[7] = f"symlink({quote(self.source)}, {quote(path)}) = 0"
                elif change == "chmod":
                    host[7] = f"chmod({quote(path.parent)}, 0777) = 0"
                elif change == "fd-chmod":
                    host[7] = f"fchmod(15<{path.parent}>, 0777) = 0"
                elif change == "duplicate-fd":
                    host[7] = f"dup(11<{path}>) = 12<{path}>"
                elif change == "fd-range":
                    child[2] = "close_range(3, ~0U, 0) = 0"
                elif change == "early-read":
                    host[3] = self.open(path, result=f"12<{path}>")
                elif change == "early-unlink":
                    host[3], host[9] = host[9], host[3]
                elif change == "second-writer":
                    host[7] = self.open(path, flags="O_WRONLY|O_TRUNC", result=f"12<{path}>")
                else:
                    host[3], host[7] = host[7], host[3]
                report = self.response_report(host, child)
                self.assertFalse(report["ok"], report)
                self.assertEqual([], report["responseFileScratchAllowanceUsage"])

    def test_response_scratch_allows_sequential_child_reopens_but_not_unfinished_overlap(self):
        path, host, child = self.response_fixture()
        host[9:9] = ["getpid() = 100", "getpid() = 100"]
        child[3:3] = [self.open(path, result=f"11<{path}>"), f"close(11<{path}>) = 0"]
        report = self.response_report(host, child)
        self.assertTrue(report["ok"], report)
        self.assertEqual(2, len(report["responseFileScratchAllowanceUsage"]))
        path, host, child = self.response_fixture()
        host[2] = host[2].split(") = ")[0] + " <unfinished ...>"
        host[3] = f"<... openat resumed>) = 10<{path}>"
        host[4] = "getpid() = 100"
        host[7] = f"ftruncate(10<{path}>, 0) = 0"
        host[8] = f"close(10<{path}>) = 0"
        report = self.response_report(host, child)
        self.assertFalse(report["ok"], report)
        self.assertEqual([], report["unresolved"])
        self.assertEqual([], report["responseFileScratchAllowanceUsage"])

    def test_response_scratch_does_not_admit_symlink_spelling_or_other_private_reads(self):
        path, host, child = self.response_fixture()
        link = self.root / "response alias"
        link.symlink_to(path)
        child[1] = self.open(link, result=f"11<{path}>")
        report = self.response_report(host, child)
        self.assertFalse(report["ok"], report)
        self.assertEqual([], report["responseFileScratchAllowanceUsage"])
        path, host, child = self.response_fixture()
        secret = path.parent / "ordinary-source.rsp"
        host[7] = self.open(secret, result=f"12<{secret}>")
        report = self.response_report(host, child)
        self.assertFalse(report["ok"], report)
        self.assertEqual([str(secret)], [e["path"] for e in report["undeclaredReads"]])
        self.assertEqual(1, len(report["responseFileScratchAllowanceUsage"]))

    def test_response_scratch_is_not_a_source_or_declared_output_allowance(self):
        original = {name: list(self.descriptor["parameters"][name]["items"])
                    for name in ("DeclaredInputs", "DeclaredOutputs")}
        for declared in ("DeclaredInputs", "DeclaredOutputs"):
            with self.subTest(declared=declared):
                for name, entries in original.items():
                    self.descriptor["parameters"][name]["items"] = list(entries)
                path, host, child = self.response_fixture()
                self.descriptor["parameters"][declared]["items"].append({"itemSpec": str(path), "metadata": {}})
                self.write_graph()
                report = self.response_report(host, child)
                self.assertEqual([], report["responseFileScratchAllowanceUsage"])
                self.assertFalse(report["responseFileScratchCandidates"][0]["eligible"])
                if declared == "DeclaredOutputs":
                    self.assertEqual(1, report["actions"][0]["counts"]["selfOutput"])
                else:
                    self.assertFalse(report["ok"])
        for name, entries in original.items():
            self.descriptor["parameters"][name]["items"] = list(entries)
        path, host, child = self.response_fixture()
        self.descriptor["parameters"]["DeclaredOutputs"]["items"].append(
            {"itemSpec": str(path.parent), "metadata": {}})
        self.write_graph()
        report = self.response_report(host, child)
        self.assertFalse(report["ok"], report)
        self.assertEqual([], report["responseFileScratchAllowanceUsage"])

    def test_response_scratch_requires_profile_and_successful_unchanged_capture(self):
        path, host, child = self.response_fixture()
        self.response_report(host, child)
        for kwargs in ({"response_file_profile": False}, {"capture_complete": False}):
            options = {"allow_response_file_scratch": True, "response_file_profile": True, **kwargs}
            report = self.analyze(**options)
            self.assertFalse(report["ok"], report)
            self.assertEqual([], report["responseFileScratchAllowanceUsage"])
        manifest = {"graph": str(self.graph), "cwd": str(self.root),
                    "files": {str(self.graph): audit.digest(self.graph)}, "complete": True,
                    "responseFileTraceProfile": 1, "commandExitCode": 1}
        audit.write_json(self.root / "capture.json", manifest)
        result = self.root / "report.json"
        self.assertEqual(1, audit.main(["analyze", "--capture", str(self.root),
                                      "--allow-response-file-scratch", "--report", str(result)]))
        self.assertEqual([], json.loads(result.read_text())["responseFileScratchAllowanceUsage"])
        manifest["commandExitCode"] = 0
        audit.write_json(self.root / "capture.json", manifest)
        self.graph.write_text(self.graph.read_text() + "# changed\n")
        self.assertEqual(1, audit.main(["analyze", "--capture", str(self.root),
                                      "--allow-response-file-scratch", "--report", str(result)]))
        changed = json.loads(result.read_text())
        self.assertEqual([], changed["responseFileScratchAllowanceUsage"])
        self.assertTrue(any(e["reason"] == "Capture input changed" for e in changed["incompleteness"]))

    def test_ordinary_undeclared_content_read_fails_but_controller_read_is_separate(self):
        secret = self.file("ordinary-secret.txt")
        controller = self.file("controller-only")
        self.log([self.open(controller), self.exec(), self.open(self.source), self.open(secret),
                  "+++ exited with 0 +++"])
        report = self.analyze()
        self.assertFalse(report["ok"])
        self.assertEqual([str(secret)], [e["path"] for e in report["undeclaredReads"]])
        self.assertEqual(1, report["controller"]["events"])
        self.assertEqual("action_0", report["undeclaredReads"][0]["action"])

    def test_declared_child_reads_inherit_action_and_cwd(self):
        subdir = self.root / "subdir"
        subdir.mkdir()
        source = self.file("subdir/child.cs")
        self.write_graph(inputs=[source, self.app])
        self.log([self.exec(), 'clone(child_stack=NULL, flags=SIGCHLD) = 101',
                  "+++ exited with 0 +++"])
        self.log([f"chdir({quote(subdir)}) = 0",
                  self.open("child.cs", result=f"3<{source}>"),
                  f"execve({quote(self.app)}, [{quote(self.app)}], 0x0) = 0",
                  self.open(source), "+++ exited with 0 +++"], pid=101, start=2)
        report = self.analyze()
        self.assertTrue(report["ok"], report)
        self.assertEqual([100, 101], report["actions"][0]["pids"])
        self.assertEqual(4, report["actions"][0]["counts"]["contentInput"])

    def test_order_only_and_descriptor_only_inputs_do_not_admit_content(self):
        prior = self.file("prior-output")
        self.write_graph(inputs=[], order=[self.source, prior])
        self.log([self.exec(), self.open(self.source), self.open(prior), "+++ exited with 0 +++"])
        report = self.analyze()
        self.assertFalse(report["ok"])
        self.assertEqual({str(self.source), str(prior)}, {e["path"] for e in report["undeclaredReads"]})

    def test_spaces_dollars_colons_quotes_and_c_escaped_filenames(self):
        source = self.file('a space/$colon:quote"back\\slash.cs')
        self.write_graph(inputs=[source])
        self.log([self.exec(), self.open(source), "+++ exited with 0 +++"])
        self.assertTrue(self.analyze()["ok"])
        self.assertEqual(b"a\tb\xff", os.fsencode(audit.c_string(r'"a\tb\377"')))
        self.assertEqual(["a b", "c:d", "$e"], audit.ninja_tokens("a$ b c$:d $$e"))

    def test_symlink_exact_target_and_dirfd_do_not_admit_siblings(self):
        target = self.file("real/one.cs")
        sibling = self.file("real/two.cs")
        link = self.root / "link.cs"
        link.symlink_to(target)
        self.write_graph(inputs=[link])
        self.log([self.exec(), self.open(link), self.open("one.cs", base=f"7<{target.parent}>",
                                                        result=f"8<{target}>"),
                  self.open("two.cs", base=f"7<{sibling.parent}>", result=f"9<{sibling}>"),
                  "+++ exited with 0 +++"])
        report = self.analyze()
        self.assertEqual([str(sibling)], [e["path"] for e in report["undeclaredReads"]])
        self.assertFalse(report["ok"])

    def test_symlink_retarget_and_annotated_canonical_mismatch_fail(self):
        other = self.file("other.cs")
        self.log([self.exec(), self.open(self.source, result=f"3<{other}>"), "+++ exited with 0 +++"])
        report = self.analyze()
        self.assertFalse(report["ok"])
        self.assertEqual(str(other), report["undeclaredReads"][0]["canonicalPath"])

    def test_symlink_dotdot_cannot_collapse_into_declared_path(self):
        (self.root / "elsewhere/sub").mkdir(parents=True)
        (self.root / "link").symlink_to(self.root / "elsewhere/sub")
        target = self.file("elsewhere/source.cs")
        path = self.root / "link/../source.cs"
        self.log([self.exec(), self.open(path, result=f"3<{target}>"), "+++ exited with 0 +++"])
        self.assertFalse(self.analyze()["ok"])

    def test_output_writes_and_self_reads_are_separate(self):
        external = self.root / "not-declared-output"
        self.log([self.exec(), self.open(external, flags="O_WRONLY|O_CREAT", result=f"3<{external}>"),
                  self.open(self.output, flags="O_RDWR|O_CREAT", result=f"4<{self.output}>"),
                  self.open(self.output), "+++ exited with 0 +++"])
        report = self.analyze()
        self.assertTrue(report["ok"])
        self.assertEqual(2, report["actions"][0]["counts"]["selfOutput"])

    def test_failed_opens_metadata_and_strict_probes(self):
        missing = self.root / "missing"
        self.log([self.exec(), self.open(missing, result="-1 ENOENT (No such file or directory)"),
                  f"newfstatat(AT_FDCWD, {quote(missing)}, 0x0, 0) = -1 ENOENT (No such file)",
                  f"access({quote(self.root)}, F_OK) = 0", "+++ exited with 0 +++"])
        report = self.analyze()
        self.assertTrue(report["ok"])
        self.assertEqual(2, len(report["negativeProbes"]))
        self.assertEqual(1, len(report["metadataProbes"]))
        self.assertFalse(self.analyze(strict=True)["ok"])

    def test_readlink_and_directory_enumeration_are_content(self):
        directory = self.root / "members"
        directory.mkdir()
        link = self.root / "undeclared-link"
        link.symlink_to(self.source)
        self.log([self.exec(), f"readlink({quote(link)}, {quote(self.source)}, 65535) = 10",
                  f"getdents64(3<{directory}>, [], 32768) = 0", "+++ exited with 0 +++"])
        report = self.analyze()
        self.assertEqual({"readlink", "directory"}, {e["kind"] for e in report["undeclaredReads"]})
        self.assertFalse(report["ok"])

    def test_runtime_and_platform_catalogs_are_exact_and_reported(self):
        runtime = self.file("runtime/libcoreclr.so")
        sibling = self.file("runtime/not-cataloged.so")
        platform = self.file("loader.so")
        runtime_catalog = self.root / "runtime.json"
        platform_catalog = self.root / "platform.json"
        audit.write_json(runtime_catalog, {"files": [{"path": str(runtime), "canonicalPath": str(runtime)}]})
        audit.write_json(platform_catalog, {"files": [str(platform)]})
        self.log([self.exec(), self.open(runtime), self.open(platform), self.open(sibling),
                  "+++ exited with 0 +++"])
        report = self.analyze(runtime_catalogs=[runtime_catalog], platform_catalogs=[platform_catalog])
        self.assertFalse(report["ok"])
        self.assertEqual([str(sibling)], [e["path"] for e in report["undeclaredReads"]])
        self.assertEqual(str(runtime), report["runtimeAllowanceUsage"][0]["path"])
        self.assertEqual(str(platform_catalog), report["platformAllowanceUsage"][0]["catalog"])
        self.assertEqual(1, len(report["allowanceRules"]["runtime"]))

    def test_catalog_directory_does_not_grant_subtree(self):
        catalog = self.root / "allow.json"
        audit.write_json(catalog, {"files": [str(self.root)]})
        with self.assertRaises(audit.AuditError):
            audit.exact_catalog([catalog])

    def test_runtime_catalog_symlink_allows_only_exact_target(self):
        target = self.file("runtime/real.so")
        link = self.root / "runtime/link.so"
        link.symlink_to(target)
        sibling = self.file("runtime/sibling.so")
        catalog = self.root / "runtime.json"
        audit.write_json(catalog, {"files": [{"path": str(link), "canonicalPath": str(target)}]})
        self.log([self.exec(), self.open(link), self.open(target), self.open(sibling),
                  "+++ exited with 0 +++"])
        report = self.analyze(runtime_catalogs=[catalog])
        self.assertEqual(2, len(report["runtimeAllowanceUsage"]))
        self.assertEqual([str(sibling)], [event["path"] for event in report["undeclaredReads"]])

    def test_proc_self_uses_traced_identity_even_without_allowance(self):
        self.log([self.exec(), 'readlink("/proc/self", "100", 1023) = 3',
                  self.open("/proc/self/maps", result="7</proc/100/maps>"), "+++ exited with 0 +++"])
        report = self.analyze()
        self.assertFalse(report["ok"])
        self.assertEqual(["/proc/100", "/proc/100/maps"],
                         [event["canonicalPath"] for event in report["undeclaredReads"]])

    def test_opt_in_proc_rules_follow_thread_group_and_record_each_use(self):
        self.log([self.exec(), "clone(child_stack=NULL, flags=CLONE_THREAD|CLONE_FILES|CLONE_FS) = 101",
                  'readlink("/proc/self", "100", 1023) = 3',
                  self.open("/proc/self/maps", result="7</proc/100/maps>"),
                  self.open("/proc/self/task/101/comm", flags="O_RDWR", result="8</proc/100/task/101/comm>"),
                  f'readlink("/proc/100/exe", {quote(self.host)}, 65535) = 20', "+++ exited with 0 +++"])
        self.log([self.open("/proc/self/cgroup", result="9</proc/100/cgroup>"),
                  self.open("/proc/self/mountinfo", result="10</proc/100/mountinfo>"),
                  "+++ exited with 0 +++"], pid=101, start=2)
        report = self.analyze(allow_runtime_proc=True)
        self.assertTrue(report["ok"], report)
        self.assertEqual(6, len(report["runtimeProcAllowanceUsage"]))
        self.assertEqual({100}, {event["processId"] for event in report["runtimeProcAllowanceUsage"]})
        self.assertEqual({"100@1.000000"}, {event["actionFamily"] for event in report["runtimeProcAllowanceUsage"]})

    def test_proc_rules_reject_terminated_same_family_process(self):
        self.log([self.exec(), "clone(child_stack=NULL, flags=SIGCHLD) = 101",
                  f"access({quote(self.source)}, F_OK) = 0",
                  self.open("/proc/101/maps", result="7</proc/101/maps>"), "+++ exited with 0 +++"])
        self.log(["+++ exited with 0 +++"], pid=101, start=1.0015)
        report = self.analyze(allow_runtime_proc=True)
        self.assertFalse(report["ok"])
        self.assertEqual("/proc/101/maps", report["undeclaredReads"][0]["path"])
        self.assertEqual([], report["runtimeProcAllowanceUsage"])

    def test_proc_rules_deny_other_family_unknown_threads_and_other_leaves(self):
        self.log(["clone(child_stack=NULL, flags=SIGCHLD) = 200", self.exec(),
                  self.open("/proc/200/maps", result="7</proc/200/maps>"),
                  self.open("/proc/self/task/999/comm", result="8</proc/100/task/999/comm>"),
                  self.open("/proc/self/environ", result="9</proc/100/environ>"),
                  self.open("/proc/self/fd/0", result="10</proc/100/fd/0>"),
                  "+++ exited with 0 +++"])
        self.log(["getcwd(" + quote(self.root) + ", 4096) = 100", "+++ exited with 0 +++"],
                 pid=200, start=2)
        report = self.analyze(allow_runtime_proc=True)
        self.assertFalse(report["ok"])
        self.assertEqual(4, len(report["undeclaredReads"]))
        self.assertEqual([], report["runtimeProcAllowanceUsage"])

    def test_proc_self_catalog_cannot_capture_analyzer_identity(self):
        catalog = self.root / "proc.json"
        audit.write_json(catalog, {"files": ["/proc/self/maps"]})
        with self.assertRaises(audit.AuditError):
            audit.exact_catalog([catalog])

    def test_fixed_directory_membership_admits_enumeration_not_child_content(self):
        directory, catalog = self.fixed_directory(["one.cs"])
        self.log([self.exec(), self.open(directory, flags="O_RDONLY|O_DIRECTORY", result=f"7<{directory}>"),
                  self.dents(directory, [".", "..", "one.cs"]), self.dents(directory, []),
                  self.open(directory / "one.cs"), "+++ exited with 0 +++"])
        report = self.analyze(platform_catalogs=[catalog])
        self.assertFalse(report["ok"])
        self.assertEqual([str(directory / "one.cs")], [e["path"] for e in report["undeclaredReads"]])
        self.assertEqual(3, len(report["directoryAllowanceUsage"]))
        self.assertEqual({"trace-and-analysis"}, {e["membershipEvidence"]
                         for e in report["directoryAllowanceUsage"] if e["kind"] == "directory"})

    def test_directory_membership_mismatch_in_trace_or_filesystem_fails_closed(self):
        directory, catalog = self.fixed_directory(["expected"])
        self.log([self.exec(), self.dents(directory, ["unexpected"]), self.dents(directory, []),
                  "+++ exited with 0 +++"])
        report = self.analyze(runtime_catalogs=[catalog])
        self.assertFalse(report["ok"])
        self.assertEqual(["unexpected"], report["directoryCatalogMismatches"][0]["extra"])
        self.assertEqual(2, len(report["undeclaredReads"]))
        self.log([self.exec(), self.dents(directory, ["expected"]), self.dents(directory, []),
                  "+++ exited with 0 +++"])
        (directory / "new-unlisted").write_text("membership changed after catalog")
        report = self.analyze(runtime_catalogs=[catalog])
        self.assertFalse(report["ok"])
        self.assertTrue(report["directoryCatalogMismatches"])

    def test_abbreviated_or_partial_directory_trace_cannot_validate_catalog(self):
        directory, catalog = self.fixed_directory(["one"])
        for line in (f"getdents64(7<{directory}>, 0x123 /* 3 entries */, 32768) = 96",
                     self.dents(directory, ["one"])):
            with self.subTest(line=line):
                self.log([self.exec(), line, "+++ exited with 0 +++"])
                report = self.analyze(platform_catalogs=[catalog])
                self.assertFalse(report["ok"])
                self.assertTrue(report["directoryCatalogMismatches"])

    def test_directory_reenumeration_does_not_reuse_prior_membership(self):
        directory, catalog = self.fixed_directory(["one", "two"])
        self.log([self.exec(), self.dents(directory, ["one", "two"]), self.dents(directory, []),
                  self.dents(directory, ["one"]), self.dents(directory, []), "+++ exited with 0 +++"])
        report = self.analyze(platform_catalogs=[catalog])
        self.assertFalse(report["ok"])
        self.assertEqual(["two"], report["directoryCatalogMismatches"][0]["missing"])
        self.assertEqual(2, len(report["undeclaredReads"]))

    def test_directory_handle_without_enumeration_is_separate_metadata(self):
        directory = self.root / "handle-only"
        directory.mkdir()
        self.log([self.exec(), self.open(directory, flags="O_RDONLY|O_DIRECTORY",
                                        result=f"7<{directory}>"), "+++ exited with 0 +++"])
        report = self.analyze()
        self.assertTrue(report["ok"], report)
        self.assertEqual([], report["undeclaredReads"])
        self.assertEqual("directoryHandle", report["directoryHandleOpens"][0]["kind"])
        self.assertFalse(self.analyze(strict=True)["ok"])

    def test_case_probe_requires_opt_in_exclusive_creation_and_unlink(self):
        path = self.root / ("CASESENSITIVETEST" + "a" * 32)
        self.log([self.exec(), self.open(path, flags="O_RDWR|O_CREAT|O_EXCL"),
                  f"unlink({quote(path)}) = 0", "+++ exited with 0 +++"])
        self.assertFalse(self.analyze()["ok"])
        report = self.analyze(allow_case_sensitivity_probe=True)
        self.assertTrue(report["ok"], report)
        self.assertEqual(1, len(report["selfCreatedProbes"]))
        self.assertEqual(1, len(report["runtimeScratchAllowanceUsage"]))
        for flags, unlink in (("O_RDWR", True), ("O_RDWR|O_CREAT|O_EXCL", False)):
            self.log([self.exec(), self.open(path, flags=flags)] +
                     ([f"unlink({quote(path)}) = 0"] if unlink else []) + ["+++ exited with 0 +++"])
            self.assertFalse(self.analyze(allow_case_sensitivity_probe=True)["ok"])

    def test_case_probe_does_not_allow_later_opens_or_other_new_files(self):
        path = self.root / ("CASESENSITIVETEST" + "b" * 32)
        other = self.root / "arbitrary-scratch"
        self.log([self.exec(), self.open(path, flags="O_RDWR|O_CREAT|O_EXCL"), self.open(path),
                  self.open(other, flags="O_RDWR|O_CREAT|O_EXCL"),
                  f"unlink({quote(path)}) = 0", f"unlink({quote(other)}) = 0",
                  "+++ exited with 0 +++"])
        report = self.analyze(allow_case_sensitivity_probe=True)
        self.assertFalse(report["ok"])
        self.assertEqual([str(path), str(other)], [e["path"] for e in report["undeclaredReads"]])
        self.assertEqual(1, len(report["runtimeScratchAllowanceUsage"]))

    def test_dirfd_unresolved_and_unknown_syscall_fail_closed(self):
        self.log([self.exec(), self.open("source.cs", base="7", result=f"8<{self.source}>"),
                  'open_tree(AT_FDCWD, ".", 0) = 8', "+++ exited with 0 +++"])
        report = self.analyze()
        self.assertFalse(report["ok"])
        self.assertEqual(2, len(report["unresolved"]))

    def test_device_fd_annotations_and_openat2(self):
        device = self.file("random-device")
        self.write_graph(inputs=[device])
        self.log([self.exec(),
                  f"openat2(AT_FDCWD, {quote(device)}, "
                  f"{{flags=O_RDONLY|O_CLOEXEC, resolve=0}}, 24) = 3<{device}<char 1:9>>",
                  "+++ exited with 0 +++"])
        report = self.analyze()
        self.assertTrue(report["ok"], report)
        self.assertEqual(2, report["actions"][0]["counts"]["contentInput"])

    def test_unfinished_resumed_clone_and_child_attribution(self):
        self.log([self.exec(), "clone(child_stack=NULL, flags=SIGCHLD <unfinished ...>",
                  "<... clone resumed>) = 101", "+++ exited with 0 +++"])
        self.log([self.open(self.source), "+++ exited with 0 +++"], pid=101, start=1.0015)
        report = self.analyze()
        self.assertTrue(report["ok"], report)
        self.assertIn(101, report["actions"][0]["pids"])

    def test_unfinished_resumed_content_open_is_audited(self):
        secret = self.file("not-an-input")
        self.log([self.exec(),
                  f"openat(AT_FDCWD, {quote(secret)}, O_RDONLY <unfinished ...>",
                  f"<... openat resumed>) = 3<{secret}>", "+++ exited with 0 +++"])
        report = self.analyze()
        self.assertFalse(report["ok"])
        self.assertEqual(str(secret), report["undeclaredReads"][0]["path"])
        self.assertEqual("1.001000", report["undeclaredReads"][0]["timestamp"])
        events, _ = audit.read_events(self.prefix)
        self.assertEqual("1.002000", next(e for e in events if e["syscall"] == "openat")["completedTimestamp"])

    def test_incomplete_or_unmatched_trace_is_not_silently_dropped(self):
        for line in ('openat(AT_FDCWD, "a", O_RDONLY <unfinished ...>',
                     '<... openat resumed>) = 3', 'openat(AT_FDCWD, "a"...',
                     'openat(AT_FDCWD, "a", O_RDONLY) = ? ERESTARTSYS'):
            with self.subTest(line=line):
                self.log([self.exec(), line])
                report = self.analyze()
                self.assertFalse(report["ok"])
                self.assertTrue(report["incompleteness"] or report["unresolved"])

    def test_missing_child_and_multiple_roots_are_incomplete(self):
        self.log([self.exec(), "clone(child_stack=NULL, flags=SIGCHLD) = 101",
                  "+++ exited with 0 +++"])
        self.assertFalse(self.analyze()["ok"])
        self.log([self.open(self.source), "+++ exited with 0 +++"], pid=102)
        self.assertTrue(any("controller root" in e["reason"] for e in self.analyze()["incompleteness"]))

    def test_truncated_argv_and_strings_are_unresolved(self):
        self.log([f'execve({quote(self.host)}, [{quote(self.host)}, "--invocation", ...], 0x0) = 0',
                  "+++ exited with 0 +++"])
        self.assertFalse(self.analyze()["ok"])
        with self.assertRaises(audit.AuditError):
            audit.c_string('"truncated"...')

    def test_generated_tree_dynamic_inputs_and_self_output_reads(self):
        tree = self.root / "generated"
        member = self.file("generated/A.cs")
        escape_target = self.file("outside.cs")
        (tree / "escape.cs").symlink_to(escape_target)
        archive, dyndep, receipt = [self.root / name for name in ("tree.zip", "tree.dd", "tree.receipt")]
        self.descriptor["parameters"]["DeclaredOutputTrees"] = {"items": [{
            "itemSpec": str(tree), "metadata": {"ArchiveFile": str(archive),
                                                "DyndepFile": str(dyndep), "ReceiptFile": str(receipt)}}]}
        self.write_graph()
        self.log([self.exec(), self.open(member), self.open(tree / "escape.cs"),
                  "+++ exited with 0 +++"])
        report = self.analyze()
        self.assertEqual(1, report["actions"][0]["counts"]["selfOutputTree"])
        self.assertEqual(1, len(report["undeclaredReads"]))
        command = " ".join(shlex.quote(str(p)) for p in
                           [self.host, self.app, "--materialize-tree", self.sidecar, tree])
        with self.graph.open("a") as graph:
            graph.write(f"rule action_tree\n  command = {command}\nbuild {escape(receipt)}: "
                        f"action_tree {escape(archive)} | {escape(self.sidecar)} "
                        f"{escape(self.host)} {escape(self.app)}\n")
        self.log([self.exec("--materialize-tree", tree), self.open(member), self.open(receipt),
                  "+++ exited with 0 +++"])
        report = self.analyze()
        self.assertTrue(report["ok"], report)
        self.assertEqual("materialize-tree", report["actions"][0]["mode"])
        consumer = self.root / "consumer.json"
        consumer.write_text(json.dumps({**self.descriptor, "parameters": {}}))
        with self.graph.open("a") as graph:
            graph.write(f"rule consumer\n  command = {self.host} {self.app} --invocation {consumer}\n"
                        f"build consumed: consumer {tree} | {self.host} {self.app} {consumer}\n")
        exec_consumer = self.exec().replace(quote(self.sidecar), quote(consumer))
        self.log([exec_consumer, self.open(member), f"getdents64(3<{tree}>, [], 32768) = 0",
                  "+++ exited with 0 +++"])
        report = self.analyze()
        self.assertTrue(report["ok"], report)
        self.assertEqual(2, report["actions"][0]["counts"]["generatedTreeInput"])
        self.assertEqual(1, report["actions"][0]["counts"]["contentInput"])

    def test_shared_clone_fs_and_fchdir(self):
        subdir = self.root / "sub"
        subdir.mkdir()
        child_source = self.file("sub/child.cs")
        self.write_graph(inputs=[child_source])
        self.log([self.exec(), "clone(child_stack=NULL, flags=CLONE_FS|SIGCHLD) = 101",
                  f"fchdir(7<{subdir}>) = 0",
                  self.open("child.cs", result=f"3<{child_source}>"), "+++ exited with 0 +++"])
        self.log([self.open("child.cs", result=f"3<{child_source}>"), "+++ exited with 0 +++"],
                 pid=101, start=2)
        self.assertTrue(self.analyze()["ok"])

    def test_failed_chdir_is_negative_and_does_not_change_cwd(self):
        missing = self.root / "missing-dir"
        self.log([self.exec(), f"chdir({quote(missing)}) = -1 ENOENT (No such file)",
                  self.open("source.cs", result=f"3<{self.source}>"), "+++ exited with 0 +++"])
        report = self.analyze()
        self.assertTrue(report["ok"], report)
        self.assertEqual(str(missing), report["negativeProbes"][0]["path"])
        self.assertFalse(self.analyze(strict=True)["ok"])

    def test_exec_unshares_clone_fs_cwd_context(self):
        subdir = self.root / "sub"
        subdir.mkdir()
        self.log([self.exec(), "clone(child_stack=NULL, flags=CLONE_FS|SIGCHLD) = 101",
                  f"access({quote(self.source)}, F_OK) = 0",
                  self.open("source.cs", result=f"3<{self.source}>"), "+++ exited with 0 +++"])
        self.log([f"execve({quote(self.app)}, [{quote(self.app)}], 0x0) = 0",
                  f"chdir({quote(subdir)}) = 0", "+++ exited with 0 +++"], pid=101, start=1.0011)
        report = self.analyze()
        self.assertTrue(report["ok"], report)
        self.assertEqual([], report["undeclaredReads"])

    def test_cli_separate_analyze_exit_code_and_capture_digest(self):
        self.log([self.exec(), self.open(self.source), "+++ exited with 0 +++"])
        capture = self.root / "capture"
        capture.mkdir()
        for path in self.root.glob("trace.*"):
            shutil.copyfile(path, capture / path.name)
        audit.write_json(capture / "capture.json", {
            "schemaVersion": 1, "graph": str(self.graph), "cwd": str(self.root), "complete": True,
            "commandExitCode": 0, "files": {str(self.graph): audit.digest(self.graph),
                                          str(self.sidecar): audit.digest(self.sidecar)}})
        report = self.root / "report.json"
        args = ["analyze", "--capture", str(capture), "--report", str(report)]
        self.assertEqual(0, audit.main(args))
        self.assertTrue(json.loads(report.read_text())["ok"])
        with self.graph.open("a") as graph:
            graph.write("# changed after trace\n")
        self.assertEqual(1, audit.main(args))
        self.assertTrue(any(e["reason"] == "Capture input changed"
                            for e in json.loads(report.read_text())["incompleteness"]))
        before = self.graph.read_bytes()
        with contextlib.redirect_stderr(io.StringIO()):
            self.assertEqual(2, audit.main(["analyze", "--capture", str(capture),
                                            "--report", str(self.graph)]))
        self.assertEqual(before, self.graph.read_bytes())

    def test_catalog_cli_and_trace_command_passthrough_without_strace(self):
        runtime = self.file("external-runtime/libcoreclr.so")
        catalog = self.root / "runtime-catalog.json"
        self.assertEqual(0, audit.main(["catalog", "--root", str(runtime.parent), "--output", str(catalog)]))
        self.assertEqual({str(runtime)}, set(audit.exact_catalog([catalog])))
        tracer = self.root / "fake-strace"
        tracer.write_text("#!" + sys.executable + "\nimport sys\n"
                          "assert sys.argv[1:9] == ['-ff','-ttt','-yy','-v','-s','65535','-e',"
                          "'trace=%file,%process,getdents64']\n"
                          "assert sys.argv[-3:] == ['--', 'unexecuted tool', 'arg with spaces']\n"
                          "sys.exit(7)\n")
        tracer.chmod(0o700)
        capture = self.root / "capture"
        self.assertEqual(7, audit.main(["trace", "--strace", str(tracer), "--graph", str(self.graph),
                                        "--cwd", str(self.root), "--output", str(capture),
                                        "--", "unexecuted tool", "arg with spaces"]))
        manifest = json.loads((capture / "capture.json").read_text())
        self.assertEqual(7, manifest["commandExitCode"])
        self.assertTrue(manifest["complete"])
        self.assertEqual(["unexecuted tool", "arg with spaces"], manifest["argv"])

    def seccomp_tracer(self, mode):
        tracer = self.root / ("strace-" + mode)
        actual = "\n".join([
            "1.000000 " + self.exec(),
            "1.001000 " + self.open(self.source),
            "1.002000 +++ exited with 0 +++", "",
        ])
        tracer.write_text(
            "#!" + sys.executable + "\n"
            "import json, pathlib, sys\n"
            f"mode = {mode!r}\n"
            f"actual = {actual!r}\n"
            f"record = pathlib.Path({str(self.root / ('argv-' + mode + '.jsonl'))!r})\n"
            "with record.open('a') as f: f.write(json.dumps(sys.argv[1:]) + '\\n')\n"
            "prefix = pathlib.Path(sys.argv[sys.argv.index('-o') + 1])\n"
            "if '-d' in sys.argv:\n"
            " executable = sys.argv[sys.argv.index('--') + 1]\n"
            " text = '1.000000 execve(' + json.dumps(executable) + ', [], []) = 0\\n'\n"
            " text += '1.001000 +++ exited with 0 +++\\n'\n"
            " pathlib.Path(str(prefix) + '.700').write_text(text)\n"
            " print(sys.argv[0] + ': seccomp filter ' + ('disabled' if mode == 'disabled' else 'enabled'), file=sys.stderr)\n"
            " if mode != 'no-event':\n"
            "  pid = 701 if mode == 'wrong-pid' else 700\n"
            "  print(sys.argv[0] + ': [wait(0x07057f) = ' + str(pid) + '] WIFSTOPPED,EVENT_SECCOMP (7)', file=sys.stderr)\n"
            " sys.exit(1 if mode == 'probe-error' else 0)\n"
            "pathlib.Path(str(prefix) + '.100').write_text(actual)\n"
            "if mode == 'actual-fallback': print(sys.argv[0] + ': seccomp filter is requested but unavailable', file=sys.stderr)\n"
            "if mode == 'actual-error': print(sys.argv[0] + ': ptrace: unexpected failure', file=sys.stderr)\n"
            "if mode == 'child-stderr': print('compiler: seccomp feature disabled in this target', file=sys.stderr)\n")
        tracer.chmod(0o700)
        return tracer

    def seccomp_cli(self, tracer, capture):
        with contextlib.redirect_stderr(io.StringIO()):
            return audit.main(["trace", "--seccomp-bpf", "--response-file-provenance",
                               "--strace", str(tracer), "--graph", str(self.graph),
                               "--cwd", str(self.root), "--output", str(capture),
                               "--", "unexecuted tool"])

    def test_seccomp_opt_in_preserves_selection_and_records_live_verification(self):
        tracer = self.seccomp_tracer("ok")
        capture = self.root / "capture"
        self.assertEqual(0, self.seccomp_cli(tracer, capture))
        manifest = json.loads((capture / "capture.json").read_text())
        self.assertTrue(manifest["seccompBpf"]["verified"])
        self.assertTrue(manifest["complete"])
        self.assertEqual(1, manifest["seccompBpf"]["preflight"]["liveSeccompStops"])
        calls = [json.loads(line) for line in (self.root / "argv-ok.jsonl").read_text().splitlines()]
        expected = "trace=%file,%process,getdents64," + audit.RESPONSE_TRACE
        self.assertEqual([expected, expected], [c[c.index("-e") + 1] for c in calls])
        self.assertTrue(all("--seccomp-bpf" in c for c in calls))
        self.assertIn("-d", calls[0])
        self.assertNotIn("-d", calls[1])
        self.assertIn(str(capture / "strace-stderr.log"), manifest["files"])
        self.assertIn(str(capture / "seccomp-preflight/trace.700"), manifest["files"])

    def test_seccomp_probe_rejects_disabled_missing_wrong_pid_and_failed_activation(self):
        for mode in ("disabled", "no-event", "wrong-pid", "probe-error"):
            with self.subTest(mode=mode):
                capture = self.root / mode
                self.assertEqual(2, self.seccomp_cli(self.seccomp_tracer(mode), capture))
                manifest = json.loads((capture / "capture.json").read_text())
                self.assertFalse(manifest["complete"])
                self.assertFalse(manifest["seccompBpf"]["verified"])
                self.assertFalse((capture / "trace.100").exists())

    def test_seccomp_probe_timeout_never_starts_workload(self):
        tracer = self.seccomp_tracer("timeout")
        capture = self.root / "capture"
        with mock.patch.object(audit.subprocess, "run",
                               side_effect=subprocess.TimeoutExpired(["strace"], 15)), \
                mock.patch.object(audit.subprocess, "Popen") as process:
            self.assertEqual(2, self.seccomp_cli(tracer, capture))
        process.assert_not_called()
        self.assertFalse(json.loads((capture / "capture.json").read_text())["complete"])

    def test_seccomp_actual_fallback_or_error_fails_even_with_zero_exit(self):
        for mode in ("actual-fallback", "actual-error"):
            with self.subTest(mode=mode):
                capture = self.root / mode
                self.assertEqual(2, self.seccomp_cli(self.seccomp_tracer(mode), capture))
                manifest = json.loads((capture / "capture.json").read_text())
                self.assertEqual(0, manifest["commandExitCode"])
                self.assertFalse(manifest["complete"])
                self.assertTrue(manifest["seccompBpf"]["rejectedDiagnostics"])
                self.assertTrue((capture / "strace-stderr.log").read_text())

    def test_seccomp_preserves_nontracer_task_diagnostics(self):
        capture = self.root / "capture"
        self.assertEqual(0, self.seccomp_cli(self.seccomp_tracer("child-stderr"), capture))
        self.assertEqual("compiler: seccomp feature disabled in this target\n",
                         (capture / "strace-stderr.log").read_text())

    def test_seccomp_analysis_rejects_unverified_policy_and_changed_probe_evidence(self):
        capture = self.root / "capture"
        self.assertEqual(0, self.seccomp_cli(self.seccomp_tracer("ok"), capture))
        report = self.root / "report.json"
        argv = ["analyze", "--capture", str(capture), "--report", str(report)]
        self.assertEqual(0, audit.main(argv))
        manifest_path = capture / "capture.json"
        manifest = json.loads(manifest_path.read_text())
        manifest["seccompBpf"]["verified"] = False
        audit.write_json(manifest_path, manifest)
        self.assertEqual(1, audit.main(argv))
        self.assertIn("Seccomp capture verification failed",
                      [e["reason"] for e in json.loads(report.read_text())["incompleteness"]])
        manifest["seccompBpf"]["verified"] = True
        audit.write_json(manifest_path, manifest)
        (capture / "seccomp-preflight/diagnostics.log").write_text("changed")
        self.assertEqual(1, audit.main(argv))

    def test_unsupported_ninja_variables_and_includes_fail(self):
        for text in ("include other.ninja\n", "rule a\n  command = $tool --invocation foo\n",
                     "build output: a $input\n"):
            with self.subTest(text=text):
                self.graph.write_text(text)
                with self.assertRaises(audit.AuditError):
                    audit.load_graph(str(self.graph), str(self.root))

    def test_cli_negative_fixture_returns_nonzero(self):
        secret = self.file("undeclared.txt")
        self.log([self.exec(), self.open(secret), "+++ exited with 0 +++"])
        capture = self.root / "capture"
        capture.mkdir()
        shutil.copyfile(str(self.prefix) + ".100", capture / "trace.100")
        audit.write_json(capture / "capture.json", {
            "schemaVersion": 1, "graph": str(self.graph), "cwd": str(self.root), "complete": True,
            "commandExitCode": 0, "files": {str(self.graph): audit.digest(self.graph)}})
        report = self.root / "negative.json"
        result = subprocess.run([sys.executable, "-B", str(MODULE), "analyze", "--capture", str(capture),
                                 "--report", str(report)], check=False, capture_output=True, text=True)
        self.assertEqual(1, result.returncode, result.stderr)
        self.assertEqual(str(secret), json.loads(report.read_text())["undeclaredReads"][0]["path"])

    @unittest.skipUnless(os.environ.get("GENBUILD_AUDIT_DOTNET"), "Set GENBUILD_AUDIT_DOTNET/APP/RUNTIME for resolver regression")
    def test_isolated_task_resolver_precedence_without_app_restaging(self):
        host = os.environ["GENBUILD_AUDIT_DOTNET"]
        toolset = Path(os.environ["GENBUILD_AUDIT_APP"]).parent / "toolset"
        runtime = Path(os.environ["GENBUILD_AUDIT_RUNTIME"])
        compiler = toolset / "Roslyn/bincore/csc.dll"
        references = [*sorted(runtime.glob("*.dll")), toolset / "Microsoft.Build.Framework.dll",
                      toolset / "Microsoft.Build.Utilities.Core.dll"]
        work = self.root / "compiler-work"
        work.mkdir()
        env = {**os.environ, "TMPDIR": str(work), "DOTNET_EnableDiagnostics": "0"}

        def compile_cs(output, sources, additional=(), executable=False):
            output.parent.mkdir(parents=True, exist_ok=True)
            command = [host, str(compiler), "-noconfig", "-nostdlib+", "-langversion:preview",
                       "-nullable:enable", "-nologo", "-target:exe" if executable else "-target:library",
                       "-out:" + str(output), *["-r:" + str(p) for p in references],
                       *["-r:" + str(p) for p in additional], *map(str, sources)]
            result = subprocess.run(command, env=env, capture_output=True, text=True, check=False)
            self.assertEqual(0, result.returncode, result.stdout + result.stderr)

        companions = {}
        for marker in ("default", "dll", "exe", "manifest"):
            directory = self.root / marker
            directory.mkdir()
            source = directory / "Companion.cs"
            source.write_text('public static class Companion { public static string Value => "' + marker + '"; }')
            output = directory / ("Companion.exe" if marker == "exe" else "Companion.dll")
            compile_cs(output, [source])
            companions[marker] = output
        task_source = self.file("TaskContainer.cs")
        task_source.write_text("""
using System.Reflection;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
public static class Probe
{
    public static string Read() => Companion.Value;
    public static Assembly Framework() => typeof(ITask).Assembly;
    public static Assembly Utilities() => typeof(TaskItem).Assembly;
}
""")
        task = self.root / "compiled/TaskContainer.dll"
        compile_cs(task, [task_source], [companions["dll"]])
        for scenario in ("adjacent-dll", "adjacent-exe", "manifest", "manifest-empty", "host-only", "invalid"):
            directory = self.root / "cases" / scenario
            directory.mkdir(parents=True)
            shutil.copyfile(task, directory / task.name)
            if scenario not in ("adjacent-exe", "host-only"):
                shutil.copyfile(companions["dll"], directory / "Companion.dll")
            if scenario in ("adjacent-dll", "adjacent-exe"):
                shutil.copyfile(companions["exe"], directory / "Companion.exe")
            if scenario in ("manifest", "manifest-empty"):
                targets = {"TaskContainer/1.0": {"runtime": {"TaskContainer.dll": {}}}}
                libraries = {"TaskContainer/1.0": {"type": "project", "serviceable": False, "sha512": ""}}
                if scenario == "manifest":
                    shutil.copyfile(companions["manifest"], directory / "Companion.exe")
                    targets["Companion/1.0"] = {"runtime": {"Companion.exe": {}}}
                    targets["TaskContainer/1.0"]["dependencies"] = {"Companion": "1.0"}
                    libraries["Companion/1.0"] = {"type": "project", "serviceable": False, "sha512": ""}
                audit.write_json(directory / "TaskContainer.deps.json", {
                    "runtimeTarget": {"name": ".NETCoreApp,Version=v11.0", "signature": ""},
                    "targets": {".NETCoreApp,Version=v11.0": targets}, "libraries": libraries})
            if scenario == "invalid":
                (directory / "TaskContainer.deps.json").write_text("{ invalid manifest")
        (self.root / "cases/produced").mkdir()
        harness_source = self.file("ResolverRegression.cs")
        harness_source.write_text("""
using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using Microsoft.Build.ActionTaskHost;
internal static class Regression
{
    public static int Main(string[] args)
    {
        AssemblyLoadContext.Default.Resolving += (_, name) =>
        {
            string path = Path.Combine(args[0], name.Name + ".dll");
            return File.Exists(path) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(path) : null;
        };
        return Run(args[1]);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Run(string root)
    {
        AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(root, "default/Companion.dll"));
        foreach (string scenario in new[] { "adjacent-dll", "adjacent-exe", "manifest", "manifest-empty", "host-only", "produced" })
        {
            string task = Path.Combine(root, "cases", scenario, "TaskContainer.dll");
            TaskAssemblyLoadContext context = new(task);
            if (scenario == "produced")
            {
                File.Copy(Path.Combine(root, "compiled/TaskContainer.dll"), task);
                File.Copy(Path.Combine(root, "dll/Companion.dll"), Path.Combine(Path.GetDirectoryName(task)!, "Companion.dll"));
            }
            Type probe = context.LoadFromAssemblyPath(task).GetType("Probe", throwOnError: true)!;
            string expected = scenario switch
            {
                "adjacent-exe" => "exe",
                "manifest" => "manifest",
                "host-only" => "default",
                _ => "dll"
            };
            string actual = (string)probe.GetMethod("Read")!.Invoke(null, null)!;
            if (actual != expected)
                throw new Exception(scenario + ": expected " + expected + ", got " + actual);
            if (probe.GetMethod("Framework")!.Invoke(null, null) != typeof(ITask).Assembly ||
                probe.GetMethod("Utilities")!.Invoke(null, null) != typeof(TaskItem).Assembly)
                throw new Exception("Shared task contract assembly identity changed");
            try
            {
                context.LoadFromAssemblyName(new AssemblyName("Microsoft.Build"));
                throw new Exception("Engine dependency was not rejected");
            }
            catch (FileLoadException error) when (error.InnerException is NotSupportedException) { }
            Console.WriteLine(scenario + ": " + actual);
        }
        try
        {
            _ = new TaskAssemblyLoadContext(Path.Combine(root, "cases/invalid/TaskContainer.dll"));
            throw new Exception("Invalid existing manifest was ignored");
        }
        catch (InvalidOperationException) { }
        return 0;
    }
}
namespace Microsoft.Build.ActionTaskHost
{
    internal static class HostResources
    {
        internal static string Format(string key, params object?[] values) => key;
    }
}
""")
        harness = self.root / "ResolverRegression.dll"
        resolver_source = MODULE.parent.parent / "src/msbuild/src/MSBuild.ActionTaskHost/TaskAssemblyLoadContext.cs"
        compile_cs(harness, [resolver_source, harness_source], executable=True)
        audit.write_json(harness.with_suffix(".runtimeconfig.json"), {
            "runtimeOptions": {"tfm": "net11.0",
                               "framework": {"name": "Microsoft.NETCore.App", "version": runtime.name}}})
        command = [host, str(harness), str(toolset), str(self.root)]
        if os.environ.get("GENBUILD_AUDIT_STRACE"):
            command = [os.environ["GENBUILD_AUDIT_STRACE"], "-ff", "-ttt", "-yy", "-v",
                       "-s", "65535", "-e", "trace=%file,%process,getdents64",
                       "-o", str(self.root / "resolver-trace"), "--", *command]
        result = subprocess.run(command, env=env, capture_output=True, text=True, check=False)
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertIn("manifest: manifest", result.stdout)
        self.assertIn("produced: dll", result.stdout)
        if os.environ.get("GENBUILD_AUDIT_STRACE"):
            events, errors = audit.read_events(self.root / "resolver-trace")
            self.assertEqual([], errors)
            discovery = [e for e in events if e["syscall"] == "getdents64" and
                         "/cases/" in e["args"][0]]
            self.assertEqual([], discovery, discovery)

    @unittest.skipUnless(os.environ.get("GENBUILD_AUDIT_STRACE"), "Set GENBUILD_AUDIT_STRACE for Linux integration")
    def test_real_strace_controller_action_and_cat_child(self):
        tracer = os.environ["GENBUILD_AUDIT_STRACE"]
        shell, cat = shutil.which("sh"), shutil.which("cat")
        self.assertIsNotNone(shell)
        self.assertIsNotNone(cat)
        self.host = Path(shell)
        self.app = self.file("tiny action.sh")
        self.descriptor["taskAssembly"] = str(self.app)
        self.source = self.file('a space/$colon:quote"back\\slash.cs')
        secret = self.file("not-declared.txt")
        (self.root / "subdir").mkdir()
        # The test runner's mapped ELF files provide an explicit, exact platform
        # catalog for the native loader/libc, not source/workspace exemptions.
        mapped = set()
        for line in Path("/proc/self/maps").read_text().splitlines():
            fields = line.split(maxsplit=5)
            if len(fields) == 6 and fields[5].startswith("/") and Path(fields[5]).is_file():
                mapped.add(fields[5])
                if fields[5].startswith("/usr/lib/"):
                    alias = fields[5][4:]
                    if os.path.realpath(alias) == fields[5] and Path(alias).is_file():
                        mapped.add(alias)
        if Path("/etc/ld.so.cache").is_file():
            mapped.add("/etc/ld.so.cache")
        catalog = self.root / "platform.json"
        directory, membership = self.fixed_directory(["member with space", 'quoted"member', "dollar$member"])
        policy = json.loads(membership.read_text())
        policy["files"] = sorted(mapped)
        audit.write_json(catalog, policy)
        for negative in (False, True):
            with self.subTest(negative=negative):
                reads = shlex.quote(os.path.relpath(self.source, self.root / "subdir"))
                reads += " ../not-declared.txt" if negative else ""
                self.app.write_text("LC_ALL=C; export LC_ALL\ncd " + shlex.quote(str(self.root / "subdir")) +
                                    "\nprintf '%s\\n' " + shlex.quote(str(directory)) + "/* > /dev/null" +
                                    "\n" + shlex.quote(cat) + " -- " + reads + " > /dev/null\n")
                self.write_graph(inputs=[self.source, Path(cat)])
                capture = self.root / ("negative-capture" if negative else "positive-capture")
                argv = [shell, str(self.app), "--invocation", str(self.sidecar)]
                controller = [shell, "-c", shlex.join(argv) + " & wait"]
                self.assertEqual(0, audit.main(["trace", "--strace", tracer, "--graph", str(self.graph),
                                                "--cwd", str(self.root), "--output", str(capture),
                                                "--", *controller]))
                report_path = self.root / ("negative-report.json" if negative else "positive-report.json")
                code = audit.main(["analyze", "--capture", str(capture), "--platform-catalog", str(catalog),
                                   "--report", str(report_path)])
                report = json.loads(report_path.read_text())
                self.assertEqual([], report["unresolved"], report)
                self.assertEqual([], report["incompleteness"], report)
                self.assertEqual(1 if negative else 0, code, report)
                self.assertGreater(report["controller"]["events"], 0)
                self.assertGreaterEqual(len(report["actions"][0]["pids"]), 2)
                self.assertEqual(str(self.app), report["actions"][0]["taskAssembly"])
                self.assertTrue(report["platformAllowanceUsage"])
                self.assertEqual([], report["directoryCatalogMismatches"])
                self.assertTrue(any(e["membershipEvidence"] == "trace-and-analysis"
                                    for e in report["directoryAllowanceUsage"]))
                self.assertEqual([str(secret)] if negative else [],
                                 [event["path"] for event in report["undeclaredReads"]])


if __name__ == "__main__":
    unittest.main()
