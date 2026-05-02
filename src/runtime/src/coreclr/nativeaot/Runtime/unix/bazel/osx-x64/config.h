#ifndef _PAL_CONFIG_H_INCLUDED
#define _PAL_CONFIG_H_INCLUDED 1

// Hardcoded for darwin-x64 (matching CMake configure results from
// src/coreclr/nativeaot/Runtime/unix/configure.cmake).

#define HAVE_AUXV_HWCAP_H 0
#define HAVE_UCONTEXT_H 0

#define HAVE_PRCTL_H 0
#define HAVE_PR_SET_PTRACER 0
#define HAVE_PTHREAD_ATTR_GET_NP 0
#define HAVE_PTHREAD_GETATTR_NP 0
#define HAVE_PTHREAD_CONDATTR_SETCLOCK 0
#define HAVE_PTHREAD_GETTHREADID_NP 0

#define HAVE_CLOCK_NANOSLEEP 0
#define HAVE_SYSCTLBYNAME 1

#define HAVE_GREGSET_T 0
#define HAVE___GREGSET_T 0

#define HAVE_SIGINFO_T 1

#define HAVE_LWP_SELF 0
#define HAVE_CLOCK_GETTIME_NSEC_NP 1
#define HAVE_SCHED_GETAFFINITY 0

#endif
