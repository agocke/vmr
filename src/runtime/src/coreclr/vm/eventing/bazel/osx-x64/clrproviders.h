
typedef struct _EVENT_DESCRIPTOR
{
    int const Level;
    ULONGLONG const Keyword;
} EVENT_DESCRIPTOR;

#if !defined(EVENTPIPE_TRACE_CONTEXT_DEF)
#define EVENTPIPE_TRACE_CONTEXT_DEF
typedef struct _EVENTPIPE_TRACE_CONTEXT
{
    const WCHAR * Name;
    UCHAR Level;
    bool IsEnabled;
    ULONGLONG EnabledKeywordsBitmask;
} EVENTPIPE_TRACE_CONTEXT, *PEVENTPIPE_TRACE_CONTEXT;
#endif // EVENTPIPE_TRACE_CONTEXT_DEF

#if !defined(LTTNG_TRACE_CONTEXT_DEF)
#define LTTNG_TRACE_CONTEXT_DEF
typedef struct _LTTNG_TRACE_CONTEXT
{
    WCHAR const * Name;
    UCHAR Level;
    bool IsEnabled;
    ULONGLONG EnabledKeywordsBitmask;
} LTTNG_TRACE_CONTEXT, *PLTTNG_TRACE_CONTEXT;
#endif // LTTNG_TRACE_CONTEXT_DEF

#if !defined(USER_EVENTS_TRACE_CONTEXT_DEF)
#define USER_EVENTS_TRACE_CONTEXT_DEF
typedef struct _USER_EVENTS_TRACE_CONTEXT
{
    UCHAR id;
    UCHAR Level;
    bool IsEnabled;
    ULONGLONG EnabledKeywordsBitmask;
} USER_EVENTS_TRACE_CONTEXT, *PUSER_EVENTS_TRACE_CONTEXT;
#endif // USER_EVENTS_TRACE_CONTEXT_DEF

#if !defined(DOTNET_TRACE_CONTEXT_DEF)
#define DOTNET_TRACE_CONTEXT_DEF
typedef struct _DOTNET_TRACE_CONTEXT
{
    EVENTPIPE_TRACE_CONTEXT EventPipeProvider;
    PLTTNG_TRACE_CONTEXT LttngProvider;
    USER_EVENTS_TRACE_CONTEXT UserEventsProvider;
} DOTNET_TRACE_CONTEXT, *PDOTNET_TRACE_CONTEXT;
#endif // DOTNET_TRACE_CONTEXT_DEF

__attribute__((weak)) EVENTPIPE_TRACE_CONTEXT MICROSOFT_WINDOWS_DOTNETRUNTIME_PROVIDER_EVENTPIPE_Context = { W("Microsoft-Windows-DotNETRuntime"), 0, false, 0 };
__attribute__((weak)) LTTNG_TRACE_CONTEXT MICROSOFT_WINDOWS_DOTNETRUNTIME_PROVIDER_LTTNG_Context = { W("Microsoft-Windows-DotNETRuntime"), 0, false, 0 };
// Keywords
#define CLR_GC_KEYWORD 0x1
#define CLR_GCHANDLE_KEYWORD 0x2
#define CLR_ASSEMBLY_LOADER_KEYWORD 0x4
#define CLR_LOADER_KEYWORD 0x8
#define CLR_JIT_KEYWORD 0x10
#define CLR_NGEN_KEYWORD 0x20
#define CLR_STARTENUMERATION_KEYWORD 0x40
#define CLR_ENDENUMERATION_KEYWORD 0x80
#define CLR_SECURITY_KEYWORD 0x400
#define CLR_APPDOMAINRESOURCEMANAGEMENT_KEYWORD 0x800
#define CLR_JITTRACING_KEYWORD 0x1000
#define CLR_INTEROP_KEYWORD 0x2000
#define CLR_CONTENTION_KEYWORD 0x4000
#define CLR_EXCEPTION_KEYWORD 0x8000
#define CLR_THREADING_KEYWORD 0x10000
#define CLR_JITTEDMETHODILTONATIVEMAP_KEYWORD 0x20000
#define CLR_OVERRIDEANDSUPPRESSNGENEVENTS_KEYWORD 0x40000
#define CLR_TYPE_KEYWORD 0x80000
#define CLR_GCHEAPDUMP_KEYWORD 0x100000
#define CLR_GCHEAPALLOCHIGH_KEYWORD 0x200000
#define CLR_GCHEAPSURVIVALANDMOVEMENT_KEYWORD 0x400000
#define CLR_MANAGEDHEAPCOLLECT_KEYWORD 0x800000
#define CLR_GCHEAPANDTYPENAMES_KEYWORD 0x1000000
#define CLR_GCHEAPALLOCLOW_KEYWORD 0x2000000
#define CLR_PERFTRACK_KEYWORD 0x20000000
#define CLR_STACK_KEYWORD 0x40000000
#define CLR_THREADTRANSFER_KEYWORD 0x80000000
#define CLR_DEBUGGER_KEYWORD 0x100000000
#define CLR_MONITORING_KEYWORD 0x200000000
#define CLR_CODESYMBOLS_KEYWORD 0x400000000
#define CLR_EVENTSOURCE_KEYWORD 0x800000000
#define CLR_COMPILATION_KEYWORD 0x1000000000
#define CLR_COMPILATIONDIAGNOSTIC_KEYWORD 0x2000000000
#define CLR_METHODDIAGNOSTIC_KEYWORD 0x4000000000
#define CLR_TYPEDIAGNOSTIC_KEYWORD 0x8000000000
#define CLR_JITINSTRUMENTEDDATA_KEYWORD 0x10000000000
#define CLR_PROFILER_KEYWORD 0x20000000000
#define CLR_WAITHANDLE_KEYWORD 0x40000000000
#define CLR_ALLOCATIONSAMPLING_KEYWORD 0x80000000000
constexpr EVENT_DESCRIPTOR GCStart = { 4, 0x1 };
constexpr EVENT_DESCRIPTOR GCStart_V1 = { 4, 0x1 };
constexpr EVENT_DESCRIPTOR GCStart_V2 = { 4, 0x1 };
constexpr EVENT_DESCRIPTOR GCEnd = { 4, 0x1 };
constexpr EVENT_DESCRIPTOR GCEnd_V1 = { 4, 0x1 };
constexpr EVENT_DESCRIPTOR GCRestartEEEnd = { 4, 0x1 };
constexpr EVENT_DESCRIPTOR GCRestartEEEnd_V1 = { 4, 0x1 };
constexpr EVENT_DESCRIPTOR GCHeapStats = { 4, 0x1 };
constexpr EVENT_DESCRIPTOR GCHeapStats_V1 = { 4, 0x1 };
constexpr EVENT_DESCRIPTOR GCHeapStats_V2 = { 4, 0x1 };
constexpr EVENT_DESCRIPTOR GCCreateSegment = { 4, 0x1 };
constexpr EVENT_DESCRIPTOR GCCreateSegment_V1 = { 4, 0x1 };
constexpr EVENT_DESCRIPTOR GCFreeSegment = { 4, 0x1 };
constexpr EVENT_DESCRIPTOR GCFreeSegment_V1 = { 4, 0x1 };
constexpr EVENT_DESCRIPTOR GCRestartEEBegin = { 4, 0x1 };
constexpr EVENT_DESCRIPTOR GCRestartEEBegin_V1 = { 4, 0x1 };
constexpr EVENT_DESCRIPTOR GCSuspendEEEnd = { 4, 0x1 };
constexpr EVENT_DESCRIPTOR GCSuspendEEEnd_V1 = { 4, 0x1 };
constexpr EVENT_DESCRIPTOR GCSuspendEEBegin = { 4, 0x1 };
constexpr EVENT_DESCRIPTOR GCSuspendEEBegin_V1 = { 4, 0x1 };
constexpr EVENT_DESCRIPTOR GCAllocationTick = { 5, 0x1 };
constexpr EVENT_DESCRIPTOR GCAllocationTick_V1 = { 5, 0x1 };
constexpr EVENT_DESCRIPTOR GCAllocationTick_V2 = { 5, 0x1 };
constexpr EVENT_DESCRIPTOR GCAllocationTick_V3 = { 5, 0x1 };
constexpr EVENT_DESCRIPTOR GCAllocationTick_V4 = { 5, 0x1 };
constexpr EVENT_DESCRIPTOR GCCreateConcurrentThread = { 4, 0x1 };
constexpr EVENT_DESCRIPTOR GCCreateConcurrentThread_V1 = { 4, 0x10001 };
constexpr EVENT_DESCRIPTOR GCTerminateConcurrentThread = { 4, 0x1 };
constexpr EVENT_DESCRIPTOR GCTerminateConcurrentThread_V1 = { 4, 0x10001 };
constexpr EVENT_DESCRIPTOR GCFinalizersEnd = { 4, 0x1 };
constexpr EVENT_DESCRIPTOR GCFinalizersEnd_V1 = { 4, 0x1 };
constexpr EVENT_DESCRIPTOR GCFinalizersBegin = { 4, 0x1 };
constexpr EVENT_DESCRIPTOR GCFinalizersBegin_V1 = { 4, 0x1 };
constexpr EVENT_DESCRIPTOR BulkType = { 4, 0x80000 };
constexpr EVENT_DESCRIPTOR GCBulkRootEdge = { 4, 0x100000 };
constexpr EVENT_DESCRIPTOR GCBulkRootConditionalWeakTableElementEdge = { 4, 0x100000 };
constexpr EVENT_DESCRIPTOR GCBulkNode = { 4, 0x100000 };
constexpr EVENT_DESCRIPTOR GCBulkEdge = { 4, 0x100000 };
constexpr EVENT_DESCRIPTOR GCSampledObjectAllocationHigh = { 4, 0x200000 };
constexpr EVENT_DESCRIPTOR GCBulkSurvivingObjectRanges = { 4, 0x400000 };
constexpr EVENT_DESCRIPTOR GCBulkMovedObjectRanges = { 4, 0x400000 };
constexpr EVENT_DESCRIPTOR GCGenerationRange = { 4, 0x400000 };
constexpr EVENT_DESCRIPTOR GCMarkStackRoots = { 4, 0x1 };
constexpr EVENT_DESCRIPTOR GCMarkFinalizeQueueRoots = { 4, 0x1 };
constexpr EVENT_DESCRIPTOR GCMarkHandles = { 4, 0x1 };
constexpr EVENT_DESCRIPTOR GCMarkOlderGenerationRoots = { 4, 0x1 };
constexpr EVENT_DESCRIPTOR FinalizeObject = { 5, 0x1 };
constexpr EVENT_DESCRIPTOR SetGCHandle = { 4, 0x2 };
constexpr EVENT_DESCRIPTOR DestroyGCHandle = { 4, 0x2 };
constexpr EVENT_DESCRIPTOR GCSampledObjectAllocationLow = { 4, 0x2000000 };
constexpr EVENT_DESCRIPTOR PinObjectAtGCTime = { 5, 0x1 };
constexpr EVENT_DESCRIPTOR GCTriggered = { 4, 0x1 };
constexpr EVENT_DESCRIPTOR GCBulkRootCCW = { 4, 0x100000 };
constexpr EVENT_DESCRIPTOR GCBulkRCW = { 4, 0x100000 };
constexpr EVENT_DESCRIPTOR GCBulkRootStaticVar = { 4, 0x100000 };
constexpr EVENT_DESCRIPTOR GCDynamicEvent = { 4, 0x3f00003 };
constexpr EVENT_DESCRIPTOR WorkerThreadCreate = { 4, 0x10000 };
constexpr EVENT_DESCRIPTOR WorkerThreadTerminate = { 4, 0x10000 };
constexpr EVENT_DESCRIPTOR WorkerThreadRetire = { 4, 0x10000 };
constexpr EVENT_DESCRIPTOR WorkerThreadUnretire = { 4, 0x10000 };
constexpr EVENT_DESCRIPTOR IOThreadCreate = { 4, 0x10000 };
constexpr EVENT_DESCRIPTOR IOThreadCreate_V1 = { 4, 0x10000 };
constexpr EVENT_DESCRIPTOR IOThreadTerminate = { 4, 0x10000 };
constexpr EVENT_DESCRIPTOR IOThreadTerminate_V1 = { 4, 0x10000 };
constexpr EVENT_DESCRIPTOR IOThreadRetire = { 4, 0x10000 };
constexpr EVENT_DESCRIPTOR IOThreadRetire_V1 = { 4, 0x10000 };
constexpr EVENT_DESCRIPTOR IOThreadUnretire = { 4, 0x10000 };
constexpr EVENT_DESCRIPTOR IOThreadUnretire_V1 = { 4, 0x10000 };
constexpr EVENT_DESCRIPTOR ThreadpoolSuspensionSuspendThread = { 4, 0x10000 };
constexpr EVENT_DESCRIPTOR ThreadpoolSuspensionResumeThread = { 4, 0x10000 };
constexpr EVENT_DESCRIPTOR ThreadPoolWorkerThreadStart = { 4, 0x10000 };
constexpr EVENT_DESCRIPTOR ThreadPoolWorkerThreadStop = { 4, 0x10000 };
constexpr EVENT_DESCRIPTOR ThreadPoolWorkerThreadRetirementStart = { 4, 0x10000 };
constexpr EVENT_DESCRIPTOR ThreadPoolWorkerThreadRetirementStop = { 4, 0x10000 };
constexpr EVENT_DESCRIPTOR ThreadPoolWorkerThreadAdjustmentSample = { 4, 0x10000 };
constexpr EVENT_DESCRIPTOR ThreadPoolWorkerThreadAdjustmentAdjustment = { 4, 0x10000 };
constexpr EVENT_DESCRIPTOR ThreadPoolWorkerThreadAdjustmentStats = { 5, 0x10000 };
constexpr EVENT_DESCRIPTOR ThreadPoolWorkerThreadWait = { 4, 0x10000 };
constexpr EVENT_DESCRIPTOR YieldProcessorMeasurement = { 4, 0x10000 };
constexpr EVENT_DESCRIPTOR ThreadPoolMinMaxThreads = { 4, 0x10000 };
constexpr EVENT_DESCRIPTOR ThreadPoolWorkingThreadCount = { 5, 0x10000 };
constexpr EVENT_DESCRIPTOR ThreadPoolEnqueue = { 5, 0x80010000 };
constexpr EVENT_DESCRIPTOR ThreadPoolDequeue = { 5, 0x80010000 };
constexpr EVENT_DESCRIPTOR ThreadPoolIOEnqueue = { 5, 0x80010000 };
constexpr EVENT_DESCRIPTOR ThreadPoolIODequeue = { 5, 0x80010000 };
constexpr EVENT_DESCRIPTOR ThreadPoolIOPack = { 5, 0x10000 };
constexpr EVENT_DESCRIPTOR ThreadCreating = { 4, 0x80010000 };
constexpr EVENT_DESCRIPTOR ThreadRunning = { 4, 0x80010000 };
constexpr EVENT_DESCRIPTOR MethodDetails = { 4, 0x4000000000 };
constexpr EVENT_DESCRIPTOR TypeLoadStart = { 4, 0x8000000000 };
constexpr EVENT_DESCRIPTOR TypeLoadStop = { 4, 0x8000000000 };
constexpr EVENT_DESCRIPTOR ExceptionThrown = { 4, 0x0 };
constexpr EVENT_DESCRIPTOR ExceptionThrown_V1 = { 2, 0x200008000 };
constexpr EVENT_DESCRIPTOR ExceptionCatchStart = { 4, 0x8000 };
constexpr EVENT_DESCRIPTOR ExceptionCatchStop = { 4, 0x8000 };
constexpr EVENT_DESCRIPTOR ExceptionFinallyStart = { 4, 0x8000 };
constexpr EVENT_DESCRIPTOR ExceptionFinallyStop = { 4, 0x8000 };
constexpr EVENT_DESCRIPTOR ExceptionFilterStart = { 4, 0x8000 };
constexpr EVENT_DESCRIPTOR ExceptionFilterStop = { 4, 0x8000 };
constexpr EVENT_DESCRIPTOR ExceptionThrownStop = { 4, 0x8000 };
constexpr EVENT_DESCRIPTOR Contention = { 4, 0x0 };
constexpr EVENT_DESCRIPTOR ContentionStart_V1 = { 4, 0x4000 };
constexpr EVENT_DESCRIPTOR ContentionStart_V2 = { 4, 0x4000 };
constexpr EVENT_DESCRIPTOR ContentionStop = { 4, 0x4000 };
constexpr EVENT_DESCRIPTOR ContentionStop_V1 = { 4, 0x4000 };
constexpr EVENT_DESCRIPTOR ContentionLockCreated = { 4, 0x4000 };
constexpr EVENT_DESCRIPTOR CLRStackWalk = { 0, 0x40000000 };
constexpr EVENT_DESCRIPTOR AppDomainMemAllocated = { 4, 0x800 };
constexpr EVENT_DESCRIPTOR AppDomainMemSurvived = { 4, 0x800 };
constexpr EVENT_DESCRIPTOR ThreadCreated = { 4, 0x10800 };
constexpr EVENT_DESCRIPTOR ThreadTerminated = { 4, 0x10800 };
constexpr EVENT_DESCRIPTOR ThreadDomainEnter = { 4, 0x10800 };
constexpr EVENT_DESCRIPTOR ILStubGenerated = { 4, 0x2000 };
constexpr EVENT_DESCRIPTOR ILStubCacheHit = { 4, 0x2000 };
constexpr EVENT_DESCRIPTOR DCStartCompleteV2 = { 4, 0x30 };
constexpr EVENT_DESCRIPTOR DCEndCompleteV2 = { 4, 0x30 };
constexpr EVENT_DESCRIPTOR MethodDCStartV2 = { 4, 0x30 };
constexpr EVENT_DESCRIPTOR MethodDCEndV2 = { 4, 0x30 };
constexpr EVENT_DESCRIPTOR MethodDCStartVerboseV2 = { 4, 0x30 };
constexpr EVENT_DESCRIPTOR MethodDCEndVerboseV2 = { 4, 0x30 };
constexpr EVENT_DESCRIPTOR MethodLoad = { 4, 0x30 };
constexpr EVENT_DESCRIPTOR MethodLoad_V1 = { 4, 0x30 };
constexpr EVENT_DESCRIPTOR MethodLoad_V2 = { 4, 0x30 };
constexpr EVENT_DESCRIPTOR R2RGetEntryPoint = { 4, 0x2000000000 };
constexpr EVENT_DESCRIPTOR R2RGetEntryPointStart = { 4, 0x2000000000 };
constexpr EVENT_DESCRIPTOR MethodUnload = { 4, 0x30 };
constexpr EVENT_DESCRIPTOR MethodUnload_V1 = { 4, 0x30 };
constexpr EVENT_DESCRIPTOR MethodUnload_V2 = { 4, 0x30 };
constexpr EVENT_DESCRIPTOR MethodLoadVerbose = { 4, 0x30 };
constexpr EVENT_DESCRIPTOR MethodLoadVerbose_V1 = { 4, 0x30 };
constexpr EVENT_DESCRIPTOR MethodLoadVerbose_V2 = { 4, 0x30 };
constexpr EVENT_DESCRIPTOR MethodUnloadVerbose = { 4, 0x30 };
constexpr EVENT_DESCRIPTOR MethodUnloadVerbose_V1 = { 4, 0x30 };
constexpr EVENT_DESCRIPTOR MethodUnloadVerbose_V2 = { 4, 0x30 };
constexpr EVENT_DESCRIPTOR MethodJittingStarted = { 5, 0x10 };
constexpr EVENT_DESCRIPTOR MethodJittingStarted_V1 = { 5, 0x10 };
constexpr EVENT_DESCRIPTOR MethodJitMemoryAllocatedForCode = { 5, 0x10 };
constexpr EVENT_DESCRIPTOR MethodJitInliningSucceeded = { 5, 0x1000 };
constexpr EVENT_DESCRIPTOR MethodJitInliningFailedAnsi = { 5, 0x1000 };
constexpr EVENT_DESCRIPTOR MethodJitTailCallSucceeded = { 5, 0x1000 };
constexpr EVENT_DESCRIPTOR MethodJitTailCallFailedAnsi = { 5, 0x1000 };
constexpr EVENT_DESCRIPTOR MethodILToNativeMap = { 5, 0x20000 };
constexpr EVENT_DESCRIPTOR MethodILToNativeMap_V1 = { 5, 0x20000 };
constexpr EVENT_DESCRIPTOR MethodJitTailCallFailed = { 5, 0x1000 };
constexpr EVENT_DESCRIPTOR MethodJitInliningFailed = { 5, 0x1000 };
constexpr EVENT_DESCRIPTOR ModuleDCStartV2 = { 4, 0x8 };
constexpr EVENT_DESCRIPTOR ModuleDCEndV2 = { 4, 0x8 };
constexpr EVENT_DESCRIPTOR DomainModuleLoad = { 4, 0x8 };
constexpr EVENT_DESCRIPTOR DomainModuleLoad_V1 = { 4, 0x8 };
constexpr EVENT_DESCRIPTOR ModuleLoad = { 4, 0x8 };
constexpr EVENT_DESCRIPTOR ModuleLoad_V1 = { 4, 0x20000008 };
constexpr EVENT_DESCRIPTOR ModuleLoad_V2 = { 4, 0x20000008 };
constexpr EVENT_DESCRIPTOR ModuleLoad_V3 = { 4, 0x20000008 };
constexpr EVENT_DESCRIPTOR ModuleUnload = { 4, 0x8 };
constexpr EVENT_DESCRIPTOR ModuleUnload_V1 = { 4, 0x20000008 };
constexpr EVENT_DESCRIPTOR ModuleUnload_V2 = { 4, 0x20000008 };
constexpr EVENT_DESCRIPTOR AssemblyLoad = { 4, 0x8 };
constexpr EVENT_DESCRIPTOR AssemblyLoad_V1 = { 4, 0x8 };
constexpr EVENT_DESCRIPTOR AssemblyUnload = { 4, 0x8 };
constexpr EVENT_DESCRIPTOR AssemblyUnload_V1 = { 4, 0x8 };
constexpr EVENT_DESCRIPTOR AppDomainLoad = { 4, 0x8 };
constexpr EVENT_DESCRIPTOR AppDomainLoad_V1 = { 4, 0x8 };
constexpr EVENT_DESCRIPTOR AppDomainUnload = { 4, 0x8 };
constexpr EVENT_DESCRIPTOR AppDomainUnload_V1 = { 4, 0x8 };
constexpr EVENT_DESCRIPTOR ModuleRangeLoad = { 4, 0x20000000 };
constexpr EVENT_DESCRIPTOR StrongNameVerificationStart = { 5, 0x400 };
constexpr EVENT_DESCRIPTOR StrongNameVerificationStart_V1 = { 5, 0x400 };
constexpr EVENT_DESCRIPTOR StrongNameVerificationStop = { 4, 0x400 };
constexpr EVENT_DESCRIPTOR StrongNameVerificationStop_V1 = { 4, 0x400 };
constexpr EVENT_DESCRIPTOR AuthenticodeVerificationStart = { 5, 0x400 };
constexpr EVENT_DESCRIPTOR AuthenticodeVerificationStart_V1 = { 5, 0x400 };
constexpr EVENT_DESCRIPTOR AuthenticodeVerificationStop = { 4, 0x400 };
constexpr EVENT_DESCRIPTOR AuthenticodeVerificationStop_V1 = { 4, 0x400 };
constexpr EVENT_DESCRIPTOR RuntimeInformationStart = { 4, 0x0 };
constexpr EVENT_DESCRIPTOR IncreaseMemoryPressure = { 5, 0x1 };
constexpr EVENT_DESCRIPTOR DecreaseMemoryPressure = { 5, 0x1 };
constexpr EVENT_DESCRIPTOR GCMarkWithType = { 4, 0x1 };
constexpr EVENT_DESCRIPTOR GCJoin_V2 = { 5, 0x1 };
constexpr EVENT_DESCRIPTOR GCPerHeapHistory_V3 = { 4, 0x1 };
constexpr EVENT_DESCRIPTOR GCGlobalHeapHistory_V2 = { 4, 0x1 };
constexpr EVENT_DESCRIPTOR GCGlobalHeapHistory_V3 = { 4, 0x1 };
constexpr EVENT_DESCRIPTOR GCGlobalHeapHistory_V4 = { 4, 0x1 };
constexpr EVENT_DESCRIPTOR GenAwareBegin = { 4, 0x100000 };
constexpr EVENT_DESCRIPTOR GenAwareEnd = { 4, 0x100000 };
constexpr EVENT_DESCRIPTOR GCLOHCompact = { 4, 0x1 };
constexpr EVENT_DESCRIPTOR GCFitBucketInfo = { 5, 0x1 };
constexpr EVENT_DESCRIPTOR DebugIPCEventStart = { 4, 0x100000000 };
constexpr EVENT_DESCRIPTOR DebugIPCEventEnd = { 4, 0x100000000 };
constexpr EVENT_DESCRIPTOR DebugExceptionProcessingStart = { 4, 0x100000000 };
constexpr EVENT_DESCRIPTOR DebugExceptionProcessingEnd = { 4, 0x100000000 };
constexpr EVENT_DESCRIPTOR CodeSymbols = { 5, 0x400000000 };
constexpr EVENT_DESCRIPTOR EventSource = { 4, 0x800000000 };
constexpr EVENT_DESCRIPTOR TieredCompilationSettings = { 4, 0x1000000000 };
constexpr EVENT_DESCRIPTOR TieredCompilationPause = { 4, 0x1000000000 };
constexpr EVENT_DESCRIPTOR TieredCompilationResume = { 4, 0x1000000000 };
constexpr EVENT_DESCRIPTOR TieredCompilationBackgroundJitStart = { 4, 0x1000000000 };
constexpr EVENT_DESCRIPTOR TieredCompilationBackgroundJitStop = { 4, 0x1000000000 };
constexpr EVENT_DESCRIPTOR AssemblyLoadStart = { 4, 0x4 };
constexpr EVENT_DESCRIPTOR AssemblyLoadStop = { 4, 0x4 };
constexpr EVENT_DESCRIPTOR ResolutionAttempted = { 4, 0x4 };
constexpr EVENT_DESCRIPTOR AssemblyLoadContextResolvingHandlerInvoked = { 4, 0x4 };
constexpr EVENT_DESCRIPTOR AppDomainAssemblyResolveHandlerInvoked = { 4, 0x4 };
constexpr EVENT_DESCRIPTOR AssemblyLoadFromResolveHandlerInvoked = { 4, 0x4 };
constexpr EVENT_DESCRIPTOR KnownPathProbed = { 4, 0x4 };
constexpr EVENT_DESCRIPTOR JitInstrumentationData = { 4, 0x10000000000 };
constexpr EVENT_DESCRIPTOR JitInstrumentationDataVerbose = { 4, 0x10000000000 };
constexpr EVENT_DESCRIPTOR ProfilerMessage = { 4, 0x20000000000 };
constexpr EVENT_DESCRIPTOR ExecutionCheckpoint = { 4, 0x20000000 };
constexpr EVENT_DESCRIPTOR WaitHandleWaitStart = { 5, 0x40000000000 };
constexpr EVENT_DESCRIPTOR WaitHandleWaitStop = { 5, 0x40000000000 };
constexpr EVENT_DESCRIPTOR AllocationSampled = { 4, 0x80000000000 };
__attribute__((weak)) EVENTPIPE_TRACE_CONTEXT MICROSOFT_WINDOWS_DOTNETRUNTIME_RUNDOWN_PROVIDER_EVENTPIPE_Context = { W("Microsoft-Windows-DotNETRuntimeRundown"), 0, false, 0 };
__attribute__((weak)) LTTNG_TRACE_CONTEXT MICROSOFT_WINDOWS_DOTNETRUNTIME_RUNDOWN_PROVIDER_LTTNG_Context = { W("Microsoft-Windows-DotNETRuntimeRundown"), 0, false, 0 };
// Keywords
#define CLR_RUNDOWNGC_KEYWORD 0x1
#define CLR_RUNDOWNLOADER_KEYWORD 0x8
#define CLR_RUNDOWNJIT_KEYWORD 0x10
#define CLR_RUNDOWNNGEN_KEYWORD 0x20
#define CLR_RUNDOWNSTART_KEYWORD 0x40
#define CLR_RUNDOWNEND_KEYWORD 0x100
#define CLR_RUNDOWNAPPDOMAINRESOURCEMANAGEMENT_KEYWORD 0x800
#define CLR_RUNDOWNTHREADING_KEYWORD 0x10000
#define CLR_RUNDOWNJITTEDMETHODILTONATIVEMAP_KEYWORD 0x20000
#define CLR_RUNDOWNOVERRIDEANDSUPPRESSNGENEVENTS_KEYWORD 0x40000
#define CLR_RUNDOWNPERFTRACK_KEYWORD 0x20000000
#define CLR_RUNDOWNSTACK_KEYWORD 0x40000000
#define CLR_COMPILATION_RUNDOWN_KEYWORD 0x1000000000
constexpr EVENT_DESCRIPTOR CLRStackWalkDCStart = { 0, 0x40000000 };
constexpr EVENT_DESCRIPTOR GCSettingsRundown = { 4, 0x0 };
constexpr EVENT_DESCRIPTOR MethodDCStart = { 4, 0x30 };
constexpr EVENT_DESCRIPTOR MethodDCStart_V1 = { 4, 0x30 };
constexpr EVENT_DESCRIPTOR MethodDCStart_V2 = { 4, 0x30 };
constexpr EVENT_DESCRIPTOR MethodDCEnd = { 4, 0x30 };
constexpr EVENT_DESCRIPTOR MethodDCEnd_V1 = { 4, 0x30 };
constexpr EVENT_DESCRIPTOR MethodDCEnd_V2 = { 4, 0x30 };
constexpr EVENT_DESCRIPTOR MethodDCStartVerbose = { 4, 0x30 };
constexpr EVENT_DESCRIPTOR MethodDCStartVerbose_V1 = { 4, 0x30 };
constexpr EVENT_DESCRIPTOR MethodDCStartVerbose_V2 = { 4, 0x30 };
constexpr EVENT_DESCRIPTOR MethodDCEndVerbose = { 4, 0x30 };
constexpr EVENT_DESCRIPTOR MethodDCEndVerbose_V1 = { 4, 0x30 };
constexpr EVENT_DESCRIPTOR MethodDCEndVerbose_V2 = { 4, 0x30 };
constexpr EVENT_DESCRIPTOR DCStartComplete = { 4, 0x20038 };
constexpr EVENT_DESCRIPTOR DCStartComplete_V1 = { 4, 0x20038 };
constexpr EVENT_DESCRIPTOR DCEndComplete = { 4, 0x20038 };
constexpr EVENT_DESCRIPTOR DCEndComplete_V1 = { 4, 0x20038 };
constexpr EVENT_DESCRIPTOR DCStartInit = { 4, 0x20038 };
constexpr EVENT_DESCRIPTOR DCStartInit_V1 = { 4, 0x20038 };
constexpr EVENT_DESCRIPTOR DCEndInit = { 4, 0x20038 };
constexpr EVENT_DESCRIPTOR DCEndInit_V1 = { 4, 0x20038 };
constexpr EVENT_DESCRIPTOR MethodDCStartILToNativeMap = { 5, 0x20000 };
constexpr EVENT_DESCRIPTOR MethodDCStartILToNativeMap_V1 = { 5, 0x20000 };
constexpr EVENT_DESCRIPTOR MethodDCEndILToNativeMap = { 5, 0x20000 };
constexpr EVENT_DESCRIPTOR MethodDCEndILToNativeMap_V1 = { 5, 0x20000 };
constexpr EVENT_DESCRIPTOR DomainModuleDCStart = { 4, 0x8 };
constexpr EVENT_DESCRIPTOR DomainModuleDCStart_V1 = { 4, 0x8 };
constexpr EVENT_DESCRIPTOR DomainModuleDCEnd = { 4, 0x8 };
constexpr EVENT_DESCRIPTOR DomainModuleDCEnd_V1 = { 4, 0x8 };
constexpr EVENT_DESCRIPTOR ModuleDCStart = { 4, 0x8 };
constexpr EVENT_DESCRIPTOR ModuleDCStart_V1 = { 4, 0x20000008 };
constexpr EVENT_DESCRIPTOR ModuleDCStart_V2 = { 4, 0x20000008 };
constexpr EVENT_DESCRIPTOR ModuleDCEnd = { 4, 0x8 };
constexpr EVENT_DESCRIPTOR ModuleDCEnd_V1 = { 4, 0x20000008 };
constexpr EVENT_DESCRIPTOR ModuleDCEnd_V2 = { 4, 0x20000008 };
constexpr EVENT_DESCRIPTOR ModuleDCEnd_V3 = { 4, 0x20000008 };
constexpr EVENT_DESCRIPTOR AssemblyDCStart = { 4, 0x8 };
constexpr EVENT_DESCRIPTOR AssemblyDCStart_V1 = { 4, 0x8 };
constexpr EVENT_DESCRIPTOR AssemblyDCEnd = { 4, 0x8 };
constexpr EVENT_DESCRIPTOR AssemblyDCEnd_V1 = { 4, 0x8 };
constexpr EVENT_DESCRIPTOR AppDomainDCStart = { 4, 0x8 };
constexpr EVENT_DESCRIPTOR AppDomainDCStart_V1 = { 4, 0x8 };
constexpr EVENT_DESCRIPTOR AppDomainDCEnd = { 4, 0x8 };
constexpr EVENT_DESCRIPTOR AppDomainDCEnd_V1 = { 4, 0x8 };
constexpr EVENT_DESCRIPTOR ThreadDC = { 4, 0x10800 };
constexpr EVENT_DESCRIPTOR ModuleRangeDCStart = { 4, 0x20000000 };
constexpr EVENT_DESCRIPTOR ModuleRangeDCEnd = { 4, 0x20000000 };
constexpr EVENT_DESCRIPTOR RuntimeInformationDCStart = { 4, 0x0 };
constexpr EVENT_DESCRIPTOR TieredCompilationSettingsDCStart = { 4, 0x1000000000 };
constexpr EVENT_DESCRIPTOR ExecutionCheckpointDCEnd = { 4, 0x0 };
__attribute__((weak)) EVENTPIPE_TRACE_CONTEXT MICROSOFT_WINDOWS_DOTNETRUNTIME_STRESS_PROVIDER_EVENTPIPE_Context = { W("Microsoft-Windows-DotNETRuntimeStress"), 0, false, 0 };
__attribute__((weak)) LTTNG_TRACE_CONTEXT MICROSOFT_WINDOWS_DOTNETRUNTIME_STRESS_PROVIDER_LTTNG_Context = { W("Microsoft-Windows-DotNETRuntimeStress"), 0, false, 0 };
// Keywords
#define CLR_STRESSSTACK_KEYWORD 0x40000000
constexpr EVENT_DESCRIPTOR StressLogEvent = { 4, 0x0 };
constexpr EVENT_DESCRIPTOR StressLogEvent_V1 = { 4, 0x0 };
constexpr EVENT_DESCRIPTOR CLRStackWalkStress = { 0, 0x40000000 };
__attribute__((weak)) EVENTPIPE_TRACE_CONTEXT MICROSOFT_WINDOWS_DOTNETRUNTIME_PRIVATE_PROVIDER_EVENTPIPE_Context = { W("Microsoft-Windows-DotNETRuntimePrivate"), 0, false, 0 };
__attribute__((weak)) LTTNG_TRACE_CONTEXT MICROSOFT_WINDOWS_DOTNETRUNTIME_PRIVATE_PROVIDER_LTTNG_Context = { W("Microsoft-Windows-DotNETRuntimePrivate"), 0, false, 0 };
// Keywords
#define CLR_PRIVATEGC_KEYWORD 0x00000001
#define CLR_PRIVATEBINDING_KEYWORD 0x00000002
#define CLR_PRIVATENGENFORCERESTORE_KEYWORD 0x00000004
#define CLR_PRIVATEFUSION_KEYWORD 0x00000008
#define CLR_PRIVATELOADERHEAP_KEYWORD 0x00000010
#define CLR_PRIVATESECURITY_KEYWORD 0x00000400
#define CLR_INTEROP_KEYWORD 0x2000
#define CLR_PRIVATEGCHANDLE_KEYWORD 0x4000
#define CLR_PRIVATEMULTICOREJIT_KEYWORD 0x20000
#define CLR_JITTED_METHOD_RICH_DEBUG_INFO_KEYWORD 0x40000
#define CLR_PERFTRACK_PRIVATE_KEYWORD 0x20000000
#define CLR_PRIVATESTACK_KEYWORD 0x40000000
#define CLR_PRIVATESTARTUP_KEYWORD 0x80000000
#define CLR_PRIVATE_DYNAMICTYPEUSAGE_KEYWORD 0x00000020
constexpr EVENT_DESCRIPTOR GCDecision = { 4, 0x1 };
constexpr EVENT_DESCRIPTOR GCDecision_V1 = { 4, 0x1 };
constexpr EVENT_DESCRIPTOR GCSettings = { 4, 0x1 };
constexpr EVENT_DESCRIPTOR GCSettings_V1 = { 4, 0x1 };
constexpr EVENT_DESCRIPTOR GCOptimized = { 5, 0x1 };
constexpr EVENT_DESCRIPTOR GCOptimized_V1 = { 5, 0x1 };
constexpr EVENT_DESCRIPTOR GCPerHeapHistory = { 4, 0x1 };
constexpr EVENT_DESCRIPTOR GCPerHeapHistory_V1 = { 4, 0x1 };
constexpr EVENT_DESCRIPTOR GCGlobalHeapHistory = { 4, 0x1 };
constexpr EVENT_DESCRIPTOR GCGlobalHeapHistory_V1 = { 4, 0x1 };
constexpr EVENT_DESCRIPTOR GCJoin = { 5, 0x1 };
constexpr EVENT_DESCRIPTOR GCJoin_V1 = { 5, 0x1 };
constexpr EVENT_DESCRIPTOR PrvGCMarkStackRoots = { 4, 0x1 };
constexpr EVENT_DESCRIPTOR PrvGCMarkStackRoots_V1 = { 4, 0x1 };
constexpr EVENT_DESCRIPTOR PrvGCMarkFinalizeQueueRoots = { 4, 0x1 };
constexpr EVENT_DESCRIPTOR PrvGCMarkFinalizeQueueRoots_V1 = { 4, 0x1 };
constexpr EVENT_DESCRIPTOR PrvGCMarkHandles = { 4, 0x1 };
constexpr EVENT_DESCRIPTOR PrvGCMarkHandles_V1 = { 4, 0x1 };
constexpr EVENT_DESCRIPTOR PrvGCMarkCards = { 4, 0x1 };
constexpr EVENT_DESCRIPTOR PrvGCMarkCards_V1 = { 4, 0x1 };
constexpr EVENT_DESCRIPTOR BGCBegin = { 4, 0x1 };
constexpr EVENT_DESCRIPTOR BGC1stNonConEnd = { 4, 0x1 };
constexpr EVENT_DESCRIPTOR BGC1stConEnd = { 4, 0x1 };
constexpr EVENT_DESCRIPTOR BGC2ndNonConBegin = { 4, 0x1 };
constexpr EVENT_DESCRIPTOR BGC2ndNonConEnd = { 4, 0x1 };
constexpr EVENT_DESCRIPTOR BGC2ndConBegin = { 4, 0x1 };
constexpr EVENT_DESCRIPTOR BGC2ndConEnd = { 4, 0x1 };
constexpr EVENT_DESCRIPTOR BGCPlanEnd = { 4, 0x1 };
constexpr EVENT_DESCRIPTOR BGCSweepEnd = { 4, 0x1 };
constexpr EVENT_DESCRIPTOR BGCDrainMark = { 4, 0x1 };
constexpr EVENT_DESCRIPTOR BGCRevisit = { 4, 0x1 };
constexpr EVENT_DESCRIPTOR BGCOverflow = { 4, 0x1 };
constexpr EVENT_DESCRIPTOR BGCOverflow_V1 = { 4, 0x1 };
constexpr EVENT_DESCRIPTOR BGCAllocWaitBegin = { 4, 0x1 };
constexpr EVENT_DESCRIPTOR BGCAllocWaitEnd = { 4, 0x1 };
constexpr EVENT_DESCRIPTOR GCFullNotify = { 4, 0x1 };
constexpr EVENT_DESCRIPTOR GCFullNotify_V1 = { 4, 0x1 };
constexpr EVENT_DESCRIPTOR BGC1stSweepEnd = { 4, 0x1 };
constexpr EVENT_DESCRIPTOR EEStartupStart = { 4, 0x80000000 };
constexpr EVENT_DESCRIPTOR EEStartupStart_V1 = { 4, 0x80000000 };
constexpr EVENT_DESCRIPTOR EEStartupEnd = { 4, 0x80000000 };
constexpr EVENT_DESCRIPTOR EEStartupEnd_V1 = { 4, 0x80000000 };
constexpr EVENT_DESCRIPTOR EEConfigSetup = { 4, 0x80000000 };
constexpr EVENT_DESCRIPTOR EEConfigSetup_V1 = { 4, 0x80000000 };
constexpr EVENT_DESCRIPTOR EEConfigSetupEnd = { 4, 0x80000000 };
constexpr EVENT_DESCRIPTOR EEConfigSetupEnd_V1 = { 4, 0x80000000 };
constexpr EVENT_DESCRIPTOR LdSysBases = { 4, 0x80000000 };
constexpr EVENT_DESCRIPTOR LdSysBases_V1 = { 4, 0x80000000 };
constexpr EVENT_DESCRIPTOR LdSysBasesEnd = { 4, 0x80000000 };
constexpr EVENT_DESCRIPTOR LdSysBasesEnd_V1 = { 4, 0x80000000 };
constexpr EVENT_DESCRIPTOR ExecExe = { 4, 0x80000000 };
constexpr EVENT_DESCRIPTOR ExecExe_V1 = { 4, 0x80000000 };
constexpr EVENT_DESCRIPTOR ExecExeEnd = { 4, 0x80000000 };
constexpr EVENT_DESCRIPTOR ExecExeEnd_V1 = { 4, 0x80000000 };
constexpr EVENT_DESCRIPTOR Main = { 4, 0x80000000 };
constexpr EVENT_DESCRIPTOR Main_V1 = { 4, 0x80000000 };
constexpr EVENT_DESCRIPTOR MainEnd = { 4, 0x80000000 };
constexpr EVENT_DESCRIPTOR MainEnd_V1 = { 4, 0x80000000 };
constexpr EVENT_DESCRIPTOR ApplyPolicyStart = { 4, 0x80000000 };
constexpr EVENT_DESCRIPTOR ApplyPolicyStart_V1 = { 4, 0x80000000 };
constexpr EVENT_DESCRIPTOR ApplyPolicyEnd = { 4, 0x80000000 };
constexpr EVENT_DESCRIPTOR ApplyPolicyEnd_V1 = { 4, 0x80000000 };
constexpr EVENT_DESCRIPTOR LdLibShFolder = { 4, 0x80000000 };
constexpr EVENT_DESCRIPTOR LdLibShFolder_V1 = { 4, 0x80000000 };
constexpr EVENT_DESCRIPTOR LdLibShFolderEnd = { 4, 0x80000000 };
constexpr EVENT_DESCRIPTOR LdLibShFolderEnd_V1 = { 4, 0x80000000 };
constexpr EVENT_DESCRIPTOR PrestubWorker = { 4, 0x80000000 };
constexpr EVENT_DESCRIPTOR PrestubWorker_V1 = { 4, 0x80000000 };
constexpr EVENT_DESCRIPTOR PrestubWorkerEnd = { 4, 0x80000000 };
constexpr EVENT_DESCRIPTOR PrestubWorkerEnd_V1 = { 4, 0x80000000 };
constexpr EVENT_DESCRIPTOR GetInstallationStart = { 4, 0x80000000 };
constexpr EVENT_DESCRIPTOR GetInstallationStart_V1 = { 4, 0x80000000 };
constexpr EVENT_DESCRIPTOR GetInstallationEnd = { 4, 0x80000000 };
constexpr EVENT_DESCRIPTOR GetInstallationEnd_V1 = { 4, 0x80000000 };
constexpr EVENT_DESCRIPTOR OpenHModule = { 4, 0x80000000 };
constexpr EVENT_DESCRIPTOR OpenHModule_V1 = { 4, 0x80000000 };
constexpr EVENT_DESCRIPTOR OpenHModuleEnd = { 4, 0x80000000 };
constexpr EVENT_DESCRIPTOR OpenHModuleEnd_V1 = { 4, 0x80000000 };
constexpr EVENT_DESCRIPTOR ExplicitBindStart = { 4, 0x80000000 };
constexpr EVENT_DESCRIPTOR ExplicitBindStart_V1 = { 4, 0x80000000 };
constexpr EVENT_DESCRIPTOR ExplicitBindEnd = { 4, 0x80000000 };
constexpr EVENT_DESCRIPTOR ExplicitBindEnd_V1 = { 4, 0x80000000 };
constexpr EVENT_DESCRIPTOR ParseXml = { 4, 0x80000000 };
constexpr EVENT_DESCRIPTOR ParseXml_V1 = { 4, 0x80000000 };
constexpr EVENT_DESCRIPTOR ParseXmlEnd = { 4, 0x80000000 };
constexpr EVENT_DESCRIPTOR ParseXmlEnd_V1 = { 4, 0x80000000 };
constexpr EVENT_DESCRIPTOR InitDefaultDomain = { 4, 0x80000000 };
constexpr EVENT_DESCRIPTOR InitDefaultDomain_V1 = { 4, 0x80000000 };
constexpr EVENT_DESCRIPTOR InitDefaultDomainEnd = { 4, 0x80000000 };
constexpr EVENT_DESCRIPTOR InitDefaultDomainEnd_V1 = { 4, 0x80000000 };
constexpr EVENT_DESCRIPTOR InitSecurity = { 4, 0x80000000 };
constexpr EVENT_DESCRIPTOR InitSecurity_V1 = { 4, 0x80000000 };
constexpr EVENT_DESCRIPTOR InitSecurityEnd = { 4, 0x80000000 };
constexpr EVENT_DESCRIPTOR InitSecurityEnd_V1 = { 4, 0x80000000 };
constexpr EVENT_DESCRIPTOR AllowBindingRedirs = { 4, 0x80000000 };
constexpr EVENT_DESCRIPTOR AllowBindingRedirs_V1 = { 4, 0x80000000 };
constexpr EVENT_DESCRIPTOR AllowBindingRedirsEnd = { 4, 0x80000000 };
constexpr EVENT_DESCRIPTOR AllowBindingRedirsEnd_V1 = { 4, 0x80000000 };
constexpr EVENT_DESCRIPTOR EEConfigSync = { 4, 0x80000000 };
constexpr EVENT_DESCRIPTOR EEConfigSync_V1 = { 4, 0x80000000 };
constexpr EVENT_DESCRIPTOR EEConfigSyncEnd = { 4, 0x80000000 };
constexpr EVENT_DESCRIPTOR EEConfigSyncEnd_V1 = { 4, 0x80000000 };
constexpr EVENT_DESCRIPTOR FusionBinding = { 4, 0x80000000 };
constexpr EVENT_DESCRIPTOR FusionBinding_V1 = { 4, 0x80000000 };
constexpr EVENT_DESCRIPTOR FusionBindingEnd = { 4, 0x80000000 };
constexpr EVENT_DESCRIPTOR FusionBindingEnd_V1 = { 4, 0x80000000 };
constexpr EVENT_DESCRIPTOR LoaderCatchCall = { 4, 0x80000000 };
constexpr EVENT_DESCRIPTOR LoaderCatchCall_V1 = { 4, 0x80000000 };
constexpr EVENT_DESCRIPTOR LoaderCatchCallEnd = { 4, 0x80000000 };
constexpr EVENT_DESCRIPTOR LoaderCatchCallEnd_V1 = { 4, 0x80000000 };
constexpr EVENT_DESCRIPTOR FusionInit = { 4, 0x80000000 };
constexpr EVENT_DESCRIPTOR FusionInit_V1 = { 4, 0x80000000 };
constexpr EVENT_DESCRIPTOR FusionInitEnd = { 4, 0x80000000 };
constexpr EVENT_DESCRIPTOR FusionInitEnd_V1 = { 4, 0x80000000 };
constexpr EVENT_DESCRIPTOR FusionAppCtx = { 4, 0x80000000 };
constexpr EVENT_DESCRIPTOR FusionAppCtx_V1 = { 4, 0x80000000 };
constexpr EVENT_DESCRIPTOR FusionAppCtxEnd = { 4, 0x80000000 };
constexpr EVENT_DESCRIPTOR FusionAppCtxEnd_V1 = { 4, 0x80000000 };
constexpr EVENT_DESCRIPTOR Fusion2EE = { 4, 0x80000000 };
constexpr EVENT_DESCRIPTOR Fusion2EE_V1 = { 4, 0x80000000 };
constexpr EVENT_DESCRIPTOR Fusion2EEEnd = { 4, 0x80000000 };
constexpr EVENT_DESCRIPTOR Fusion2EEEnd_V1 = { 4, 0x80000000 };
constexpr EVENT_DESCRIPTOR SecurityCatchCall = { 4, 0x80000000 };
constexpr EVENT_DESCRIPTOR SecurityCatchCall_V1 = { 4, 0x80000000 };
constexpr EVENT_DESCRIPTOR SecurityCatchCallEnd = { 4, 0x80000000 };
constexpr EVENT_DESCRIPTOR SecurityCatchCallEnd_V1 = { 4, 0x80000000 };
constexpr EVENT_DESCRIPTOR CLRStackWalkPrivate = { 0, 0x40000000 };
constexpr EVENT_DESCRIPTOR ModuleRangeLoadPrivate = { 4, 0x20000000 };
constexpr EVENT_DESCRIPTOR BindingPolicyPhaseStart = { 4, 0x2 };
constexpr EVENT_DESCRIPTOR BindingPolicyPhaseEnd = { 4, 0x2 };
constexpr EVENT_DESCRIPTOR BindingNgenPhaseStart = { 4, 0x2 };
constexpr EVENT_DESCRIPTOR BindingNgenPhaseEnd = { 4, 0x2 };
constexpr EVENT_DESCRIPTOR BindingLookupAndProbingPhaseStart = { 4, 0x2 };
constexpr EVENT_DESCRIPTOR BindingLookupAndProbingPhaseEnd = { 4, 0x2 };
constexpr EVENT_DESCRIPTOR LoaderPhaseStart = { 4, 0x2 };
constexpr EVENT_DESCRIPTOR LoaderPhaseEnd = { 4, 0x2 };
constexpr EVENT_DESCRIPTOR BindingPhaseStart = { 4, 0x2 };
constexpr EVENT_DESCRIPTOR BindingPhaseEnd = { 4, 0x2 };
constexpr EVENT_DESCRIPTOR BindingDownloadPhaseStart = { 4, 0x2 };
constexpr EVENT_DESCRIPTOR BindingDownloadPhaseEnd = { 4, 0x2 };
constexpr EVENT_DESCRIPTOR LoaderAssemblyInitPhaseStart = { 4, 0x2 };
constexpr EVENT_DESCRIPTOR LoaderAssemblyInitPhaseEnd = { 4, 0x2 };
constexpr EVENT_DESCRIPTOR LoaderMappingPhaseStart = { 4, 0x2 };
constexpr EVENT_DESCRIPTOR LoaderMappingPhaseEnd = { 4, 0x2 };
constexpr EVENT_DESCRIPTOR LoaderDeliverEventsPhaseStart = { 4, 0x2 };
constexpr EVENT_DESCRIPTOR LoaderDeliverEventsPhaseEnd = { 4, 0x2 };
constexpr EVENT_DESCRIPTOR EvidenceGenerated = { 4, 0x400 };
constexpr EVENT_DESCRIPTOR ModuleTransparencyComputationStart = { 4, 0x400 };
constexpr EVENT_DESCRIPTOR ModuleTransparencyComputationEnd = { 4, 0x400 };
constexpr EVENT_DESCRIPTOR TypeTransparencyComputationStart = { 4, 0x400 };
constexpr EVENT_DESCRIPTOR TypeTransparencyComputationEnd = { 4, 0x400 };
constexpr EVENT_DESCRIPTOR MethodTransparencyComputationStart = { 4, 0x400 };
constexpr EVENT_DESCRIPTOR MethodTransparencyComputationEnd = { 4, 0x400 };
constexpr EVENT_DESCRIPTOR FieldTransparencyComputationStart = { 4, 0x400 };
constexpr EVENT_DESCRIPTOR FieldTransparencyComputationEnd = { 4, 0x400 };
constexpr EVENT_DESCRIPTOR TokenTransparencyComputationStart = { 4, 0x400 };
constexpr EVENT_DESCRIPTOR TokenTransparencyComputationEnd = { 4, 0x400 };
constexpr EVENT_DESCRIPTOR NgenBindEvent = { 4, 0x8 };
constexpr EVENT_DESCRIPTOR JittedMethodRichDebugInfo = { 4, 0x40000 };
constexpr EVENT_DESCRIPTOR FailFast = { 1, 0x0 };
constexpr EVENT_DESCRIPTOR PrvFinalizeObject = { 5, 0x1 };
constexpr EVENT_DESCRIPTOR CCWRefCountChangeAnsi = { 5, 0x2000 };
constexpr EVENT_DESCRIPTOR PrvSetGCHandle = { 5, 0x4000 };
constexpr EVENT_DESCRIPTOR PrvDestroyGCHandle = { 5, 0x4000 };
constexpr EVENT_DESCRIPTOR FusionMessageEvent = { 4, 0x2 };
constexpr EVENT_DESCRIPTOR FusionErrorCodeEvent = { 4, 0x2 };
constexpr EVENT_DESCRIPTOR PinPlugAtGCTime = { 5, 0x1 };
constexpr EVENT_DESCRIPTOR CCWRefCountChange = { 5, 0x2000 };
constexpr EVENT_DESCRIPTOR AllocRequest = { 5, 0x10 };
constexpr EVENT_DESCRIPTOR MulticoreJit = { 4, 0x20000 };
constexpr EVENT_DESCRIPTOR MulticoreJitMethodCodeReturned = { 4, 0x20000 };
constexpr EVENT_DESCRIPTOR IInspectableRuntimeClassName = { 4, 0x20 };
constexpr EVENT_DESCRIPTOR WinRTUnbox = { 4, 0x20 };
constexpr EVENT_DESCRIPTOR CreateRCW = { 4, 0x20 };
constexpr EVENT_DESCRIPTOR RCWVariance = { 4, 0x20 };
constexpr EVENT_DESCRIPTOR RCWIEnumerableCasting = { 4, 0x20 };
constexpr EVENT_DESCRIPTOR CreateCCW = { 4, 0x20 };
constexpr EVENT_DESCRIPTOR CCWVariance = { 4, 0x20 };
constexpr EVENT_DESCRIPTOR ObjectVariantMarshallingToNative = { 4, 0x20 };
constexpr EVENT_DESCRIPTOR GetTypeFromGUID = { 4, 0x20 };
constexpr EVENT_DESCRIPTOR GetTypeFromProgID = { 4, 0x20 };
constexpr EVENT_DESCRIPTOR ConvertToCallbackEtw = { 4, 0x20 };
constexpr EVENT_DESCRIPTOR BeginCreateManagedReference = { 4, 0x20 };
constexpr EVENT_DESCRIPTOR EndCreateManagedReference = { 4, 0x20 };
constexpr EVENT_DESCRIPTOR ObjectVariantMarshallingToManaged = { 4, 0x20 };
__attribute__((weak)) EVENTPIPE_TRACE_CONTEXT MICROSOFT_DOTNETRUNTIME_MONO_PROFILER_PROVIDER_EVENTPIPE_Context = { W("Microsoft-DotNETRuntimeMonoProfiler"), 0, false, 0 };
__attribute__((weak)) LTTNG_TRACE_CONTEXT MICROSOFT_DOTNETRUNTIME_MONO_PROFILER_PROVIDER_LTTNG_Context = { W("Microsoft-DotNETRuntimeMonoProfiler"), 0, false, 0 };
// Keywords
#define CLR_MONO_PROFILER_GC_KEYWORD 0x1
#define CLR_MONO_PROFILER_GC_HANDLE_KEYWORD 0x2
#define CLR_MONO_PROFILER_LOADER_KEYWORD 0x8
#define CLR_MONO_PROFILER_JIT_KEYWORD 0x10
#define CLR_MONO_PROFILER_CONTENTION_KEYWORD 0x4000
#define CLR_MONO_PROFILER_EXCEPTION_KEYWORD 0x8000
#define CLR_MONO_PROFILER_THREADING_KEYWORD 0x10000
#define CLR_MONO_PROFILER_GC_HEAPDUMP_KEYWORD 0x100000
#define CLR_MONO_PROFILER_GC_ALLOCATION_KEYWORD 0x200000
#define CLR_MONO_PROFILER_GC_MOVES_KEYWORD 0x400000
#define CLR_MONO_PROFILER_GC_HEAPCOLLECT_KEYWORD 0x800000
#define CLR_MONO_PROFILER_GC_FINALIZATION_KEYWORD 0x1000000
#define CLR_MONO_PROFILER_GC_RESIZE_KEYWORD 0x2000000
#define CLR_MONO_PROFILER_GC_ROOT_KEYWORD 0x4000000
#define CLR_MONO_PROFILER_GC_HEAPDUMP_VTABLE_CLASS_REFERENCE_KEYWORD 0x8000000
#define CLR_MONO_PROFILER_METHOD_TRACING_KEYWORD 0x20000000
#define CLR_MONO_PROFILER_TYPE_LOADING_KEYWORD 0x8000000000
#define CLR_MONO_PROFILER_MONITOR_KEYWORD 0x10000000000
constexpr EVENT_DESCRIPTOR MonoProfilerContextLoaded = { 4, 0x8 };
constexpr EVENT_DESCRIPTOR MonoProfilerContextUnloaded = { 4, 0x8 };
constexpr EVENT_DESCRIPTOR MonoProfilerAppDomainLoading = { 5, 0x8 };
constexpr EVENT_DESCRIPTOR MonoProfilerAppDomainLoaded = { 4, 0x8 };
constexpr EVENT_DESCRIPTOR MonoProfilerAppDomainUnloading = { 5, 0x8 };
constexpr EVENT_DESCRIPTOR MonoProfilerAppDomainUnloaded = { 4, 0x8 };
constexpr EVENT_DESCRIPTOR MonoProfilerAppDomainName = { 5, 0x8 };
constexpr EVENT_DESCRIPTOR MonoProfilerJitBegin = { 4, 0x10 };
constexpr EVENT_DESCRIPTOR MonoProfilerJitFailed = { 4, 0x10 };
constexpr EVENT_DESCRIPTOR MonoProfilerJitDone = { 4, 0x10 };
constexpr EVENT_DESCRIPTOR MonoProfilerJitDone_V1 = { 4, 0x10 };
constexpr EVENT_DESCRIPTOR MonoProfilerJitChunkCreated = { 4, 0x10 };
constexpr EVENT_DESCRIPTOR MonoProfilerJitChunkDestroyed = { 4, 0x10 };
constexpr EVENT_DESCRIPTOR MonoProfilerJitCodeBuffer = { 4, 0x10 };
constexpr EVENT_DESCRIPTOR MonoProfilerClassLoading = { 5, 0x8000000000 };
constexpr EVENT_DESCRIPTOR MonoProfilerClassFailed = { 4, 0x8000000000 };
constexpr EVENT_DESCRIPTOR MonoProfilerClassLoaded = { 4, 0x8000000000 };
constexpr EVENT_DESCRIPTOR MonoProfilerClassLoaded_V1 = { 4, 0x8000000000 };
constexpr EVENT_DESCRIPTOR MonoProfilerVTableLoading = { 5, 0x8000000000 };
constexpr EVENT_DESCRIPTOR MonoProfilerVTableFailed = { 4, 0x8000000000 };
constexpr EVENT_DESCRIPTOR MonoProfilerVTableLoaded = { 4, 0x8000000000 };
constexpr EVENT_DESCRIPTOR MonoProfilerModuleLoading = { 5, 0x8 };
constexpr EVENT_DESCRIPTOR MonoProfilerModuleFailed = { 4, 0x8 };
constexpr EVENT_DESCRIPTOR MonoProfilerModuleLoaded = { 4, 0x8 };
constexpr EVENT_DESCRIPTOR MonoProfilerModuleUnloading = { 5, 0x8 };
constexpr EVENT_DESCRIPTOR MonoProfilerModuleUnloaded = { 4, 0x8 };
constexpr EVENT_DESCRIPTOR MonoProfilerAssemblyLoading = { 5, 0x8 };
constexpr EVENT_DESCRIPTOR MonoProfilerAssemblyLoaded = { 4, 0x8 };
constexpr EVENT_DESCRIPTOR MonoProfilerAssemblyUnloading = { 5, 0x8 };
constexpr EVENT_DESCRIPTOR MonoProfilerAssemblyUnloaded = { 4, 0x8 };
constexpr EVENT_DESCRIPTOR MonoProfilerMethodEnter = { 4, 0x20000000 };
constexpr EVENT_DESCRIPTOR MonoProfilerMethodLeave = { 4, 0x20000000 };
constexpr EVENT_DESCRIPTOR MonoProfilerMethodTailCall = { 4, 0x20000000 };
constexpr EVENT_DESCRIPTOR MonoProfilerMethodExceptionLeave = { 4, 0x20000000 };
constexpr EVENT_DESCRIPTOR MonoProfilerMethodFree = { 4, 0x20000000 };
constexpr EVENT_DESCRIPTOR MonoProfilerMethodBeginInvoke = { 4, 0x20000000 };
constexpr EVENT_DESCRIPTOR MonoProfilerMethodEndInvoke = { 4, 0x20000000 };
constexpr EVENT_DESCRIPTOR MonoProfilerExceptionThrow = { 4, 0x8000 };
constexpr EVENT_DESCRIPTOR MonoProfilerExceptionClause = { 4, 0x8000 };
constexpr EVENT_DESCRIPTOR MonoProfilerGCEvent = { 4, 0x1 };
constexpr EVENT_DESCRIPTOR MonoProfilerGCAllocation = { 4, 0x200000 };
constexpr EVENT_DESCRIPTOR MonoProfilerGCMoves = { 4, 0x400000 };
constexpr EVENT_DESCRIPTOR MonoProfilerGCResize = { 4, 0x2000000 };
constexpr EVENT_DESCRIPTOR MonoProfilerGCHandleCreated = { 4, 0x2 };
constexpr EVENT_DESCRIPTOR MonoProfilerGCHandleDeleted = { 4, 0x2 };
constexpr EVENT_DESCRIPTOR MonoProfilerGCFinalizing = { 4, 0x1000000 };
constexpr EVENT_DESCRIPTOR MonoProfilerGCFinalized = { 4, 0x1000000 };
constexpr EVENT_DESCRIPTOR MonoProfilerGCFinalizingObject = { 4, 0x1000000 };
constexpr EVENT_DESCRIPTOR MonoProfilerGCFinalizedObject = { 4, 0x1000000 };
constexpr EVENT_DESCRIPTOR MonoProfilerGCRootRegister = { 4, 0x4000000 };
constexpr EVENT_DESCRIPTOR MonoProfilerGCRootUnregister = { 4, 0x4000000 };
constexpr EVENT_DESCRIPTOR MonoProfilerGCRoots = { 4, 0x4000000 };
constexpr EVENT_DESCRIPTOR MonoProfilerGCHeapDumpStart = { 4, 0x100000 };
constexpr EVENT_DESCRIPTOR MonoProfilerGCHeapDumpStop = { 4, 0x100000 };
constexpr EVENT_DESCRIPTOR MonoProfilerGCHeapDumpObjectReference = { 4, 0x100000 };
constexpr EVENT_DESCRIPTOR MonoProfilerMonitorContention = { 4, 0x10000004000 };
constexpr EVENT_DESCRIPTOR MonoProfilerMonitorFailed = { 4, 0x10000000000 };
constexpr EVENT_DESCRIPTOR MonoProfilerMonitorAcquired = { 4, 0x10000000000 };
constexpr EVENT_DESCRIPTOR MonoProfilerThreadStarted = { 4, 0x10000 };
constexpr EVENT_DESCRIPTOR MonoProfilerThreadStopping = { 5, 0x10000 };
constexpr EVENT_DESCRIPTOR MonoProfilerThreadStopped = { 4, 0x10000 };
constexpr EVENT_DESCRIPTOR MonoProfilerThreadExited = { 4, 0x10000 };
constexpr EVENT_DESCRIPTOR MonoProfilerThreadName = { 5, 0x10000 };
constexpr EVENT_DESCRIPTOR MonoProfilerJitDoneVerbose = { 5, 0x10 };
constexpr EVENT_DESCRIPTOR MonoProfilerGCHeapDumpVTableClassReference = { 4, 0x8000000 };
#define NB_PROVIDERS 5
constexpr LTTNG_TRACE_CONTEXT * ALL_LTTNG_PROVIDERS_CONTEXT[NB_PROVIDERS] = { &MICROSOFT_WINDOWS_DOTNETRUNTIME_PROVIDER_LTTNG_Context, &MICROSOFT_WINDOWS_DOTNETRUNTIME_RUNDOWN_PROVIDER_LTTNG_Context, &MICROSOFT_WINDOWS_DOTNETRUNTIME_STRESS_PROVIDER_LTTNG_Context, &MICROSOFT_WINDOWS_DOTNETRUNTIME_PRIVATE_PROVIDER_LTTNG_Context, &MICROSOFT_DOTNETRUNTIME_MONO_PROFILER_PROVIDER_LTTNG_Context };
