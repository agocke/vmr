#pragma once

// Hardcoded for linux-x64 (matching CMake configure results from
// System.Globalization.Native/configure.cmake).
//
// Probes system ICU for:
//   - UDAT_STANDALONE_SHORTER_WEEKDAYS enum value in <unicode/udat.h>
//   - ucol_clone symbol in libicui18n
//
// When adding new platforms, create a per-platform copy of this file
// and use select() in BUILD.bazel to pick the right one.

#define HAVE_UDAT_STANDALONE_SHORTER_WEEKDAYS 1
#define HAVE_UCOL_CLONE 1
