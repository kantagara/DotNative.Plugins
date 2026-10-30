package com.dotnative.plugins
import android.app.Activity
fun PPluginNativeSymbolPlugin(activity: Activity) {
    NativeChannels.channel("dotnative.plugins.pluginchannel").handle("read") { _, reply ->
        reply.success("Android: " + android.os.Build.MODEL)
    }
}