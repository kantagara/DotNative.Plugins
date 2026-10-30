package com.dotnative.plugins
import android.app.Activity
import java.security.MessageDigest
fun StandaloneProbe(activity: Activity) {
    val channel = NativeChannels.channel("dotnative.standalone.probe")
    channel.handle("sha256") { value, reply ->
        val bytes = value as? ByteArray
        if (bytes == null) reply.failure("arguments", "Expected bytes")
        else reply.success(MessageDigest.getInstance("SHA-256").digest(bytes))
    }
    channel.handle("echo") { value, reply -> reply.success(value) }
    channel.handle("fail") { _, reply -> reply.failure("fixture", "Native failure") }
    channel.handle("wait") { _, _ -> }
}
