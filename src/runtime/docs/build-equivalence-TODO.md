# Build Equivalence: Remaining Differences

Tracked by `compare-bazel.sh`. Run with `--skip-build` only after a baseline
rebuild so the MSBuild binlogs contain the full managed compilation set.

## Summary (as of 2026-02-17)

| Category                      | Count  | Notes                                      |
|-------------------------------|--------|--------------------------------------------|
| Native mismatches             | 1000   | defines, flags, optimization in every file |
| Native only-in-CMake          | 640    | Unmatched CMake compilation units          |
| Native only-in-Bazel          | 45     | Unmatched Bazel compilation units          |
| Managed mismatches            | 23     | All 23 compared assemblies have diffs      |
| Managed only-in-MSBuild       | 405    | Unmatched MSBuild assemblies               |
| Managed only-in-Bazel         | 415    | Unmatched Bazel assemblies                 |

## Already fixed (comparison tool)

- [x] Source file path normalization (resolve against project dir, not CWD)
- [x] Generated file matching by filename + content verification
- [x] `/d:` short-form define parsing for Bazel aquery
- [x] Case-insensitive target_type comparison (Library == library)
- [x] lang_version normalization (preview == 14.0)
- [x] `/nowarn:` parsing from Csc CommandLineArguments
- [x] Warning code normalization (1701 → CS1701)
- [x] Filter SDK-generated AssemblyAttributes.cs
- [x] Skip analyzer comparison (Bazel doesn't wire analyzers yet)

## Already fixed (Bazel BUILD files)

- [x] Add NETCOREAPP define for net5.0+ targets
- [x] Filter NETSTANDARD*_OR_GREATER for non-netstandard targets
- [x] Decouple `skip_locals_init` from `exclude_sr` in `impl_assembly` macro —
  SkipLocalsInit.cs is now added for all `IsNETCoreAppSrc` assemblies regardless
  of whether they have a resx file
- [x] Remove CP0003 from `MULTITARGET_NOWARN` — CP0003 (NuGet packaging compat)
  only applies to source assemblies, not tests; now added per-assembly
- [x] Add `MULTITARGET_NOWARN + ["CP0003"]` to 11 multi-target OOB libraries
  missing CA1510-CA1513/CA1845-CA1847 nowarns from MSBuild's Directory.Build.targets
- [x] Add `skip_locals_init = False` to 60 shim/facade assemblies that are not
  `IsNETCoreAppSrc` in MSBuild
- [x] Remove debug-only `System.Console` reference from System.Net.Security and
  System.Security.Cryptography BUILD files (MSBuild only includes it in Debug config)

---

## Managed: remaining differences

### source_files (23/23 assemblies)

- **SkipLocalsInit.cs** (20 assemblies): MSBuild includes `Common/src/SkipLocalsInit.cs`
  via Directory.Build.targets. Bazel targets don't include it.
  **Fix**: Add SkipLocalsInit.cs to Bazel srcs for each affected library, or add it
  as a default common source in the `impl_assembly` macro (controlled by
  `skip_locals_init`; `netcoreapp_impl_assembly` defaults it to True).

- **Forwards.cs missing** (11 assemblies): MSBuild generates type-forwarder files via
  GenAPI. Bazel doesn't generate Forwards.cs for these assemblies.
  **Fix**: Add `generate_forwards = True` or equivalent to these Bazel targets.

- **ref/*.cs in Bazel only** (12 assemblies): Facade assemblies compile ref source files
  in Bazel but the impl source (from S.P.CoreLib) in MSBuild. This is a structural
  difference in how facades are built.
  **Fix**: Align facade build strategy — either use ref source in both, or impl in both.

- **SR.cs** (4 assemblies): MSBuild includes `Common/src/System/SR.cs` but Bazel doesn't
  for some libraries.

### generated_file_content (23/23 assemblies)

- **AssemblyInfo.cs** (23): MSBuild generates rich assembly metadata (Company, Product,
  FileVersion, InformationalVersion, etc.). Bazel generates minimal metadata.
  **Fix**: Enhance Bazel AssemblyInfo generation to include full metadata, or accept
  this as a non-functional difference.

- **Forwards.cs** (3): Content differs where both systems generate the file — typically
  missing type forwarders in the Bazel version.

### nowarn (23/23 assemblies)

MSBuild has more warning suppressions from Directory.Build.props:
- Always: CS1702, CS1705, IDE0060, IDE0100, NU5105
- Most (19): SYSLIB0003/0004/0015/0017/0021-0023/0025/0032/0036
- Most (21): CS1591
- Some: CS0649, CS8602-8632, CA1510-1513/1845-1847, SYSLIB0011/0050/0051

**Fix**: Add equivalent `/nowarn:` flags to Bazel's csharp_library rules, either in
the macro or via a shared default.

### references (16/23 assemblies)

Facade assemblies: MSBuild references System.Private.CoreLib directly; Bazel references
individual ref assemblies (System.Runtime, System.Threading, etc.).
**Fix**: This is a structural difference. May not need fixing if the resulting
assemblies are equivalent.

### defines (3/23 assemblies)

- **mscorlib**: CMake-only `LINUX`, `LINUX1_0` — platform-specific defines not in Bazel.
- **System.Private.CoreLib**: CMake `INPLACE_RUNTIME`/`NATIVEAOT` vs Bazel `CORECLR`/
  `FEATURE_*` — different build variants being compared. The comparison tool is matching
  the wrong variant.
- **TestLibrary**: Entirely different TFM (`netstandard2.0` vs `net10.0`).

---

## Native: remaining differences

### defines (1000/1000)

Every native compilation unit has define mismatches. Common patterns:
- CMake: `FEATURE_COMWRAPPERS`, `FEATURE_EVENT_TRACE`, `FEATURE_PERFTRACING` (no `=1`)
- Bazel: `FEATURE_COMWRAPPERS=1`, `FEATURE_EVENT_TRACE=1` (with `=1`)
- **Fix**: Normalize boolean defines — `FEATURE_X` and `FEATURE_X=1` should compare equal.

Additional define differences:
- CMake-only: `BUILDENV_DEBUG=1`, `DISABLE_CONTRACTS`, `URTBLDENV_FRIENDLY=Debug`, `_DBG`
- Bazel-only: `FEATURE_INTERPRETER`, `FEATURE_JAVAMARSHAL`, `_GNU_SOURCE`
- **Fix**: Audit and align define sets in coreclr_defs.bzl / native_defs.bzl.

### flags (1000/1000)

Warning flags, optimization flags, and other compiler options differ between CMake and
Bazel. Needs audit of .bazelrc vs CMakeLists.txt.

### optimization (1000/1000)

CMake and Bazel report optimization differently. May be a comparison tool issue or a
genuine config mismatch.

### language_standard (30/1000)

Some files show C vs C++ standard differences.

### undefines (99/1000)

CMake undefines that Bazel doesn't.

---

## Unmatched compilations

### Native only-in-CMake (640)
Many CMake compilation units not yet ported to Bazel BUILD files.

### Native only-in-Bazel (45)
Bazel targets without CMake equivalents (possibly new or restructured).

### Managed only-in-MSBuild (405)
Libraries built by MSBuild but not yet in Bazel.

### Managed only-in-Bazel (415)
Libraries/targets in Bazel without MSBuild equivalents (ref assemblies, test helpers, etc.).
