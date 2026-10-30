package com.dotnative.plugins

import android.os.Looper
import java.util.ArrayDeque

/** Emits DotNative event envelopes using the native channel codec. */
class NativeEventSink internal constructor(private val emit: (Map<String, Any?>, Boolean) -> Unit) {
    fun success(value: Any?) = emit(mapOf("event" to "data", "value" to value), false)

    fun failure(code: String, message: String, details: Any? = null) =
        emit(
            mapOf("event" to "error", "code" to code, "message" to message, "details" to details),
            true,
        )

    fun endOfStream() = emit(mapOf("event" to "end"), true)
}

/** One subscription, one pending read and at most 64 buffered events per channel. */
class NativeEventChannel(
    descriptor: ChannelDescriptor,
    private val onListen: (Any?, NativeEventSink) -> Unit,
    private val onCancel: (Any?) -> Unit,
) {
    constructor(
        name: String,
        onListen: (Any?, NativeEventSink) -> Unit,
        onCancel: (Any?) -> Unit,
        version: Long = 1,
    ) : this(ChannelDescriptor(name, version), onListen, onCancel)

    private var active = false
    private var ended = false
    private var producing = false
    private var generation = 0L
    private var arguments: Any? = null
    private val queue = ArrayDeque<Map<String, Any?>>()
    private var waiting: PluginReply? = null

    init {
        val channel = NativeChannels.channel(descriptor)
        channel.onReset = { close() }
        channel.onDetach = { close() }
        channel.handle("listen") { args, reply -> listen(args, reply) }
        channel.handle("next") { _, reply -> next(reply) }
        channel.handle("cancel") { _, reply ->
            close()
            reply.success()
        }
    }

    private fun listen(args: Any?, reply: PluginReply) {
        if (active) {
            reply.failure("busy", "Event channel already has a consumer")
            return
        }
        active = true
        ended = false
        producing = true
        arguments = args
        generation++
        val token = generation
        val sink = NativeEventSink { packet, terminal ->
            if (Looper.myLooper() == Looper.getMainLooper()) enqueue(token, packet, terminal)
            else NativeChannels.main.post { enqueue(token, packet, terminal) }
        }
        try {
            onListen(args, sink)
            reply.success()
        } catch (error: Exception) {
            close()
            reply.failure("stream_error", error.message ?: "Could not start event subscription")
        }
    }

    private fun next(reply: PluginReply) {
        when {
            !active -> reply.failure("not_listening", "No event subscription")
            waiting != null -> reply.failure("busy", "An event read is already pending")
            queue.isNotEmpty() -> reply.success(queue.removeFirst())
            ended -> reply.success(mapOf("event" to "end"))
            else -> {
                waiting = reply
                reply.onCancel = { close() }
            }
        }
    }

    private fun enqueue(token: Long, value: Map<String, Any?>, terminal: Boolean) {
        if (!active || ended || token != generation) return
        var packet = value
        var stop = terminal
        try {
            PluginCodec.encode(packet)
        } catch (_: Exception) {
            packet =
                mapOf(
                    "event" to "error",
                    "code" to "invalid_event",
                    "message" to "Native event exceeds codec limits",
                )
            stop = true
        }
        val reply = waiting
        if (reply != null) {
            waiting = null
            reply.success(packet)
        } else if (queue.size >= 64) {
            queue.clear()
            queue.add(
                mapOf(
                    "event" to "error",
                    "code" to "overflow",
                    "message" to "Native event buffer exceeded 64 events",
                ),
            )
            stop = true
        } else {
            queue.add(packet)
        }
        if (stop) {
            ended = true
            stopProducer()
        }
    }

    private fun stopProducer() {
        if (!producing) return
        producing = false
        onCancel(arguments)
    }

    private fun close() {
        active = false
        ended = false
        generation++
        queue.clear()
        val reply = waiting
        waiting = null
        reply?.success(mapOf("event" to "end"))
        stopProducer()
        arguments = null
    }
}
