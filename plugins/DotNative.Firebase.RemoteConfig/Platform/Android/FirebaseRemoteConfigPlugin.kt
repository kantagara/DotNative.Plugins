package com.dotnative.plugins

import android.app.Activity
import com.google.firebase.FirebaseApp
import com.google.firebase.FirebaseOptions
import com.google.firebase.remoteconfig.FirebaseRemoteConfig

class FirebaseRemoteConfigPlugin(activity: Activity) {
    init {
        val channel = NativeChannels.channel("dotnative.firebase.remoteconfig")
        channel.handle("initialize") { args, reply ->
            val fields = args as? Map<*, *> ?: error("Expected Firebase options")
            fun text(name: String) = (fields[name] as? String)?.takeIf { it.isNotBlank() } ?: error("Missing Firebase option: $name")
            val options = FirebaseOptions.Builder().setApiKey(text("apiKey")).setApplicationId(text("applicationId"))
                .setProjectId(text("projectId")).setGcmSenderId(text("senderId")).build()
            val current = FirebaseApp.getApps(activity).firstOrNull { it.name == FirebaseApp.DEFAULT_APP_NAME }
            if (current == null) FirebaseApp.initializeApp(activity.applicationContext, options)
            else require(current.options.applicationId == options.applicationId && current.options.projectId == options.projectId) { "Firebase is already configured for a different app" }
            reply.success()
        }
        channel.handle("setDefaults") { args, reply ->
            val values = (args as? Map<*, *>)?.entries?.associate { (key, value) -> key.toString() to value.toString() } ?: error("Expected defaults")
            FirebaseRemoteConfig.getInstance().setDefaultsAsync(values).addOnCompleteListener { task ->
                if (task.isSuccessful) reply.success() else reply.failure("firebase", "Setting defaults failed")
            }
        }
        channel.handle("fetch") { _, reply ->
            FirebaseRemoteConfig.getInstance().fetchAndActivate().addOnCompleteListener { task ->
                if (task.isSuccessful) reply.success() else reply.failure("firebase", task.exception?.message ?: "Fetch failed")
            }
        }
        channel.handle("getString") { args, reply ->
            val key = (args as? Map<*, *>)?.get("key") as? String ?: error("Missing key")
            reply.success(FirebaseRemoteConfig.getInstance().getString(key))
        }
    }
}
