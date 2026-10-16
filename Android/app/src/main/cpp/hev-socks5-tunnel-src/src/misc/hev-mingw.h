/*
 ============================================================================
 Name        : hev-mingw.h
 Description : MinGW-w64 compatibility layer for hev-socks5-tunnel.

 The upstream tree targets MSYS2/Cygwin on Windows, where POSIX sockets,
 pipes, signal.h and friends are emulated by msys-2.0.dll. That runtime is
 also what trips antivirus products in third-party processes (its DllMain
 fails with ERROR_DLL_INIT_FAILED / 0x8007045A), so this port builds the
 same sources with the MinGW-w64 toolchain instead: the result is a plain
 Windows DLL whose only dependencies are system DLLs.

 Force-included by Windows/native/build-hev.sh (`-include`) into every
 translation unit. Rules that make this work:

   1. All system headers that declare the POSIX names we redirect
      (winsock2, unistd, io, fcntl, sys/socket, sys/uio, ...) are included
      HERE, at the top, before any macro below. Later per-file #includes of
      those headers are guard-skipped, so no system declaration of read,
      recv, close, ... can ever collide with the redirections.
   2. Redirects are function-like macros: an identifier that is not
      immediately followed by `(` (e.g. chan->read.used) never expands.
   3. Every socket created anywhere in the tree is a raw Winsock SOCKET
      stored in an `int`; the IOCP reactor treats the int as the handle
      directly (no get_osfhandle). close()/read()/write() therefore map to
      closesocket()/recv()/send(), and winsock errors are translated into
      errno so the retry loops (`errno == EAGAIN`) behave as on POSIX.
   4. File descriptors of any other kind (CRT fds from the two loggers)
      never exist: file logging is compiled to a no-op under MinGW.
 ============================================================================
 */

#ifndef HEV_MINGW_H
#define HEV_MINGW_H

/* gcc defines __ASSEMBLER__ while preprocessing .S files; the whole layer is
 * C, and the Makefiles compile .S with CCFLAGS (which force-includes this
 * header), so skip everything to keep the assembly clean. */
#if defined(__ASSEMBLER__)
/* nothing */
#elif defined(__MINGW32__) && !defined(__MSYS__)

#ifndef _WIN32_WINNT
#define _WIN32_WINNT 0x0A00
#endif

/* Winsock first: windows.h and the rest must not pull the old winsock.h. */
#include <winsock2.h>
#include <ws2tcpip.h>

#include <io.h>
#include <fcntl.h>
#include <unistd.h>
#include <sys/types.h>
#include <sys/stat.h>
#include <errno.h>
#include <stdarg.h>
#include <stddef.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>
#include <signal.h>
#include <setjmp.h>
#include <pthread.h>
#include <poll.h>
#include <sys/time.h>

/* POSIX headers this tree includes, provided by win-compat-include/ (their
 * content is winsock2-based and identical whether or not MinGW ships its
 * own copy: ours wins the include-path race either way). */
#include <sys/socket.h>
#include <sys/uio.h>
#include <sys/ioctl.h>
#include <sys/resource.h>
#include <sys/wait.h>
#include <netinet/in.h>
#include <arpa/inet.h>
#include <netdb.h>
#include <net/if.h>

/* --- names MinGW does not provide --- */
#ifndef PF_LOCAL
#define PF_LOCAL AF_UNIX
#endif
/* Always take the ioctl(fd, FIONBIO) nonblocking path: MinGW has no
 * accept4() and its SOCK_NONBLOCK (if any) is not honored by winsock. */
#ifdef SOCK_NONBLOCK
#undef SOCK_NONBLOCK
#endif
#ifdef SOCK_CLOEXEC
#undef SOCK_CLOEXEC
#endif
#ifndef MSG_DONTWAIT
#define MSG_DONTWAIT 0
#endif
#ifndef MSG_NOSIGNAL
#define MSG_NOSIGNAL 0
#endif
#ifndef WNOHANG
#define WNOHANG 1
#endif
/* POSIX shutdown() how-values; winsock uses SD_*. */
#ifndef SHUT_RD
#define SHUT_RD SD_RECEIVE
#define SHUT_WR SD_SEND
#define SHUT_RDWR SD_BOTH
#endif
/* Signals the MS CRT's signal() does not name; winsock never raises SIGPIPE,
 * and the only uses here are signal(SIGPIPE, SIG_IGN) / signal(SIGCHLD, ..)
 * which compile and stay inert. */
#ifndef SIGPIPE
#define SIGPIPE 13
#endif
#ifndef SIGCHLD
#define SIGCHLD 17
#endif
/* MinGW's fcntl.h has no O_NONBLOCK (CRT files are always blocking). The
 * only uses are hev_task_io_open/openat OR-ing it into open() flags, where a
 * zero value is harmless - file descriptors here are the no-op loggers. */
#ifndef O_NONBLOCK
#define O_NONBLOCK 0
#endif

/* --- shims: defined before the macros that redirect to them --- */

/* Last raw WSAGetLastError() seen by a shim, for diagnostics (the LOG_E
 * call sites log this alongside errno, since hev_mw_sockerr collapses most
 * WSA codes onto EIO). */
static int hev_mw_last_wsa;

static inline int
hev_mw_sockerr (int wsaerr)
{
    hev_mw_last_wsa = wsaerr;
    switch (wsaerr) {
    case WSAEWOULDBLOCK:
        errno = EAGAIN;
        break;
    case WSAEINTR:
        errno = EINTR;
        break;
    case WSAECONNRESET:
        errno = ECONNRESET;
        break;
    case WSAECONNABORTED:
        errno = ECONNABORTED;
        break;
    case WSAETIMEDOUT:
        errno = ETIMEDOUT;
        break;
    case WSAEMSGSIZE:
        errno = EMSGSIZE;
        break;
    case WSAECONNREFUSED:
        errno = ECONNREFUSED;
        break;
    case WSAENETUNREACH:
    case WSAEHOSTUNREACH:
        errno = EHOSTUNREACH;
        break;
    default:
        errno = EIO;
        break;
    }
    return -1;
}

static inline ssize_t
hev_mw_recv (int fd, void *buf, size_t len, int flags)
{
    int res;

    res = recv ((SOCKET)(intptr_t)fd, (char *)buf, (int)len, flags);
    return res == SOCKET_ERROR ? hev_mw_sockerr (WSAGetLastError ()) : res;
}

static inline ssize_t
hev_mw_send (int fd, const void *buf, size_t len, int flags)
{
    int res;

    res = send ((SOCKET)(intptr_t)fd, (const char *)buf, (int)len, flags);
    return res == SOCKET_ERROR ? hev_mw_sockerr (WSAGetLastError ()) : res;
}

static inline ssize_t
hev_mw_recvfrom (int fd, void *buf, size_t len, int flags,
                 struct sockaddr *addr, socklen_t *addr_len)
{
    int res;

    res = recvfrom ((SOCKET)(intptr_t)fd, (char *)buf, (int)len, flags, addr,
                    (int *)addr_len);
    return res == SOCKET_ERROR ? hev_mw_sockerr (WSAGetLastError ()) : res;
}

static inline ssize_t
hev_mw_sendto (int fd, const void *buf, size_t len, int flags,
               const struct sockaddr *addr, socklen_t addr_len)
{
    int res;

    res = sendto ((SOCKET)(intptr_t)fd, (const char *)buf, (int)len, flags,
                  addr, (int)addr_len);
    return res == SOCKET_ERROR ? hev_mw_sockerr (WSAGetLastError ()) : res;
}

/* Winsock has no recvmsg/sendmsg (only WSAMSG-flavoured WSARecv*), and this
 * tree's callers only ever use iovec + optional name, never cmsg. */
static inline ssize_t
hev_mw_recvmsg (int fd, struct msghdr *msg, int flags)
{
    WSABUF *wsa;
    DWORD recvd = 0;
    DWORD wflags;
    size_t i;
    int res;

    msg->msg_flags = 0;
    if (msg->msg_iovlen == 0)
        return 0;

    wsa = (WSABUF *)malloc (msg->msg_iovlen * sizeof (WSABUF));
    if (!wsa) {
        errno = ENOMEM;
        return -1;
    }
    for (i = 0; i < msg->msg_iovlen; i++) {
        wsa[i].buf = (CHAR *)msg->msg_iov[i].iov_base;
        wsa[i].len = (ULONG)msg->msg_iov[i].iov_len;
    }

    wflags = (DWORD)flags;
    if (msg->msg_name) {
        int fromlen = (int)msg->msg_namelen;

        res = WSARecvFrom ((SOCKET)(intptr_t)fd, wsa, (DWORD)msg->msg_iovlen,
                           &recvd, &wflags, (struct sockaddr *)msg->msg_name,
                           &fromlen, NULL, NULL);
        if (res == SOCKET_ERROR) {
            free (wsa);
            return hev_mw_sockerr (WSAGetLastError ());
        }
        msg->msg_namelen = fromlen;
    }
    else {
        res = WSARecv ((SOCKET)(intptr_t)fd, wsa, (DWORD)msg->msg_iovlen,
                       &recvd, &wflags, NULL, NULL);
        if (res == SOCKET_ERROR) {
            free (wsa);
            return hev_mw_sockerr (WSAGetLastError ());
        }
    }

    free (wsa);
    return (ssize_t)recvd;
}

static inline ssize_t
hev_mw_sendmsg (int fd, const struct msghdr *msg, int flags)
{
    WSABUF *wsa;
    DWORD sent = 0;
    size_t i;
    int res;

    if (msg->msg_iovlen == 0)
        return 0;

    wsa = (WSABUF *)malloc (msg->msg_iovlen * sizeof (WSABUF));
    if (!wsa) {
        errno = ENOMEM;
        return -1;
    }
    for (i = 0; i < msg->msg_iovlen; i++) {
        wsa[i].buf = (CHAR *)msg->msg_iov[i].iov_base;
        wsa[i].len = (ULONG)msg->msg_iov[i].iov_len;
    }

    if (msg->msg_name) {
        res = WSASendTo ((SOCKET)(intptr_t)fd, wsa, (DWORD)msg->msg_iovlen,
                         &sent, (DWORD)flags,
                         (const struct sockaddr *)msg->msg_name,
                         (int)msg->msg_namelen, NULL, NULL);
    }
    else {
        res = WSASend ((SOCKET)(intptr_t)fd, wsa, (DWORD)msg->msg_iovlen,
                       &sent, (DWORD)flags, NULL, NULL);
    }
    if (res == SOCKET_ERROR) {
        free (wsa);
        return hev_mw_sockerr (WSAGetLastError ());
    }

    free (wsa);
    return (ssize_t)sent;
}

static inline int
hev_mw_connect (int fd, const struct sockaddr *addr, socklen_t addr_len)
{
    int res;

    res = connect ((SOCKET)(intptr_t)fd, addr, (int)addr_len);
    if (res == SOCKET_ERROR) {
        int wsaerr = WSAGetLastError ();

        if (wsaerr == WSAEWOULDBLOCK)
            errno = EINPROGRESS;
        else if (wsaerr == WSAEALREADY || wsaerr == WSAEINVAL)
            errno = EALREADY;
        else if (wsaerr == WSAEISCONN)
            errno = EISCONN;
        else
            errno = EIO;
        return -1;
    }
    return res;
}

static inline int
hev_mw_accept (int fd, struct sockaddr *addr, socklen_t *addr_len)
{
    SOCKET res;

    res = accept ((SOCKET)(intptr_t)fd, addr, (int *)addr_len);
    return res == INVALID_SOCKET ? hev_mw_sockerr (WSAGetLastError ())
                                 : (int)res;
}

/* Winsock takes `const char *optval', POSIX takes `const void *': a shim so
 * callers keep passing `int *' without a pointer-type error. */
static inline int
hev_mw_setsockopt (int fd, int level, int optname, const void *optval,
                   socklen_t optlen)
{
    return setsockopt ((SOCKET)(intptr_t)fd, level, optname,
                       (const char *)optval, (int)optlen);
}

static inline ssize_t
hev_mw_read (int fd, void *buf, size_t len)
{
    /* Under this port every readable fd is a socket (file logging is a
     * no-op), so POSIX read() is winsock recv() with no flags. */
    return hev_mw_recv (fd, buf, len, 0);
}

static inline ssize_t
hev_mw_write (int fd, const void *buf, size_t len)
{
    return hev_mw_send (fd, buf, len, 0);
}

static inline ssize_t
hev_mw_readv (int fd, const struct iovec *iov, int iovcnt)
{
    ssize_t total = 0;
    int i;

    for (i = 0; i < iovcnt; i++) {
        ssize_t res = hev_mw_recv (fd, iov[i].iov_base, iov[i].iov_len, 0);

        if (res < 0)
            return total ? total : res;
        total += res;
        if ((size_t)res < iov[i].iov_len)
            break;
    }
    return total;
}

static inline ssize_t
hev_mw_writev (int fd, const struct iovec *iov, int iovcnt)
{
    ssize_t total = 0;
    int i;

    for (i = 0; i < iovcnt; i++) {
        ssize_t res = hev_mw_send (fd, iov[i].iov_base, iov[i].iov_len, 0);

        if (res < 0)
            return total ? total : res;
        total += res;
        if ((size_t)res < iov[i].iov_len)
            break;
    }
    return total;
}

static inline int
hev_mw_close (int fd)
{
    return closesocket ((SOCKET)(intptr_t)fd) == SOCKET_ERROR ? -1 : 0;
}

static inline int
hev_mw_pipe (int fds[2])
{
    return _pipe (fds, 4096, _O_BINARY);
}

static inline int
hev_mw_ioctl (int fd, unsigned long request, ...)
{
    va_list ap;
    char *arg;
    u_long value;

    if (request != FIONBIO)
        return -1;

    va_start (ap, request);
    arg = va_arg (ap, char *);
    va_end (ap);

    value = (arg && *(int *)arg == 0) ? 0ul : 1ul;
    return ioctlsocket ((SOCKET)(intptr_t)fd, FIONBIO, &value) == 0 ? 0 : -1;
}

static inline int
hev_mw_openat (int dirfd, const char *pathname, int flags, ...)
{
    (void)dirfd;
    (void)pathname;
    (void)flags;
    return -1;
}

/* AF_UNIX socketpair() does not exist on Windows. A loopback TCP pair has
 * the same semantics for the two users of socketpair() in this tree (the
 * stop-wakeup event pair and the DNS resolver pair): connected, bidirectional,
 * stream, and after WSAEventSelect/ioctlsocket nonblocking like its siblings.
 */
static inline int
hev_mw_socketpair (int domain, int type, int protocol, int socket_vector[2])
{
    struct sockaddr_in addr;
    socklen_t addr_len;
    SOCKET listener, peer[2];
    u_long nonblock = 1;
    int i;

    (void)domain;
    (void)type;
    (void)protocol;

    listener = socket (AF_INET, SOCK_STREAM, IPPROTO_TCP);
    if (listener == INVALID_SOCKET)
        return hev_mw_sockerr (WSAGetLastError ());

    memset (&addr, 0, sizeof (addr));
    addr.sin_family = AF_INET;
    addr.sin_addr.s_addr = htonl (INADDR_LOOPBACK);
    addr.sin_port = 0;

    if (bind (listener, (struct sockaddr *)&addr, sizeof (addr)) == SOCKET_ERROR)
        goto err_listener;
    if (listen (listener, 1) == SOCKET_ERROR)
        goto err_listener;

    addr_len = sizeof (addr);
    if (getsockname (listener, (struct sockaddr *)&addr, (int *)&addr_len) == SOCKET_ERROR)
        goto err_listener;

    peer[0] = socket (AF_INET, SOCK_STREAM, IPPROTO_TCP);
    if (peer[0] == INVALID_SOCKET)
        goto err_listener;

    if (connect (peer[0], (struct sockaddr *)&addr, sizeof (addr)) == SOCKET_ERROR) {
        int err = WSAGetLastError ();
        closesocket (peer[0]);
        return hev_mw_sockerr (err);
    }

    peer[1] = accept (listener, NULL, NULL);
    closesocket (listener);
    if (peer[1] == INVALID_SOCKET) {
        int err = WSAGetLastError ();
        closesocket (peer[0]);
        return hev_mw_sockerr (err);
    }

    for (i = 0; i < 2; i++) {
        if (ioctlsocket (peer[i], FIONBIO, &nonblock) == SOCKET_ERROR) {
            int err = WSAGetLastError ();
            closesocket (peer[0]);
            closesocket (peer[1]);
            return hev_mw_sockerr (err);
        }
    }

    socket_vector[0] = (int)peer[0];
    socket_vector[1] = (int)peer[1];
    return 0;

err_listener:
    {
        int err = WSAGetLastError ();
        closesocket (listener);
        return hev_mw_sockerr (err);
    }
}

static inline int
hev_mw_daemon (int nochld, int noclose)
{
    (void)nochld;
    (void)noclose;
    return 0;
}

static inline int
hev_mw_usleep (unsigned int usec)
{
    Sleep (usec / 1000u + (usec % 1000u ? 1u : 0u));
    return 0;
}

static inline int
hev_mw_dup (int fd)
{
    return _dup (fd);
}

static inline int
hev_mw_dup2 (int oldfd, int newfd)
{
    return _dup2 (oldfd, newfd);
}

/* --- redirections (function-like: bare identifiers never expand) ---
 *
 * Only for the hev translation units (HEV_MW_SOCKET_API, defined by
 * build-hev.sh). lwip's tcp/udp/raw pcb structs declare callback members
 * literally named recv/poll/send and call them as `(pcb)->recv (arg)`,
 * which a function-like macro would rewrite into a nonexistent member.
 * lwip's own Makefile compiles with -UHEV_MW_SOCKET_API so it never sees
 * these macros; the winsock functions and my struct/type shims remain. */
#ifdef HEV_MW_SOCKET_API

#define recv(fd, buf, len, flags) hev_mw_recv ((fd), (buf), (len), (flags))
#define send(fd, buf, len, flags) hev_mw_send ((fd), (buf), (len), (flags))
#define recvfrom(fd, buf, len, flags, addr, addr_len) \
    hev_mw_recvfrom ((fd), (buf), (len), (flags), (addr), (addr_len))
#define sendto(fd, buf, len, flags, addr, addr_len) \
    hev_mw_sendto ((fd), (buf), (len), (flags), (addr), (addr_len))
#define recvmsg(fd, msg, flags) hev_mw_recvmsg ((fd), (msg), (flags))
#define sendmsg(fd, msg, flags) hev_mw_sendmsg ((fd), (msg), (flags))
#define connect(fd, addr, addr_len) \
    hev_mw_connect ((fd), (addr), (addr_len))
#define setsockopt(fd, level, optname, optval, optlen) \
    hev_mw_setsockopt ((fd), (level), (optname), (optval), (optlen))
#define accept(fd, addr, addr_len) \
    hev_mw_accept ((fd), (addr), (addr_len))
#define read(fd, buf, len) hev_mw_read ((fd), (buf), (len))
#define write(fd, buf, len) hev_mw_write ((fd), (buf), (len))
#define readv(fd, iov, iovcnt) hev_mw_readv ((fd), (iov), (iovcnt))
#define writev(fd, iov, iovcnt) hev_mw_writev ((fd), (iov), (iovcnt))
#define close(fd) hev_mw_close ((fd))
#define pipe(fds) hev_mw_pipe ((fds))
#define ioctl(fd, request, ...) hev_mw_ioctl ((fd), (request), __VA_ARGS__)
#define openat(dirfd, pathname, flags, ...) \
    hev_mw_openat ((dirfd), (pathname), (flags))
#define socketpair(domain, type, protocol, socket_vector) \
    hev_mw_socketpair ((domain), (type), (protocol), (socket_vector))
#define daemon(nochld, noclose) hev_mw_daemon ((nochld), (noclose))
#define usleep(usec) hev_mw_usleep ((usec))
#define dup(fd) hev_mw_dup ((fd))
#define dup2(oldfd, newfd) hev_mw_dup2 ((oldfd), (newfd))

#endif /* HEV_MW_SOCKET_API */

/* The task scheduler's context switch is written against the BSD-style
 * _setjmp/_longjmp (one/two args); the MS CRT only offers setjmp/longjmp.
 * setjmp.h is pre-included above, so its own setjmp/longjmp macros (if any)
 * resolve the real calls; blue-painting stops the chain from looping back
 * into _setjmp here. */
#define _setjmp(env) setjmp (env)
#define _longjmp(env, val) longjmp ((env), (val))

#endif /* defined(__MINGW32__) && !defined(__MSYS__) */

#endif /* HEV_MINGW_H */
