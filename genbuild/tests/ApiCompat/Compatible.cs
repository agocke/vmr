// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#nullable enable

namespace ApiCompatFixture;

#if CONTRACT
[System.Obsolete("Contract-only attribute excluded by the ApiCompat input file.")]
#endif
public sealed class Surface
#if REFERENCE_ORDER
    : Dependency.Base
#endif
{
    public int Required(int value) => value + 1;
}
