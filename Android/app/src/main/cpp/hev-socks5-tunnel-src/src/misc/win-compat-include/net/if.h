#ifndef _HEV_COMPAT_NET_IF_H
#define _HEV_COMPAT_NET_IF_H
/* IFNAMSIZ for hev-tunnel.h; interface enumeration goes through Win32
 * GetAdaptersAddresses in RouteManager, not through if_nameindex(). */
#ifndef IFNAMSIZ
#define IFNAMSIZ 16
#endif
#endif
