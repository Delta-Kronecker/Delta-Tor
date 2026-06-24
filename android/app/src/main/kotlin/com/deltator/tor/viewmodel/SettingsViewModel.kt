package com.deltator.tor.viewmodel

import android.app.Application
import androidx.lifecycle.AndroidViewModel
import com.deltator.tor.core.Config
import com.deltator.tor.core.SettingsManager

class SettingsViewModel(application: Application) : AndroidViewModel(application) {
    val settings = SettingsManager(application)

    fun getBoolean(key: String) = settings.getBoolean(key)
    fun getInt(key: String) = settings.getInt(key)
    fun getString(key: String) = settings.getString(key)

    fun setBoolean(key: String, value: Boolean) = settings.setBoolean(key, value)
    fun setInt(key: String, value: Int) = settings.setInt(key, value)
    fun setString(key: String, value: String) = settings.setString(key, value)

    fun clearData() {
        settings.clearAll()
    }
}
