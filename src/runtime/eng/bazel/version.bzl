"""Product version constants for the .NET runtime Bazel build.

Keep in sync with eng/Versions.props.
"""

PRODUCT_VERSION = "10.0.4"

# Version suffix for dev builds, matching MSBuild's versioning.
# Set to "" for release builds.
PRODUCT_VERSION_LABEL = "dev"

# Full version string used in archive names and directory paths.
PRODUCT_VERSION_FULL = PRODUCT_VERSION + ("-" + PRODUCT_VERSION_LABEL if PRODUCT_VERSION_LABEL else "")
