# Platform defines matching CMake configurecompiler.cmake.
# Used by native cc_library targets via local_defines = PLATFORM_DEFINES.
#
# This file is intentionally kept free of @rules_dotnet loads so that
# changes to the C# tooling / rules_dotnet version don't invalidate the
# Bazel analysis cache for C++ targets.
PLATFORM_DEFINES = [
    "DISABLE_CONTRACTS",
] + select({
    "//:macos_arm64": [
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
    "//:macos_x64": [
        "HOST_64BIT",
        "HOST_AMD64",
        "HOST_UNIX",
        "HOST_APPLE",
        "HOST_OSX",
        "TARGET_64BIT",
        "TARGET_AMD64",
        "TARGET_UNIX",
        "TARGET_APPLE",
        "TARGET_OSX",
        "_XOPEN_SOURCE",
        "_DARWIN_C_SOURCE",
        "__DARWIN_NON_CANCELABLE=1",
        "__APPLE_USE_RFC_3542",
    ],
    "@platforms//os:windows": [
        "HOST_64BIT",
        "HOST_AMD64",
        "HOST_WINDOWS",
        "TARGET_64BIT",
        "TARGET_AMD64",
        "TARGET_WINDOWS",
        # configurecompiler.cmake — Windows platform defines
        "WIN32",
        "_WIN32",
        "_WIN64",
        "UNICODE",
        "_UNICODE",
        "_CRT_SECURE_NO_WARNINGS",
        "_CRT_NONSTDC_NO_WARNINGS",
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

# Platform-specific compiler flags matching CMake configurecompiler.cmake.
# Used by native cc_library targets via copts = PLATFORM_COPTS.
#
# On Unix, baseline flags (language standards, warnings) are set in
# .bazelrc via build:unix because --conlyopt/--cxxopt/--per_file_copt
# have no BUILD-file equivalent.  PLATFORM_COPTS is empty on Unix.
#
# On Windows, MSVC flags live here so targets opt in explicitly.
PLATFORM_COPTS = select({
    "@platforms//os:windows": [
        # Exception handling & code generation
        "/EHsc",
        "/GS",
        "/Oi",
        "/Oy-",
        "/Gm-",
        "/Gy",
        "/fp:precise",
        "/GR-",
        "/FC",
        "/Zp8",
        # Security
        "/guard:cf",
        # Conformance
        "/Zc:strictStrings",
        "/Zc:wchar_t",
        "/Zc:inline",
        "/Zc:forScope",
        "/source-charset:utf-8",
        # Warnings
        "/W4",
        "/wd4005",
        "/wd4100",
        "/wd4127",
        "/wd4131",
        "/wd4189",
        "/wd4200",
        "/wd4201",
        "/wd4206",
        "/wd4239",
        "/wd4245",
        "/wd4291",
        "/wd4310",
        "/wd4324",
        "/wd4366",
        "/wd4456",
        "/wd4457",
        "/wd4458",
        "/wd4459",
        "/wd4463",
        "/wd4505",
        "/wd4702",
        "/wd4706",
        "/wd4733",
        "/wd4815",
        "/wd4838",
        "/wd4918",
        "/wd4960",
        "/wd4961",
        "/wd5105",
        "/wd5205",
    ],
    "//conditions:default": [],
})

# Platform-specific linker flags matching CMake configurecompiler.cmake.
# Used by native cc_library / cc_binary / cc_shared_library targets
# via linkopts or user_link_flags.
PLATFORM_LINKOPTS = select({
    "@platforms//os:windows": [
        "/MANIFEST:NO",
        "/LARGEADDRESSAWARE",
        "/DEBUGTYPE:CV,FIXUP",
        "/PDBCOMPRESS",
        "/DEPENDENTLOADFLAG:0x800",
        "/STACK:0x180000",
        "/guard:cf",
    ],
    "//conditions:default": [],
})
