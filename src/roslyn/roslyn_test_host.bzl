"""Roslyn test host rule.

Assembles corerun, coreclr, System.Private.CoreLib, managed framework
assemblies, and native libraries into a flat directory that corerun
can execute .NET assemblies from.
"""

def _roslyn_test_host_impl(ctx):
    testhost = ctx.actions.declare_directory("roslyn_test_host")

    corerun = ctx.file.corerun
    coreclr = ctx.file.coreclr
    corelib = ctx.file.corelib
    managed = ctx.files.managed_assemblies + ctx.files.additional_assemblies
    native = ctx.files.native_libs

    all_inputs = [corerun, coreclr, corelib] + managed + native

    ctx.actions.run_shell(
        inputs = all_inputs,
        outputs = [testhost],
        command = """\
OUT="$1"
CORERUN="$2"
CORECLR="$3"
CORELIB="$4"
shift 4

cp -aL "$CORERUN" "$OUT/corerun"
cp -aL "$CORECLR" "$OUT/"
# crossgen output is System.Private.CoreLib.r2r.dll — rename it
cp -aL "$CORELIB" "$OUT/System.Private.CoreLib.dll"

for f in "$@"; do
    cp -afL "$f" "$OUT/"
done
""",
        arguments = [
            testhost.path,
            corerun.path,
            coreclr.path,
            corelib.path,
        ] + [f.path for f in managed + native],
        mnemonic = "BuildRoslynTestHost",
        progress_message = "Building Roslyn test host",
    )

    return [DefaultInfo(files = depset([testhost]))]

roslyn_test_host = rule(
    _roslyn_test_host_impl,
    doc = """Build a test host directory for Roslyn xunit tests.

    Assembles corerun + runtime into a flat directory that corerun can
    load .NET assemblies from directly (no dotnet host required).
    """,
    attrs = {
        "corerun": attr.label(
            doc = "The corerun host executable",
            mandatory = True,
            allow_single_file = True,
        ),
        "coreclr": attr.label(
            doc = "The coreclr shared library (libcoreclr.so or libcoreclr.dylib)",
            mandatory = True,
            allow_single_file = True,
        ),
        "corelib": attr.label(
            doc = "System.Private.CoreLib (crossgen R2R or impl)",
            mandatory = True,
            allow_single_file = True,
        ),
        "managed_assemblies": attr.label(
            doc = "Filegroup containing managed framework assemblies (impl_netcoreapp)",
            mandatory = True,
            allow_files = True,
        ),
        "native_libs": attr.label_list(
            doc = "Native libraries to include",
            allow_files = True,
        ),
        "additional_assemblies": attr.label_list(
            doc = "Additional managed assemblies not in the main filegroup (e.g. shims)",
            allow_files = True,
            default = [],
        ),
    },
)
