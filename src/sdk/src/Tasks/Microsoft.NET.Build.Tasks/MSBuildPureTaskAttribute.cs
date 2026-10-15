// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;

namespace Microsoft.Build.Framework;

/// <summary>
/// Marks a task whose outputs and observable behavior are determined entirely by its parameters.
/// </summary>
/// <remarks>
/// MSBuild recognizes this compatibility definition by namespace and name. Remove it when the SDK
/// references a version of Microsoft.Build.Framework that defines MSBuildPureTaskAttribute.
/// </remarks>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
internal sealed class MSBuildPureTaskAttribute : Attribute;
