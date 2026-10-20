// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;

namespace Microsoft.Build.Framework
{
    // MSBuild recognizes this marker by namespace and name, allowing compilation against older reference assemblies.
    [AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
    internal sealed class MSBuildDeclaredIOTaskAttribute : Attribute
    {
    }
}
