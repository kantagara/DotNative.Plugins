package com.dotnative.plugins

import android.app.Activity
import android.app.Fragment

/** Owns permission delivery without requiring framework Activity overrides. */
@Suppress("DEPRECATION")
class PluginResultFragment : Fragment() {
    override fun onRequestPermissionsResult(
        requestCode: Int, permissions: Array<out String>, grantResults: IntArray
    ) {
        NativeChannels.permissionResult(requestCode, permissions.map { it }.toTypedArray(), grantResults)
    }

    companion object {
        fun get(activity: Activity): PluginResultFragment {
            check(!activity.isFinishing && !activity.isDestroyed) { "Plugin activity is stopping" }
            val manager = activity.fragmentManager
            val tag = "com.dotnative.plugins.results"
            (manager.findFragmentByTag(tag) as? PluginResultFragment)?.let { return it }
            val fragment = PluginResultFragment()
            manager.beginTransaction().add(fragment, tag).commit()
            manager.executePendingTransactions()
            return fragment
        }
    }
}
