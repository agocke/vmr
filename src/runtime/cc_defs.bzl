# Platform defines matching CMake configurecompiler.cmake.
# Used by native cc_library targets via local_defines = PLATFORM_DEFINES.
#
# This file is intentionally kept free of @rules_dotnet loads so that
# changes to the C# tooling / rules_dotnet version don't invalidate the
# Bazel analysis cache for C++ targets.
PLATFORM_DEFINES = [
    # configurecompiler.cmake — unconditional on Unix
    "DISABLE_CONTRACTS",
] + select({
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
        # configurecompiler.cmake — macOS platform defines
        "_XOPEN_SOURCE",
        "_DARWIN_C_SOURCE",
        "__DARWIN_NON_CANCELABLE=1",
        # src/native/libs/CMakeLists.txt — macOS networking
        "__APPLE_USE_RFC_3542",
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
    ],
})
