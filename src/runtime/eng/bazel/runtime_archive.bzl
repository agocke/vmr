"""Rule to assemble a .NET runtime directory layout matching the MSBuild packs output.

Produces a tree artifact with the standard runtime layout:
    ./dotnet                                            # host executable
    ./LICENSE.txt                                       # license
    ./ThirdPartyNotices.txt                             # third-party notices
    ./host/fxr/{version}/libhostfxr.so                  # framework resolver
    ./shared/Microsoft.NETCore.App/{version}/            # shared framework
        libcoreclr.so, libclrjit.so, ...                    # native runtime
        System.Private.CoreLib.dll, System.Runtime.dll, ... # managed framework
        Microsoft.NETCore.App.deps.json                     # framework manifest
        Microsoft.NETCore.App.runtimeconfig.json            # runtime config

To produce a tar.gz archive, compose with tar() from @aspect_bazel_lib//lib:tar.bzl.
"""

def _runtime_staging_impl(ctx):
    version = ctx.attr.version
    output = ctx.actions.declare_directory(ctx.label.name)

    fxr_dir = "host/fxr/{}".format(version)
    fx_dir = "shared/Microsoft.NETCore.App/{}".format(version)

    inputs = []
    commands = [
        'set -euo pipefail',
        'mkdir -p "{out}/{fxr}" "{out}/{fx}"'.format(out = output.path, fxr = fxr_dir, fx = fx_dir),
    ]

    # Root files: dotnet host
    for dep in ctx.attr.root_files:
        for f in dep.files.to_list():
            inputs.append(f)
            commands.append('cp "{src}" "{out}/{name}"'.format(
                src = f.path,
                out = output.path,
                name = f.basename,
            ))
            commands.append('chmod +x "{out}/{name}"'.format(out = output.path, name = f.basename))

    # License files (with rename support)
    for src_label, dest_name in ctx.attr.license_files.items():
        for f in src_label.files.to_list():
            inputs.append(f)
            commands.append('cp "{src}" "{out}/{name}"'.format(
                src = f.path,
                out = output.path,
                name = dest_name,
            ))

    # Host FXR files
    for dep in ctx.attr.fxr_files:
        for f in dep.files.to_list():
            inputs.append(f)
            commands.append('cp "{src}" "{out}/{fxr}/{name}"'.format(
                src = f.path,
                out = output.path,
                fxr = fxr_dir,
                name = f.basename,
            ))

    # Native framework files (shared libs, executables)
    for dep in ctx.attr.framework_native_files:
        for f in dep.files.to_list():
            inputs.append(f)
            commands.append('cp "{src}" "{out}/{fx}/{name}"'.format(
                src = f.path,
                out = output.path,
                fx = fx_dir,
                name = f.basename,
            ))

    # Managed framework files (DLLs)
    # Process renames first to build the rename map
    rename_map = {}
    for src_name, dest_name in ctx.attr.rename_files.items():
        rename_map[src_name] = dest_name

    exclude_set = {name: True for name in ctx.attr.exclude_managed_files}

    for dep in ctx.attr.framework_managed_files:
        for f in dep.files.to_list():
            if f.extension == "dll":
                if f.basename in exclude_set:
                    continue
                inputs.append(f)
                dest_name = rename_map.get(f.basename, f.basename)
                # Use cp -f to allow later entries to overwrite earlier ones
                # (e.g. R2R CoreLib overwrites IL CoreLib).
                commands.append('cp -f "{src}" "{out}/{fx}/{name}"'.format(
                    src = f.path,
                    out = output.path,
                    fx = fx_dir,
                    name = dest_name,
                ))

    # Config/data files placed directly into the framework directory
    for dep in ctx.attr.framework_data_files:
        for f in dep.files.to_list():
            inputs.append(f)
            commands.append('cp "{src}" "{out}/{fx}/{name}"'.format(
                src = f.path,
                out = output.path,
                fx = fx_dir,
                name = f.basename,
            ))

    ctx.actions.run_shell(
        inputs = inputs,
        outputs = [output],
        command = "\n".join(commands),
    )

    return [DefaultInfo(files = depset([output]))]

def _runtime_archive_impl(ctx):
    version = ctx.attr.version
    rid = ctx.attr.rid
    staging = ctx.attr.staging[DefaultInfo].files.to_list()[0]
    deps_json = ctx.file.deps_json

    output = ctx.actions.declare_file(
        "dotnet-runtime-{}-{}.tar.gz".format(version, rid),
    )

    fx_dir = "shared/Microsoft.NETCore.App/{}".format(version)

    ctx.actions.run_shell(
        inputs = [staging, deps_json],
        outputs = [output],
        command = """
            set -euo pipefail
            STAGING="{staging}"
            DEPS="{deps}"
            OUT="{out}"
            WORK=$(mktemp -d)
            trap "rm -rf $WORK" EXIT
            cp -rL "$STAGING" "$WORK/archive"
            cp "$DEPS" "$WORK/archive/{fx_dir}/Microsoft.NETCore.App.deps.json"
            find "$WORK/archive" -type f -exec chmod 644 {{}} +
            find "$WORK/archive/shared" -type f \\( {native_pattern} -o -name "createdump" \\) -exec chmod 755 {{}} +
            chmod 755 "$WORK/archive/dotnet"
            tar czf "$OUT" -C "$WORK/archive" .
        """.format(
            staging = staging.path,
            deps = deps_json.path,
            out = output.path,
            fx_dir = fx_dir,
            native_pattern = ctx.attr.native_lib_pattern,
        ),
    )

    return [DefaultInfo(files = depset([output]))]

runtime_archive = rule(
    implementation = _runtime_archive_impl,
    attrs = {
        "version": attr.string(
            mandatory = True,
            doc = "Product version string for the archive filename and internal paths (e.g. '10.0.4-dev').",
        ),
        "rid": attr.string(
            mandatory = True,
            doc = "Runtime identifier for the archive filename (e.g. 'linux-x64').",
        ),
        "staging": attr.label(
            mandatory = True,
            doc = "The runtime_staging target providing the directory layout.",
        ),
        "deps_json": attr.label(
            mandatory = True,
            allow_single_file = True,
            doc = "The generated deps.json file to inject into the archive.",
        ),
        "native_lib_pattern": attr.string(
            mandatory = True,
            doc = "find(1) -name pattern for native shared libraries (e.g. '*.so' or '*.dylib').",
        ),
    },
)

runtime_staging = rule(
    implementation = _runtime_staging_impl,
    attrs = {
        "version": attr.string(
            mandatory = True,
            doc = "Product version string used in paths and archive name (e.g. '10.0.4-dev').",
        ),
        "root_files": attr.label_list(
            allow_files = True,
            doc = "Files at the archive root (e.g. dotnet host executable).",
        ),
        "license_files": attr.label_keyed_string_dict(
            allow_files = True,
            doc = "License files mapped to their archive names (e.g. LICENSE.TXT -> LICENSE.txt).",
        ),
        "fxr_files": attr.label_list(
            allow_files = True,
            doc = "Files under host/fxr/{version}/.",
        ),
        "framework_native_files": attr.label_list(
            allow_files = True,
            doc = "Native files under shared/Microsoft.NETCore.App/{version}/.",
        ),
        "framework_managed_files": attr.label_list(
            allow_files = True,
            doc = "Managed DLLs under shared/Microsoft.NETCore.App/{version}/ (only .dll files are included).",
        ),
        "exclude_managed_files": attr.string_list(
            doc = "DLL basenames to exclude from framework_managed_files (e.g. assemblies in impl_netcoreapp not in the runtime pack).",
        ),
        "rename_files": attr.string_dict(
            doc = "Map of source filename -> destination filename for renames (e.g. R2R CoreLib).",
        ),
        "framework_data_files": attr.label_list(
            allow_files = True,
            doc = "Data/config files under shared/Microsoft.NETCore.App/{version}/ (deps.json, runtimeconfig.json).",
        ),
    },
)
