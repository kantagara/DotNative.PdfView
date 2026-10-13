package com.dotnative.plugins
class PluginReply {
 var onCancel: (() -> Unit)? = null
 val done = java.util.concurrent.CountDownLatch(1)
 var error: String? = null
 var message: String? = null
 var value: Any? = null
 private var completed = false
 fun success(value: Any? = null): Boolean { if (completed) return false; completed = true; this.value = value; onCancel = null; done.countDown(); return true }
 fun failure(code: String, message: String, details: Any? = null) { if (completed) return; error = code; this.message = message; success() }
 fun cancel() { if (completed) return; completed = true; onCancel?.invoke(); onCancel = null; done.countDown() }
}
class HarnessChannel {
 val handlers = mutableMapOf<String,(Any?,PluginReply)->Unit>()
 fun handle(name: String, handler: (Any?,PluginReply)->Unit) { handlers[name] = handler }
 fun call(name: String, args: Any?, reply: PluginReply) { handlers[name]!!(args,reply) }
}
object NativeChannels {
 private val channels = mutableMapOf<String,HarnessChannel>()
 fun channel(name: String): HarnessChannel = channels.getOrPut(name) { HarnessChannel() }
}
