// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#nullable enable

namespace Dependency;

public class Base
{
#if UPDATED
    public int Inherited => 43;
#else
    public int Inherited => 42;
#endif
}
