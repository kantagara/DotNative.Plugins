// DotNative's NativeAOT/preview host ABI adapter. All channel and request state
// lives in Kotlin. MAUI/Uno/Avalonia Android use JNI directly and do not load this.
#include <jni.h>
#include <stdint.h>
#include <stdlib.h>
#include <string.h>
#include <dlfcn.h>
#include <pthread.h>
#include <stdatomic.h>
static JavaVM* vm;
static jclass channels;
static jmethodID send_method, poll_method, reset_method, wake_method;
static pthread_mutex_t connect_lock = PTHREAD_MUTEX_INITIALIZER;
static atomic_int bridge_ready;
static uint8_t* cached;
static size_t cached_length;
static JNIEnv* environment(void) {
    JNIEnv* env = NULL;
    if (!atomic_load_explicit(&bridge_ready, memory_order_acquire)
        || (*vm)->GetEnv(vm, (void**)&env, JNI_VERSION_1_6) != JNI_OK) return NULL;
    return env;
}
void dotnative_request_tick(void) {
    if (!atomic_load_explicit(&bridge_ready, memory_order_acquire)) return;
    JNIEnv* env = NULL;
    int attached = 0;
    jint status = (*vm)->GetEnv(vm, (void**)&env, JNI_VERSION_1_6);
    if (status == JNI_EDETACHED) {
        if ((*vm)->AttachCurrentThread(vm, &env, NULL) != JNI_OK) return;
        attached = 1;
    } else if (status != JNI_OK) return;
    (*env)->CallStaticVoidMethod(env, channels, wake_method);
    if ((*env)->ExceptionCheck(env)) (*env)->ExceptionClear(env);
    if (attached) (*vm)->DetachCurrentThread(vm);
}
int32_t dotnative_send_plugin(const uint8_t* bytes, size_t length) {
    JNIEnv* env = environment();
    if (!env || !channels || !bytes || length < 16 || length > 1048592) return -1;
    jbyteArray data = (*env)->NewByteArray(env, (jsize)length);
    if (!data) return -1;
    (*env)->SetByteArrayRegion(env, data, 0, (jsize)length, (const jbyte*)bytes);
    jint result = (*env)->CallStaticIntMethod(env, channels, send_method, data);
    (*env)->DeleteLocalRef(env, data);
    if ((*env)->ExceptionCheck(env)) { (*env)->ExceptionClear(env); return -1; }
    return result;
}
size_t dotnative_poll_plugin(uint8_t* buffer, size_t capacity) {
    JNIEnv* env = environment();
    if (!env || !channels) return 0;
    if (!cached) {
        jbyteArray data = (*env)->CallStaticObjectMethod(env, channels, poll_method);
        if ((*env)->ExceptionCheck(env)) { (*env)->ExceptionClear(env); return 0; }
        if (!data) return 0;
        jsize length = (*env)->GetArrayLength(env, data);
        if (length < 13 || length > 1048588) { (*env)->DeleteLocalRef(env, data); return 0; }
        cached = malloc((size_t)length);
        if (!cached) { (*env)->DeleteLocalRef(env, data); return 0; }
        (*env)->GetByteArrayRegion(env, data, 0, length, (jbyte*)cached);
        (*env)->DeleteLocalRef(env, data);
        cached_length = (size_t)length;
    }
    size_t length = cached_length;
    if (!buffer || capacity < length) return length;
    memcpy(buffer, cached, length); free(cached); cached = NULL; cached_length = 0;
    return length;
}
int32_t dotnative_plugins_reset(void) {
    JNIEnv* env = environment();
    if (!env || !channels) return 0;
    (*env)->CallStaticVoidMethod(env, channels, reset_method);
    if ((*env)->ExceptionCheck(env)) { (*env)->ExceptionClear(env); return -1; }
    free(cached); cached = NULL; cached_length = 0;
    return 0;
}
JNIEXPORT void JNICALL Java_com_dotnative_plugins_NativeChannels_connectNative(JNIEnv* env, jclass type, jstring library) {
    // Worker continuations may wake the host while a new Activity reconnects.
    // Publish one immutable process-lifetime class reference, never replace it
    // underneath CallStaticVoidMethod on another thread.
    pthread_mutex_lock(&connect_lock);
    if (!atomic_load_explicit(&bridge_ready, memory_order_acquire)) {
        if ((*env)->GetJavaVM(env, &vm) != JNI_OK
            || !(channels = (*env)->NewGlobalRef(env, type))
            || !(send_method = (*env)->GetStaticMethodID(env, type, "send", "([B)I"))
            || !(poll_method = (*env)->GetStaticMethodID(env, type, "poll", "()[B"))
            || !(reset_method = (*env)->GetStaticMethodID(env, type, "reset", "()V"))
            || !(wake_method = (*env)->GetStaticMethodID(env, type, "wakeHost", "()V"))) {
            if (channels) (*env)->DeleteGlobalRef(env, channels);
            channels = NULL;
            pthread_mutex_unlock(&connect_lock);
            return;
        }
        atomic_store_explicit(&bridge_ready, 1, memory_order_release);
    }
    pthread_mutex_unlock(&connect_lock);
    const char* name = (*env)->GetStringUTFChars(env, library, NULL);
    void* host = dlopen(name, RTLD_NOW | RTLD_NOLOAD);
    (*env)->ReleaseStringUTFChars(env, library, name);
    typedef void (*Connect)(int32_t (*)(const uint8_t*, size_t), size_t (*)(uint8_t*, size_t), int32_t (*)(void));
    Connect connect = (Connect)dlsym(host ? host : RTLD_DEFAULT, "dotnative_app_set_service_transport");
    if (connect) connect(dotnative_send_plugin, dotnative_poll_plugin, dotnative_plugins_reset);
    if (host) dlclose(host);
}
