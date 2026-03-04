// Unity-build wrapper for native EventPipe/DiagnosticServer sources.
// CMake compiles these .c files as C++ (LANGUAGE CXX) with UNITY_BUILD_BATCH_SIZE 0.
// This wrapper achieves the same effect in Bazel by including them textually into a .cpp TU.

// DiagnosticServer sources
#include "ds-dump-protocol.c"
#include "ds-eventpipe-protocol.c"
#include "ds-ipc.c"
#include "ds-ipc-pal-socket.c"
#include "ds-portable-rid.c"
#include "ds-process-protocol.c"
#include "ds-profiler-protocol.c"
#include "ds-protocol.c"
#include "ds-server.c"

// EventPipe sources
#include "ep.c"
#include "ep-block.c"
#include "ep-buffer.c"
#include "ep-buffer-manager.c"
#include "ep-config.c"
#include "ep-event.c"
#include "ep-event-instance.c"
#include "ep-event-payload.c"
#include "ep-event-source.c"
#include "ep-file.c"
#include "ep-json-file.c"
#include "ep-metadata-generator.c"
#include "ep-provider.c"
#include "ep-sample-profiler.c"
#include "ep-session.c"
#include "ep-session-provider.c"
#include "ep-stack-contents.c"
#include "ep-stream.c"
#include "ep-string.c"
#include "ep-thread.c"
