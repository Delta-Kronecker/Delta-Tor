#ifndef _HEV_COMPAT_SYS_RESOURCE_H
#define _HEV_COMPAT_SYS_RESOURCE_H
/* MinGW has no <sys/resource.h>. The only user (hev-utils.c set_limit_nofile)
 * is satisfied by trivial implementations: Windows cannot change fd limits
 * this way, and the tunnel never depends on the return value. */
typedef unsigned long rlim_t;
struct rlimit {
    rlim_t rlim_cur;
    rlim_t rlim_max;
};
#ifndef RLIMIT_NOFILE
#define RLIMIT_NOFILE 7
#endif
static inline int getrlimit (int resource, struct rlimit *lim)
{
    (void)resource;
    lim->rlim_cur = 65536;
    lim->rlim_max = 65536;
    return 0;
}
static inline int setrlimit (int resource, const struct rlimit *lim)
{
    (void)resource;
    (void)lim;
    return 0;
}
#endif
