/*
 ============================================================================
 Name        : poll.h
 Description : MinGW-w64 replacement for the POSIX <poll.h>.

 MinGW-w64's winsock2.h already provides struct pollfd / WSAPOLLFD
 (fd is a SOCKET) and the POLL* macros, but no poll() function and no
 <poll.h> header at all. This stub supplies the header plus a poll()
 redirect over WSAPoll.

 Because struct pollfd here IS WSAPOLLFD (same tag, same layout, winsock's
 own POLL* encoding), hev_mw_poll hands the array to WSAPoll directly - no
 conversion needed. Every consumer in this tree goes through these same
 macros, so event/revent values are consistent end to end.

 Every fd handed to poll() in this build is a socket: pipes are emulated
 by loopback socketpairs (hev-mingw.h), and the two CRT-fd file loggers
 are compiled to no-ops under MinGW.
 ============================================================================
 */

#ifndef _HEV_MW_POLL_H_
#define _HEV_MW_POLL_H_

#include <windows.h>
#include <winsock2.h>
#include <errno.h>

/* Fallback in case a winsock2.h variant skips the POLL* block. mingw-w64
 * defines them together with struct pollfd, so normally guard-skipped. */
#ifndef POLLIN
#define POLLRDNORM 0x0100
#define POLLRDBAND 0x0200
#define POLLIN     (POLLRDNORM | POLLRDBAND)
#define POLLPRI    0x0400
#define POLLWRNORM 0x0010
#define POLLOUT    (POLLWRNORM)
#define POLLWRBAND 0x0020
#define POLLERR    0x0001
#define POLLHUP    0x0002
#define POLLNVAL   0x0004
#endif

static inline int
hev_mw_poll (struct pollfd *fds, unsigned long nfds, int timeout)
{
    int res;

    if (nfds == 0)
    {
        Sleep (timeout < 0 ? INFINITE : (DWORD)timeout);
        return 0;
    }

    res = WSAPoll ((PWSAPOLLFD)fds, (ULONG)nfds, (INT)timeout);
    if (res == SOCKET_ERROR)
    {
        int err = WSAGetLastError ();
        if (err == WSAEWOULDBLOCK)
            errno = EAGAIN;
        else if (err == WSAEINTR)
            errno = EINTR;
        else
            errno = EINVAL;
        return -1;
    }

    return res;
}

/* Function-like: an identifier `poll' not followed by `(' never expands. */
#define poll(...) hev_mw_poll (__VA_ARGS__)

#endif /* _HEV_MW_POLL_H_ */
