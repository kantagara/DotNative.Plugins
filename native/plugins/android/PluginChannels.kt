package com.dotnative.plugins

import android.app.Activity
import android.content.Intent
import android.os.Handler
import android.os.Looper
import java.io.ByteArrayOutputStream
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.nio.charset.CodingErrorAction
import java.security.MessageDigest

class ChannelDescriptor internal constructor(internal val id: Int) {
    constructor(name: String, version: Long = 1) : this(identity(name, version))

    companion object {
        private fun identity(name: String, version: Long): Int {
            require(name.isNotBlank() && version in 1..0xffffffffL) {
                "Channel name and version must be valid"
            }
            val digest =
                MessageDigest.getInstance("SHA-256")
                    .digest("$name/v$version".toByteArray(Charsets.UTF_8))
            val id = ByteBuffer.wrap(digest).order(ByteOrder.LITTLE_ENDIAN).int
            require(id != 0) { "Channel name resolves to a reserved transport ID" }
            return id
        }
    }
}

object PluginCodec {
    const val LIMIT = 1048576

    fun encode(value: Any?): ByteArray {
        val out = ByteArrayOutputStream()
        fun number(n: Long, count: Int) {
            repeat(count) { out.write((n ushr (it * 8)).toInt() and 255) }
        }
        fun write(v: Any?, depth: Int) {
            require(depth <= 16 && out.size() <= LIMIT)
            when (v) {
                null -> out.write(0)
                is Boolean -> out.write(if (v) 2 else 1)
                is Double -> {
                    out.write(8)
                    number(v.toRawBits(), 8)
                }
                is Float -> {
                    out.write(8)
                    number(v.toDouble().toRawBits(), 8)
                }
                is Number -> {
                    require(v is Long || v is Int)
                    out.write(3)
                    number(v.toLong(), 8)
                }
                is String -> {
                    val bytes = v.toByteArray(Charsets.UTF_8)
                    require(bytes.size <= LIMIT - out.size() - 5)
                    out.write(4)
                    number(bytes.size.toLong(), 4)
                    out.write(bytes)
                }
                is ByteArray -> {
                    require(v.size <= LIMIT - out.size() - 5)
                    out.write(5)
                    number(v.size.toLong(), 4)
                    out.write(v)
                }
                is Map<*, *> -> {
                    require(v.size <= 4096)
                    out.write(6)
                    number(v.size.toLong(), 4)
                    v.forEach { (k, x) ->
                        require(k is String)
                        write(k, depth + 1)
                        write(x, depth + 1)
                    }
                }
                is List<*> -> {
                    require(v.size <= 4096)
                    out.write(7)
                    number(v.size.toLong(), 4)
                    v.forEach { write(it, depth + 1) }
                }
                else -> error("Unsupported plugin value")
            }
        }
        write(value, 0)
        require(out.size() <= LIMIT)
        return out.toByteArray()
    }

    fun decode(bytes: ByteArray): Any? {
        require(bytes.size <= LIMIT)
        val data = ByteBuffer.wrap(bytes).order(ByteOrder.LITTLE_ENDIAN)
        fun count(max: Int): Int {
            val n = data.int
            require(n in 0..max)
            return n
        }
        fun blob(): ByteArray {
            val n = count(LIMIT)
            require(n <= data.remaining())
            return ByteArray(n).also { data.get(it) }
        }
        fun read(depth: Int): Any? {
            require(depth <= 16)
            return when (data.get().toInt()) {
                0 -> null
                1 -> false
                2 -> true
                3 -> data.long
                8 -> data.double
                4 ->
                    Charsets.UTF_8.newDecoder()
                        .onMalformedInput(CodingErrorAction.REPORT)
                        .decode(ByteBuffer.wrap(blob()))
                        .toString()
                5 -> blob()
                6 ->
                    linkedMapOf<String, Any?>().also { map ->
                        repeat(count(4096)) {
                            val k = read(depth + 1)
                            require(k is String && !map.containsKey(k))
                            map[k] = read(depth + 1)
                        }
                    }
                7 -> List(count(4096)) { read(depth + 1) }
                else -> error("Unknown plugin value")
            }
        }
        val result = read(0)
        require(!data.hasRemaining())
        return result
    }
}

class PluginReply internal constructor(private val id: Long) {
    private var completed = false
    var onCancel: (() -> Unit)? = null

    fun success(value: Any? = null): Boolean = finish(0, value)

    fun failure(code: String, message: String, details: Any? = null) {
        finish(1, mapOf("code" to code, "message" to message, "details" to details))
    }

    internal fun cancel() {
        if (!completed) {
            completed = true
            runCatching { onCancel?.invoke() }
            onCancel = null
        }
    }

    private fun finish(status: Int, value: Any?): Boolean {
        check(Looper.myLooper() == Looper.getMainLooper())
        if (completed) return false
        completed = true
        onCancel = null
        NativeChannels.pending.remove(id)
        val payload =
            try {
                PluginCodec.encode(value)
            } catch (_: Exception) {
                return NativeChannels.reply(
                    id,
                    1,
                    PluginCodec.encode(
                        mapOf(
                            "code" to "invalid_reply",
                            "message" to "Plugin returned an invalid value",
                        ),
                    ),
                )
            }
        return NativeChannels.reply(id, status, payload)
    }
}

class NativeChannel {
    var methodCallHandler: ((String, Any?, PluginReply) -> Unit)? = null
    var onDetach: (() -> Unit)? = null
    internal val handlers = mutableMapOf<String, (Any?, PluginReply) -> Unit>()
    var onReset: (() -> Unit)? = null

    fun handle(method: String, handler: (Any?, PluginReply) -> Unit) {
        check(!handlers.containsKey(method))
        handlers[method] = handler
    }
}

object NativeChannels {
    val main = Handler(Looper.getMainLooper())
    internal val pending = mutableMapOf<Long, PluginReply>()
    private val channels = mutableMapOf<Int, NativeChannel>()
    private val results = mutableMapOf<Int, (Int, Intent?) -> Unit>()
    val activityResultListeners = mutableListOf<(Int, Int, Intent?) -> Boolean>()
    val permissionResultListeners = mutableListOf<(Int, Array<String>, IntArray) -> Boolean>()
    val newIntentListeners = mutableListOf<(Intent) -> Boolean>()
    private var requestCode = 4096

    @JvmStatic private external fun connectNative(library: String)
    private var hostWake: java.lang.reflect.Method? = null
    @JvmStatic fun wakeHost() { hostWake?.invoke(null) }

    @JvmStatic fun connectHost() {
        // A standalone .NET Android host has no DotNative Java runner.
        val config = try { Class.forName("com.dotnative.runtime.BuildConfig") }
            catch (_: ClassNotFoundException) { return }
        val library = config.getField("DOTNATIVE_LIBRARY").get(null) as String
        hostWake = try { Class.forName("com.dotnative.runtime.NativeHost").getMethod("requestTick") }
            catch (_: NoSuchMethodException) { null }
        System.loadLibrary("dotnative_plugin_bridge")
        connectNative("lib$library.so")
    }

    private val requests = mutableMapOf<Long, Int>()
    private val replies = java.util.ArrayDeque<ByteArray>()
    private var replyBytes = 0

    @JvmStatic fun send(bytes: ByteArray): Int {
        check(Looper.myLooper() == Looper.getMainLooper())
        if (bytes.size !in 16..1048592) return -1
        val header = ByteBuffer.wrap(bytes).order(ByteOrder.LITTLE_ENDIAN)
        val id = header.long
        val channel = header.int
        val kind = header.int
        if (id == 0L || (kind != 1 && kind != 2) || (kind == 2 && bytes.size != 16)) return -1
        if (kind == 1) {
            if (requests.containsKey(id) || requests.size + replies.size >= 256) return -1
            requests[id] = channel
        } else {
            if (requests[id] != channel) return 0
            requests.remove(id)
        }
        if (channels.containsKey(channel)) dispatch(bytes)
        else if (kind == 1) reply(id, 1, PluginCodec.encode(mapOf("code" to "missing_plugin", "message" to "Plugin channel is not registered on this platform.")))
        return 0
    }

    @JvmStatic fun poll(): ByteArray? {
        check(Looper.myLooper() == Looper.getMainLooper())
        val value = replies.pollFirst() ?: return null
        replyBytes -= value.size
        return value
    }

    @JvmStatic fun reset() {
        check(Looper.myLooper() == Looper.getMainLooper())
        requests.clear(); replies.clear(); replyBytes = 0
        val calls = pending.values.toList()
        pending.clear()
        calls.forEach { it.cancel() }
        channels.values.toList().forEach { it.onReset?.invoke() }
    }

    private fun nativeReply(bytes: ByteArray): Int {
        check(Looper.myLooper() == Looper.getMainLooper())
        val header = ByteBuffer.wrap(bytes).order(ByteOrder.LITTLE_ENDIAN)
        val id = header.long
        if (requests.remove(id) == null) return 1
        val overloaded = replyBytes + bytes.size > 15 * 1048576
        val value = if (overloaded) {
            val payload = PluginCodec.encode(mapOf("code" to "busy", "message" to "Plugin reply queue is full."))
            ByteBuffer.allocate(12 + payload.size).order(ByteOrder.LITTLE_ENDIAN).putLong(id).putInt(1).put(payload).array()
        } else bytes
        replies.addLast(value); replyBytes += value.size
        wakeHost()
        return if (overloaded) 2 else 0
    }

    fun channel(name: String, version: Long = 1): NativeChannel =
        channel(ChannelDescriptor(name, version))

    fun channel(id: ChannelDescriptor): NativeChannel {
        check(Looper.myLooper() == Looper.getMainLooper())
        return channels.getOrPut(id.id) {
            check(id.id != 0 && channels.size < 256)
            NativeChannel()
        }
    }

    internal fun reply(id: Long, status: Int, payload: ByteArray): Boolean {
        val packet =
            ByteBuffer.allocate(12 + payload.size)
                .order(ByteOrder.LITTLE_ENDIAN)
                .putLong(id)
                .putInt(status)
                .put(payload)
                .array()
        return nativeReply(packet) == 0
    }

    @JvmStatic
    fun dispatch(bytes: ByteArray) {
        check(Looper.myLooper() == Looper.getMainLooper())
        val header = ByteBuffer.wrap(bytes).order(ByteOrder.LITTLE_ENDIAN)
        val id = header.long
        val channel = header.int
        val kind = header.int
        if (kind == 2) {
            pending.remove(id)?.cancel()
            return
        }
        if (kind == 3) {
            val calls = pending.values.toList()
            pending.clear()
            calls.forEach { it.cancel() }
            channels[channel]?.onReset?.invoke()
            return
        }
        val reply = PluginReply(id)
        pending[id] = reply
        try {
            val call =
                PluginCodec.decode(bytes.copyOfRange(16, bytes.size)) as? Map<*, *>
                    ?: error("Expected a method call")
            val method = call["method"] as? String ?: error("Expected a method name")
            val handler = channels[channel]?.handlers?.get(method)
            if (handler != null) handler(call["arguments"], reply)
            else {
                val fallback = channels[channel]?.methodCallHandler
                if (fallback != null) fallback(method, call["arguments"], reply)
                else reply.failure("not_implemented", "Unknown plugin method")
            }
        } catch (error: Exception) {
            reply.failure("invalid_arguments", error.message ?: "Plugin request failed")
        }
    }

    fun launch(activity: Activity, intent: Intent, callback: (Int, Intent?) -> Unit): Int {
        val code = allocateRequestCode()
        results[code] = callback
        try {
            PluginResultActivity.launch(activity, intent, code)
        } catch (error: Exception) {
            results.remove(code)
            throw error
        }
        return code
    }

    fun allocateRequestCode(): Int {
        check(requestCode < 65535) { "Plugin request codes exhausted" }
        return requestCode++
    }

    fun requestPermissions(activity: Activity, code: Int, permissions: Array<String>) {
        PluginResultFragment.get(activity).requestPermissions(permissions, code)
    }

    fun cancelResult(code: Int) {
        results.remove(code)
        PluginResultActivity.cancel(code)
    }

    @JvmStatic
    fun activityResult(code: Int, status: Int, data: Intent?) {
        results.remove(code)?.invoke(status, data)
        activityResultListeners.toList().forEach { it(code, status, data) }
    }

    @JvmStatic
    fun permissionResult(code: Int, permissions: Array<String>, grants: IntArray) {
        permissionResultListeners.toList().forEach { it(code, permissions, grants) }
    }

    @JvmStatic
    fun newIntent(intent: Intent) {
        newIntentListeners.toList().forEach { it(intent) }
    }

    @JvmStatic
    fun detach() {
        check(Looper.myLooper() == Looper.getMainLooper())
        requests.clear(); replies.clear(); replyBytes = 0
        results.clear()
        PluginResultActivity.cancelAll()
        channels.values.forEach {
            it.onReset?.invoke()
            it.onDetach?.invoke()
        }
        val calls = pending.values.toList()
        pending.clear()
        calls.forEach { it.cancel() }
        channels.clear()
        activityResultListeners.clear()
        permissionResultListeners.clear()
        newIntentListeners.clear()
    }
}
