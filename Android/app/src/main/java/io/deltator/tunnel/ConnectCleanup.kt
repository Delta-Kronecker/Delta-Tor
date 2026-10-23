package io.deltator.tunnel

import android.content.Context
import android.system.Os
import io.deltator.util.AppLog as Log
import java.io.File
import java.net.InetSocketAddress
import java.net.ServerSocket

/**
 * The pre-flight a connect runs before it is allowed to start a single new
 * process or listener.
 *
 * A connect used to assume a clean slate. The parts of the engine were torn down
 * by their own cursors -- [race] starts with `stopAll`, [TorSocksBridge.start]
 * stops the previous bridge, [TorRunner.start] stops its own processes -- but
 * every one of those cursors only knows about the objects still alive in this
 * process. What none of them covered:
 *
 *  - a core that survived `destroyForcibly`, which [TorRunner.stop] then reports
 *    as `confirmedStopped = false` and forgets; its listening socket outlives the
 *    runner that was responsible for it.
 *  - the Tor and lyrebird processes of an app instance that is no longer around.
 *    If Android killed the app between connects -- a crash, a background limit,
 *    a reboot of the process between a failed connect and the user pressing the
 *    ring again -- the `runners` map died with it and nothing in the new instance
 *    holds a [java.lang.Process] to stop, while the orphans can still sit there
 *    holding every port the new connect wants to bind.
 *  - the reverse hold: a previous session's bridge listener or a previous
 *    session's tun2socks, both of which live in this process and are not the
 *    runners' responsibility at all.
 *
 * A survivor holding a runner port takes its transport down with a confusing
 * half-truth (Tor prints its own bind failure and the race marks the runner
 * failed); a survivor holding the base port takes the *connect* down, because
 * the bridge cannot bind. So instead of hoping, the connect now runs this first:
 *
 *  1. close this process's own leftovers -- the bridge listener and tun2socks
 *     of a session that this instance still owns some plumbing of.
 *  2. wait out the runners it does know about ([ParallelTorManager.stopAllAndWait]),
 *     which includes their confirmed-dead bookkeeping.
 *  3. sweep `/proc` for anything left of the cores as such: a process of this
 *     app's UID whose argv0 is the bundled `libtor.so` or `libobfs4proxy.so` is
 *     ours by definition -- no other binary in this device's system names those
 *     files -- and it is killed with SIGKILL. The UID check is what makes the
 *     scan safe to run: a Tor bundled by another app runs under another UID,
 *     and the sweep neither identifies nor kills it.
 *  4. verify the whole port reservation basePort..basePort+19 is bindable and,
 *     for any port still held, map the listening socket to a pid through
 *     `/proc/net/tcp` + `/proc/[pid]/fd`, kill holders that are ours, and wait.
 *     One budget covers the whole range; a holder that belongs to another app
 *     cannot be killed and is reported instead, which is all the system allows.
 *
 * Step 3 matches on argv0 rather than a pid list saved to disk. A saved registry
 * would cover the same set -- the sweep sees every process of our UID, tracked
 * or not -- and a stale pid can be reassigned by the kernel between runs, which
 * an argv0 match cannot be fooled by: the check is on what the process *is*,
 * not on what something once claimed it was.
 */
object ConnectCleanup {

    private const val TAG = "ConnectCleanup"

    /** Same waiting pitch [ParallelTorManager] and [TorVpnService] teardown use. */
    private const val PORT_POLL_MS = 250L

    /**
     * The whole port sweep gets this much, not each port. A sales pitch of
     * per-port waits would turn one stubborn holder into a minute of two of
     * nothing happening before the connect even begins; a compact budget means
     * the fastest realistic teardown (everything already dead) costs one
     * bind-test per port and nothing more.
     */
    private const val PORT_SWEEP_BUDGET_MS = 15_000L

    /** How long a SIGKILL gets to take effect before the holder is reported. */
    private const val KILL_GRACE_MS = 2_000L

    /**
     * One port that is still held at the end of the sweep.
     *
     * @param pid the listening process, when the kernel let us map the socket to
     *   it; null when the holder could not be attributed at all
     * @param command its argv[0], empty when it could not be read
     * @param ours true when the holder is owned by this app's UID, false when it
     *   belongs to something else, null when there was no pid to attribute. What
     *   a returned [Report.blocked] carries is false or null, plus one special
     *   true: a holder that is this app's own process, which nothing inside can
     *   kill.
     */
    data class Holder(
        val port: Int,
        val pid: Int?,
        val command: String,
        val ours: Boolean?
    ) {
        /** How to name it on screen: "another app (pid 4321, /system/bin/x)". */
        fun describe(): String = when (pid) {
            null -> "an unknown process"
            else -> if (command.isNotBlank()) "pid $pid ($command)" else "pid $pid"
        }
    }

    /**
     * What the pre-flight did.
     *
     * @param orphanCoresKilled leftover Tor/lyrebird processes found by the
     *   /proc sweep and SIGKILLed, from this and from previous app instances
     * @param ownHoldersKilled port holders owned by this app's UID, found on the
     *   wire during the sweep and killed so the port could be reused
     * @param blocked ports still held when the budget ran out. The caller
     *   decides which of these are fatal
     */
    data class Report(
        val orphanCoresKilled: Int,
        val ownHoldersKilled: Int,
        val blocked: List<Holder>
    ) {
        val everyPortFree: Boolean get() = blocked.isEmpty()
    }

    /**
     * The whole pre-flight, in the order the ports need it.
     *
     * The bridge listener is closed before the runner teardown, not after: it
     * holds basePort itself, and [ParallelTorManager.stopAllAndWait] verifies
     * port freedom by bind-testing, so a listener of ours left open would make
     * that verification fail for a port the connect needs next.
     */
    fun run(context: Context, basePort: Int): Report {
        val startedAt = System.currentTimeMillis()
        Log.i(TAG, "---- cleanup begin (basePort $basePort) ----")

        // 1. This process's own listener leftovers.
        TorSocksBridge.stop()
        if (HevSocks5Tunnel.isRunning()) {
            HevSocks5Tunnel.stop()
            Log.i(TAG, "cleanup | leftover tun2socks stopped")
        }

        // 2. Runners of the previous session this instance still knows about.
        try {
            if (!ParallelTorManager.stopAllAndWait(basePort)) {
                Log.w(TAG, "cleanup | reservation not fully free after the known teardown")
            }
        } catch (e: Exception) {
            Log.e(TAG, "cleanup | known-runner teardown failed: ${e.message}")
        }

        // 3. Cores of previous app instances, killed by name, not by memory.
        val orphans = killOrphanCores(context)

        // 4. The range itself, one shared budget. Held ports get their owner
        //    looked up, and owners that are ours are killed; everything else
        //    has to be waited out or reported.
        val myUid = android.os.Process.myUid()
        val myPid = android.os.Process.myPid()
        val reservation = ParallelTorManager.reservedPorts(basePort)
        val deadline = System.currentTimeMillis() + PORT_SWEEP_BUDGET_MS
        val killedPids = mutableSetOf<Int>()
        var ownPidWarned = false
        var ownHolders = 0
        var lastDescriptions = emptyMap<Int, Holder>()
        var stillHeld = reservation.filter { !isPortFree(it) }

        while (stillHeld.isNotEmpty()) {
            val descriptions = mutableMapOf<Int, Holder>()
            val inodes = listeningInodes()
            val pidByInode = pidsForInodes(inodes.values.toSet())
            for (port in stillHeld) {
                val pid = inodes[port]?.let { pidByInode[it] }
                val holder = Holder(
                    port = port,
                    pid = pid,
                    command = pid?.let { argv0(it) } ?: "",
                    ours = pid?.let { uidOf(it) == myUid }
                )
                descriptions[port] = holder
                when {
                    pid != null && pid != myPid && pid !in killedPids && holder.ours == true -> {
                        Log.i(TAG, "cleanup | port $port held by own leftover pid $pid " +
                            "(${holder.command.ifBlank { "?" }}) - killed")
                        killedPids.add(pid)
                        if (killProcess(pid)) {
                            ownHolders++
                        } else {
                            Log.w(TAG, "cleanup | pid $pid did not die from SIGKILL")
                        }
                    }
                    pid == myPid && !ownPidWarned -> {
                        // Nothing in this app is allowed to hold a reserved port
                        // of another session, so when it does, a second connect
                        // can never run again until it releases; report it loudly
                        // rather than spend the budget polling in silence, and
                        // say it once, not once per poll.
                        ownPidWarned = true
                        Log.e(TAG, "cleanup | port $port is held by this app's own " +
                            "process and cannot be killed from inside")
                    }
                }
            }
            lastDescriptions = descriptions
            try { Thread.sleep(PORT_POLL_MS) } catch (_: InterruptedException) {}
            stillHeld = reservation.filter { !isPortFree(it) }
            if (System.currentTimeMillis() >= deadline) break
        }

        if (stillHeld.isEmpty()) {
            logEnd(startedAt, orphans, ownHolders, 0)
            return Report(orphans, ownHolders, emptyList())
        }
        // Still held when the budget ran out. These are not killed (not ours, or
        // not attributable), so the caller chooses what each of them costs.
        val blocked = stillHeld.map { lastDescriptions[it] ?: Holder(it, null, "", null) }
        Log.w(TAG, "cleanup | still held after the budget: " +
            blocked.joinToString(", ") { "port ${it.port} by ${it.describe()}" })
        logEnd(startedAt, orphans, ownHolders, blocked.size)
        return Report(orphans, ownHolders, blocked)
    }

    private fun logEnd(startedAt: Long, orphans: Int, holders: Int, blocked: Int) {
        Log.i(TAG, "---- cleanup end (${System.currentTimeMillis() - startedAt}ms, " +
            "orphans=$orphans holders=$holders blocked=$blocked) ----")
    }

    /**
     * Every process of this UID whose argv0 is one of the two cores is a Tor or
     * lyrebird the current runners map does not own, and it is killed. The paths
     * are matched by suffix, not equality, on purpose: the app's
     * `nativeLibraryDir` moves when the app is updated (the APK path embeds a
     * content hash), so an orphan of an install *before* the update names a
     * directory the current context no longer does. Same-UID ownership is the
     * security boundary; the suffix is only the discriminator inside it.
     */
    private fun killOrphanCores(context: Context): Int {
        val myUid = android.os.Process.myUid()
        val coreNames = setOf("libtor.so", "libobfs4proxy.so")
        var killed = 0
        for (pid in runningPids()) {
            if (pid == android.os.Process.myPid()) continue
            if (uidOf(pid) != myUid) continue
            val cmd = argv0(pid) ?: continue
            if (coreNames.none { cmd.endsWith("/" + it) }) continue
            Log.i(TAG, "cleanup | leftover core pid=$pid ($cmd) killed")
            if (killProcess(pid)) killed++ else Log.e(TAG, "cleanup | pid $pid did not die from SIGKILL")
        }
        return killed
    }

    // --- /proc plumbing --------------------------------------------------------

    private fun runningPids(): List<Int> =
        File("/proc").list { _, name -> name.isNotEmpty() && name.all { it.isDigit() } }
            ?.mapNotNull { it.toIntOrNull() }
            ?: emptyList()

    /** The argv[0] of a process, null when unreadable (foreign app or dead). */
    private fun argv0(pid: Int): String? = try {
        val raw = File("/proc/$pid/cmdline").readBytes().toString(Charsets.UTF_8)
        raw.substringBefore('\u0000').ifBlank { null }
    } catch (_: Exception) {
        null
    }

    /** The real UID of a process, null when unreadable. */
    private fun uidOf(pid: Int): Int? = try {
        File("/proc/$pid/status").useLines { lines ->
            lines.firstOrNull { it.startsWith("Uid:") }
                ?.split(Regex("\\s+"))
                ?.getOrNull(1)
                ?.toIntOrNull()
        }
    } catch (_: Exception) {
        null
    }

    /**
     * Kill process [pid] via SIGKILL and wait until `/proc` no longer carries
     * it: a listening socket is only released when the kernel reaps the owner,
     * which is exactly the loop this whole object is here to close.
     */
    private fun killProcess(pid: Int): Boolean {
        try {
            android.os.Process.killProcess(pid)
        } catch (e: Exception) {
            Log.e(TAG, "cleanup | kill of pid $pid failed: ${e.message}")
            return false
        }
        val deadline = System.currentTimeMillis() + KILL_GRACE_MS
        while (System.currentTimeMillis() < deadline) {
            if (!File("/proc/$pid").exists()) return true
            try { Thread.sleep(PORT_POLL_MS) } catch (_: InterruptedException) {}
        }
        return !File("/proc/$pid").exists()
    }

    /**
     * port -> socket inode, for every LISTEN socket on the device, IPv4 and IPv6.
     * The ports read back out of the kernel's own view of the world; anything
     * unreadable just means that row is invisible here, what the bind-test below
     * deals with on its own.
     */
    private fun listeningInodes(): Map<Int, Long> {
        val result = HashMap<Int, Long>()
        for (path in listOf("/proc/net/tcp", "/proc/net/tcp6")) {
            val lines = try {
                File(path).readLines()
            } catch (_: Exception) {
                continue
            }
            for (line in lines.drop(1)) {
                val fields = line.trim().split(Regex("\\s+"))
                // sl local rem st (others) uid timeout inode ...
                if (fields.size < 10 || fields[3] != "0A") continue
                val port = fields[1].substringAfter(':').toIntOrNull(16) ?: continue
                val inode = fields[9].toLongOrNull() ?: continue
                result[port] = inode
            }
        }
        return result
    }

    /**
     * inode -> pid, in one pass over the fd lists: the inodes of the sockets the
     * caller is interested in get matched while walking each pid once, so the
     * cost of naming every holder does not grow with how many ports are held.
     * fd lists of other UIDs are closed to apps by SELinux -- throws there are
     * skipped, and the seen world is our own processes' anyway, which is what
     * this can act on.
     */
    private fun pidsForInodes(inodes: Set<Long>): Map<Long, Int> {
        if (inodes.isEmpty()) return emptyMap()
        val result = HashMap<Long, Int>()
        for (pid in runningPids()) {
            val fds = try {
                File("/proc/$pid/fd").list()
            } catch (_: Exception) {
                continue
            } ?: continue
            for (fd in fds) {
                val link = try {
                    Os.readlink("/proc/$pid/fd/$fd")
                } catch (_: Exception) {
                    continue
                }
                if (!link.startsWith("socket:[")) continue
                val inode = link.substringAfter('[').substringBefore(']').toLongOrNull() ?: continue
                if (inode in inodes && inode !in result) result[inode] = pid
            }
        }
        return result
    }

    /** Bind-test one port, exactly as the binders themselves will bind it. */
    private fun isPortFree(port: Int): Boolean = try {
        ServerSocket().use { s ->
            s.reuseAddress = true
            s.bind(InetSocketAddress("127.0.0.1", port))
            true
        }
    } catch (_: Exception) {
        false
    }
}
