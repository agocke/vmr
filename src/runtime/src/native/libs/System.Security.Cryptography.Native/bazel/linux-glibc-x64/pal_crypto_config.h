#pragma once

// Hardcoded for linux-x64 (matching CMake configure results from
// System.Security.Cryptography.Native/configure.cmake).
//
// With FEATURE_DISTRO_AGNOSTIC_SSL, opensslshim.h overrides all of these to 1
// and provides forward declarations for missing functions, resolving them at
// runtime via dlopen/dlsym. The values below therefore don't affect the
// portable build — they only matter for non-portable builds that link directly
// against a specific OpenSSL version.
//
// When adding new platforms, create a per-platform copy of this file
// and use select() in BUILD.bazel to pick the right one.

#define HAVE_OPENSSL_EC2M 1
#define HAVE_OPENSSL_ALPN 1
#define HAVE_OPENSSL_CHACHA20POLY1305 1
#define HAVE_OPENSSL_SHA3_SQUEEZE 0
#define HAVE_OPENSSL_EVP_PKEY_SIGN_MESSAGE_INIT 0
#define HAVE_OPENSSL_ENGINE 1
