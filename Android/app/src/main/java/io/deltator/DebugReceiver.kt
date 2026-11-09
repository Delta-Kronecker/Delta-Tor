package io.deltator

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.os.Build
import kotlinx.coroutines.launch
import io.deltator.util.AppLog as Log

/** Debug/test receiver to forward actions to TorVpnService (adb). */
class DebugReceiver : BroadcastReceiver() {
    companion object { private const val TAG = "DebugReceiver" }
    override fun onReceive(context: Context, intent: Intent?) {
        val action = intent?.action ?: return
        android.util.Log.i(TAG, "received $action")
        Log.i(TAG, "received $action")

        if (action == "io.deltator.ACTION_AUTO_MATCH") {
            val typeStr = intent.getStringExtra("type") ?: "WARP"
            val type = try {
                io.deltator.feature.chain.ChainProfileType.valueOf(typeStr)
            } catch (_: Exception) {
                io.deltator.feature.chain.ChainProfileType.WARP
            }
            kotlinx.coroutines.CoroutineScope(kotlinx.coroutines.Dispatchers.IO).launch {
                val res = io.deltator.feature.chain.ChainProfileManager.autoMatchNearest(type)
                android.util.Log.i(TAG, "Auto-match completed: ${res?.summary}")
            }
            return
        }

        if (action == "io.deltator.ACTION_PING_ALL") {
            val typeStr = intent.getStringExtra("type") ?: "WARP"
            val type = try {
                io.deltator.feature.chain.ChainProfileType.valueOf(typeStr)
            } catch (_: Exception) {
                io.deltator.feature.chain.ChainProfileType.WARP
            }
            kotlinx.coroutines.CoroutineScope(kotlinx.coroutines.Dispatchers.IO).launch {
                val res = io.deltator.feature.chain.ChainProfileManager.pingAll(type)
                android.util.Log.i(TAG, "Ping all $type completed: ${res.size} profiles tested")
            }
            return
        }

        val svc = Intent(intent).setClass(context, TorVpnService::class.java)
        // Prefer startService when the FGS is already running; fall back to startForegroundService.
        try {
            context.startService(svc)
        } catch (e: Exception) {
            try {
                if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) context.startForegroundService(svc)
                else context.startService(svc)
            } catch (e2: Exception) {
                android.util.Log.w(TAG, "start service failed: ${e2.message}")
                Log.w(TAG, "start service failed: ${e2.message}")
            }
        }
    }
}
