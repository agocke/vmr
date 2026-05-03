"Generated"

load(":paket.illink.bzl", _illink = "illink")

def _illink_impl(module_ctx):
    _illink()
    return module_ctx.extension_metadata(reproducible = True)

illink_extension = module_extension(
    implementation = _illink_impl,
)
