// Copyright (c) .NET Foundation and contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;

namespace Microsoft.Build.Framework;

// Older MSBuild references do not expose this attribute; the graph engine recognizes its name.
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
internal sealed class MSBuildDeclaredIOTaskAttribute : Attribute;
