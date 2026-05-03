"""Rule to remap data file paths for test data directories.

MSBuild uses Link attributes to remap paths (e.g., Xslt/TestFiles/** → TestFiles/**).
This rule creates symlinks at the target paths so library_test data handling
places them correctly next to the test DLL.
"""

def _remap_data_impl(ctx):
    outputs = []
    pkg_prefix = ctx.label.package + "/"
    for src in ctx.files.srcs:
        sp = src.short_path
        if sp.startswith(pkg_prefix):
            rel = sp[len(pkg_prefix):]
        else:
            # Cross-package file: use the full_strip_prefix if set,
            # otherwise fall back to basename
            if ctx.attr.full_strip_prefix and sp.startswith(ctx.attr.full_strip_prefix):
                rel = sp[len(ctx.attr.full_strip_prefix):]
            else:
                rel = src.basename

        if rel.startswith(ctx.attr.strip_prefix):
            new_rel = ctx.attr.target_prefix + rel[len(ctx.attr.strip_prefix):]
        else:
            new_rel = ctx.attr.target_prefix + rel

        out = ctx.actions.declare_file(new_rel)
        ctx.actions.symlink(output = out, target_file = src)
        outputs.append(out)

    return [DefaultInfo(files = depset(outputs))]

remap_data = rule(
    implementation = _remap_data_impl,
    attrs = {
        "srcs": attr.label_list(allow_files = True),
        "strip_prefix": attr.string(default = ""),
        "target_prefix": attr.string(default = ""),
        "full_strip_prefix": attr.string(
            default = "",
            doc = "Prefix to strip from cross-package file short_paths (e.g. 'src/libraries/Foo/').",
        ),
    },
)
