#ifndef _HEV_COMPAT_SYS_SOCKET_H
#define _HEV_COMPAT_SYS_SOCKET_H
/* MinGW-w64 ships this header too; this copy is guaranteed identical to the
 * winsock2-based one and is pre-included by hev-mingw.h before any macro. */
#include <winsock2.h>
#include <ws2tcpip.h>
#ifndef PF_LOCAL
#define PF_LOCAL AF_UNIX
#endif
#ifndef PF_INET
#define PF_INET AF_INET
#endif
#ifndef MSG_DONTWAIT
#define MSG_DONTWAIT 0
#endif
#ifndef MSG_NOSIGNAL
#define MSG_NOSIGNAL 0
#endif
#endif
