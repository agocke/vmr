// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;

namespace Microsoft.Build.Framework;

// Older MSBuild references do not expose this attribute; the graph engine recognizes its name.
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
internal sealed class MSBuildDeclaredIOTaskAttribute : Attribute;
