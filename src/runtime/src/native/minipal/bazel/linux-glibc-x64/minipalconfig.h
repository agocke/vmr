#ifndef HAVE_MINIPAL_MINIPALCONFIG_H
#define HAVE_MINIPAL_MINIPALCONFIG_H

// Hardcoded for linux-x64 (matching CMake configure results from
// src/native/minipal/configure.cmake).
//
// Probes system headers for feature availability (arc4random, getrandom,
// auxv, sysctl, clock functions, etc.).
//
// When adding new platforms, create a per-platform copy of this file
// and use select() in BUILD.bazel to pick the right one.
#define HAVE_ARC4RANDOM_BUF 0
#define HAVE_GETRANDOM 1
#define HAVE_AUXV_HWCAP_H 0
#define HAVE_HWPROBE_H 0
#define HAVE_RESOURCE_H 1
#define HAVE_O_CLOEXEC 1
#define HAVE_SYSCTLBYNAME 0
#define HAVE_CLOCK_MONOTONIC 1
#define HAVE_CLOCK_MONOTONIC_COARSE 1
#define HAVE_CLOCK_GETTIME_NSEC_NP 0
#define BIGENDIAN 0
#define HAVE_BCRYPT_H 0
#define HAVE_FSYNC 1

#endif
