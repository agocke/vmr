// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Linq;
using System.Reflection;
using Xunit;

namespace Microsoft.SourceLink.GitHub.UnitTests
{
    public class PureTaskAttributeTests
    {
        [Fact]
        public void UrlTransformsUseTheLocalPureTaskCompatibilityAttribute()
        {
            AssertPure(typeof(GetSourceLinkUrl));
            AssertPure(typeof(TranslateRepositoryUrls));
        }

        private static void AssertPure(Type taskType)
        {
            CustomAttributeData attribute = CustomAttributeData
                .GetCustomAttributes(taskType)
                .Single(attribute => attribute.AttributeType.FullName == "Microsoft.Build.Framework.MSBuildPureTaskAttribute");

            Assert.Same(taskType.Assembly, attribute.AttributeType.Assembly);
            Assert.False(attribute.AttributeType.GetCustomAttribute<AttributeUsageAttribute>()!.Inherited);
        }
    }
}
