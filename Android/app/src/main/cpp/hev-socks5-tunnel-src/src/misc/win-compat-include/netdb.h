#ifndef _HEV_COMPAT_NETDB_H
#define _HEV_COMPAT_NETDB_H
/* getaddrinfo()/freeaddrinfo()/gai_strerror() live in ws2tcpip.h on Windows. */
#include <ws2tcpip.h>

/* EAI_SYSTEM is glibc-specific (means "check errno"); MinGW's winsock
 * getaddrinfo never returns it. hev-task-dns.c has a case for it, so give
 * it a value that cannot collide with winsock's EAI_* (those are all
 * positive WSA* codes). */
#ifndef EAI_SYSTEM
#define EAI_SYSTEM (-1)
#endif
#endif
