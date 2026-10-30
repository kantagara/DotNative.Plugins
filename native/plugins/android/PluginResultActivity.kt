package com.dotnative.plugins

import android.app.Activity
import android.content.Intent
import android.os.Bundle
import java.lang.ref.WeakReference

/** Owns external activities so cancellation can close them through public Android APIs. */
class PluginResultActivity : Activity() {
    private var code = -1

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        code = intent.getIntExtra(CODE, -1)
        val request = requests[code]
        if (request == null) { finish(); return }
        request.activity = WeakReference(this)
        if (savedInstanceState == null) {
            try { startActivityForResult(request.intent, code) }
            catch (_: Exception) {
                requests.remove(code)
                NativeChannels.activityResult(code, RESULT_CANCELED, null)
                finish()
            }
        }
    }

    override fun onActivityResult(requestCode: Int, resultCode: Int, data: Intent?) {
        super.onActivityResult(requestCode, resultCode, data)
        if (requestCode != code) return
        requests.remove(code)
        finish()
        NativeChannels.activityResult(code, resultCode, data)
    }

    override fun onDestroy() {
        if (!isChangingConfigurations && requests[code]?.activity?.get() === this) {
            requests.remove(code)
            NativeChannels.activityResult(code, RESULT_CANCELED, null)
        }
        super.onDestroy()
    }

    companion object {
        private const val CODE = "com.dotnative.plugins.request"
        private class Request(val intent: Intent, var activity: WeakReference<PluginResultActivity>? = null)
        private val requests = mutableMapOf<Int, Request>()

        fun launch(owner: Activity, intent: Intent, code: Int) {
            requests[code] = Request(intent)
            try { owner.startActivity(Intent(owner, PluginResultActivity::class.java).putExtra(CODE, code)) }
            catch (error: Exception) { requests.remove(code); throw error }
        }

        fun cancel(code: Int) {
            val activity = requests.remove(code)?.activity?.get() ?: return
            activity.finishActivity(code)
            activity.finish()
        }

        fun cancelAll() { requests.keys.toList().forEach { cancel(it) } }
    }
}
