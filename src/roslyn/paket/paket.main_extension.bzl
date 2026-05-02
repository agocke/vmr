"Generated"

load(":paket.main.bzl", _main = "main")

def _roslyn_impl(module_ctx):
    _main()
    return module_ctx.extension_metadata(reproducible = True)

roslyn_extension = module_extension(
    implementation = _roslyn_impl,
)
