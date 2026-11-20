import os
from pathlib import Path
import select
import socket
import subprocess
import tempfile
import time
import unittest


LAUNCHER = Path(os.environ["GENBUILD_LAUNCHER"]).resolve()
FIXTURE = Path(os.environ["GENBUILD_FIXTURE"]).resolve()
TEST_ROOT = Path(os.environ["GENBUILD_TEST_ROOT"]).resolve()


class LandlockLauncherTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        probe = subprocess.run(
            [str(LAUNCHER), "--probe"], capture_output=True, text=True, check=False
        )
        if probe.returncode == 78 and "Landlock is unsupported by this kernel" in probe.stderr:
            raise unittest.SkipTest(probe.stderr.strip())
        if probe.returncode != 0:
            raise AssertionError(
                f"--probe failed unexpectedly ({probe.returncode}): {probe.stderr}"
            )
        try:
            cls.abi = int(probe.stdout.strip())
        except ValueError as error:
            raise AssertionError(f"--probe did not print a numeric ABI: {probe.stdout!r}") from error
        if cls.abi < 1:
            raise AssertionError(f"--probe returned invalid ABI {cls.abi}")

    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="landlock-test-", dir=TEST_ROOT)
        self.root = Path(self.temp.name)
        self.scratch_parent = self.root / "scratch-parent"
        self.scratch_parent.mkdir()
        self.allowed_dir = self.root / "inputs"
        self.allowed_dir.mkdir()
        self.allowed_file = self.allowed_dir / "allowed.txt"
        self.allowed_file.write_bytes(b"known-input\n")
        self.sibling_file = self.allowed_dir / "sibling.txt"
        self.sibling_file.write_bytes(b"not-declared\n")
        self.outside_file = self.root / "outside.txt"
        self.outside_file.write_bytes(b"outside\n")

    def tearDown(self):
        self.temp.cleanup()

    def policy(self, *records):
        values = ["genbuild-landlock-v1", *records, "scratch-parent", str(self.scratch_parent)]
        return ("\0".join(values) + "\0").encode("utf-8")

    def write_policy(self, data=None, records=None):
        policy_path = self.root / "policy.bin"
        if data is None:
            data = self.policy(*(records or ()))
        policy_path.write_bytes(data)
        return policy_path

    def run_launcher(self, mode, *args, policy_records=None, policy_data=None, **kwargs):
        policy_path = self.write_policy(data=policy_data, records=policy_records)
        environment = os.environ.copy()
        for name in (
            "MSBUILD_ACTION_STAGING_ROOT",
            "MSBUILD_ACTION_BROKER_SOCKET",
            "MSBUILD_ACTION_BROKER_LAUNCHER",
        ):
            environment.pop(name, None)
        environment.update(kwargs.pop("env", {}))
        return subprocess.run(
            [
                str(LAUNCHER),
                "--policy",
                str(policy_path),
                "--",
                str(FIXTURE),
                mode,
                *map(str, args),
            ],
            env=environment,
            capture_output=True,
            text=True,
            check=False,
            **kwargs,
        )

    def standard_records(self):
        return (
            "execute-file",
            str(FIXTURE),
            "read-file",
            str(self.allowed_file),
        )

    def broker_records(self):
        records = [
            "execute-file",
            str(FIXTURE),
            "execute-file",
            str(LAUNCHER),
            "read-self-file",
            "/proc/self/maps",
        ]
        dependencies = subprocess.run(
            ["ldd", str(LAUNCHER)], capture_output=True, text=True, check=True
        ).stdout.splitlines()
        for line in dependencies:
            path = None
            kind = "read-file"
            if "=>" in line:
                path = line.split("=>", 1)[1].strip().split(" ", 1)[0]
            else:
                candidate = line.strip().split(" ", 1)[0]
                if candidate.startswith("/"):
                    path = candidate
                    kind = "execute-file"
            if path and path.startswith("/") and Path(path).exists():
                records.extend((kind, path))
        for path in (Path("/etc/ld.so.cache"), Path("/etc/ld.so.preload")):
            if path.is_file():
                records.extend(("read-file", str(path)))
        return tuple(records)

    def test_probe_reports_current_abi(self):
        probe = subprocess.run([str(LAUNCHER), "--probe"], capture_output=True, text=True)
        self.assertEqual(probe.returncode, 0, probe.stderr)
        self.assertEqual(int(probe.stdout.strip()), self.abi)

    def test_exact_allowed_file_is_readable(self):
        result = self.run_launcher("read", self.allowed_file, policy_records=self.standard_records())
        self.assertEqual(result.returncode, 0, result.stderr)

    def test_file_symlink_to_regular_input_is_allowed(self):
        file_link = self.root / "allowed-link.txt"
        file_link.symlink_to(self.allowed_file)
        records = (
            "execute-file",
            str(FIXTURE),
            "read-file",
            str(file_link),
        )
        result = self.run_launcher("read", file_link, policy_records=records)
        self.assertEqual(result.returncode, 0, result.stderr)

    def test_undeclared_sibling_file_is_denied(self):
        result = self.run_launcher(
            "read-denied",
            self.sibling_file,
            policy_records=self.standard_records(),
        )
        self.assertEqual(result.returncode, 0, result.stderr)

    def test_directory_rule_lists_but_does_not_read_children(self):
        result = self.run_launcher(
            "directory-read-denied",
            self.allowed_dir,
            self.allowed_file,
            policy_records=("execute-file", str(FIXTURE), "read-directory", str(self.allowed_dir)),
        )
        self.assertEqual(result.returncode, 0, result.stderr)

    def test_denial_is_inherited_by_exec_descendant(self):
        result = self.run_launcher(
            "descendant-denied",
            self.sibling_file,
            policy_records=self.standard_records(),
        )
        self.assertEqual(result.returncode, 0, result.stderr)

    def test_preopened_nonstandard_descriptor_is_closed(self):
        with self.outside_file.open("rb") as inherited:
            result = self.run_launcher(
                "closed-fd",
                inherited.fileno(),
                policy_records=self.standard_records(),
                pass_fds=(inherited.fileno(),),
            )
        self.assertEqual(result.returncode, 0, result.stderr)

    def test_stdin_is_detached_from_callers_pipe(self):
        result = self.run_launcher(
            "stdin",
            policy_records=self.standard_records(),
            input="caller-secret",
        )
        self.assertEqual(result.returncode, 0, result.stderr)

    def test_child_temp_environment_points_to_reserved_root(self):
        result = self.run_launcher(
            "private-temp-env",
            policy_records=("execute-file", str(FIXTURE)),
            env={"TMPDIR": "/ambient/tmp", "TMP": "/ambient/temp", "TEMP": "/ambient/temp"},
        )
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(list(self.scratch_parent.iterdir()), [])

    def test_self_maps_is_process_local_and_other_pid_maps_are_denied(self):
        result = self.run_launcher(
            "self-proc",
            policy_records=(
                "execute-file",
                str(FIXTURE),
                "read-self-file",
                "/proc/self/maps",
            ),
        )
        self.assertEqual(result.returncode, 0, result.stderr)

    def test_generic_proc_file_and_directory_grants_are_rejected(self):
        for records in (
            ("execute-file", str(FIXTURE), "read-file", "/proc/self/maps"),
            ("execute-file", str(FIXTURE), "read-directory", "/proc"),
            ("execute-file", str(FIXTURE), "read-self-file", "/proc/1/maps"),
            ("execute-file", str(FIXTURE), "execute-file", "/proc/meminfo"),
            (
                "execute-file",
                str(FIXTURE),
                "execute-file",
                "/proc/sys/vm/overcommit_memory",
            ),
        ):
            with self.subTest(records=records):
                result = self.run_launcher("exit", policy_records=records)
                self.assertNotEqual(result.returncode, 0)

    def test_only_explicitly_declared_reviewed_global_proc_files_are_readable(self):
        for path in ("/proc/meminfo", "/proc/sys/vm/overcommit_memory"):
            with self.subTest(path=path):
                positive = self.run_launcher(
                    "read-one-byte",
                    path,
                    policy_records=("execute-file", str(FIXTURE), "read-file", path),
                )
                self.assertEqual(positive.returncode, 0, positive.stderr)

                implicit = self.run_launcher(
                    "read-denied",
                    path,
                    policy_records=("execute-file", str(FIXTURE)),
                )
                self.assertEqual(implicit.returncode, 0, implicit.stderr)

    def test_brokered_process_forwards_output_status_cwd_and_environment(self):
        marker = "broker-env-value"
        result = self.run_launcher(
            "broker-run",
            "remote-probe",
            str(self.root),
            marker,
            policy_records=self.broker_records(),
            env={"GENBUILD_BROKER_TEST_VALUE": marker},
            cwd=self.root,
            timeout=10,
        )
        self.assertEqual(result.returncode, 37, result.stderr)
        self.assertEqual(result.stdout, "broker-child-stdout\n")
        self.assertEqual(result.stderr, "broker-child-stderr\n")
        self.assertEqual(list(self.scratch_parent.iterdir()), [])

    def test_brokered_process_has_no_extra_file_grant(self):
        result = self.run_launcher(
            "broker-run",
            "read-denied",
            self.outside_file,
            policy_records=self.broker_records(),
            timeout=10,
        )
        self.assertEqual(result.returncode, 0, result.stderr)

    def test_brokered_process_does_not_inherit_extra_descriptors_or_stdin(self):
        result = self.run_launcher(
            "broker-open-fd",
            self.allowed_file,
            policy_records=(*self.broker_records(), "read-file", str(self.allowed_file)),
            timeout=10,
        )
        self.assertEqual(result.returncode, 0, result.stderr)
        stdin_result = self.run_launcher(
            "broker-run",
            "stdin",
            policy_records=self.broker_records(),
            timeout=10,
        )
        self.assertEqual(stdin_result.returncode, 0, stdin_result.stderr)

    def test_nested_brokered_process_gets_its_own_proc_maps_rule(self):
        result = self.run_launcher(
            "broker-run",
            "nested",
            policy_records=self.broker_records(),
            timeout=10,
        )
        self.assertEqual(result.returncode, 0, result.stderr)

    def test_spoofed_broker_environment_is_overridden(self):
        result = self.run_launcher(
            "broker-run-spoof",
            "remote-probe",
            str(self.root),
            "broker-env-value",
            policy_records=self.broker_records(),
            env={"GENBUILD_BROKER_TEST_VALUE": "broker-env-value"},
            cwd=self.root,
            timeout=10,
        )
        self.assertEqual(result.returncode, 37, result.stderr)
        self.assertEqual(result.stdout, "broker-child-stdout\n")
        self.assertEqual(result.stderr, "broker-child-stderr\n")

    def test_broker_rejects_malformed_oversized_truncated_and_extra_fd_requests(self):
        for kind in ("bad-magic", "oversize", "truncated", "extra-fds"):
            with self.subTest(kind=kind):
                result = self.run_launcher(
                    "broker-raw",
                    kind,
                    policy_records=self.broker_records(),
                    timeout=10,
                )
                self.assertEqual(result.returncode, 0, result.stderr)

    def test_unrelated_socket_client_is_rejected(self):
        release_path = self.root / "release-host"
        policy_path = self.write_policy(records=("execute-file", str(FIXTURE)))
        environment = os.environ.copy()
        for name in (
            "MSBUILD_ACTION_STAGING_ROOT",
            "MSBUILD_ACTION_BROKER_SOCKET",
            "MSBUILD_ACTION_BROKER_LAUNCHER",
        ):
            environment.pop(name, None)
        process = subprocess.Popen(
            [
                str(LAUNCHER),
                "--policy",
                str(policy_path),
                "--",
                str(FIXTURE),
                "wait-broker",
                str(release_path),
            ],
            cwd=self.root,
            env=environment,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True,
        )
        try:
            ready, _, _ = select.select([process.stdout], [], [], 5)
            self.assertTrue(ready, "host did not publish its broker socket name")
            socket_name = process.stdout.readline().strip()
            self.assertTrue(socket_name.startswith("@genbuild-"))
            client = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
            try:
                client.settimeout(5)
                client.connect("\0" + socket_name[1:])
                self.assertEqual(client.recv(1), b"")
            finally:
                client.close()
        finally:
            release_path.touch()
        stdout, stderr = process.communicate(timeout=10)
        self.assertEqual(process.returncode, 0, stderr)
        self.assertFalse(list(self.scratch_parent.iterdir()))

    def test_disconnected_broker_client_cancels_worker_before_cleanup(self):
        started = time.monotonic()
        result = self.run_launcher(
            "abandon-client",
            policy_records=self.broker_records(),
            timeout=10,
        )
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertLess(time.monotonic() - started, 5)
        self.assertEqual(list(self.scratch_parent.iterdir()), [])

    def test_undeclared_executable_is_denied(self):
        result = self.run_launcher(
            "exec-denied",
            "/bin/true",
            policy_records=self.standard_records(),
        )
        self.assertEqual(result.returncode, 0, result.stderr)

    def test_new_private_scratch_content_can_be_read_and_is_removed(self):
        result = self.run_launcher("scratch", policy_records=("execute-file", str(FIXTURE)))
        self.assertEqual(result.returncode, 0, result.stderr)
        scratch_path = Path(result.stdout.strip())
        self.assertEqual(scratch_path.parent, self.scratch_parent)
        self.assertTrue(scratch_path.name.startswith(".genbuild-action-"))
        self.assertFalse(scratch_path.exists())

    def test_scratch_is_cleaned_when_host_fails(self):
        result = self.run_launcher("exit", policy_records=self.standard_records())
        self.assertEqual(result.returncode, 23, result.stderr)
        self.assertEqual(list(self.scratch_parent.iterdir()), [])

    def test_scratch_root_may_be_removed_by_host(self):
        result = self.run_launcher(
            "remove-scratch",
            policy_records=("execute-file", str(FIXTURE),),
        )
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(list(self.scratch_parent.iterdir()), [])

    def test_scratch_is_cleaned_when_exec_fails(self):
        policy_path = self.write_policy(records=self.standard_records())
        result = subprocess.run(
            [
                str(LAUNCHER),
                "--policy",
                str(policy_path),
                "--",
                str(self.root / "missing-program"),
            ],
            capture_output=True,
            text=True,
            check=False,
        )
        self.assertEqual(result.returncode, 127, result.stderr)
        self.assertEqual(list(self.scratch_parent.iterdir()), [])

    def test_child_signal_is_forwarded_after_cleanup(self):
        result = self.run_launcher("signal", policy_records=self.standard_records())
        self.assertEqual(result.returncode, -15, result.stderr)
        self.assertEqual(list(self.scratch_parent.iterdir()), [])

    def test_reserved_staging_variable_is_rejected(self):
        result = self.run_launcher(
            "scratch",
            policy_records=self.standard_records(),
            env={"MSBUILD_ACTION_STAGING_ROOT": "/already/set"},
        )
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(list(self.scratch_parent.iterdir()), [])

    def test_malformed_policies_are_rejected(self):
        malformed = (
            b"wrong-version\0scratch-parent\0" + str(self.scratch_parent).encode() + b"\0",
            b"genbuild-landlock-v1\0read-file\0",
            b"genbuild-landlock-v1\0scratch-parent\0" + str(self.scratch_parent).encode(),
            b"genbuild-landlock-v1\0unknown-kind\0/file\0scratch-parent\0"
            + str(self.scratch_parent).encode()
            + b"\0",
            b"genbuild-landlock-v1\0read-file\0relative\0scratch-parent\0"
            + str(self.scratch_parent).encode()
            + b"\0",
            self.policy("read-file", str(self.allowed_dir)),
            b"genbuild-landlock-v1\0scratch-parent\0"
            + str(self.scratch_parent).encode()
            + b"\0scratch-parent\0"
            + str(self.root).encode()
            + b"\0",
        )
        for policy_data in malformed:
            with self.subTest(policy=policy_data):
                result = self.run_launcher("scratch", policy_data=policy_data)
                self.assertNotEqual(result.returncode, 0)
                self.assertEqual(list(self.scratch_parent.iterdir()), [])

    def test_directory_symlink_rule_is_rejected(self):
        directory_link = self.root / "directory-link"
        directory_link.symlink_to(self.allowed_dir, target_is_directory=True)
        result = self.run_launcher(
            "scratch",
            policy_records=("execute-file", str(FIXTURE), "read-directory", str(directory_link)),
        )
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(list(self.scratch_parent.iterdir()), [])


if __name__ == "__main__":
    unittest.main()
