#!/usr/bin/env python3
"""Generate BUILD.bazel files for CoreCLR tests in src/tests that are missing them.

Parses .csproj files and generates coreclr_test() / il_coreclr_test() targets.
Skips tests that need native code (CMakeProjectReference), are build-only helpers,
or use non-xunit entry points (ReferenceXUnitWrapperGenerator=false).
"""

import os
import sys
import xml.etree.ElementTree as ET
from pathlib import Path
from collections import defaultdict


REPO_ROOT = Path(__file__).resolve().parent.parent.parent.parent

# Directories to process (all test directories under src/tests)
TEST_ROOT = REPO_ROOT / "src" / "tests"

# Known ProjectReference mappings to Bazel targets
KNOWN_PROJECT_REFS = {
    "CoreCLRTestLibrary.csproj": "//src/tests/Common:TestLibrary",
    # $(TestSourceDir)Common/CoreCLRTestLibrary/CoreCLRTestLibrary.csproj
    # $(TestLibraryProjectPath) also resolves to CoreCLRTestLibrary
}


def parse_csproj(csproj_path: Path) -> dict | None:
    """Parse a .csproj file and extract test-relevant properties.
    
    Returns None if the test should be skipped (CMake deps, BuildOnly, etc.)
    """
    try:
        tree = ET.parse(csproj_path)
    except ET.ParseError:
        return None
    
    root = tree.getroot()
    # Strip namespace if present
    ns = ""
    if root.tag.startswith("{"):
        ns = root.tag.split("}")[0] + "}"
    
    def find_all(tag):
        return root.iter(f"{ns}{tag}")
    
    def find_text(tag, default=""):
        for elem in find_all(tag):
            if elem.text:
                return elem.text.strip()
        return default
    
    # Check for skip conditions
    # 1. CMakeProjectReference
    for elem in find_all("CMakeProjectReference"):
        return None
    
    # 2. CLRTestKind = BuildOnly or SharedLibrary
    kind = find_text("CLRTestKind", "BuildAndRun")
    if kind in ("BuildOnly", "SharedLibrary"):
        return None
    
    # 3. ReferenceXUnitWrapperGenerator = false
    ref_xunit = find_text("ReferenceXUnitWrapperGenerator", "true")
    if ref_xunit.lower() == "false":
        return None
    
    # 4. MergedWrapperProjectReference (these are aggregator projects)
    for elem in find_all("MergedWrapperProjectReference"):
        return None
    
    # 5. Skip ilproj-referencing projects (those are handled by il_coreclr_test)
    # but don't skip the .ilproj itself
    
    # Extract properties
    result = {
        "name": csproj_path.stem,
        "allow_unsafe_blocks": find_text("AllowUnsafeBlocks", "false").lower() == "true",
        "optimize": find_text("Optimize", ""),  # empty means no explicit setting (uses macro default=True)
        "pri": 1 if find_text("CLRTestPriority", "0") == "1" else 0,
        "sources": [],
        "deps": [],
        "is_ilproj": csproj_path.suffix == ".ilproj",
        "windows_only": False,
    }

    # Check for CLRTestTargetUnsupported with platform conditions
    for elem in find_all("CLRTestTargetUnsupported"):
        cond = elem.get("Condition", "")
        if "TargetsWindows" in cond or ("'windows'" in cond.lower() and "TargetOS" in cond):
            result["windows_only"] = True
            break
        if "TargetsOSX" in cond:
            result["macos_unsupported"] = True
            break
    
    # Extract source files
    proj_name = csproj_path.stem
    proj_dir = csproj_path.parent
    
    for compile_elem in find_all("Compile"):
        include = compile_elem.get("Include", "")
        if not include:
            continue
        
        # Resolve MSBuild variables
        include = include.replace("$(MSBuildProjectName)", proj_name)
        include = include.replace("$(MSBuildProjectFileName)", csproj_path.name)
        include = include.replace("$(MSBuildThisFileName)", proj_name)
        
        # Skip references to files with unresolvable MSBuild variables
        if include.startswith("$("):
            continue
        
        # Handle relative paths to parent directories (e.g., ../structdef.cs)
        if include.startswith(".."):
            include = include.replace("\\", "/")
            # Resolve to absolute path, then convert to Bazel label
            resolved = (proj_dir / include).resolve()
            if resolved.exists():
                try:
                    rel = resolved.relative_to(REPO_ROOT)
                    # Convert to Bazel label: //package:target
                    pkg = str(rel.parent)
                    fname = rel.name
                    result["sources"].append(("file", "//%s:%s" % (pkg, fname)))
                except ValueError:
                    continue
            continue
        
        # Handle glob patterns
        if "**" in include or "*" in include:
            result["sources"].append(("glob", include))
        else:
            # Normalize path separators and strip ./ prefix
            include = include.replace("\\", "/")
            if include.startswith("./"):
                include = include[2:]
            result["sources"].append(("file", include))
    
    # If no Compile items found, check for default SDK-style inclusion
    if not result["sources"]:
        # SDK-style projects auto-include *.cs, but most coreclr tests
        # explicitly list sources. If nothing is listed, try the project name.
        cs_file = proj_name + ".cs"
        if (proj_dir / cs_file).exists():
            result["sources"].append(("file", cs_file))
        else:
            # Try to find any .cs files
            cs_files = list(proj_dir.glob("*.cs"))
            if cs_files:
                result["sources"].append(("glob", "*.cs"))
            else:
                return None  # No source files found
    
    # Extract ProjectReferences
    for ref_elem in find_all("ProjectReference"):
        include = ref_elem.get("Include", "")
        if not include:
            continue
        
        # Resolve known references
        ref_basename = Path(include.replace("\\", "/")).name
        
        # Handle $(TestSourceDir)Common/CoreCLRTestLibrary/CoreCLRTestLibrary.csproj
        if "CoreCLRTestLibrary" in include or "TestLibraryProjectPath" in include:
            result["deps"].append("//src/tests/Common:TestLibrary")
            result["deps"].append("@paket.main//microsoft.dotnet.xunitextensions")
            continue
        
        # Handle local project references (same directory or subdirectory)
        if ref_basename in KNOWN_PROJECT_REFS:
            result["deps"].append(KNOWN_PROJECT_REFS[ref_basename])
            continue
        
        # Unknown project reference - skip this test
        # (it depends on something we can't resolve)
        return None
    
    return result


def format_string_list(items, indent=8):
    """Format a list of strings for BUILD.bazel."""
    prefix = " " * indent
    return ",\n".join(f'{prefix}"{item}"' for item in items)


def generate_build_bazel(tests: list[dict], dir_path: Path) -> str:
    """Generate BUILD.bazel content for a list of tests in a directory."""
    has_cs = any(not t["is_ilproj"] for t in tests)
    has_il = any(t["is_ilproj"] for t in tests)
    
    imports = []
    if has_cs and has_il:
        imports.append('load("//src/tests:live_test.bzl", "coreclr_test", "il_coreclr_test")')
    elif has_cs:
        imports.append('load("//src/tests:live_test.bzl", "coreclr_test")')
    elif has_il:
        imports.append('load("//src/tests:live_test.bzl", "il_coreclr_test")')
    
    lines = imports + [""]
    
    for test in sorted(tests, key=lambda t: t["name"]):
        if test["is_ilproj"]:
            lines.extend(generate_il_test(test))
        else:
            lines.extend(generate_cs_test(test))
        lines.append("")
    
    return "\n".join(lines).rstrip() + "\n"


def generate_cs_test(test: dict) -> list[str]:
    """Generate a coreclr_test() call."""
    lines = ["coreclr_test("]
    lines.append(f'    name = "{test["name"]}",')
    
    # Sources
    srcs = []
    globs = []
    for kind, value in test["sources"]:
        if kind == "glob":
            globs.append(value)
        else:
            srcs.append(value)
    
    if globs and srcs:
        glob_part = "glob([%s])" % ", ".join(f'"{g}"' for g in globs)
        file_part = "[%s]" % ", ".join(f'"{s}"' for s in srcs)
        lines.append(f"    srcs = {glob_part} + {file_part},")
    elif globs:
        glob_part = "glob([%s])" % ", ".join(f'"{g}"' for g in globs)
        lines.append(f"    srcs = {glob_part},")
    elif len(srcs) == 1:
        lines.append(f'    srcs = ["{srcs[0]}"],')
    else:
        lines.append("    srcs = [")
        for s in srcs:
            lines.append(f'        "{s}",')
        lines.append("    ],")
    
    # Optional properties (only if non-default)
    # Note: allow_unsafe_blocks defaults to True in coreclr_test macro
    # (matching src/tests/Directory.Build.props), so we don't emit it.
    
    # Only emit optimize when explicitly set in csproj (macro default is True)
    if test["optimize"].lower() == "false":
        lines.append("    optimize = False,")
    
    if test["pri"] == 1:
        lines.append("    pri = 1,")
        lines.append('    size = "medium",')
    
    # Deps
    if test["deps"]:
        if len(test["deps"]) == 1:
            lines.append(f'    deps = ["{test["deps"][0]}"],')
        else:
            lines.append("    deps = [")
            for dep in sorted(test["deps"]):
                lines.append(f'        "{dep}",')
            lines.append("    ],")
    
    # Platform constraints
    if test.get("windows_only"):
        lines.append('    target_compatible_with = ["@platforms//os:windows"],')
    elif test.get("macos_unsupported"):
        lines.append("    target_compatible_with = select({")
        lines.append('        "@platforms//os:macos": ["@platforms//:incompatible"],')
        lines.append('        "//conditions:default": [],')
        lines.append("    }),")
    
    lines.append(")")
    return lines


def generate_il_test(test: dict) -> list[str]:
    """Generate an il_coreclr_test() call."""
    lines = ["il_coreclr_test("]
    lines.append(f'    name = "{test["name"]}",')
    
    # Sources
    srcs = []
    for kind, value in test["sources"]:
        srcs.append(value)
    
    if len(srcs) == 1:
        lines.append(f'    srcs = ["{srcs[0]}"],')
    else:
        lines.append("    srcs = [")
        for s in srcs:
            lines.append(f'        "{s}",')
        lines.append("    ],")
    
    if test["optimize"].lower() == "false":
        lines.append("    optimize = False,")
    
    if test["pri"] == 1:
        lines.append("    pri = 1,")
        lines.append('    size = "medium",')
    
    # Platform constraints
    if test.get("windows_only"):
        lines.append('    target_compatible_with = ["@platforms//os:windows"],')
    elif test.get("macos_unsupported"):
        lines.append("    target_compatible_with = select({")
        lines.append('        "@platforms//os:macos": ["@platforms//:incompatible"],')
        lines.append('        "//conditions:default": [],')
        lines.append("    }),")
    
    lines.append(")")
    return lines


def generate_merged_test(dir_name: str, test_deps: list[str]) -> str:
    """Generate a coreclr_merged_test() BUILD.bazel for a directory."""
    lines = [
        'load("//src/tests:live_test.bzl", "coreclr_merged_test")',
        "",
        "coreclr_merged_test(",
        f'    name = "{dir_name}",',
        "    test_deps = [",
    ]
    for dep in sorted(test_deps):
        lines.append(f'        "{dep}",')
    lines.append("    ],")
    lines.append(")")
    return "\n".join(lines) + "\n"


def process_directory(test_dir: Path, dry_run: bool = False) -> dict:
    """Process a directory tree and generate BUILD.bazel files.
    
    Returns stats dict with counts.
    """
    stats = {
        "generated": 0,
        "skipped_existing": 0,
        "skipped_cmake": 0,
        "skipped_buildonly": 0,
        "skipped_noxunit": 0,
        "skipped_merged": 0,
        "skipped_no_sources": 0,
        "skipped_unknown_ref": 0,
        "errors": 0,
    }
    
    # Group csproj files by directory
    dir_tests = defaultdict(list)
    
    for csproj in sorted(test_dir.rglob("*.csproj")):
        csproj_dir = csproj.parent
        
        # Skip if BUILD.bazel already exists
        if (csproj_dir / "BUILD.bazel").exists():
            stats["skipped_existing"] += 1
            continue
        
        result = parse_csproj(csproj)
        if result is None:
            # Determine skip reason for stats
            try:
                tree = ET.parse(csproj)
                root = tree.getroot()
                text = ET.tostring(root, encoding="unicode")
                if "CMakeProjectReference" in text:
                    stats["skipped_cmake"] += 1
                elif "BuildOnly" in text or "SharedLibrary" in text:
                    stats["skipped_buildonly"] += 1
                elif "ReferenceXUnitWrapperGenerator" in text and "false" in text.lower():
                    stats["skipped_noxunit"] += 1
                elif "MergedWrapperProjectReference" in text:
                    stats["skipped_merged"] += 1
                else:
                    stats["skipped_unknown_ref"] += 1
            except Exception:
                stats["errors"] += 1
            continue
        
        dir_tests[csproj_dir].append(result)
    
    # Also process .ilproj files
    for ilproj in sorted(test_dir.rglob("*.ilproj")):
        ilproj_dir = ilproj.parent
        
        # Skip if BUILD.bazel already exists
        if (ilproj_dir / "BUILD.bazel").exists():
            stats["skipped_existing"] += 1
            continue
        
        result = parse_csproj(ilproj)
        if result is None:
            stats["skipped_no_sources"] += 1
            continue
        
        dir_tests[ilproj_dir].append(result)
    
    # Generate BUILD.bazel files
    for dir_path, tests in sorted(dir_tests.items()):
        if not tests:
            continue
        
        content = generate_build_bazel(tests, dir_path)
        
        if dry_run:
            rel = dir_path.relative_to(REPO_ROOT)
            print(f"Would create {rel}/BUILD.bazel ({len(tests)} test(s))")
        else:
            build_path = dir_path / "BUILD.bazel"
            build_path.write_text(content)
        
        stats["generated"] += len(tests)
    
    return stats


def main():
    dry_run = "--dry-run" in sys.argv
    specific_dir = None
    
    for arg in sys.argv[1:]:
        if arg != "--dry-run" and not arg.startswith("-"):
            specific_dir = arg
    
    if specific_dir:
        test_dir = TEST_ROOT / specific_dir
        if not test_dir.exists():
            print(f"Error: {test_dir} does not exist")
            sys.exit(1)
        dirs_to_process = [test_dir]
    else:
        # Process all test directories
        dirs_to_process = sorted(
            d for d in TEST_ROOT.iterdir()
            if d.is_dir() and d.name not in ("Common", ".git")
        )
    
    total_stats = defaultdict(int)
    
    for test_dir in dirs_to_process:
        dir_name = test_dir.name
        print(f"\nProcessing {dir_name}...")
        stats = process_directory(test_dir, dry_run=dry_run)
        
        for key, val in stats.items():
            total_stats[key] += val
        
        print(f"  Generated: {stats['generated']}")
        print(f"  Skipped (existing BUILD.bazel): {stats['skipped_existing']}")
        print(f"  Skipped (CMake deps): {stats['skipped_cmake']}")
        print(f"  Skipped (BuildOnly): {stats['skipped_buildonly']}")
        print(f"  Skipped (no xunit): {stats['skipped_noxunit']}")
        print(f"  Skipped (merged): {stats['skipped_merged']}")
        print(f"  Skipped (unknown ref): {stats['skipped_unknown_ref']}")
        if stats["errors"]:
            print(f"  Errors: {stats['errors']}")
    
    print(f"\n{'='*60}")
    print(f"TOTAL Generated: {total_stats['generated']}")
    print(f"TOTAL Skipped (existing): {total_stats['skipped_existing']}")
    print(f"TOTAL Skipped (cmake): {total_stats['skipped_cmake']}")
    print(f"TOTAL Skipped (build-only): {total_stats['skipped_buildonly']}")
    print(f"TOTAL Skipped (no xunit): {total_stats['skipped_noxunit']}")
    print(f"TOTAL Skipped (merged): {total_stats['skipped_merged']}")
    print(f"TOTAL Skipped (unknown ref): {total_stats['skipped_unknown_ref']}")
    print(f"TOTAL Errors: {total_stats['errors']}")


if __name__ == "__main__":
    main()
