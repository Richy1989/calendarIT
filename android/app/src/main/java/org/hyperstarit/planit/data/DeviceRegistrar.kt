package org.hyperstarit.planit.data

import android.content.Context
import android.os.Build
import android.provider.Settings
import org.hyperstarit.planit.BuildConfig
import org.hyperstarit.planit.auth.SessionManager
import org.hyperstarit.planit.auth.SessionStore
import org.hyperstarit.planit.net.ApiClient
import org.hyperstarit.planit.net.RegisterDeviceRequest

/** Tells the server about this device (/api/devices), so the user sees it and push can reach it. */
class DeviceRegistrar(
    private val context: Context,
    private val api: ApiClient,
    private val store: SessionStore,
) {
    suspend fun register(session: SessionManager) {
        val id = store.deviceId()
        val request = RegisterDeviceRequest(name = deviceName(), platform = "android", appVersion = BuildConfig.VERSION_NAME)
        session.authorized { server, token -> api.registerDevice(server, token, id, request) }
    }

    private fun deviceName(): String =
        Settings.Global.getString(context.contentResolver, Settings.Global.DEVICE_NAME)?.takeIf { it.isNotBlank() }
            ?: "${Build.MANUFACTURER} ${Build.MODEL}"
}
