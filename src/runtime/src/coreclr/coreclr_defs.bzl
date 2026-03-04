# Shared constants for CoreCLR Bazel builds.
# Import into BUILD.bazel files with:
#   load("//src/coreclr:coreclr_defs.bzl", "CORECLR_DEFINES", "CORECLR_COPTS")
#   load("//src/coreclr:coreclr_defs.bzl", "CLR_CONFIG_DEFINES", "CLR_CONFIG_COPTS")

# --- Feature defines ---
# Derived from clrdefinitions.cmake and clrfeatures.cmake.
# Each platform list is the full set of defines for that platform.

# Shared defines (identical across all supported platforms).
_CORECLR_COMMON_DEFINES = [
    "FEATURE_CORECLR",
    "FEATURE_JIT",
    "DEBUGGING_SUPPORTED",
    "PROFILING_SUPPORTED",
    "FEATURE_METADATA_UPDATER",
    "FEATURE_REMAP_FUNCTION",
    "FEATURE_COLLECTIBLE_TYPES",
    "FEATURE_BASICFREEZE",
    "FEATURE_DBGIPC_TRANSPORT_DI",
    "FEATURE_DBGIPC_TRANSPORT_VM",
    "FEATURE_DEFAULT_INTERFACES",
    "FEATURE_EVENT_TRACE=1",
    "FEATURE_PERFTRACING=1",
    "FEATURE_GDBJIT_LANGID_CS",
    "FEATURE_HIJACK",
    "FEATURE_PERFMAP",
    "FEATURE_PAL_ANSI",
    "FEATURE_MULTICOREJIT",
    "FEATURE_READYTORUN",
    "FEATURE_PROFAPI_ATTACH_DETACH",
    "FEATURE_REMOTE_PROC_MEM",
    "FEATURE_SVR_GC",
    "FEATURE_SYMDIFF",
    "FEATURE_CODE_VERSIONING",
    "FEATURE_TIERED_COMPILATION=1",
    "FEATURE_PGO",
    "FEATURE_USE_ASM_GC_WRITE_BARRIERS",
    "FEATURE_USE_SOFTWARE_WRITE_WATCH_FOR_GC_HEAP",
    "FEATURE_MANUALLY_MANAGED_CARD_BUNDLES",
    "_SECURE_SCL=0",
    "UNICODE",
    "_UNICODE",
    "FEATURE_REJIT=1",
    "FEATURE_DBGIPC=1",
    "FEATURE_STANDALONE_GC=1",
    "FEATURE_SINGLE_FILE_DIAGNOSTICS=1",
    "FEATURE_COMWRAPPERS=1",
    "FEATURE_VIRTUAL_STUB_DISPATCH=1",
    "FEATURE_CORECLR_FLUSH_INSTRUCTION_CACHE_TO_PROTECT_STUB_READS=1",
    "FEATURE_EH_FUNCLETS",
    "FEATURE_PORTABLE_SHUFFLE_THUNKS",
    "FEATURE_INSTANTIATINGSTUB_AS_IL",
    "FEATURE_STATICALLY_LINKED",
]

CORECLR_DEFINES = _CORECLR_COMMON_DEFINES + select({
    "@platforms//os:macos": [
        # Platform (configurecompiler.cmake)
        "HOST_64BIT",
        "HOST_ARM64",
        "HOST_UNIX",
        "HOST_APPLE",
        "HOST_OSX",
        "TARGET_64BIT",
        "TARGET_ARM64",
        "TARGET_UNIX",
        "TARGET_APPLE",
        "TARGET_OSX",
        # clrdefinitions.cmake — ARM64-specific
        "FEATURE_EMULATE_SINGLESTEP",
        "OSX_ARM64_ABI",
        # configurecompiler.cmake — macOS platform defines
        "_XOPEN_SOURCE",
        "_DARWIN_C_SOURCE",
        "__DARWIN_NON_CANCELABLE=1",
        # clrfeatures.cmake — macOS-only features
        "FEATURE_OBJCMARSHAL",
    ],
    "@platforms//os:linux": [
        # Platform (configurecompiler.cmake)
        "_GNU_SOURCE",
        "HOST_64BIT",
        "HOST_AMD64",
        "HOST_UNIX",
        "TARGET_64BIT",
        "TARGET_AMD64",
        "TARGET_UNIX",
        "TARGET_LINUX",
        # clrdefinitions.cmake — AMD64-specific
        "UNIX_AMD64_ABI",
        "UNIX_AMD64_ABI_ITF",
        # clrfeatures.cmake — Linux-only features
        "FEATURE_EVENTSOURCE_XPLAT=1",
    ],
})

# --- Global include paths for all coreclr components ---
# Matches include_directories() from src/coreclr/CMakeLists.txt (Unix path).

CORECLR_COPTS = [
    # Warning suppression matching configurecompiler.cmake + src/coreclr/CMakeLists.txt for GCC C++
    "-Wno-invalid-offsetof",
    "-Wno-class-memaccess",
    "-Wno-conversion-null",
    "-Wno-pointer-arith",
    "-Wno-misleading-indentation",
    "-Wno-stringop-overflow",
    "-Wno-restrict",
    # Include paths (matching include_directories from src/coreclr/CMakeLists.txt)
    "-Isrc/coreclr/inc",
    "-Isrc/coreclr/pal/inc",
    "-Isrc/coreclr/pal/inc/rt",
    "-Isrc/coreclr/pal/src/safecrt",
    "-Isrc/coreclr/pal/prebuilt/inc",
    "-Isrc/coreclr/debug/inc",
    "-Isrc/coreclr/debug/inc/dump",
    "-Isrc/coreclr/md/inc",
    "-Isrc/coreclr/hosts/inc",
    "-Isrc/coreclr/interpreter",
    "-Isrc/coreclr/classlibnative/bcltype",
    "-Isrc/coreclr/classlibnative/inc",
    "-Isrc/coreclr/minipal",
    "-Isrc/native",
    "-Isrc/native/inc",
] + select({
    "@platforms//cpu:arm64": ["-Isrc/coreclr/debug/inc/arm64"],
    "@platforms//cpu:x86_64": ["-Isrc/coreclr/debug/inc/amd64"],
})

# --- Debug/checked/release defines ---
# Matches CMake's per-config compile_definitions from configurecompiler.cmake.
#
# CLR_BASE_CONFIG_DEFINES: base defines from configurecompiler.cmake (lines 60-62)
#   that apply to ALL coreclr targets, including NativeAOT.
#
# CLR_CONFIG_DEFINES: full set including clrdefinitions.cmake additions
#   (FEATURE_INTERPRETER, FEATURE_JAVAMARSHAL) that only apply to coreclr
#   proper (not NativeAOT — CMake includes clrdefinitions.cmake AFTER
#   add_subdirectory(nativeaot)).
#
# Standalone GC targets must NOT use either — they are always retail and get
# their own defines via _GC_STANDALONE_DEFINES in gc/BUILD.bazel.

_BASE_DEBUG = [
    "DEBUG",
    "_DEBUG",
    "_DBG",
    "DISABLE_CONTRACTS",
]

_BASE_RELEASE = [
    "NDEBUG",
    "DISABLE_CONTRACTS",
]

CLR_BASE_CONFIG_DEFINES = select({
    "//:clr_debug": _BASE_DEBUG + [
        "BUILDENV_DEBUG=1",
        "URTBLDENV_FRIENDLY=Debug",
    ],
    "//:clr_checked": _BASE_DEBUG + [
        "BUILDENV_CHECKED=1",
        "URTBLDENV_FRIENDLY=Checked",
    ],
    "//:clr_release": _BASE_RELEASE + [
        "URTBLDENV_FRIENDLY=Retail",
    ],
})

CLR_CONFIG_DEFINES = select({
    "//:clr_debug": _BASE_DEBUG + [
        "BUILDENV_DEBUG=1",
        "URTBLDENV_FRIENDLY=Debug",
        "FEATURE_INTERPRETER",
        "FEATURE_JAVAMARSHAL",
    ],
    "//:clr_checked": _BASE_DEBUG + [
        "BUILDENV_CHECKED=1",
        "URTBLDENV_FRIENDLY=Checked",
        "FEATURE_INTERPRETER",
        "FEATURE_JAVAMARSHAL",
    ],
    "//:clr_release": _BASE_RELEASE + [
        "URTBLDENV_FRIENDLY=Retail",
    ],
})

# --- Debug/checked/release copts ---
# Flags that vary by config but aren't -D defines (e.g. optimization level).
# Checked and release both need -O2.  Previously release relied on Bazel's
# global -c opt, but that also switches library C# from DEBUG to RELEASE,
# which is wrong for the "release CLR + debug libs" CI configuration.
CLR_CONFIG_COPTS = select({
    "//:clr_debug": [],
    "//:clr_checked": ["-O2"],
    "//:clr_release": ["-O2"],
})
