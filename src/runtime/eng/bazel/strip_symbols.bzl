"""Strip debug symbols from shared libraries.

On Linux, matches CMake's strip_symbols() from eng/native/functions.cmake:
  objcopy --only-keep-debug  =>  .so.dbg
  objcopy --strip-debug --strip-unneeded  =>  stripped .so
  objcopy --add-gnu-debuglink  =>  link .so to .dbg

On macOS, uses dsymutil + strip since objcopy is not available.
"""

def strip_symbols(name, src, visibility = None):
    """Produces a stripped shared library and a separate debug symbol file.

    Outputs:
        {name}_stripped.so       - stripped shared library
        {name}_stripped.so.dbg   - debug symbols (Linux) or empty file (macOS; dSYM is a directory)
    """
    native.genrule(
        name = name,
        srcs = [src],
        outs = [name + "_stripped.so", name + "_stripped.so.dbg"],
        cmd = " && ".join([
            "cp $< $(location {name}_stripped.so)".format(name = name),
            "chmod u+w $(location {name}_stripped.so)".format(name = name),
            "if [ \"$$(uname)\" = \"Darwin\" ]; then " +
                "dsymutil $(location {name}_stripped.so) 2>/dev/null || true && ".format(name = name) +
                "strip -x $(location {name}_stripped.so) && ".format(name = name) +
                "touch $(location {name}_stripped.so.dbg)".format(name = name) +
            "; else " +
                "$(OBJCOPY) --only-keep-debug $(location {name}_stripped.so) $(location {name}_stripped.so.dbg) && ".format(name = name) +
                "$(OBJCOPY) --strip-debug --strip-unneeded $(location {name}_stripped.so) && ".format(name = name) +
                "$(OBJCOPY) --add-gnu-debuglink=$(location {name}_stripped.so.dbg) $(location {name}_stripped.so)".format(name = name) +
            "; fi",
        ]),
        toolchains = ["@rules_cc//cc:current_cc_toolchain"],
        visibility = visibility,
    )
