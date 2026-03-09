// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;

internal sealed class Logger
{
    public bool HasLoggedErrors { get; private set; }

    public void LogError(string message, params object[] messageArgs)
    {
        Console.WriteLine("Error" + message);
        Console.WriteLine(messageArgs);
        HasLoggedErrors = true;
    }

    public void LogErrorFromException(Exception ex)
    {
        Console.WriteLine(ex.Message);
    }

    public void LogWarning(string message, params object[] messageArgs)
    {
        Console.WriteLine(message, messageArgs);
    }
    public void LogMessage(string message, params object[] messageArgs)
    {
        Console.WriteLine(message, messageArgs);
    }

    internal void LogMessageFromResources(MessageImportance importance, string resName, params object[] args)
    {
        if (importance != MessageImportance.Low)
        {
            LogMessageFromResources(resName, args);
        }
    }

    internal void LogMessageFromResources(string resName, params object[] _)
        => LogMessage(resName);
    internal void LogErrorFromResources(string rsCode, params object[] _)
        => LogError(rsCode);
    internal void LogErrorWithCodeFromResources(string rsCode, params object [] _)
        => LogError(rsCode);
    internal void LogWarningWithCodeFromResources(string rsCode, params object [] _)
        => LogWarning(rsCode);

    public string FormatResourceString(string resourceName, params object[] args)
        => string.Format(resourceName, args);
}

internal enum MessageImportance
{
    Low
}
