// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Simple entry point for library tests that uses reflection-based test discovery.
// This replaces the XUnitWrapperGenerator approach used by CoreCLR tests.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

public static class LibraryTestRunner
{
    public static int Main(string[] args)
    {
        string? filter = args.Length > 0 ? args[0] : null;
        var assembly = Assembly.GetExecutingAssembly();

        int passed = 0;
        int failed = 0;
        int skipped = 0;
        var failures = new List<string>();

        foreach (var type in assembly.GetTypes())
        {
            if (type.IsAbstract || type.IsInterface || !type.IsClass)
                continue;

            MethodInfo[] methods;
            try
            {
                methods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static);
            }
            catch
            {
                continue;
            }

            foreach (var method in methods)
            {
                Attribute[] attrs;
                try
                {
                    attrs = Attribute.GetCustomAttributes(method);
                }
                catch
                {
                    continue;
                }

                var factAttr = attrs.FirstOrDefault(
                    a => a.GetType().Name == "FactAttribute" || a.GetType().Name == "ConditionalFactAttribute");
                var theoryAttr = attrs.FirstOrDefault(
                    a => a.GetType().Name == "TheoryAttribute" || a.GetType().Name == "ConditionalTheoryAttribute");

                if (factAttr is null && theoryAttr is null)
                    continue;

                string testName = $"{type.FullName}.{method.Name}";

                if (filter is not null && !testName.Contains(filter, StringComparison.OrdinalIgnoreCase))
                    continue;

                // Check for Skip
                var skipProp = factAttr?.GetType().GetProperty("Skip") ?? theoryAttr?.GetType().GetProperty("Skip");
                var skipValue = skipProp?.GetValue(factAttr ?? theoryAttr) as string;
                if (skipValue is not null)
                {
                    skipped++;
                    continue;
                }

                if (theoryAttr is not null)
                {
                    var dataSets = new List<object?[]>();

                    foreach (var inlineData in attrs.Where(a => a.GetType().Name == "InlineDataAttribute"))
                    {
                        var dataProp = inlineData.GetType().GetProperty("Data");
                        if (dataProp?.GetValue(inlineData) is object?[] data)
                            dataSets.Add(data);
                    }

                    foreach (var memberData in attrs.Where(a => a.GetType().Name == "MemberDataAttribute"))
                    {
                        var memberNameProp = memberData.GetType().GetProperty("MemberName");
                        var memberName = memberNameProp?.GetValue(memberData) as string;
                        if (memberName is null) continue;

                        var memberTypeProp = memberData.GetType().GetProperty("MemberType");
                        var memberType = memberTypeProp?.GetValue(memberData) as Type ?? type;

                        var prop = memberType.GetProperty(memberName, BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
                        if (prop is not null)
                        {
                            if (prop.GetValue(null) is System.Collections.IEnumerable enumerable)
                                foreach (var item in enumerable)
                                    if (item is object?[] row) dataSets.Add(row);
                        }
                        else
                        {
                            var meth = memberType.GetMethod(memberName, BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy, null, Type.EmptyTypes, null);
                            if (meth is not null && meth.Invoke(null, null) is System.Collections.IEnumerable enumerable)
                                foreach (var item in enumerable)
                                    if (item is object?[] row) dataSets.Add(row);
                        }
                    }

                    if (dataSets.Count == 0)
                    {
                        skipped++;
                        continue;
                    }

                    foreach (var dataSet in dataSets)
                    {
                        RunTest(type, method, dataSet, testName, ref passed, ref failed, ref skipped, failures);
                    }
                }
                else
                {
                    RunTest(type, method, null, testName, ref passed, ref failed, ref skipped, failures);
                }
            }
        }

        Console.WriteLine();
        Console.WriteLine($"Tests: {passed + failed + skipped} total, {passed} passed, {failed} failed, {skipped} skipped");

        if (failures.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Failures:");
            foreach (var f in failures)
                Console.WriteLine($"  {f}");
        }

        // Return 100 for success (CoreCLR convention)
        return failed == 0 ? 100 : 1;
    }

    private static void RunTest(Type type, MethodInfo method, object?[]? args, string testName, ref int passed, ref int failed, ref int skipped, List<string> failures)
    {
        try
        {
            // Convert arguments to match parameter types
            if (args is not null)
            {
                var parameters = method.GetParameters();
                for (int i = 0; i < args.Length && i < parameters.Length; i++)
                {
                    if (args[i] is not null && args[i]!.GetType() != parameters[i].ParameterType)
                    {
                        try { args[i] = Convert.ChangeType(args[i], parameters[i].ParameterType); }
                        catch { /* let it fail naturally */ }
                    }
                }
            }

            object? instance = method.IsStatic ? null : Activator.CreateInstance(type);
            try
            {
                method.Invoke(instance, args);
                passed++;
            }
            finally
            {
                (instance as IDisposable)?.Dispose();
            }
        }
        catch (TargetInvocationException ex) when (ex.InnerException?.GetType().Name == "SkipException")
        {
            skipped++;
        }
        catch (Exception ex)
        {
            failed++;
            var inner = ex is TargetInvocationException tie ? tie.InnerException ?? ex : ex;
            string argsStr = args is not null ? $"({string.Join(", ", args.Select(a => a?.ToString() ?? "null"))})" : "";
            failures.Add($"{testName}{argsStr}: {inner.GetType().Name}: {inner.Message}");
            Console.Error.WriteLine($"FAIL: {testName}{argsStr}");
            Console.Error.WriteLine($"  {inner.GetType().Name}: {inner.Message}");
        }
    }
}
