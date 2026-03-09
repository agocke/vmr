// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using Microsoft.DotNet.Build.Tasks;

internal sealed class Logger : Microsoft.DotNet.Build.Tasks.ILog
{
    public void LogError(string message, params object[] messageArgs)
    {
        Console.WriteLine(message, messageArgs);
    }
    public void LogMessage(LogImportance importance, string message, params object[] messageArgs)
    {
        Console.WriteLine(message, messageArgs);
    }
    public void LogWarning(string message, params object[] messageArgs)
    {
        Console.WriteLine(message, messageArgs);
    }
    public void LogMessage(string message, params object[] messageArgs)
    {
        Console.WriteLine(message, messageArgs);
    }
}
