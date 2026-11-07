package net.oxog.phonebox.deskpanel

import android.annotation.SuppressLint
import android.app.Activity
import android.content.Intent
import android.content.IntentFilter
import android.os.BatteryManager
import android.os.Bundle
import android.os.VibrationEffect
import android.os.Vibrator
import android.view.KeyEvent
import android.view.WindowInsets
import android.view.WindowInsetsController
import android.view.WindowManager
import android.webkit.JavascriptInterface
import android.webkit.WebResourceRequest
import android.webkit.WebResourceResponse
import android.webkit.WebSettings
import android.webkit.WebView
import android.webkit.WebViewClient
import androidx.webkit.WebViewAssetLoader
import org.json.JSONObject

/**
 * Full-screen desk panel. The UI is plain HTML/JS bundled in assets/www.
 * The UI calls "/agent/..." URLs; they are intercepted here and forwarded to the PhoneBox agent on the PC,
 * over USB (adb reverse) or, once paired, over Wi-Fi / Tailscale (see AgentLink). The WebView itself therefore
 * never makes local-network requests (no CORS, mixed-content or local-network-access issues).
 */
class MainActivity : Activity() {

    private lateinit var web: WebView
    private lateinit var link: AgentLink
    @Volatile private var volumeKeysFlipPages = true

    private val assetLoader by lazy {
        WebViewAssetLoader.Builder()
            .addPathHandler("/assets/", WebViewAssetLoader.AssetsPathHandler(this))
            .build()
    }

    @SuppressLint("SetJavaScriptEnabled")
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        link = AgentLink(this)

        web = WebView(this)
        web.setBackgroundColor(0xFF000000.toInt())
        setContentView(web)

        with(web.settings) {
            javaScriptEnabled = true
            domStorageEnabled = true
            mediaPlaybackRequiresUserGesture = false
            cacheMode = WebSettings.LOAD_NO_CACHE
            textZoom = 100
        }
        web.isVerticalScrollBarEnabled = false
        web.isHorizontalScrollBarEnabled = false
        web.webViewClient = object : WebViewClient() {
            override fun shouldInterceptRequest(view: WebView, request: WebResourceRequest): WebResourceResponse? {
                val url = request.url
                if (url.host == ASSET_HOST && url.path?.startsWith("/agent/") == true) return proxy(request)
                return assetLoader.shouldInterceptRequest(url)
            }
        }
        web.addJavascriptInterface(Bridge(), "Android")
        // Lets you debug the panel from the computer via chrome://inspect.
        WebView.setWebContentsDebuggingEnabled(BuildConfig.DEBUG)

        web.loadUrl("https://$ASSET_HOST/assets/www/index.html")
        hideSystemUi()
    }

    private fun proxy(req: WebResourceRequest): WebResourceResponse {
        val path = req.url.encodedPath!!.removePrefix("/agent")
        val query = req.url.encodedQuery?.let { "?$it" } ?: ""
        val r = link.request(req.method, path + query)
        val ct = r.contentType ?: "application/octet-stream"
        val mime = ct.substringBefore(';').trim()
        val charset = if (ct.contains("charset=")) ct.substringAfter("charset=").trim() else null
        return WebResourceResponse(mime, charset, r.code, if (r.code < 400) "OK" else "Error", NO_STORE, r.body)
    }
    override fun onWindowFocusChanged(hasFocus: Boolean) {
        super.onWindowFocusChanged(hasFocus)
        if (hasFocus) hideSystemUi()
    }

    private fun hideSystemUi() {
        window.setDecorFitsSystemWindows(false)
        window.insetsController?.let {
            it.hide(WindowInsets.Type.systemBars())
            it.systemBarsBehavior = WindowInsetsController.BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE
        }
    }

    /** Volume up / down flip to the previous / next page (unless turned off in the settings). */
    override fun dispatchKeyEvent(event: KeyEvent): Boolean {
        val key = when (event.keyCode) {
            KeyEvent.KEYCODE_VOLUME_UP -> "prev"
            KeyEvent.KEYCODE_VOLUME_DOWN -> "next"
            else -> null
        }
        if (key == null || !volumeKeysFlipPages) return super.dispatchKeyEvent(event)
        if (event.action == KeyEvent.ACTION_DOWN && event.repeatCount == 0)
            web.evaluateJavascript("window.onAndroidKey && window.onAndroidKey('$key')", null)
        return true
    }

    @Deprecated("Deprecated in Java")
    override fun onBackPressed() {
        // Kiosk behaviour: Back does not leave the app, it returns to the first page.
        web.evaluateJavascript("window.onAndroidBack && window.onAndroidBack()", null)
    }

    override fun onResume() {
        super.onResume()
        web.onResume()
        hideSystemUi()
    }

    override fun onPause() {
        web.onPause()
        super.onPause()
    }

    inner class Bridge {
        /** Screen brightness 0..1; a negative value returns control to the system setting. */
        @JavascriptInterface
        fun setBrightness(value: Float) = runOnUiThread {
            val lp = window.attributes
            lp.screenBrightness =
                if (value < 0f) WindowManager.LayoutParams.BRIGHTNESS_OVERRIDE_NONE else value.coerceIn(0.01f, 1f)
            window.attributes = lp
        }

        @JavascriptInterface
        fun haptic() {
            val v = getSystemService(Vibrator::class.java) ?: return
            if (v.hasVibrator()) v.vibrate(VibrationEffect.createOneShot(14, VibrationEffect.DEFAULT_AMPLITUDE))
        }

        /** Notification vibration; pattern is a "wait,vibrate,wait,vibrate..." list of milliseconds. */
        @JavascriptInterface
        fun buzz(pattern: String) {
            val v = getSystemService(Vibrator::class.java) ?: return
            val timings = pattern.split(',').mapNotNull { it.trim().toLongOrNull() }.toLongArray()
            if (v.hasVibrator() && timings.isNotEmpty()) v.vibrate(VibrationEffect.createWaveform(timings, -1))
        }

        @JavascriptInterface
        fun battery(): String {
            val i = registerReceiver(null, IntentFilter(Intent.ACTION_BATTERY_CHANGED))
            val level = i?.getIntExtra(BatteryManager.EXTRA_LEVEL, -1) ?: -1
            val scale = i?.getIntExtra(BatteryManager.EXTRA_SCALE, 100) ?: 100
            val plugged = (i?.getIntExtra(BatteryManager.EXTRA_PLUGGED, 0) ?: 0) != 0
            val temp = (i?.getIntExtra(BatteryManager.EXTRA_TEMPERATURE, 0) ?: 0) / 10.0
            return JSONObject()
                .put("level", if (level >= 0) level * 100 / scale else -1)
                .put("plugged", plugged)
                .put("temp", temp)
                .toString()
        }

        /** "usb", "wifi" or "none": how the last agent request was delivered. */
        @JavascriptInterface
        fun transport(): String = link.route.name.lowercase()

        /** Whether the volume keys flip pages (true) or change the phone's volume (false). */
        @JavascriptInterface
        fun setVolumeKeys(enabled: Boolean) { volumeKeysFlipPages = enabled }

        @JavascriptInterface
        fun version(): String = packageManager.getPackageInfo(packageName, 0).versionName ?: ""
    }

    companion object {
        private const val ASSET_HOST = "appassets.androidplatform.net"
        private val NO_STORE = mapOf("Cache-Control" to "no-store")
    }
}
