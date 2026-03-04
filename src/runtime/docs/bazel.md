# Bazel Build for dotnet/runtime

## Goal

Build all artifacts in dotnet/runtime with Bazel — native C/C++ components,
managed C# libraries, and the assembled runtime layout — eventually replacing
the CMake + MSBuild build pipeline. The Bazel build must produce equivalent
output binaries and support all platforms CMake/MSBuild currently targets.

## Current Status

The Bazel build produces a fully functional .NET runtime on **linux-x64**.
All native C/C++ components build with Bazel (CoreCLR, corehost, 6 native
interop libs, NativeAOT runtime). Managed C# libraries (System.Private.CoreLib,
154 framework assemblies) also build with Bazel via `rules_dotnet`.
A hybrid build script assembles everything into a standard `dotnet` runtime
layout.

### What Works

- **Native C++**: libcoreclr.so (with statically-linked JIT), dotnet host,
  hostfxr, hostpolicy, apphost, nethost, 6 native interop libraries,
  NativeAOT runtime, standalone GC, AOT JIT interface
- **Managed C#**: System.Private.CoreLib, 154 framework assemblies
  (145 of 150 non-shim NetCoreApp assemblies + shims + extras),
  ref assemblies, source generators
- **Build tools**: ResGen, GenerateResxSource, GenFacades, ilasm, LibraryImportGenerator
- **Tests**: corehost native tests, xUnit-based managed test infrastructure,
  93 library test suites (Microsoft.CSharp, Microsoft.Win32.Primitives,
  System.CodeDom, System.Collections, System.Collections.Concurrent,
  System.Collections.Immutable, System.Collections.NonGeneric,
  System.Collections.Specialized, System.ComponentModel,
  System.ComponentModel.Annotations, System.ComponentModel.EventBasedAsync,
  System.ComponentModel.Primitives, System.ComponentModel.TypeConverter,
  System.Console, System.Data.Common, System.Diagnostics.Contracts,
  System.Diagnostics.DiagnosticSource, System.Diagnostics.FileVersionInfo,
  System.Diagnostics.StackTrace, System.Diagnostics.TextWriterTraceListener,
  System.Diagnostics.TraceSource, System.Diagnostics.Tracing,
  System.Drawing.Primitives, System.Formats.Asn1, System.Formats.Cbor,
  System.Formats.Nrbf, System.Formats.Tar, System.IO.Compression,
  System.IO.Compression.Brotli, System.IO.Compression.ZipFile,
  System.IO.FileSystem.DriveInfo, System.IO.FileSystem.Watcher,
  System.IO.Hashing, System.IO.IsolatedStorage, System.IO.MemoryMappedFiles,
  System.IO.Pipelines, System.IO.Pipes, System.Linq,
  System.Linq.AsyncEnumerable, System.Linq.Expressions,
  System.Linq.Parallel, System.Linq.Queryable, System.Memory,
  System.Net.HttpListener, System.Net.Mail, System.Net.NameResolution,
  System.Net.NetworkInformation, System.Net.Ping, System.Net.Primitives,
  System.Net.Requests, System.Net.Sockets, System.Net.WebClient,
  System.Net.WebHeaderCollection, System.Net.WebProxy,
  System.Net.WebSockets, System.Net.WebSockets.Client,
  System.Numerics.Vectors, System.ObjectModel, System.Private.Uri,
  System.Private.Xml.Linq, System.Reflection.DispatchProxy,
  System.Reflection.Emit, System.Reflection.Emit.ILGeneration,
  System.Reflection.Emit.Lightweight, System.Reflection.Extensions,
  System.Reflection.Metadata, System.Reflection.TypeExtensions,
  System.Resources.Writer, System.Runtime.CompilerServices.VisualC,
  System.Runtime.InteropServices (UnitTests), System.Runtime.Intrinsics,
  System.Runtime.Numerics, System.Runtime.Serialization.Json,
  System.Runtime.Serialization.Primitives, System.Runtime.Serialization.Xml,
  System.Security.Claims, System.Security.Cryptography,
  System.Security.Cryptography.Pkcs, System.Security.Cryptography.Xml,
  System.Text.Encoding.CodePages, System.Text.Encoding.Extensions,
  System.Text.Encodings.Web, System.Text.RegularExpressions,
  System.Threading, System.Threading.Channels, System.Threading.Overlapped,
  System.Threading.RateLimiting, System.Threading.Tasks.Dataflow,
  System.Threading.Tasks.Parallel, System.Threading.Thread,
  System.Threading.ThreadPool, System.Transactions.Local,
  System.Web.HttpUtility)
- **Per-component configuration**: independent debug/checked/release for
  CoreCLR and Libraries (matching MSBuild's `-rc`/`-lc` flags)
- **Runtime layout**: `runtime_layout` rule assembles stripped binaries into
  standard .NET hosting directory structure

### What's Next

- Remaining managed libraries (21 NetFxReference shims + 5 non-shim assemblies not yet in Bazel)
- Library unit tests (93 of ~187 libraries have Bazel test BUILD files)
- CoreCLR diagnostic tooling: DAC (mscordac), DBI (mscordbi), createdump, SOS
- CoreCLR tools: SuperPMI, ildasm (full binary)
- ILC BUILD files and end-to-end NativeAOT pipeline (see [NativeAOT Compilation Pipeline](#nativeaot-compilation-pipeline))
- Bootstrap mode: NativeAOT-compile crossgen2, use it for System.Private.CoreLib
- ILLink (IL trimmer/linker)
- Installer/packaging (NuGet packs, runtime packs, targeting packs)
- Additional platforms (linux-arm64, macOS, Windows)
- Full test suite integration
- Eliminate MSBuild dependency for managed builds

## Per-Component Configuration

CoreCLR and Libraries have independent build configurations, matching
MSBuild's `-rc` (runtime configuration) and `-lc` (libraries configuration):

| Config flags | CoreCLR | Libraries |
|---|---|---|
| *(default)* | Debug | Debug |
| `--config=release` | Release | Release |
| `--config=clr_release` | Release | Debug |
| `--config=clr_checked` | Checked | Debug |
| `--config=libs_release` | Debug | Release |
| `--config=clr_checked --config=libs_release` | Checked | Release |

Implementation uses modern Bazel `string_flag` build settings (`//:clr_config`,
`//:libs_config`) with `per_file_copt` scoping C++ defines by source path.
Clang is the default compiler (matching CMake).

## Verifying Build Equivalence

`compare-bazel.sh` automates build-input equivalence checking between Bazel and
CMake/MSBuild. It compares every compilation unit's source files, preprocessor
defines, compiler flags, references, and other inputs.

```bash
# Prerequisites: build both systems first
./build.sh clr+libs -rc release                         # MSBuild + CMake
bazel build //...                                       # Bazel

# Run comparison
./compare-bazel.sh                                      # default (debug)
./compare-bazel.sh --config release                     # release mode
./compare-bazel.sh --skip-build                         # reuse existing build artifacts
./compare-bazel.sh --verbose                            # show full diffs
./compare-bazel.sh --json-output results.json           # machine-readable output
```

The tool lives in `eng/tools/BuildEquivalenceCheck/`. It parses MSBuild `.binlog`
files, CMake `compile_commands.json`, and Bazel `aquery` output to extract and
normalize compilation records, then compares them field-by-field.

### Known equivalence gaps

See [build-equivalence-TODO.md](build-equivalence-TODO.md) for the full list. Key
areas:

- **Native defines** (1000 files): Boolean define normalization needed (`-DFOO` vs
  `-DFOO=1`), plus missing/extra defines in `coreclr_defs.bzl` and `native_defs.bzl`
- **Native flags/optimization** (1000 files): Warning flags and optimization level
  mismatches between `.bazelrc` and `CMakeLists.txt`
- **Managed source files** (23 assemblies): Missing `SkipLocalsInit.cs`, `Forwards.cs`
  generation gaps, facade build strategy differences
- **Managed nowarn** (23 assemblies): MSBuild's `Directory.Build.props` suppressions
  not replicated in Bazel
- **Unmatched compilations**: 640 CMake-only + 405 MSBuild-only units not yet ported
  to Bazel

### Verifying Runtime Pack Output

`compare-runtime-packs.sh` is the acceptance test for the Bazel runtime build.
It verifies that the Bazel-produced runtime archive (`dotnet-runtime-*-linux-x64.tar.gz`)
is **bit-for-bit identical** to the one produced by CMake+MSBuild's `packs` subset.

This is the primary correctness gate: if the archives match, the Bazel build is
producing the correct output.

```bash
# 1. Build the MSBuild runtime archive (if not already built)
./build.sh clr+libs+host -rc Release -lc Release
./build.sh packs -rc Release -lc Release
# → artifacts/packages/*/Shipping/dotnet-runtime-*-linux-x64.tar.gz

# 2. Build the Bazel runtime archive (once implemented)
# bazel build //:runtime_archive
# → bazel-bin/dotnet-runtime-*-linux-x64.tar.gz

# 3. Compare them
./compare-runtime-packs.sh \
  artifacts/packages/Debug/Shipping/dotnet-runtime-*-linux-x64.tar.gz \
  path/to/bazel-runtime.tar.gz
```

The script runs three phases:

| Phase | What it checks |
|---|---|
| **File inventory** | Both archives contain exactly the same set of files |
| **Content** | Every file is bit-for-bit identical (SHA-256 comparison) |
| **Permissions** | File permission bits match |

The archive contains ~192 files: the `dotnet` host, `libhostfxr.so`,
`libcoreclr.so`, native interop libraries, `createdump`, managed framework DLLs,
config files (`deps.json`, `runtimeconfig.json`), and license files.

## Platform Support Status

CMake currently supports all of these OS × architecture combinations. Bazel support status is tracked below.

### Operating Systems

| OS | CMake | Bazel | Notes |
|----|-------|-------|-------|
| Linux (glibc) | ✅ | 🔨 In progress | First target; linux-x64 compiler flags verified |
| Linux (musl/Alpine) | ✅ | ❌ Not started | |
| macOS (Darwin) | ✅ | ❌ Not started | |
| Windows | ✅ | ❌ Not started | |
| FreeBSD | ✅ | ❌ Not started | |
| NetBSD | ✅ | ❌ Not started | |
| OpenBSD | ✅ | ❌ Not started | |
| illumos/Solaris (SunOS) | ✅ | ❌ Not started | |
| Haiku | ✅ | ❌ Not started | |
| Android | ✅ | ❌ Not started | |
| iOS / iOS Simulator | ✅ | ❌ Not started | |
| tvOS / tvOS Simulator | ✅ | ❌ Not started | |
| Mac Catalyst | ✅ | ❌ Not started | |
| Browser (Emscripten/WASM) | ✅ | ⊘ Out of scope | Mono only |
| WASI | ✅ | ⊘ Out of scope | Mono only |
| Tizen | ✅ | ⊘ Out of scope | Mono only |

### Architectures

| Architecture | CMake | Bazel | Notes |
|-------------|-------|-------|-------|
| x64 (AMD64) | ✅ | 🔨 In progress | First target |
| x86 (i386) | ✅ | ❌ Not started | |
| ARM64 (AArch64) | ✅ | ❌ Not started | |
| ARM (32-bit) | ✅ | ❌ Not started | |
| ARMv6 | ✅ | ❌ Not started | |
| RISC-V 64 | ✅ | ❌ Not started | |
| LoongArch64 | ✅ | ❌ Not started | |
| s390x | ✅ | ❌ Not started | |
| PowerPC64 (ppc64le) | ✅ | ❌ Not started | |
| MIPS64 | ✅ | ❌ Not started | |
| WASM | ✅ | ⊘ Out of scope | Mono only |

---

## 1. Native Libraries (`src/native/libs/`)

### 1.1 System.Native — ✅ DONE
- [x] `src/native/libs/System.Native/BUILD.bazel`
- [x] Produces `libSystem.Native.so` (277 exports, matches CMake)

### 1.2 System.IO.Compression.Native — ✅ DONE
- [x] `src/native/libs/System.IO.Compression.Native/BUILD.bazel`
- [x] Produces `libSystem.IO.Compression.Native.so` (472 exports, matches CMake)

### 1.3 System.IO.Ports.Native — ✅ DONE
- [x] `src/native/libs/System.IO.Ports.Native/BUILD.bazel`
- [x] Produces `libSystem.IO.Ports.Native.so` (19 exports, matches CMake)

### 1.4 System.Net.Security.Native — ✅ DONE
- [x] `src/native/libs/System.Net.Security.Native/BUILD.bazel`
- [x] Produces `libSystem.Net.Security.Native.so` (21 exports, matches CMake)
- [x] Uses `GSS_SHIM` on Linux (loads `libgssapi_krb5` via dlopen at runtime)

### 1.5 System.Globalization.Native — ✅ DONE
- [x] `src/native/libs/System.Globalization.Native/BUILD.bazel`
- [x] `src/native/libs/System.Globalization.Native/bazel/config.h` (hardcoded linux-x64)
- [x] Produces `libSystem.Globalization.Native.so` (36 exports, matches CMake)
- [x] Uses `pal_icushim.c` on Linux (loads ICU via dlopen at runtime)

### 1.6 System.Security.Cryptography.Native.OpenSsl — ✅ DONE
- [x] `src/native/libs/System.Security.Cryptography.Native/BUILD.bazel`
- [x] `src/native/libs/System.Security.Cryptography.Native/bazel/linux-glibc-x64/pal_crypto_config.h` (hardcoded linux-x64)
- [x] Produces `libSystem.Security.Cryptography.Native.OpenSsl.so` (379 exports, matches CMake)
- [x] Uses `FEATURE_DISTRO_AGNOSTIC_SSL` on Linux (loads OpenSSL via dlopen at runtime; `opensslshim.h` overrides all `HAVE_OPENSSL_*` to 1, making the binary OpenSSL-version-independent)

### 1.7 Platform-specific libs
- [ ] System.Security.Cryptography.Native.Android (Android only)
- [ ] System.Security.Cryptography.Native.Apple (macOS/iOS/tvOS/Mac Catalyst only)
- ⊘ System.Native.Browser — out of scope (Mono/WASM only)
- ⊘ System.Runtime.InteropServices.JavaScript.Native — out of scope (Mono/WASM only)

---

## 2. Shared Infrastructure (`src/native/`)

### 2.1 minipal — 🔨 linux-x64 done
- [x] `src/native/minipal/BUILD.bazel`
- [x] `src/native/minipal/bazel/linux-glibc-x64/minipalconfig.h` (hardcoded linux-x64)
- [ ] Platform-specific `minipalconfig.h` for other OS/arch combinations (or genrule)

### 2.2 Common headers — 🔨 linux-x64 done
- [x] `src/native/libs/BUILD.bazel` (Common headers)
- [x] `src/native/libs/bazel/linux-glibc-x64/pal_config.h` (hardcoded linux-x64)
- [ ] Platform-specific `pal_config.h` for other OS/arch combinations (or genrule)

### 2.3 Vendored external deps — ✅ DONE
- [x] `src/native/external/zlib-ng/BUILD.bazel`
- [x] `src/native/external/zstd/BUILD.bazel`
- [x] `src/native/external/brotli/BUILD.bazel` (updated for Bazel 9)

### 2.4 containers — ✅ DONE
- [x] `src/native/containers/BUILD.bazel`
  - [x] Two variants: `dn-containers` and `dn-containers-no-lto` (LTO-incompatible scenarios like NativeAOT)
  - [x] `src/native/containers/bazel/linux-glibc-x64/dn-config.h` (hardcoded linux-x64)

### 2.5 eventpipe — ✅ DONE (linux-x64)
- [x] `src/native/eventpipe/BUILD.bazel`
  - [x] `dn-eventpipe-srcs` filegroup (20 source files)
  - [x] `dn-diagnosticserver-srcs` filegroup (8 source files)
  - [x] `dn-diagnosticserver-pal-srcs` filegroup (socket PAL for linux)
  - [x] `eventpipe-headers` cc_library (headers + include paths)
  - [x] Interface library pattern: sources exposed as filegroups for consuming runtimes (CoreCLR, NativeAOT) to compile with their own `ep-rt.h`/`ds-rt.h`
- [x] `src/native/eventpipe/bazel/linux-glibc-x64/ep-shared-config.h` (hardcoded linux-x64)

### 2.6 watchdog
- [ ] `src/native/watchdog/BUILD.bazel`
  - [ ] Small watchdog utility

### 2.7 Vendored external deps (remaining)
- [ ] `src/native/external/llvm-libunwind/BUILD.bazel`
  - [ ] LLVM's libunwind, alternative to GNU libunwind

### 2.8 Bundled GNU libunwind — ✅ DONE (linux-x64)
- [x] `src/native/external/libunwind/BUILD.bazel`
  - [x] `libunwind_generic` cc_library (G-prefix sources: remote/generic unwind)
  - [x] `libunwind_local` cc_library (L-prefix sources: local-only unwind)
  - [x] `libunwind` combined cc_library
  - [x] `bazel/linux-glibc-x64/include/config.h` (hardcoded linux-x64)
  - [x] Generated `libunwind-common.h`, `libunwind.h`, `tdep/libunwind_i.h`

### 2.8 rapidjson — ✅ DONE
- [x] `src/native/external/rapidjson/BUILD.bazel`
  - [x] Header-only JSON library, used by corehost
  - [x] Consumers include via `#include <rapidjson/document.h>` etc.

---

## 3. Core Host (`src/native/corehost/`) — ✅ DONE (linux-x64)

The .NET host (dotnet CLI, apphost, hostfxr, hostpolicy). C++ codebase, 25 CMakeLists.txt files.

### 3.1 hostmisc (static lib) — ✅ DONE
- [x] Platform abstraction (trace, utils, PAL, fx_ver)

### 3.2 libhostcommon (static lib) — ✅ DONE
- [x] JSON parsing, runtime config, bundle support

### 3.3 hostfxr (shared lib) — ✅ DONE
- [x] `libhostfxr.so` (18 exports, matches CMake)
- [x] Version script via genrule from `hostfxr_unixexports.src`

### 3.4 hostpolicy (shared lib) — ✅ DONE
- [x] `libhostpolicy.so` (7 exports, matches CMake)
- [x] Version script via genrule from `hostpolicy_unixexports.src`

### 3.5 dotnet host executable — ✅ DONE
- [x] `dotnet` binary

### 3.6 apphost — ✅ DONE
- [x] `apphost` binary (with `FEATURE_APPHOST` define)

### 3.7 nethost (shared lib) — ✅ DONE
- [x] `libnethost.so` (1 export: `get_hostfxr_path`)

### 3.8 comhost / ijwhost (Windows only)
- [ ] `src/native/corehost/comhost/BUILD.bazel` (Windows COM hosting)
- [ ] `src/native/corehost/ijwhost/BUILD.bazel` (Windows IJW/C++CLI)

### 3.9 apphost/static (single-file host)
- [ ] Depends on CoreCLR being Bazel-built

### 3.10 corehost tests (cross-platform) — ✅ DONE (linux-x64)
- [x] `src/native/corehost/test/BUILD.bazel`
  - [x] `test_fx_ver` executable (framework version parsing tests)
  - [x] `mockcoreclr` shared library (mock CoreCLR)
  - [x] `mockhostfxr_2_2` / `mockhostfxr_5_0` shared libraries (mock hostfxr, two API versions)
  - [x] `mockhostpolicy` shared library (mock hostpolicy)
  - [x] `nativehost` executable (native hosting API tests)
- [ ] Windows-only tests: comsxs, ijw, typelibs (require Windows toolchain)

---

## 4. CoreCLR Runtime (`src/coreclr/`)

The main CLR runtime engine. Large C++ codebase, 86 CMakeLists.txt files.

### 4.1 Common headers and defines — ✅ DONE (linux-x64)
- [x] `src/coreclr/inc/BUILD.bazel`
  - [x] `coreclr_inc` cc_library (161 headers + CORECLR_DEFINES + global copts); depends on all component header targets (binder, debug, dlls, gc, gcdump, hosts, interpreter, jit, md, minipal, pal, vm, eventing, native_inc, native minipal, version_headers)
  - [x] `coreclr_inc_headers_only` lightweight header-only target (no transitive deps, used by PAL)
  - [x] `CORECLR_DEFINES` constant list (~60 defines for linux-x64 retail)
  - [x] `CORECLR_COPTS` constant list (global include paths + warning suppression)

### 4.2 CoreCLR minipal — ✅ DONE (linux-x64)
- [x] `src/coreclr/minipal/BUILD.bazel`
  - [x] `coreclrminipal_headers` (dn-u16.h, dn-stdio.h, minipal.h)
  - [x] `coreclrminipal` (4 C++ sources: doublemapping, dn-u16, dn-stdio, memory)

### 4.3 Native resources — ✅ DONE (linux-x64)
- [x] `src/coreclr/nativeresources/BUILD.bazel`
  - [x] `nativeresourcestring` static lib (resourcestring.cpp)

### 4.4 GC — ✅ DONE (linux-x64)
- [x] `src/coreclr/gc/BUILD.bazel` — `gc_headers` (headers + inlines + defs + env/ + vxsort/ + unix/)
- [x] `gc_pal` cc_library (OBJECT) — Unix PAL (gcenv.unix.cpp, numasupport.cpp, events.cpp, cgroup.cpp)
- [x] `gc_vxsort` cc_library (OBJECT) — Vectorized sorting (AMD64 AVX2/AVX512 sources with `-mavx2`)
- [x] Data descriptor stubs (gc_dll_wks_descriptor, gc_dll_svr_descriptor, gcexp_dll_wks_descriptor, gcexp_dll_svr_descriptor)
- [x] `clrgc` cc_shared_library — Standalone GC with segments (`libclrgc.so`, 2 exports: GC_Initialize, GC_VersionInfo)
- [x] `clrgcexp` cc_shared_library — Standalone GC with regions (`libclrgcexp.so`, 2 exports + USE_REGIONS)
- [x] `src/coreclr/gc/unix/bazel/linux-glibc-x64/config.gc.h` (hardcoded linux-x64)

### 4.5 JIT compiler — ✅ DONE (linux-x64)
- [x] `src/coreclr/jit/BUILD.bazel` — `jit_headers` (headers + hpp + defs + jitstd/)
- [x] `clrjit_static` cc_library (105 .cpp AMD64 sources, compiled as static archive)

### 4.6 VM (execution engine) — ✅ DONE (linux-x64)
- [x] `src/coreclr/vm/BUILD.bazel` — `vm_headers` (headers + hpp + inlines + amd64/ + i386/)
- [x] `cee_wks_asm` cc_library (23 .S assembly files, separate target without PCH)
- [x] `cee_wks_core` cc_library (~220 .cpp VM core sources)
- [x] `cee_wks` cc_library (ceemain.cpp, codeman.cpp, peimagelayout.cpp)
- [x] `src/coreclr/vm/eventing/BUILD.bazel`
  - [x] `eventing_headers` — pre-generated event headers for linux-glibc-x64
  - [x] `eventpipe_gen_srcs` — pre-generated eventpipe C++ sources (5 files)
  - [x] `eventpipe_shim_headers` — CoreCLR-specific eventpipe shim headers
  - [x] `eventpipe` cc_library — native eventpipe/diagnosticserver (unity build .c→C++) + shim + generated sources
- [x] `src/coreclr/vm/datadescriptor/BUILD.bazel`
  - [x] `cdac_contract_descriptor`, `gc_wks_descriptor`, `gc_svr_descriptor` stubs
- [x] `src/coreclr/runtime/BUILD.bazel` — `runtime_headers` + exported .cpp/.S

### 4.7 PAL (Platform Abstraction Layer) — ✅ DONE (linux-x64)
- [x] `src/coreclr/pal/BUILD.bazel`
  - [x] `coreclrpal` static library (~50 C/C++ + 4 assembly files)
  - [x] `tracepointprovider` object library
  - [x] Pre-generated `config.h` for linux-glibc-x64

### 4.8 Binder (assembly loading) — ✅ DONE (linux-x64)
- [x] `src/coreclr/binder/BUILD.bazel` — `binder_headers` (inc/*.h, inc/*.hpp, inc/*.inl)
- [x] `v3binder` cc_library (11 .cpp assembly binder sources)

### 4.9 Metadata (IL metadata reader) — ✅ DONE (linux-x64)
- [x] `src/coreclr/md/BUILD.bazel` — `md_inc` (inc/*.h, *.inl)
- [x] `mdcompiler_wks` cc_library (18 .cpp compiler sources)
- [x] `mdruntime_wks` cc_library (12 .cpp runtime sources)
- [x] `mdruntimerw_wks` cc_library (10 .cpp ENC sources)
- [x] `src/coreclr/md/ceefilegen/BUILD.bazel` — `ceefgen` cc_library (5 .cpp)

### 4.10 Utility code — ✅ DONE (linux-x64)
- [x] `src/coreclr/utilcode/BUILD.bazel` — `utilcode` (OBJECT) + `utilcodestaticnohost` (STATIC)
- [x] `src/coreclr/gcinfo/BUILD.bazel` — `gcinfo` static library
- [x] `src/coreclr/unwinder/BUILD.bazel` — `unwinder_wks` OBJECT library
- [x] `src/coreclr/interop/BUILD.bazel` — `interop` OBJECT library
- [x] `src/coreclr/gcdump/BUILD.bazel` — `gcdump_headers` (headers done, compiled lib pending)
- [x] `src/coreclr/interpreter/BUILD.bazel` — `interpreter_headers` (headers done, compiled lib pending)

### 4.11 Debug support — 🔨 Partial (linux-x64)
- [x] `src/coreclr/debug/BUILD.bazel` — `debug_inc` (inc/ + ee/ + daccess/ + dbgutil/ + di/ headers)
- [x] `debug-pal` cc_library (2 .cpp debug PAL sources)
- [x] `cordbee_wks` cc_library (16 sources — debugger EE, workstation)
- [ ] DAC (`mscordac`) — data access component for debugging/diagnostics
- [ ] DBI (`mscordbi`) — debug interface library
- [ ] dbgutil — debug utility library
- [ ] createdump — crash dump generation tool
- [ ] runtimeinfo — runtime info for debuggers

### 4.12 Hosts & DLLs — ✅ libcoreclr DONE (linux-x64)
- [x] `src/coreclr/hosts/BUILD.bazel` — `hosts_inc` (inc/*.h)
- [x] `src/coreclr/dlls/BUILD.bazel` — `dlls_headers` (**/*.h)
- [x] `src/coreclr/dlls/mscorrc/BUILD.bazel` — `mscorrc` cc_library (pre-generated resource strings, 795 entries)
- [x] `src/coreclr/dlls/mscoree/coreclr/BUILD.bazel` — `libcoreclr.so` (209 MB, 12 exported symbols with V1.0 versioning)
  - [x] Version script from `mscorwks_unixexports.src` (pre-generated `coreclr.exports`)
  - [x] Links all component libraries: VM, JIT, metadata, binder, debug, GC, PAL, eventpipe, etc.
  - [x] 737 total Bazel actions, ~79s clean build
- [x] `src/coreclr/pal/BUILD.bazel` — `eventprovider` cc_library (pre-generated dummy LTTng stubs)
  - [ ] mscordac, mscordbi (diagnostic tooling — pending)

### 4.13 IL Assembler — ✅ DONE (linux-x64)
- [x] `src/coreclr/ilasm/BUILD.bazel` — `ilasm` cc_binary (cfg="exec" tool)
  - [x] Parser includes via textual_hdrs (grammar_before.cpp, grammar_after.cpp)

### 4.14 IL Disassembler — 🔨 Headers only
- [x] `src/coreclr/ildasm/BUILD.bazel` — `ildasm_inc` cc_library (headers only)
- [ ] Full `ildasm` cc_binary

### 4.15 NativeAOT — 🔨 Native runtime only (linux-x64)
- [x] `src/coreclr/nativeaot/BUILD.bazel` — native runtime static libraries
  - [x] nativeaot_runtime_wks, nativeaot_runtime_svr (workstation/server GC variants)
  - [x] standalonegc_disabled, standalonegc_enabled
  - [x] nativeaot_vxsort_enabled, nativeaot_vxsort_disabled
  - [x] bootstrapper, bootstrapperdll, stdc_compat, eventpipe_disabled
  - [x] Per-file copt strips debug defines to avoid REGDISPLAY conflicts
- [ ] ILC compiler (managed C# AOT compiler) — see [NativeAOT Pipeline](#nativeaot-compilation-pipeline) below
- [ ] NativeAOT managed libraries (System.Private.CoreLib, Reflection.Execution, StackTraceMetadata, TypeLoader, Runtime.Base)

### 4.16 Tools — 🔨 Partial
- [x] `src/coreclr/tools/aot/jitinterface/BUILD.bazel` — AOT JIT interface shared library (native C++)
- [ ] SuperPMI — JIT method replay/diff tool (5 native C++ sub-components)
- [ ] SOS — debugging extension (native C++)
- [ ] crossgen2 — ReadyToRun AOT compiler (managed C#)
- [ ] R2RDump — ReadyToRun image dumper (managed C#)


---

## 5. Managed Libraries (`src/libraries/`) — 🔨 In Progress

Managed C# framework assemblies built with `rules_dotnet`. The NetCoreApp shared
framework contains 150 assemblies (per `NetCoreAppLibrary.props`). Of those, **145
have `impl_` targets** in Bazel and are in the `impl_netcoreapp` aggregate. The
remaining 5 need special support: Microsoft.VisualBasic.Core (VB compiler),
System.IO.Pipes.AccessControl and System.Threading.AccessControl (Windows PNSE stubs),
System.Net.Quic (msquic native library), and System.Runtime.InteropServices.JavaScript
(Browser/WASM-only). 93 libraries have Bazel test BUILD files (out of ~187 with
test projects).

### 5.1 System.Private.CoreLib — ✅ DONE
- [x] `src/coreclr/System.Private.CoreLib/BUILD.bazel`
  - [x] `impl_System.Private.CoreLib` — full CoreLib with NativeRuntimeEventSource generator
  - [x] Debug/release feature alignment with C++ side via `//:clr_config` select()
  - [x] `src/libraries/System.Private.CoreLib/src/files.bzl` — source file lists

### 5.2 Framework ref + impl assemblies — 🔨 In Progress
- [x] `src/libraries/BUILD.bazel` — root-level ref/impl targets + `impl_netcoreapp` aggregate (154 assemblies)
- [x] 35 type-forwarder shim assemblies (`src/libraries/shims/`)
- [x] `src/libraries/defs.bzl` — netcoreapp_ref_assembly, netcoreapp_impl_assembly, gen_facades, ref_impl_pair macros
- [x] Source generators: LibraryImportGenerator, Microsoft.Interop.SourceGeneration, RegexGenerator
- [ ] Remaining 26 NetCoreApp assemblies: 21 NetFxReference shims + 5 non-shim (see §9 for breakdown)

### 5.3 Build Tools — 🔨 Partial
- [x] `src/tools/GenerateResxSource` — resource source generator
- [x] `src/tools/ResGen` — resource compiler
- [x] `src/tools/GenFacades` — type-forward facade generator
- [ ] ILLink / IL trimmer (`src/tools/illink/`) — IL linker, Roslyn analyzers, tasks
- [ ] StressLogAnalyzer (`src/tools/StressLogAnalyzer/`)

### 5.4 Tests — 🔨 Partial
- [x] `src/tests/defs.bzl` — test infrastructure, live_csharp_library, xUnit runner
- [x] `src/tests/live_test.bzl` — `library_test` macro for library unit tests
- [x] 18 test BUILD files (JIT directed tests, common infrastructure)
- [x] 93 library test suites (Microsoft.CSharp, Microsoft.Win32.Primitives,
  System.CodeDom, System.Collections, System.Collections.Concurrent,
  System.Collections.Immutable, System.Collections.NonGeneric,
  System.Collections.Specialized, System.ComponentModel,
  System.ComponentModel.Annotations, System.ComponentModel.EventBasedAsync,
  System.ComponentModel.Primitives, System.ComponentModel.TypeConverter,
  System.Console, System.Data.Common, System.Diagnostics.Contracts,
  System.Diagnostics.DiagnosticSource, System.Diagnostics.FileVersionInfo,
  System.Diagnostics.StackTrace, System.Diagnostics.TextWriterTraceListener,
  System.Diagnostics.TraceSource, System.Diagnostics.Tracing,
  System.Drawing.Primitives, System.Formats.Asn1, System.Formats.Cbor,
  System.Formats.Nrbf, System.Formats.Tar, System.IO.Compression,
  System.IO.Compression.Brotli, System.IO.Compression.ZipFile,
  System.IO.FileSystem.DriveInfo, System.IO.FileSystem.Watcher,
  System.IO.Hashing, System.IO.IsolatedStorage, System.IO.MemoryMappedFiles,
  System.IO.Pipelines, System.IO.Pipes, System.Linq,
  System.Linq.AsyncEnumerable, System.Linq.Expressions,
  System.Linq.Parallel, System.Linq.Queryable, System.Memory,
  System.Net.HttpListener, System.Net.Mail, System.Net.NameResolution,
  System.Net.NetworkInformation, System.Net.Ping, System.Net.Primitives,
  System.Net.Requests, System.Net.Sockets, System.Net.WebClient,
  System.Net.WebHeaderCollection, System.Net.WebProxy,
  System.Net.WebSockets, System.Net.WebSockets.Client,
  System.Numerics.Vectors, System.ObjectModel, System.Private.Uri,
  System.Private.Xml.Linq, System.Reflection.DispatchProxy,
  System.Reflection.Emit, System.Reflection.Emit.ILGeneration,
  System.Reflection.Emit.Lightweight, System.Reflection.Extensions,
  System.Reflection.Metadata, System.Reflection.TypeExtensions,
  System.Resources.Writer, System.Runtime.CompilerServices.VisualC,
  System.Runtime.InteropServices (UnitTests), System.Runtime.Intrinsics,
  System.Runtime.Numerics, System.Runtime.Serialization.Json,
  System.Runtime.Serialization.Primitives, System.Runtime.Serialization.Xml,
  System.Security.Claims, System.Security.Cryptography,
  System.Security.Cryptography.Pkcs, System.Security.Cryptography.Xml,
  System.Text.Encoding.CodePages, System.Text.Encoding.Extensions,
  System.Text.Encodings.Web, System.Text.RegularExpressions,
  System.Threading, System.Threading.Channels, System.Threading.Overlapped,
  System.Threading.RateLimiting, System.Threading.Tasks.Dataflow,
  System.Threading.Tasks.Parallel, System.Threading.Thread,
  System.Threading.ThreadPool, System.Transactions.Local,
  System.Web.HttpUtility)
- [ ] Remaining CoreCLR test suite (~thousands of tests)
- [ ] Remaining library unit tests (~94 libraries)

### 5.5 Installer / Packaging
- [ ] `src/installer/` — runtime packs, NuGet packaging, SDK integration
- [ ] Targeting packs, runtime packs, host packs

---

## 6. Mono Runtime — ⊘ Out of Scope

The Mono runtime (`src/mono/`) is explicitly out of scope for the Bazel build.
Mono-only platforms (Browser/WASM, WASI, Tizen) are also excluded.

---

## 7. Bazel Infrastructure — 🔨 linux-x64 done

- [x] `MODULE.bazel` — Bzlmod workspace, depends on rules_cc@0.2.14, rules_dotnet, bazel_skylib@1.8.2
- [x] `.bazelrc` — Compiler flags matching CMake for linux-x64, per-component config system
- [x] `BUILD.bazel` (root) — Root package, string_flag build settings, config_settings, runtime layout
- [x] `defs.bzl` — Shared macros (csharp_library wrapper, gen_resx_source, resgen)
- [x] `src/libraries/defs.bzl` — Library macros (netcoreapp_ref_assembly, netcoreapp_impl_assembly, gen_facades, ref_impl_pair)
- [x] `src/tests/defs.bzl` — Test infrastructure (live_csharp_library, test runner)
- [x] Compiler flag parity verified against CMake (`-g`, `-O3`, `-std=gnu11`/`-std=c++17`, all warning flags, all defines)
- [x] Clang is the default compiler (matching CMake), with GCC available via `--repo_env=CC=gcc`
- [x] Per-component configuration: `//:clr_config` (debug/checked/release) + `//:libs_config` (debug/release)
- [ ] `.bazelrc` platform configs for other OS/arch targets (e.g., `build:linux-arm64`, `build:macos-x64`)
- [ ] Bazel toolchain definitions for cross-compilation
- [ ] Bazel `select()` rules for platform-conditional source files and defines

---

## 8. Hybrid Runtime Build (`build-bazel-runtime.sh`) — ✅ DONE (linux-x64)

Assembles a working .NET runtime from Bazel-built native components + MSBuild-built managed libraries.

### Usage

```bash
# Full build (managed + native) — first run takes 15-30 min for MSBuild
./build-bazel-runtime.sh

# Per-component configuration (mirrors build.sh -rc / -lc flags)
./build-bazel-runtime.sh -rc checked -lc release      # Checked CLR + release libs
./build-bazel-runtime.sh -rc release                   # Release CLR, debug libs
./build-bazel-runtime.sh -c release                    # Release everything

# Native-only rebuild (fast iteration on C++ changes, ~77s clean)
./build-bazel-runtime.sh --native-only

# Managed-only rebuild
./build-bazel-runtime.sh --managed-only

# Run smoke test after build
./build-bazel-runtime.sh --smoke-test
```

Or use Bazel directly:

```bash
# Build everything, debug (default)
bazel build //...

# Per-component configuration
bazel build --config=clr_checked --config=libs_release //...

# Build just native runtime layout
bazel build //:runtime_native

# Build a specific library
bazel build //src/native/libs/System.Native:System.Native
```

### Output Layout

```
artifacts/bazel-dotnet/
├── dotnet                                          (host executable)
├── host/fxr/11.0.0/libhostfxr.so                  (framework resolver)
└── shared/Microsoft.NETCore.App/11.0.0/
    ├── libcoreclr.so                               (runtime + JIT, statically linked)
    ├── libhostpolicy.so                            (host policy)
    ├── libSystem.Native.so                         (+ 5 other native interop libs)
    ├── System.Private.CoreLib.dll                  (+ ~150 managed framework DLLs)
    └── Microsoft.NETCore.App.deps.json             (framework manifest)
```

### Running an App

```bash
DOTNET_ROOT=artifacts/bazel-dotnet artifacts/bazel-dotnet/dotnet <app.dll>
```

### Build Flow

1. **MSBuild** (one-time, cached): `./build.sh clr.corelib+libs -rc Release -lc Release` → produces System.Private.CoreLib.dll + managed framework DLLs
2. **Bazel** (fast incremental): builds libcoreclr.so, dotnet, hostfxr, hostpolicy, and 6 native interop libs (1,020 actions, ~77s clean)
3. **Assembly**: copies Bazel native outputs + MSBuild managed DLLs into the `dotnet` runtime directory layout

---

## 9. NetCoreApp Assembly Tracking

The NetCoreApp shared framework (`NetCoreAppLibrary.props`) contains 150
non-shim assemblies (plus 22 NetFxReference shims). Of the non-shim assemblies,
**145 are Bazel-built** and in the `impl_netcoreapp` aggregate; **5 still need
special support**. The `impl_netcoreapp` filegroup contains 154 total entries
(including 8 non-NetCoreApp extras and 1 NetFxReference shim: mscorlib).

### 9.1 Status Summary

| Category | Count | Description |
|----------|------:|-------------|
| ✅ In `impl_netcoreapp` | 154 | Built and aggregated (145 non-shim NetCoreApp + mscorlib shim + 8 non-NetCoreApp extras) |
| ❌ VB project | 1 | Microsoft.VisualBasic.Core — needs VB compiler in Bazel |
| ❌ Windows PNSE | 2 | System.IO.Pipes.AccessControl, System.Threading.AccessControl — need PNSE stub generation |
| ❌ Native deps | 1 | System.Net.Quic — needs msquic native library |
| ❌ Browser-only | 1 | System.Runtime.InteropServices.JavaScript — WASM/Browser platform only |
| ❌ NetFxRef shims | 21 | Legacy .NET Framework type-forwarder shims (System, System.Core, etc.) |


### 9.2 Remaining 5 Assemblies

| Library | Reason | Notes |
|---------|--------|-------|
| Microsoft.VisualBasic.Core | VB compiler | Needs VB compilation support in rules_dotnet |
| System.IO.Pipes.AccessControl | Windows PNSE | Needs PlatformNotSupportedException stub generator |
| System.Net.Quic | Native deps | Needs msquic native library for Linux build |
| System.Runtime.InteropServices.JavaScript | Browser-only | WASM/Browser platform, PNSE on other platforms |
| System.Threading.AccessControl | Windows PNSE | Needs PlatformNotSupportedException stub generator |

---

## Notes

- **No CMake files are modified or deleted** — Bazel files are purely additive
- Config headers (`pal_config.h`, `minipalconfig.h`, `config.h`, `pal_crypto_config.h`) live in platform-specific subdirectories under `bazel/` (e.g., `bazel/linux-glibc-x64/`). The directory name encodes the relevant dimensions (OS, libc, arch). Multi-platform support will add sibling directories and `select()` rules to pick the right one.
- Platform-specific libraries (Browser, Android, Apple) each need their own platform toolchains before they can be ported
- Clang is the default compiler (matching CMake). Override with `--repo_env=CC=gcc` if needed.
- Build commands (linux-x64):
  - **Full runtime (hybrid)**: `./build-bazel-runtime.sh` (or `--native-only` for fast C++ iteration)
  - **Everything**: `bazel build //... --config=clr_checked`
  - Native libs: `bazel build //src/native/libs/System.Native:System.Native //src/native/libs/System.IO.Compression.Native:System.IO.Compression.Native //src/native/libs/System.IO.Ports.Native:System.IO.Ports.Native //src/native/libs/System.Net.Security.Native:System.Net.Security.Native //src/native/libs/System.Globalization.Native:System.Globalization.Native //src/native/libs/System.Security.Cryptography.Native:System.Security.Cryptography.Native.OpenSsl`
  - Corehost: `bazel build //src/native/corehost:hostfxr //src/native/corehost:hostpolicy //src/native/corehost:dotnet //src/native/corehost:apphost //src/native/corehost:nethost`
  - **libcoreclr.so**: `bazel build //src/coreclr/dlls/mscoree/coreclr:libcoreclr.so`
  - **Managed libs**: `bazel build //src/libraries:impl_netcoreapp`
  - **Runtime layout**: `bazel build //:runtime_native`
  - **Core_Root (test runtime)**: `bazel build //:Core_Root`
- Test commands (linux-x64):
  - **All tests**: `bazel test //... --config=clr_checked`
  - **Library tests**: `bazel test //src/tests:all --config=clr_checked`

---

## NativeAOT Compilation Pipeline

### Overview

NativeAOT compiles .NET IL assemblies directly to native executables using the
ILC (IL Compiler). The MSBuild pipeline for this lives in
`src/coreclr/nativeaot/BuildIntegration/` (the `.targets` files ship inside
the SDK's runtime pack). The Bazel equivalent splits across two repos:

| Component | Location | Role |
|-----------|----------|------|
| ILC compiler (managed C#) | `src/coreclr/tools/aot/ILCompiler*` (this repo) | Build ILC from source |
| NativeAOT native runtime | `src/coreclr/nativeaot/BUILD.bazel` (this repo) | Static libs linked into final binary |
| `nativeaot_pack` rule | `src/coreclr/tools/aot/crossgen2/BUILD.bazel` (this repo) | Package ILC + framework + runtime libs |
| `nativeaot_binary` rule | `rules_dotnet` (`dotnet/private/rules/nativeaot/`) | Invoke ILC → link (ILC args + linker flags) |

### Architecture

```
nativeaot_pack (runtime repo)          nativeaot_binary (rules_dotnet)
┌─────────────────────────┐            ┌──────────────────────────────┐
│ ilc executable          │            │ Step 1: ILC invocation       │
│ framework assemblies    │───feeds───→│   RSP file with ~60 args     │
│ runtime static libs     │            │   → produces native .o       │
│ mibc profiles           │            │                              │
└─────────────────────────┘            │ Step 2: cc_toolchain link    │
                                       │   Unix or Windows flags      │
                                       │   → produces native exe      │
                                       └──────────────────────────────┘
```

**rules_dotnet owns the build logic** (ILC invocation and linker flags),
matching how the SDK owns the `.targets` files in MSBuild. The runtime repo
owns the compiler source and packaging. `extra_ilc_args` / `extra_linker_args`
attrs serve as escape hatches when ILC gains new flags before rules_dotnet is
updated.

### ILC BUILD Files (from source)

Building ILC from source requires four managed libraries plus the binary:

| Target | Sources | Key deps |
|--------|---------|----------|
| `ILCompiler.MetadataTransform` | ~50 files, `NATIVEFORMAT_PUBLICWRITER` define | ILCompiler.TypeSystem |
| `ILCompiler.Compiler` | ~547 files (largest), shared sources from `Common/` | TypeSystem, DependencyAnalysis, Diagnostics, MetadataTransform |
| `ILCompiler.RyuJit` | ~35 files from 4 locations | Compiler, MetadataTransform, TypeSystem |
| `ilc` (binary) | 5 local + 3 shared sources, Resources.resx | All above + System.CommandLine |

These depend on existing BUILD files: `ILCompiler.DependencyAnalysisFramework`,
`ILCompiler.TypeSystem`, `ILCompiler.ReadyToRun`, `ILCompiler.Diagnostics`.

### MSBuild Parity

The `nativeaot_binary` rule in rules_dotnet faithfully replicates the MSBuild
targets line-by-line:

**ILC args** (matching `Microsoft.NETCore.Native.targets` `WriteIlcRspFileForCompilation`):
- `--targetos`, `--targetarch`, `-o`, `-r:` (references), `--mibc:`
- `-O` / `--Ot` / `--Os` (optimization modes)
- `--dehydrate`, `--scanreflection`, `--methodbodyfolding:generic`
- `--stacktracedata`, `--resilient`, `--export-dynamic-symbol`
- `--feature:` switches, `--initassembly:`, `--directpinvoke:`, `--root:`
- RSP file via `use_param_file("@%s")` matching MSBuild's `WriteLinesToFile`

**Unix linker flags** (matching `Microsoft.NETCore.Native.Unix.targets`):
- Argument ordering: dependents before dependencies (single-pass linkers)
- Native libs: obj → bootstrapper → Runtime.WorkstationGC → eventpipe → stdc++compat → minipal
- System libs: `-ldl -lrt -lm -lz` (Linux), `-lobjc -lswiftCore` (Apple), `-licucore` (Apple, conditional)
- Frameworks: CoreFoundation, CryptoKit, Foundation, Network, Security, GSS
- Security: `--build-id=sha1`, PIE, RELRO, BIND_NOW, noexecstack
- Optional: `-gz=zlib`, `-fuse-ld=`, `-lstdc++`, static ICU, static OpenSSL, system brotli/zstd

**Windows linker flags** (matching `Microsoft.NETCore.Native.Windows.targets`):
- `/MERGE:.modules=.rdata`, `/MERGE:.managedcode=.text`
- `/INCREMENTAL:NO`, `/MANIFEST:NO`, `/DYNAMICBASE`, `/NXCOMPAT`
- Runtime libs: bootstrapper.lib, Runtime.WorkstationGC.lib, eventpipe-disabled.lib
- Windows SDK: advapi32, bcrypt, crypt32, iphlpapi, kernel32, mswsock, ntdll, ole32,
  oleaut32, user32, version, ws2_32, shell32, Synchronization.lib
- UCRT: api-ms-win-crt-*.lib (dynamic linking)
- Optional: `/CETCOMPAT`, `/guard:cf`, `/guard:ehcont`, `/safeseh` (x86), `/STACK:`

### Bootstrap Mode (TODO)

MSBuild supports `UseBootstrap=true` which:
1. Builds crossgen2 from source
2. NativeAOT-publishes it (`crossgen2_publish.csproj`)
3. Uses that native crossgen2 to compile System.Private.CoreLib

The Bazel infrastructure is partially in place:
- `crossgen_corelib` rule has a `native_crossgen2` attr
- `crossgen2-publish` nativeaot_binary target exists (or will exist)

**Not yet wired together.** Needs a `bool_flag` (e.g., `--//src/coreclr:bootstrap`)
that toggles `crossgen_corelib` between:
- **Default**: LKG SDK crossgen2 (fast, no circular dependency)
- **Bootstrap**: from-source `//src/coreclr/tools/aot/crossgen2:crossgen2-publish`

### NativeAOT Versioning (TODO)

Each .NET version (8/9/10) ships different ILC flags and linker libraries.
Currently only .NET 10 is targeted. When multiple versions need support,
add version awareness to `nativeaot_binary` via the `nativeaot_pack` provider
metadata (e.g., `runtime_version` field) with per-version flag conditionals.
The `extra_ilc_args` / `extra_linker_args` escape hatches cover the gap in
the meantime.

---

## macOS Test Limitations

On macOS, some tests that require access to the macOS Keychain do not work
under `bazel test`. This is because Bazel's test wrapper (`test-setup.sh`)
creates an isolated environment that does not have access to the user's
login session, which is required by the macOS Security framework APIs
(e.g., `SecKeychainCopyDefault`).

**Affected tests:** Tests in `System.Security.Cryptography.Pkcs` that call
`EnvelopedCms.Decrypt()` without providing explicit certificates attempt to
search the system certificate stores, which requires keychain access.

**Workaround:** These tests are marked with `[Trait("category", "RequiresKeychain")]`
and are excluded from `bazel test` runs via `-notrait "category=RequiresKeychain"`.

**Running excluded tests:** Use `bazel run` instead of `bazel test` for
individual tests that require keychain access:

```bash
# This works (inherits full user environment):
bazel run //src/libraries/System.Security.Cryptography.Pkcs/tests:System.Security.Cryptography.Pkcs.Tests

# This fails for keychain tests (isolated environment):
bazel test //src/libraries/System.Security.Cryptography.Pkcs/tests:System.Security.Cryptography.Pkcs.Tests
```

**Adding new keychain-dependent tests:** Mark them with the trait:

```csharp
[Fact]
[Trait("category", "RequiresKeychain")]
public void MyTestThatNeedsKeychain()
{
    // ...
}
```
