package net.oxog.phonebox.deskpanel

import java.io.Closeable

/**
 * Keeps one live connection to the agent's /api/stream (server-sent events) and hands every event to [deliver].
 * Reconnects after errors. On Wi-Fi it reconnects every minute so a freshly plugged-in USB cable is preferred again.
 * "open" / "close" pseudo-events tell the panel when to stop or resume its polling fallback.
 */
class AgentStream(private val link: AgentLink, private val deliver: (event: String, data: String) -> Unit) {

    @Volatile private var running = false
    @Volatile private var current: Closeable? = null
    private var thread: Thread? = null

    fun start() {
        if (running) return
        running = true
        thread = Thread(::loop, "agent-stream").apply { isDaemon = true; start() }
    }

    fun stop() {
        running = false
        try { current?.close() } catch (_: Exception) {}
        thread?.interrupt()
        thread = null
    }

    private fun loop() {
        while (running) {
            var opened = false
            try {
                val (route, reply) = link.stream("/api/stream") ?: throw java.io.IOException("offline")
                if (reply.code != 200) { reply.body.close(); throw java.io.IOException("HTTP ${reply.code}") }
                current = reply.body
                opened = true
                deliver("open", "{\"route\":\"${route.name.lowercase()}\"}")
                val since = System.currentTimeMillis()
                reply.body.bufferedReader(Charsets.UTF_8).use { r ->
                    var event = "message"
                    val data = StringBuilder()
                    while (running) {
                        val line = r.readLine() ?: break
                        when {
                            line.isEmpty() -> {
                                if (data.isNotEmpty()) deliver(event, data.toString())
                                event = "message"
                                data.setLength(0)
                                if (route == AgentLink.Route.WIFI && System.currentTimeMillis() - since > 60_000) break
                            }
                            line.startsWith("event:") -> event = line.substring(6).trim()
                            line.startsWith("data:") -> data.append(line.substring(5).trimStart())
                        }
                    }
                }
            } catch (_: Exception) {
            } finally {
                current = null
            }
            if (opened) deliver("close", "{}")
            if (running) try { Thread.sleep(if (opened) 300 else 2000) } catch (_: InterruptedException) {}
        }
    }
}
