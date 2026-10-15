// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Reflection;

namespace Microsoft.NET.Build.Tasks.UnitTests
{
    [TestClass]
    public class GivenAGetAssemblyVersion
    {
        [TestMethod]
        public void ItUsesTheLocalPureTaskCompatibilityAttribute()
        {
            CustomAttributeData attribute = CustomAttributeData
                .GetCustomAttributes(typeof(GetAssemblyVersion))
                .Single(attribute =>
                    attribute.AttributeType.FullName == "Microsoft.Build.Framework.MSBuildPureTaskAttribute");

            attribute.AttributeType.Assembly.Should().BeSameAs(typeof(GetAssemblyVersion).Assembly);

            AttributeUsageAttribute? usage = attribute.AttributeType.GetCustomAttribute<AttributeUsageAttribute>();
            usage.Should().NotBeNull();
            usage!.Inherited.Should().BeFalse();
        }

        [TestMethod]
        [DataRow("1.2.3-beta.4", "1.2.3.0")]
        [DataRow("1.2.3.4", "1.2.3.4")]
        public void ItConvertsANuGetVersionToAnAssemblyVersion(string nugetVersion, string expectedAssemblyVersion)
        {
            var task = new GetAssemblyVersion
            {
                BuildEngine = new MockBuildEngine(),
                NuGetVersion = nugetVersion,
            };

            task.Execute().Should().BeTrue();
            task.AssemblyVersion.Should().Be(expectedAssemblyVersion);
        }
    }
}
