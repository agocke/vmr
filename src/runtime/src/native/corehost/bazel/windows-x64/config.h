// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Hardcoded for windows-x64 (Bazel build).
// Windows does not have dirent or getauxval.

#ifndef PAL_HOST_MISC_CONFIG_H_INCLUDED
#define PAL_HOST_MISC_CONFIG_H_INCLUDED

#define HAVE_DIRENT_D_TYPE 0
#define HAVE_GETAUXVAL 0

#endif // PAL_HOST_MISC_CONFIG_H_INCLUDED
