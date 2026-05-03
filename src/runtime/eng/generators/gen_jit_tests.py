#!/usr/bin/env python3
"""Generate BUILD.bazel files for JIT tests from MSBuild .csproj/.ilproj files.

Reads the src/tests/JIT/ tree and produces BUILD.bazel files that define
coreclr_test(), il_coreclr_test(), and coreclr_merged_test() targets matching
the MSBuild test configuration.

Usage:
    python3 eng/generators/gen_jit_tests.py [--dry-run] [--verbose]
"""

import argparse
import fnmatch
import glob as globmod
import os
import sys
import xml.etree.ElementTree as ET
from collections import defaultdict
from dataclasses import dataclass, field
from pathlib import Path
from typing import Optional


REPO_ROOT = Path(__file__).resolve().parent.parent.parent
JIT_ROOT = REPO_ROOT / "src" / "tests" / "JIT"
TESTS_ROOT = REPO_ROOT / "src" / "tests"

# Reasons a test might be skipped.
SKIP_NATIVE_REF = "has NativeProjectReference"
SKIP_TARGET_UNSUPPORTED = "has CLRTestTargetUnsupported condition"
SKIP_RUN_ONLY = "CLRTestKind is RunOnly"
SKIP_NO_XUNIT = "ReferenceXUnitWrapperGenerator is false"
SKIP_NO_SOURCES = "no source files resolved"
SKIP_COMPLEX_PROJREF = "has complex ProjectReference"
SKIP_EXECUTION_ARGS = "has CLRTestExecutionArguments (needs corerun args)"


@dataclass
class TestProject:
    """Parsed MSBuild test project."""
    path: Path
    name: str  # project filename without extension
    is_il: bool  # .ilproj (IL SDK)
    is_library: bool  # OutputType=Library
    is_merged: bool  # has MergedWrapperProjectReference
    debug_type: Optional[str]  # Full, PdbOnly, None, Embedded, Portable
    optimize: Optional[bool]
    priority: int
    allow_unsafe: bool
    sources: list  # resolved source file paths (relative to project dir)
    defines: list  # DefineConstants
    project_refs: list  # ProjectReference Include values
    has_native_ref: bool
    has_target_unsupported: bool
    has_run_only: bool
    has_no_xunit: bool
    has_execution_args: bool
    skip_reason: Optional[str]
    # For merged projects:
    merged_includes: list = field(default_factory=list)
    merged_removes: list = field(default_factory=list)
    merged_imports: list = field(default_factory=list)


def parse_project(proj_path: Path) -> TestProject:
    """Parse a .csproj or .ilproj file into a TestProject."""
    tree = ET.parse(proj_path)
    root = tree.getroot()
    # Strip namespace if present
    ns = ""
    if root.tag.startswith("{"):
        ns = root.tag.split("}")[0] + "}"

    name = proj_path.stem
    is_il = proj_path.suffix == ".ilproj"
    sdk = root.attrib.get("Sdk", "")
    if not is_il and "Sdk.IL" in sdk:
        is_il = True

    # Extract properties
    props = {}
    for pg in root.iter(ns + "PropertyGroup"):
        for child in pg:
            tag = child.tag.replace(ns, "")
            if child.text:
                props[tag] = child.text.strip()

    debug_type = props.get("DebugType")
    optimize_str = props.get("Optimize", "").strip()
    optimize = None
    if optimize_str.lower() == "true":
        optimize = True
    elif optimize_str.lower() == "false":
        optimize = False

    priority = int(props.get("CLRTestPriority", "0"))
    allow_unsafe = props.get("AllowUnsafeBlocks", "").lower() == "true"
    is_library = props.get("OutputType", "").lower() == "library"
    has_run_only = props.get("CLRTestKind", "").lower() == "runonly"
    has_no_xunit = props.get("ReferenceXUnitWrapperGenerator", "").lower() == "false"
    has_execution_args = "CLRTestExecutionArguments" in props

    # Check for CLRTestTargetUnsupported
    has_target_unsupported = False
    for pg in root.iter(ns + "PropertyGroup"):
        for child in pg:
            tag = child.tag.replace(ns, "")
            if tag == "CLRTestTargetUnsupported":
                cond = child.attrib.get("Condition", "")
                if cond:
                    has_target_unsupported = True

    # Extract source files
    sources = []
    has_native_ref = False
    project_refs = []
    merged_includes = []
    merged_removes = []
    merged_imports = []
    defines = []

    # DefineConstants
    dc = props.get("DefineConstants", "")
    if dc:
        # Format: $(DefineConstants);FOO;BAR
        for part in dc.split(";"):
            part = part.strip()
            if part and not part.startswith("$("):
                defines.append(part)

    for ig in root.iter(ns + "ItemGroup"):
        for child in ig:
            tag = child.tag.replace(ns, "")
            include = child.attrib.get("Include", "")

            if tag == "Compile":
                sources.append(include)
            elif tag == "NativeProjectReference":
                has_native_ref = True
            elif tag == "ProjectReference":
                project_refs.append(include)
            elif tag == "MergedWrapperProjectReference":
                if include:
                    merged_includes.append(include)

    # Handle MergedWrapperProjectReference Remove
    for ig in root.iter(ns + "ItemGroup"):
        for child in ig:
            tag = child.tag.replace(ns, "")
            remove = child.attrib.get("Remove", "")
            if tag == "MergedWrapperProjectReference" and remove:
                merged_removes.append(remove)

    # Handle imports (for merged tests that import removal lists)
    for imp in root.iter(ns + "Import"):
        proj = imp.attrib.get("Project", "")
        if proj and "MergedTestRunner" not in proj:
            merged_imports.append(proj)

    is_merged = len(merged_includes) > 0

    # Determine skip reason
    skip_reason = None
    if is_merged:
        skip_reason = None  # Merged tests handled separately
    elif has_native_ref:
        skip_reason = SKIP_NATIVE_REF
    elif has_target_unsupported:
        skip_reason = SKIP_TARGET_UNSUPPORTED
    elif has_run_only:
        skip_reason = SKIP_RUN_ONLY
    elif has_no_xunit:
        skip_reason = SKIP_NO_XUNIT
    elif has_execution_args:
        skip_reason = SKIP_EXECUTION_ARGS

    return TestProject(
        path=proj_path,
        name=name,
        is_il=is_il,
        is_library=is_library,
        is_merged=is_merged,
        debug_type=debug_type,
        optimize=optimize,
        priority=priority,
        allow_unsafe=allow_unsafe,
        sources=sources,
        defines=defines,
        project_refs=project_refs,
        has_native_ref=has_native_ref,
        has_target_unsupported=has_target_unsupported,
        has_run_only=has_run_only,
        has_no_xunit=has_no_xunit,
        has_execution_args=has_execution_args,
        skip_reason=skip_reason,
        merged_includes=merged_includes,
        merged_removes=merged_removes,
        merged_imports=merged_imports,
    )


def resolve_sources(proj: TestProject) -> list:
    """Resolve source file Include patterns to actual file paths relative to the project dir."""
    proj_dir = proj.path.parent
    resolved = []
    ext = ".il" if proj.is_il else ".cs"

    for src in proj.sources:
        # Normalize backslashes to forward slashes
        src = src.replace("\\", "/")

        # Handle $(MSBuildProjectName).cs / $(MSBuildProjectName).il
        src = src.replace("$(MSBuildProjectName)", proj.name)

        if "*" in src:
            # Glob pattern - use recursive=True for ** patterns
            matches = sorted(globmod.glob(str(proj_dir / src), recursive=True))
            for m in matches:
                resolved.append(os.path.relpath(m, proj_dir).replace("\\", "/"))
        else:
            # Explicit file - normalize the path
            resolved.append(src)

    return resolved


def map_debug_type(dt: Optional[str]) -> Optional[str]:
    """Map MSBuild DebugType to Bazel debug_type parameter."""
    if dt is None:
        return None
    dt_lower = dt.lower()
    if dt_lower == "full":
        return "full"
    elif dt_lower == "pdbonly":
        return "pdbonly"
    elif dt_lower in ("none", "embedded", "portable", ""):
        return None  # Use default
    return None


def classify_project_refs(proj: TestProject) -> tuple:
    """Classify project references into known deps and complex refs.

    Returns (known_deps, is_complex) where known_deps is a list of Bazel dep
    strings and is_complex indicates if there are unresolvable refs.
    """
    known_deps = []
    is_complex = False

    for ref in proj.project_refs:
        if "CoreCLRTestLibrary" in ref:
            known_deps.append("//src/tests/Common:TestLibrary")
        elif ref.endswith(".csproj") and not ref.startswith(".."):
            # Local project reference in same directory - will be handled as
            # a library dependency within the same BUILD.bazel
            pass  # Handled by the caller
        else:
            is_complex = True

    return known_deps, is_complex


def generate_test_target(proj: TestProject, sources: list, extra_deps: list = None) -> str:
    """Generate a coreclr_test() or il_coreclr_test() call."""
    if extra_deps is None:
        extra_deps = []

    lines = []
    macro = "il_coreclr_test" if proj.is_il else "coreclr_test"

    lines.append(f'{macro}(')
    lines.append(f'    name = "{proj.name}",')
    lines.append(f'    srcs = {_format_list(sources)},')

    if proj.priority > 0:
        lines.append(f'    pri = {proj.priority},')

    debug_type = map_debug_type(proj.debug_type)
    if debug_type:
        lines.append(f'    debug_type = "{debug_type}",')

    if proj.optimize is True:
        lines.append(f'    optimize = True,')
    elif proj.optimize is False and proj.debug_type and proj.debug_type.lower() == "full":
        # Only emit optimize = False when debug_type is full (matching _d pattern)
        lines.append(f'    optimize = False,')

    if proj.allow_unsafe and not proj.is_il:
        lines.append(f'    allow_unsafe_blocks = True,')

    if proj.defines:
        lines.append(f'    defines = {_format_list(proj.defines)},')

    if extra_deps:
        lines.append(f'    deps = {_format_list(extra_deps)},')

    lines.append(')')
    return "\n".join(lines)


def generate_library_target(proj: TestProject, sources: list) -> Optional[str]:
    """Generate a live_csharp_library() call for a helper library.

    Returns None for IL library projects (not supported yet).
    """
    # IL libraries need ilasm compilation, which live_csharp_library doesn't
    # support. Skip them for now.
    if proj.is_il:
        return None

    lines = []
    lines.append(f'live_csharp_library(')
    lines.append(f'    name = "{proj.name}",')
    lines.append(f'    srcs = {_format_list(sources)},')

    if proj.allow_unsafe:
        lines.append(f'    allow_unsafe_blocks = True,')

    debug_type = map_debug_type(proj.debug_type)
    if debug_type:
        compiler_opts = [f"/debug:{debug_type}"]
        lines.append(f'    compiler_options = {_format_list(compiler_opts)},')

    lines.append(f'    visibility = ["//visibility:public"],')
    lines.append(')')
    return "\n".join(lines)


def _format_list(items: list) -> str:
    """Format a Python list as a Bazel list literal."""
    if len(items) == 1:
        return f'["{items[0]}"]'
    inner = ", ".join(f'"{item}"' for item in items)
    if len(inner) < 60:
        return f"[{inner}]"
    lines = ["["]
    for item in items:
        lines.append(f'        "{item}",')
    lines.append("    ]")
    return "\n".join(lines)


def generate_build_file(dir_path: Path, projects: list, verbose: bool = False) -> Optional[str]:
    """Generate BUILD.bazel content for a directory of test projects.

    Returns None if no targets can be generated.
    """
    # Separate projects by type
    tests = []
    libraries = []
    merged = []
    skipped = []

    for proj in projects:
        if proj.is_merged:
            merged.append(proj)
            continue

        if proj.skip_reason:
            skipped.append(proj)
            continue

        sources = resolve_sources(proj)
        if not sources:
            proj.skip_reason = SKIP_NO_SOURCES
            skipped.append(proj)
            continue

        # Skip tests with cross-package source references (../)
        if any(s.startswith("../") or s.startswith("..\\") for s in sources):
            proj.skip_reason = "has cross-package source references (../)"
            skipped.append(proj)
            continue

        # Check for complex project refs
        known_deps, is_complex = classify_project_refs(proj)
        if is_complex:
            proj.skip_reason = SKIP_COMPLEX_PROJREF
            skipped.append(proj)
            continue

        # Check for local library project references that haven't been resolved
        local_lib_refs = []
        has_il_lib_ref = False
        for ref in proj.project_refs:
            if "CoreCLRTestLibrary" not in ref and not ref.startswith(".."):
                if ref.endswith(".ilproj"):
                    has_il_lib_ref = True
                elif ref.endswith(".csproj"):
                    local_lib_refs.append(ref)

        if has_il_lib_ref:
            proj.skip_reason = "references IL library project (unsupported)"
            skipped.append(proj)
            continue

        if proj.is_library:
            libraries.append((proj, sources))
        else:
            extra_deps = known_deps
            # Add local library deps
            for ref in local_lib_refs:
                lib_name = Path(ref).stem
                extra_deps.append(f":{lib_name}")
            tests.append((proj, sources, extra_deps))

    # Filter out IL libraries that can't be generated
    generatable_libs = [(p, s) for p, s in libraries if not p.is_il]

    if not tests and not generatable_libs:
        return None

    # Determine which load statements we need
    needs_coreclr_test = any(not p.is_il for p, _, _ in tests)
    needs_il_test = any(p.is_il for p, _, _ in tests)
    needs_library = any(not p.is_il for p, _ in libraries)

    loads = []
    test_macros = []
    if needs_coreclr_test:
        test_macros.append("coreclr_test")
    if needs_il_test:
        test_macros.append("il_coreclr_test")
    if test_macros:
        macro_str = ", ".join(f'"{m}"' for m in sorted(test_macros))
        loads.append(f'load("//src/tests:live_test.bzl", {macro_str})')
    if needs_library:
        loads.append('load("//src/libraries:defs.bzl", "live_csharp_library")')

    parts = []
    parts.extend(loads)
    parts.append("")

    # Emit libraries first (tests may depend on them)
    for proj, sources in libraries:
        lib_target = generate_library_target(proj, sources)
        if lib_target:
            parts.append(lib_target)
            parts.append("")

    # Emit tests
    for proj, sources, extra_deps in tests:
        parts.append(generate_test_target(proj, sources, extra_deps))
        parts.append("")

    # Add skipped tests as comments
    if skipped and verbose:
        parts.append("# Skipped tests (unsupported features):")
        for proj in skipped:
            parts.append(f"# - {proj.name}: {proj.skip_reason}")
        parts.append("")

    content = "\n".join(parts).rstrip() + "\n"
    return content


def resolve_merged_refs(merged_proj: TestProject, all_projects_by_dir: dict) -> list:
    """Resolve MergedWrapperProjectReference patterns to Bazel target labels.

    Returns a list of Bazel label strings for the tests to include.
    """
    proj_dir = merged_proj.path.parent

    # Parse any imported removal lists
    extra_removes = []
    for imp in merged_proj.merged_imports:
        imp_path = imp.replace("$(TestSourceDir)", str(TESTS_ROOT) + "/")
        imp_path = proj_dir / imp_path
        if imp_path.exists():
            try:
                tree = ET.parse(imp_path)
                root = tree.getroot()
                ns = ""
                if root.tag.startswith("{"):
                    ns = root.tag.split("}")[0] + "}"
                for ig in root.iter(ns + "ItemGroup"):
                    for child in ig:
                        tag = child.tag.replace(ns, "")
                        remove = child.attrib.get("Remove", "")
                        if tag == "MergedWrapperProjectReference" and remove:
                            extra_removes.append(remove)
            except Exception:
                pass

    all_removes = merged_proj.merged_removes + extra_removes

    # Find all matching project files
    included_files = set()
    for pattern in merged_proj.merged_includes:
        # Patterns like "*/**/*_d.??proj" or "CLR-x86-*/**/*.??proj"
        # Convert ??proj to glob-compatible pattern
        glob_pattern = pattern.replace("??proj", "*proj")
        matches = globmod.glob(str(proj_dir / glob_pattern), recursive=True)
        for m in matches:
            p = Path(m)
            if p.suffix in (".csproj", ".ilproj"):
                included_files.add(p.resolve())

    # Remove excluded files
    for pattern in all_removes:
        glob_pattern = pattern.replace("??proj", "*proj")
        matches = globmod.glob(str(proj_dir / glob_pattern), recursive=True)
        for m in matches:
            p = Path(m).resolve()
            included_files.discard(p)

    # Remove the merged project itself and other merged projects
    included_files.discard(merged_proj.path.resolve())

    # Convert to Bazel labels
    labels = []
    for f in sorted(included_files):
        try:
            rel = f.relative_to(REPO_ROOT)
        except ValueError:
            continue

        # The Bazel target name is the project file stem
        test_name = f.stem
        # The Bazel package is the directory relative to repo root
        pkg = str(rel.parent).replace(os.sep, "/")
        labels.append(f"//{pkg}:{test_name}")

    return labels


def generate_merged_test(merged_proj: TestProject, all_projects_by_dir: dict) -> Optional[str]:
    """Generate a coreclr_merged_test() target for a merged wrapper project."""
    labels = resolve_merged_refs(merged_proj, all_projects_by_dir)
    if not labels:
        return None

    lines = []
    lines.append(f'coreclr_merged_test(')
    lines.append(f'    name = "{merged_proj.name}",')
    lines.append(f'    deps = [')
    lines.append(f'        "@paket.main//microsoft.dotnet.xunitassert",')
    lines.append(f'        "@paket.main//xunit.abstractions",')
    lines.append(f'        "@paket.main//xunit.extensibility.core",')
    lines.append(f'    ],')
    lines.append(f'    test_deps = [')
    for label in labels:
        lines.append(f'        "{label}",')
    lines.append(f'    ],')
    lines.append(f')')
    return "\n".join(lines)


def collect_all_projects() -> dict:
    """Walk the JIT test tree and collect all test projects by directory.

    Returns a dict mapping directory Path -> list of TestProject.
    """
    projects_by_dir = defaultdict(list)

    for root, dirs, files in os.walk(JIT_ROOT):
        root_path = Path(root)
        for f in files:
            if f.endswith((".csproj", ".ilproj")):
                proj_path = root_path / f
                try:
                    proj = parse_project(proj_path)
                    projects_by_dir[root_path].append(proj)
                except Exception as e:
                    print(f"WARNING: Failed to parse {proj_path}: {e}", file=sys.stderr)

    return dict(projects_by_dir)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dry-run", action="store_true",
                        help="Print what would be generated without writing files")
    parser.add_argument("--verbose", "-v", action="store_true",
                        help="Include skipped test comments in BUILD files and print details")
    parser.add_argument("--force", action="store_true",
                        help="Overwrite existing BUILD.bazel files")
    args = parser.parse_args()

    print("Collecting test projects...", file=sys.stderr)
    projects_by_dir = collect_all_projects()

    total_dirs = len(projects_by_dir)
    total_projects = sum(len(v) for v in projects_by_dir.values())
    print(f"Found {total_projects} projects in {total_dirs} directories", file=sys.stderr)

    generated_count = 0
    skipped_dir_count = 0
    skipped_test_count = 0
    merged_count = 0
    individual_count = 0

    # Phase 1: Generate individual test BUILD.bazel files per directory
    for dir_path in sorted(projects_by_dir.keys()):
        projects = projects_by_dir[dir_path]

        # Separate merged wrapper projects from individual tests
        merged_projects = [p for p in projects if p.is_merged]
        individual_projects = [p for p in projects if not p.is_merged]

        # Skip directories that already have a BUILD.bazel (unless --force)
        build_file = dir_path / "BUILD.bazel"
        has_existing = build_file.exists()

        if individual_projects:
            if has_existing and not args.force:
                if args.verbose:
                    print(f"SKIP (existing): {dir_path.relative_to(REPO_ROOT)}", file=sys.stderr)
                skipped_dir_count += 1
            else:
                content = generate_build_file(dir_path, individual_projects, verbose=args.verbose)
                if content:
                    # Count stats
                    for p in individual_projects:
                        if p.skip_reason:
                            skipped_test_count += 1
                        elif not p.is_merged:
                            individual_count += 1

                    if args.dry_run:
                        rel = dir_path.relative_to(REPO_ROOT)
                        print(f"=== {rel}/BUILD.bazel ===")
                        print(content)
                    else:
                        if has_existing:
                            # Append to existing BUILD.bazel
                            with open(build_file, "a") as f:
                                f.write("\n" + content)
                        else:
                            build_file.write_text(content)
                    generated_count += 1
                else:
                    skipped_test_count += len(individual_projects)

        # Phase 2: Generate merged test targets
        # Merged projects at the same level as the tests they aggregate.
        # These are appended to the directory's BUILD.bazel.
        for mp in merged_projects:
            merged_content = generate_merged_test(mp, projects_by_dir)
            if merged_content:
                # The merged test goes in the same directory as the merged .csproj
                merged_build = dir_path / "BUILD.bazel"

                # Check if this merged test already exists (idempotency)
                if merged_build.exists():
                    existing = merged_build.read_text()
                    if f'name = "{mp.name}"' in existing:
                        continue
                else:
                    existing = ""

                merged_count += 1

                if not merged_build.exists() and not args.dry_run:
                    # Need a new BUILD.bazel just for the merged test
                    full_content = 'load("//src/tests:live_test.bzl", "coreclr_merged_test")\n\n'
                    full_content += merged_content + "\n"
                    merged_build.write_text(full_content)
                else:
                    if "coreclr_merged_test" not in existing:
                        # Need to add the load
                        load_line = 'load("//src/tests:live_test.bzl", "coreclr_merged_test")\n'
                        if args.dry_run:
                            print(f"=== ADD LOAD to {dir_path.relative_to(REPO_ROOT)}/BUILD.bazel ===")
                            print(load_line)
                        else:
                            _add_merged_load(merged_build)

                    if args.dry_run:
                        rel = dir_path.relative_to(REPO_ROOT)
                        print(f"=== MERGED in {rel}/BUILD.bazel ===")
                        print(merged_content)
                    else:
                        with open(merged_build, "a") as f:
                            f.write("\n" + merged_content + "\n")

    # Summary
    print(f"\n--- Summary ---", file=sys.stderr)
    print(f"Directories processed: {total_dirs}", file=sys.stderr)
    print(f"BUILD.bazel files generated/updated: {generated_count}", file=sys.stderr)
    print(f"Individual test targets: {individual_count}", file=sys.stderr)
    print(f"Merged test targets: {merged_count}", file=sys.stderr)
    print(f"Directories skipped (existing BUILD): {skipped_dir_count}", file=sys.stderr)
    print(f"Tests skipped (unsupported): {skipped_test_count}", file=sys.stderr)

    if args.verbose:
        print(f"\nSkipped test details:", file=sys.stderr)
        for dir_path in sorted(projects_by_dir.keys()):
            for proj in projects_by_dir[dir_path]:
                if proj.skip_reason and not proj.is_merged:
                    rel = proj.path.relative_to(REPO_ROOT)
                    print(f"  {rel}: {proj.skip_reason}", file=sys.stderr)


def _add_merged_load(build_file: Path):
    """Add coreclr_merged_test to the load statement in an existing BUILD.bazel."""
    content = build_file.read_text()

    # Check if there's already a load from live_test.bzl
    if 'load("//src/tests:live_test.bzl"' in content:
        # Add coreclr_merged_test to existing load
        import re
        pattern = r'(load\("//src/tests:live_test\.bzl",\s*)(.*?)(\))'
        match = re.search(pattern, content, re.DOTALL)
        if match:
            existing_imports = match.group(2)
            if "coreclr_merged_test" not in existing_imports:
                new_imports = existing_imports.rstrip().rstrip(")")
                if new_imports.endswith('"'):
                    new_imports += ', "coreclr_merged_test"'
                else:
                    new_imports += '"coreclr_merged_test"'
                content = content[:match.start(2)] + new_imports + content[match.end(2):]
                build_file.write_text(content)
    else:
        # Add a new load line at the top
        load_line = 'load("//src/tests:live_test.bzl", "coreclr_merged_test")\n'
        content = load_line + content
        build_file.write_text(content)


if __name__ == "__main__":
    main()
