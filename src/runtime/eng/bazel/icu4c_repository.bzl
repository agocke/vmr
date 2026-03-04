# Repository rule to find ICU4C headers on macOS (Homebrew) or Linux (system).

def _icu4c_repository_impl(rctx):
    # Detect the OS
    os_name = rctx.os.name.lower()

    if "mac" in os_name or "darwin" in os_name:
        # macOS: use Homebrew ICU4C
        # Check both arm64 and x64 Homebrew paths
        arm64_path = "/opt/homebrew/opt/icu4c"
        x64_path = "/usr/local/opt/icu4c"

        if rctx.path(arm64_path).exists:
            icu_path = arm64_path
        elif rctx.path(x64_path).exists:
            icu_path = x64_path
        else:
            fail("ICU4C not found. Install with: brew install icu4c")

        # Symlink the include directory
        rctx.symlink(icu_path + "/include", "include")
    else:
        # Linux: ICU headers are in /usr/include (system-wide)
        # Create include/unicode symlink pointing to /usr/include/unicode
        rctx.symlink("/usr/include/unicode", "include/unicode")

    # Write the BUILD file
    rctx.file("BUILD.bazel", """
load("@rules_cc//cc:defs.bzl", "cc_library")

cc_library(
    name = "headers",
    hdrs = glob(["include/unicode/*.h"]),
    includes = ["include"],
    visibility = ["//visibility:public"],
)
""")

icu4c_repository = repository_rule(
    implementation = _icu4c_repository_impl,
    local = True,
    doc = "Locates ICU4C headers on macOS (Homebrew) or Linux (system).",
)
