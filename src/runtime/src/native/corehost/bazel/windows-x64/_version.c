// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Static version file for Bazel builds (Windows).
// On Unix this uses __attribute__((used)) which MSVC does not support.
// Use #pragma comment(linker, ...) to keep the string in the binary.

#ifdef _MSC_VER
#pragma comment(linker, "/INCLUDE:sccsid")
#pragma section(".rdata$zzz",read)
__declspec(allocate(".rdata$zzz")) static char sccsid[] = "@(#)Version N/A @Commit: bazel-build";
#else
static char sccsid[] __attribute__((used)) = "@(#)Version N/A @Commit: bazel-build";
#endif
