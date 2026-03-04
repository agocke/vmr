# Shared native library definitions for debug/release configuration.
#
# Usage in BUILD files:
#   load("//src/native:native_defs.bzl", "NATIVE_CONFIG_DEFINES", "NATIVE_CONFIG_COPTS")
#   cc_library(
#       ...
#       copts = NATIVE_CONFIG_COPTS + [...],
#       local_defines = NATIVE_CONFIG_DEFINES,
#   )

NATIVE_CONFIG_DEFINES = select({
    "//:libs_debug": [
        "DEBUG",
        "_DEBUG",
    ],
    "//:libs_release": [
        "NDEBUG",
    ],
})

# Optimization flags matching CMake's per-config behavior.
# Libraries default to debug (no optimization); release gets -O2.
# Native components linked into CoreCLR also get -O2 via
# CLR_CONFIG_COPTS in their coreclr BUILD files.
NATIVE_CONFIG_COPTS = select({
    "//:libs_debug": [],
    "//:libs_release": ["-O2"],
})
