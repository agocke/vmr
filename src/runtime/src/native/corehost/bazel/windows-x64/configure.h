// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Hardcoded for windows-x64 (Bazel build).

#ifndef PAL_HOST_CONFIGURE_H_INCLUDED
#define PAL_HOST_CONFIGURE_H_INCLUDED

#define REPO_COMMIT_HASH "bazel-build"

#define FALLBACK_HOST_OS "win"
#define CURRENT_OS_NAME "win"
#define CURRENT_ARCH_NAME "x64"

#define HOST_POLICY_PKG_NAME "runtime.win-x64.Microsoft.NETCore.DotNetHostPolicy"
#define HOST_POLICY_PKG_REL_DIR "runtime.win-x64/native"

#endif // PAL_HOST_CONFIGURE_H_INCLUDED
