#pragma once

// Hardcoded for darwin-arm64 (matching CMake configure results from
// System.Globalization.Native/configure.cmake).
//
// macOS ships ICU as part of the system; these values reflect the
// system ICU version available on macOS.

#define HAVE_UDAT_STANDALONE_SHORTER_WEEKDAYS 1
#define HAVE_UCOL_CLONE 1
