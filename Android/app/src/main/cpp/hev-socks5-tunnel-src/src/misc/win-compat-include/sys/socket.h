#ifndef _HEV_COMPAT_SYS_SOCKET_H
#define _HEV_COMPAT_SYS_SOCKET_H
/* MinGW-w64 ships this header too; this copy is guaranteed identical to the
 * winsock2-based one and is pre-included by hev-mingw.h before any macro. */
#include <winsock2.h>
#include <ws2tcpip.h>
#include <sys/uio.h>
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

/* POSIX msghdr: Winsock has WSAMSG instead, and mingw's headers provide
 * neither struct. Consumers in this tree never use ancillary data (they
 * all set msg_control = NULL), so the shim recvmsg/sendmsg in hev-mingw.h
 * only works with name + iovec. */
#ifndef _HEV_COMPAT_MSGHDR
#define _HEV_COMPAT_MSGHDR
struct msghdr {
    void *msg_name;
    socklen_t msg_namelen;
    struct iovec *msg_iov;
    size_t msg_iovlen;
    void *msg_control;
    size_t msg_controllen;
    int msg_flags;
};
#endif
#endif
