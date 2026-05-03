# Shared native library definitions for debug/release configuration.
#
# Native code follows clr_config (not libs_config) because it is part of
# the runtime, not the managed libraries.  Default: clr_config=checked
# (assertions enabled + release-level optimization).
#
# Usage in BUILD files:
#   load("//src/native:native_defs.bzl", "NATIVE_CONFIG_DEFINES", "NATIVE_CONFIG_COPTS")
#   cc_library(
#       ...
#       copts = NATIVE_CONFIG_COPTS + [...],
#       local_defines = NATIVE_CONFIG_DEFINES,
#   )

# Debug/release defines matching CMake's per-config behavior.
#   debug   → DEBUG, _DEBUG  (assertions, debug CRT macros)
#   checked → DEBUG, _DEBUG  (assertions, debug CRT macros)
#   release → NDEBUG         (no assertions)
NATIVE_CONFIG_DEFINES = select({
    "//:clr_release": [
        "NDEBUG",
    ],
    "//conditions:default": [
        "DEBUG",
        "_DEBUG",
    ],
})

# Optimization and CRT flags matching CMake's configurecompiler.cmake.
#
# On Windows the CRT must match the debug defines:
#   /MTd  — static debug CRT, required when _DEBUG is defined
#   /MT   — static release CRT, used with NDEBUG
# On Unix, only optimization flags are needed (-O2 for checked/release).
#
# Bazel's default dynamic CRT (/MD) is disabled via
# --features=-dynamic_link_msvcrt in .bazelrc.
NATIVE_CONFIG_COPTS = select({
    "//:clr_debug_windows": ["/MTd"],
    "//:clr_checked_windows": ["/MTd", "/O2"],
    "//:clr_release_windows": ["/MT", "/O2"],
    "//:clr_debug": [],
    "//:clr_checked": ["-O2"],
    "//:clr_release": ["-O2"],
})
