#ifndef _HEV_COMPAT_NETDB_H
#define _HEV_COMPAT_NETDB_H
/* getaddrinfo()/freeaddrinfo()/gai_strerror() live in ws2tcpip.h on Windows. */
#include <ws2tcpip.h>
#endif
