// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Bazel stub: user_events provider compiled without generated tracepoint code.
// The full implementation requires genUserEvents.py-generated providers.
// This stub disables user_events at runtime (InitUserEvents is a no-op).

#include <common.h>
#include "user_events.h"

void InitUserEvents()
{
}

bool IsUserEventsEnabled()
{
    return false;
}

bool IsUserEventsEnabledByKeyword(UCHAR providerId, uint8_t level, uint64_t keyword)
{
    return false;
}
