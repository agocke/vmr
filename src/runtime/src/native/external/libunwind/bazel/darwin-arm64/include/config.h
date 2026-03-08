// This is a custom file written for .NET Core's build system

#ifndef __LIBUNWIND_CONFIG_H__
#define __LIBUNWIND_CONFIG_H__

// On macOS, these are provided by include/remote headers
#define HAVE_ELF_H
#define HAVE_ENDIAN_H

/* #undef HAVE_SYS_ELF_H */
/* #undef HAVE_SYS_ENDIAN_H */
/* #undef HAVE_LINK_H */
/* #undef HAVE_SYS_LINK_H */

#define HAVE__BUILTIN_UNREACHABLE

#define HAVE_STDALIGN_H

/* #undef HAVE_PIPE2 */

#endif
