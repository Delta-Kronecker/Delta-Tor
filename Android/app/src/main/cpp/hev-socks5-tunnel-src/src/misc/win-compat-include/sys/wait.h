#ifndef _HEV_COMPAT_SYS_WAIT_H
#define _HEV_COMPAT_SYS_WAIT_H
/* The only POSIX wait() user (hev-exec.c) is compiled to a no-op stub on
 * Windows; the header exists so its #include still resolves. */
#ifndef WNOHANG
#define WNOHANG 1
#endif
#endif
