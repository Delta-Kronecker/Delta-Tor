package kittoku.osc.control

import kittoku.osc.ControlMessage
import kittoku.osc.Result
import kittoku.osc.SharedBridge
import kittoku.osc.SstpMode
import kittoku.osc.Where
import kittoku.osc.client.SSTP_REQUEST_TIMEOUT
import kittoku.osc.client.SstpClient
import kittoku.osc.client.ppp.IpcpClient
import kittoku.osc.client.ppp.Ipv6cpClient
import kittoku.osc.client.ppp.LCPClient
import kittoku.osc.client.ppp.PPPClient
import kittoku.osc.client.ppp.PPP_NEGOTIATION_TIMEOUT
import kittoku.osc.client.ppp.auth.ChapClient
import kittoku.osc.client.ppp.auth.ChapMSCHAPV2Client
import kittoku.osc.client.ppp.auth.EAPClient
import kittoku.osc.client.ppp.auth.EAPMSAuthClient
import kittoku.osc.client.ppp.auth.PAPClient
import kittoku.osc.debug.assertAlways
import kittoku.osc.io.OutgoingManager
import kittoku.osc.io.incoming.IncomingManager
import kittoku.osc.preference.AUTH_PROTOCOL_EAP_MSCHAPv2
import kittoku.osc.preference.AUTH_PROTOCOL_MSCHAPv2
import kittoku.osc.preference.AUTH_PROTOCOl_PAP
import kittoku.osc.terminal.SSL_REQUEST_INTERVAL
import kittoku.osc.unit.sstp.SSTP_MESSAGE_TYPE_CALL_ABORT
import kittoku.osc.unit.sstp.SSTP_MESSAGE_TYPE_CALL_DISCONNECT
import kittoku.osc.unit.sstp.SSTP_MESSAGE_TYPE_CALL_DISCONNECT_ACK
import kotlinx.coroutines.CoroutineExceptionHandler
import kotlinx.coroutines.Job
import kotlinx.coroutines.launch
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.withTimeoutOrNull


/*
 * Copyright (c) 2024 kittoku
 *
 * This source code is licensed under the MIT license found in the
 * LICENSE file in the root directory of this project's source tree.
 * (adapted from the original Open-SSTP-Client: reconnection, notifications and
 * the network observer moved out to DeltaTor's engine; a [SstpMode.PROBE]
 * session stops right after IPCP and never attaches a tunnel)
 */
internal class Controller(private val bridge: SharedBridge) {
    private val isProbe get() = bridge.params.mode == SstpMode.PROBE

    private var sstpClient: SstpClient? = null
    private var pppClient: PPPClient? = null
    private var incomingManager: IncomingManager? = null
    private var outgoingManager: OutgoingManager? = null

    private var lcpClient: LCPClient? = null
    private var papClient: PAPClient? = null
    private var chapClient: ChapClient? = null
    private var eapClient: EAPClient? = null
    private var ipcpClient: IpcpClient? = null
    private var ipv6cpClient: Ipv6cpClient? = null

    private var jobMain: Job? = null

    private val mutex = Mutex()

    private fun attachHandler() {
        bridge.handler = CoroutineExceptionHandler { _, throwable ->
            kill {
                val header = "OSC: ERR_UNEXPECTED"
                bridge.log("$header\n${throwable.stackTraceToString()}")
            }
        }
    }

    internal fun launchJobMain() {
        attachHandler()

        jobMain = bridge.scope.launch(bridge.handler) {
            bridge.attachSSLTerminal()
            if (!isProbe) {
                bridge.attachIPTerminal()
            }


            bridge.sslTerminal!!.initialize()
            if (!expectProceeded(Where.SSL, SSL_REQUEST_INTERVAL)) {
                return@launch
            }


            IncomingManager(bridge).also {
                it.launchJobMain()
                incomingManager = it
            }


            SstpClient(bridge).also {
                sstpClient = it
                incomingManager!!.registerMailbox(it)
                it.launchJobRequest()

                if (!expectProceeded(Where.SSTP_REQUEST, SSTP_REQUEST_TIMEOUT)) {
                    return@launch
                }

                sstpClient!!.launchJobControl()
            }


            PPPClient(bridge).also {
                pppClient = it
                incomingManager!!.registerMailbox(it)
                it.launchJobControl()
            }


            LCPClient(bridge).also {
                incomingManager!!.registerMailbox(it)
                it.launchJobNegotiation()

                if (!expectProceeded(Where.LCP, PPP_NEGOTIATION_TIMEOUT)) {
                    return@launch
                }

                incomingManager!!.unregisterMailbox(it)
            }


            val authTimeout = bridge.params.authTimeout
            when (bridge.currentAuth) {
                AUTH_PROTOCOl_PAP -> PAPClient(bridge).also {
                    incomingManager!!.registerMailbox(it)
                    it.launchJobAuth()

                    if (!expectProceeded(Where.PAP, authTimeout)) {
                        return@launch
                    }

                    incomingManager!!.unregisterMailbox(it)
                }

                AUTH_PROTOCOL_MSCHAPv2 -> ChapMSCHAPV2Client(bridge).also {
                    chapClient = it
                    incomingManager!!.registerMailbox(it)
                    it.launchJobAuth()

                    if (!expectProceeded(Where.CHAP, authTimeout)) {
                        return@launch
                    }
                }

                AUTH_PROTOCOL_EAP_MSCHAPv2 -> EAPMSAuthClient(bridge).also {
                    eapClient = it
                    incomingManager!!.registerMailbox(it)
                    it.launchJobAuth()

                    if (!expectProceeded(Where.EAP, authTimeout)) {
                        return@launch
                    }
                }

                else -> throw NotImplementedError(bridge.currentAuth)
            }


            sstpClient!!.sendCallConnected()


            if (bridge.PPP_IPv4_ENABLED) {
                IpcpClient(bridge).also {
                    incomingManager!!.registerMailbox(it)
                    it.launchJobNegotiation()

                    if (!expectProceeded(Where.IPCP, PPP_NEGOTIATION_TIMEOUT)) {
                        return@launch
                    }

                    incomingManager!!.unregisterMailbox(it)
                }
            }


            if (bridge.PPP_IPv6_ENABLED) {
                Ipv6cpClient(bridge).also {
                    incomingManager!!.registerMailbox(it)
                    it.launchJobNegotiation()

                    if (!expectProceeded(Where.IPV6CP, PPP_NEGOTIATION_TIMEOUT)) {
                        return@launch
                    }

                    incomingManager!!.unregisterMailbox(it)
                }
            }


            if (isProbe) {
                // A probe session goes this far only: IP/DNS have been taken
                // from the server and later errors simply end up in the
                // mailbox until the caller tears the session down.
                assertAlways(bridge.ipTerminal == null)
                bridge.log("OSC: session established (probe)")
                bridge.onNegotiated()
                return@launch
            }

            bridge.ipTerminal!!.initialize()
            if (!expectProceeded(Where.IP, null)) {
                return@launch
            }


            OutgoingManager(bridge).also {
                it.launchJobMain()
                outgoingManager = it
            }

            // The tunnel now passes packets both ways: the moment where a
            // chain becomes a working VPN.
            bridge.onNegotiated()

            expectProceeded(Where.SSTP_CONTROL, null) // wait ERR_ message until disconnection
        }
    }

    private suspend fun expectProceeded(where: Where, timeout: Long?): Boolean {
        val received = if (timeout != null) {
            withTimeoutOrNull(timeout) {
                bridge.controlMailbox.receive()
            } ?: ControlMessage(where, Result.ERR_TIMEOUT)
        } else {
            bridge.controlMailbox.receive()
        }

        if (received.result == Result.PROCEEDED) {
            assertAlways(received.from == where)

            return true
        }

        val lastPacketType = if (received.result == Result.ERR_DISCONNECT_REQUESTED) {
            SSTP_MESSAGE_TYPE_CALL_DISCONNECT_ACK
        } else {
            SSTP_MESSAGE_TYPE_CALL_ABORT
        }

        kill {
            sstpClient?.sendLastPacket(lastPacketType)

            val header = "${received.from.name}: ${received.result.name}"
            var log = header
            if (received.supplement != null) {
                log += "\n${received.supplement}"
            }

            bridge.log(log)
        }

        return false
    }

    internal fun disconnect() { // use if the user want to normally disconnect
        kill {
            sstpClient?.sendLastPacket(SSTP_MESSAGE_TYPE_CALL_DISCONNECT)
        }
    }

    internal fun kill(cleanup: (suspend () -> Unit)? = null) {
        if (!mutex.tryLock()) return

        bridge.scope.launch {
            try {
                jobMain?.cancel()
                cancelClients()

                cleanup?.invoke()

                closeTerminals()
            } finally {
                bridge.onSessionEnded()
            }
        }
    }

    private fun cancelClients() {
        lcpClient?.cancel()
        papClient?.cancel()
        chapClient?.cancel()
        eapClient?.cancel()
        ipcpClient?.cancel()
        ipv6cpClient?.cancel()
        sstpClient?.cancel()
        pppClient?.cancel()
        incomingManager?.cancel()
        outgoingManager?.cancel()
    }

    private fun closeTerminals() {
        bridge.sslTerminal?.close()
        bridge.ipTerminal?.close()
    }
}
