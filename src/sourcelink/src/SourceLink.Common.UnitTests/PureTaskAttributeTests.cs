// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Linq;
using System.Reflection;
using Xunit;

namespace Microsoft.SourceLink.Common.UnitTests
{
    public class PureTaskAttributeTests
    {
        [Fact]
        public void ProviderSelectionUsesTheLocalPureTaskCompatibilityAttribute()
        {
            CustomAttributeData attribute = CustomAttributeData
                .GetCustomAttributes(typeof(SourceLinkHasSingleProvider))
                .Single(attribute => attribute.AttributeType.FullName == "Microsoft.Build.Framework.MSBuildPureTaskAttribute");

            Assert.Same(typeof(SourceLinkHasSingleProvider).Assembly, attribute.AttributeType.Assembly);
            Assert.False(attribute.AttributeType.GetCustomAttribute<AttributeUsageAttribute>()!.Inherited);
        }
    }
}
