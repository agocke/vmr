# Shared constants for NativeAOT Bazel builds.
# Derived from src/coreclr/nativeaot/CMakeLists.txt and
# src/coreclr/nativeaot/Runtime/CMakeLists.txt.

# Shared defines (identical across all supported platforms).
NATIVEAOT_COMMON_DEFINES = [
    "FEATURE_NATIVEAOT",
    "NATIVEAOT",
    "VERIFY_HEAP",
    "FEATURE_BASICFREEZE",
    "FEATURE_CONSERVATIVE_GC",
    "FEATURE_CACHED_INTERFACE_DISPATCH",
    "_LIB",
    "FEATURE_HIJACK",
    "FEATURE_PERFTRACING",
    "FEATURE_EVENT_TRACE=1",
    "_LIBUNWIND_DISABLE_ZERO_COST_APIS=1",
    "_LIBUNWIND_IS_NATIVE_ONLY",
]

NATIVEAOT_DEFINES = NATIVEAOT_COMMON_DEFINES + select({
    "@platforms//os:macos": [
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
        "FEATURE_EMULATE_SINGLESTEP",
        "OSX_ARM64_ABI",
    ],
    "@platforms//os:linux": [
        "_GNU_SOURCE",
        "HOST_64BIT",
        "HOST_AMD64",
        "HOST_UNIX",
        "TARGET_64BIT",
        "TARGET_AMD64",
        "TARGET_UNIX",
        "TARGET_LINUX",
        "FEATURE_USE_SOFTWARE_WRITE_WATCH_FOR_GC_HEAP",
        "FEATURE_MANUALLY_MANAGED_CARD_BUNDLES",
        "FEATURE_READONLY_GS_COOKIE",
        "UNIX_AMD64_ABI",
        "FEATURE_RX_THUNKS",
    ],
})

_NATIVEAOT_COMMON_COPTS = [
    "-fno-exceptions",
    "-fno-asynchronous-unwind-tables",
    "-Wno-invalid-offsetof",
    "-Wno-class-memaccess",
    "-Wno-conversion-null",
    "-Wno-pointer-arith",
    "-Wno-misleading-indentation",
    "-Wno-stringop-overflow",
    "-Wno-restrict",
    "-Wno-unused-but-set-parameter",
    "-Isrc/coreclr/nativeaot/Runtime",
    "-Isrc/coreclr/nativeaot/Runtime/inc",
    "-Isrc/coreclr/nativeaot/Runtime/unix",
    "-Isrc/coreclr/gc",
    "-Isrc/coreclr/gc/env",
    "-Isrc/coreclr/runtime",
    "-Isrc/coreclr/pal/inc/rt",
    "-Isrc/native",
    "-Isrc/native/inc",
    "-Isrc/native/external/llvm-libunwind/include",
]

NATIVEAOT_COPTS = _NATIVEAOT_COMMON_COPTS + select({
    "@platforms//os:macos": [
        "-Isrc/coreclr/nativeaot/Runtime/arm64",
    ],
    "@platforms//os:linux": [
        "-Isrc/coreclr/nativeaot/Runtime/amd64",
        "-mcx16",
    ],
})
