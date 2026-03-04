// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Hardcoded for darwin-arm64 (Bazel build).

#ifndef PAL_HOST_CONFIGURE_H_INCLUDED
#define PAL_HOST_CONFIGURE_H_INCLUDED

#define REPO_COMMIT_HASH "bazel-build"

#define FALLBACK_HOST_OS "osx"
#define CURRENT_OS_NAME "osx"
#define CURRENT_ARCH_NAME "arm64"

#define HOST_POLICY_PKG_NAME "runtime.osx-arm64.Microsoft.NETCore.DotNetHostPolicy"
#define HOST_POLICY_PKG_REL_DIR "runtime.osx-arm64/native"

#endif // PAL_HOST_CONFIGURE_H_INCLUDED
