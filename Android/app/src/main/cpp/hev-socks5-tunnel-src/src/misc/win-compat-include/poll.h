/*
 ============================================================================
 Name        : poll.h
 Description : MinGW-w64 replacement for the POSIX <poll.h>.

 MinGW-w64 does not ship poll.h at all (no glibc poll(), only WSAPoll in
 ws2_32). The hev tree includes <poll.h> from several core files, so this
 stub provides struct pollfd, the POLL* macros and a poll() redirect over
 WSAPoll.

 Encoding note: the POLL* macros are winsock2.h's own bits (POLLRDNORM
 etc.), and every consumer in this tree goes through these same macros, so
 event/revent values pass straight through; the only real work is the
 layout conversion between struct pollfd (int fd) and WSAPOLLFD
 (SOCKET fd, 64-bit). Every fd in this build is a raw Winsock SOCKET
 stored in an int (see src/misc/hev-mingw.h), so the casts are lossless
 in practice.

 All fds handed to poll() here are sockets: pipes are emulated by loopback
 socketpairs (hev-mingw.h), and the two CRT-fd file loggers are compiled
 to no-ops under MinGW.
 ============================================================================
 */

#ifndef _HEV_MW_POLL_H_
#define _HEV_MW_POLL_H_

#include <winsock2.h>
#include <errno.h>
#include <stdlib.h>

/* Winsock's POLL* encoding. Guarded as a block: the mingw-w64 winsock2.h
 * defines these together. */
#ifndef POLLIN
#define POLLRDNORM 0x0100
#define POLLRDBAND 0x0200
#define POLLIN     (POLLRDNORM | POLLRDBAND)
#define POLLPRI    0x0400
#define POLLOUT    0x0010
#define POLLWRNORM 0x0100
#define POLLWRBAND 0x0020
#define POLLERR    0x0008
#define POLLHUP    0x0010
#define POLLNVAL   0x0004
#define POLLMSG    0x0400
#endif

#ifndef _HEV_MW_POLLFD_DEFINED
#define _HEV_MW_POLLFD_DEFINED
struct pollfd
{
    int fd;
    short events;
    short revents;
};
#endif

#ifndef _HEV_MW_NFDS_T_DEFINED
#define _HEV_MW_NFDS_T_DEFINED
typedef unsigned long nfds_t;
#endif

static inline int
hev_mw_poll (struct pollfd *fds, nfds_t nfds, int timeout)
{
    WSAPOLLFD *wfds;
    nfds_t i;
    int res;

    if (nfds == 0)
    {
        Sleep (timeout < 0 ? INFINITE : (DWORD)timeout);
        return 0;
    }

    wfds = (WSAPOLLFD *)malloc (nfds * sizeof (*wfds));
    if (!wfds)
    {
        errno = ENOMEM;
        return -1;
    }

    for (i = 0; i < nfds; i++)
    {
        wfds[i].fd = (SOCKET)(intptr_t)fds[i].fd;
        wfds[i].events = (SHORT)fds[i].events;
        wfds[i].revents = 0;
        fds[i].revents = 0;
    }

    res = WSAPoll (wfds, (ULONG)nfds, timeout);
    if (res == SOCKET_ERROR)
    {
        int err = WSAGetLastError ();
        if (err == WSAEWOULDBLOCK)
            errno = EAGAIN;
        else if (err == WSAEINTR)
            errno = EINTR;
        else if (err == WSAEINVAL)
            errno = EINVAL;
        else
            errno = EINVAL;
        res = -1;
    }
    else if (res > 0)
    {
        for (i = 0; i < nfds; i++)
            fds[i].revents = (short)wfds[i].revents;
    }

    free (wfds);
    return res;
}

/* Function-like: an identifier `poll' not followed by `(' never expands
 * (e.g. the token inside hev_task_io_poll is not `poll' anyway). */
#define poll(...) hev_mw_poll (__VA_ARGS__)

#endif /* _HEV_MW_POLL_H_ */
